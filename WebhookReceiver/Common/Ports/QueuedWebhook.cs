namespace WebhookReceiver.Common.Ports;

/// <summary>A verified webhook ready to be queued: the body exactly as the gateway sent it, plus its labels.</summary>
public sealed class QueuedWebhook
{
    /// <summary>The raw request body, unchanged.</summary>
    public required string Body { get; init; }

    /// <summary>The gateway's own id for the event; the payment service de-duplicates on it.</summary>
    public required string GatewayEventId { get; init; }

    /// <summary>Id that follows this webhook through the payment service's logs.</summary>
    public required string CorrelationId { get; init; }
}
