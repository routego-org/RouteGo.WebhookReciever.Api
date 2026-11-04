using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebhookReceiver.Common;
using WebhookReceiver.Common.Ports;
using WebhookReceiver.Domain;

namespace WebhookReceiver.Features.ReceivePaymentWebhook;

/// <summary>A webhook as it arrived: the exact body bytes and the signature header value, if any.</summary>
public sealed class ReceivePaymentWebhookRequest
{
    /// <summary>The body bytes exactly as received; the signature is computed over these.</summary>
    public required byte[] Body { get; init; }

    /// <summary>The signature header value, or null when the header was absent.</summary>
    public string? Signature { get; init; }
}

/// <summary>What happened to a webhook.</summary>
public enum ReceivePaymentWebhookOutcome
{
    /// <summary>Verified and safely queued.</summary>
    Accepted,

    /// <summary>The signature was missing or wrong; nothing was queued.</summary>
    SignatureInvalid,

    /// <summary>The signature was right but the body is not a usable payment event; nothing was queued.</summary>
    PayloadInvalid,

    /// <summary>The event is older than the replay window; nothing was queued.</summary>
    Stale,
}

/// <summary>
/// Verifies a payment webhook and queues it for the payment service. Does no business logic: what the event
/// means is the payment service's decision. Safe to receive the same event many times, because the payment
/// service de-duplicates on the gateway event id this handler passes along.
/// </summary>
public sealed class ReceivePaymentWebhookHandler
{
    private readonly IWebhookSecretSource _secrets;
    private readonly IWebhookQueue _queue;
    private readonly ReplayGuard _replayGuard;
    private readonly ILogger<ReceivePaymentWebhookHandler> _logger;

    /// <summary>Creates the handler from its ports.</summary>
    public ReceivePaymentWebhookHandler(
        IWebhookSecretSource secrets,
        IWebhookQueue queue,
        ReplayGuard replayGuard,
        ILogger<ReceivePaymentWebhookHandler> logger)
    {
        _secrets = secrets;
        _queue = queue;
        _replayGuard = replayGuard;
        _logger = logger;
    }

    /// <summary>Verifies and queues one webhook; throws if the secret or the queue is unavailable.</summary>
    public async Task<ReceivePaymentWebhookOutcome> HandleAsync(
        ReceivePaymentWebhookRequest request,
        CancellationToken cancellationToken)
    {
        string secret = await _secrets.GetWebhookSecretAsync(cancellationToken);
        if (!WebhookSignature.IsValid(request.Body, request.Signature, secret))
        {
            _logger.LogWarning("signature_invalid: rejected a webhook with a missing or wrong signature.");
            return ReceivePaymentWebhookOutcome.SignatureInvalid;
        }

        PaymentEventLabels? labels = ReadLabels(request.Body);
        if (labels == null)
        {
            _logger.LogWarning("payload_invalid: a correctly signed webhook had no gatewayEventId; not queued.");
            return ReceivePaymentWebhookOutcome.PayloadInvalid;
        }

        if (labels.OccurredAt != null && _replayGuard.IsStale(labels.OccurredAt.Value))
        {
            _logger.LogWarning(
                "timestamp_stale: webhook {GatewayEventId} is older than the replay window; not queued.",
                labels.GatewayEventId);
            return ReceivePaymentWebhookOutcome.Stale;
        }

        var webhook = new QueuedWebhook
        {
            Body = Encoding.UTF8.GetString(request.Body),
            GatewayEventId = labels.GatewayEventId,
            CorrelationId = Guid.NewGuid().ToString(),
        };
        await _queue.EnqueueAsync(webhook, cancellationToken);

        _logger.LogInformation(
            "Queued webhook {GatewayEventId} with correlation {CorrelationId}.",
            webhook.GatewayEventId,
            webhook.CorrelationId);
        return ReceivePaymentWebhookOutcome.Accepted;
    }

    private static PaymentEventLabels? ReadLabels(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("gatewayEventId", out JsonElement idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return new PaymentEventLabels { GatewayEventId = id, OccurredAt = ReadOccurredAt(root) };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadOccurredAt(JsonElement root)
    {
        if (!root.TryGetProperty("occurredAt", out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        if (element.TryGetDateTimeOffset(out DateTimeOffset occurredAt))
        {
            return occurredAt;
        }

        return null;
    }

    private sealed class PaymentEventLabels
    {
        public required string GatewayEventId { get; init; }

        public DateTimeOffset? OccurredAt { get; init; }
    }
}
