using System.Text;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SecretsManager;
using Amazon.SQS;
using Microsoft.Extensions.Logging;
using WebhookReceiver.Common;
using WebhookReceiver.Common.Ports;
using WebhookReceiver.Features.ReceivePaymentWebhook;
using WebhookReceiver.Infrastructure;

namespace WebhookReceiver;

/// <summary>
/// The Lambda entry point behind the API Gateway HTTP API route POST /webhooks/payment. It only translates
/// between the HTTP event and the handler; verifying and queueing live in the handler.
/// </summary>
public class Function
{
    private const string ContentTypeJson = "application/json";

    private readonly ReceivePaymentWebhookHandler _handler;
    private readonly string _signatureHeader;
    private readonly ILogger<Function> _logger;

    /// <summary>Used by the Lambda runtime: builds everything from environment variables once per container.</summary>
    public Function()
        : this(ReceiverSettings.FromEnvironment(), CreateLoggerFactory())
    {
    }

    /// <summary>Used by tests, which supply their own handler.</summary>
    internal Function(ReceivePaymentWebhookHandler handler, string signatureHeader, ILogger<Function> logger)
    {
        _handler = handler;
        _signatureHeader = signatureHeader;
        _logger = logger;
    }

    private Function(ReceiverSettings settings, ILoggerFactory loggerFactory)
        : this(CreateHandler(settings, loggerFactory), settings.SignatureHeader, loggerFactory.CreateLogger<Function>())
    {
    }

    /// <summary>Answers one webhook: 200 when queued, 401 or 400 when refused, 500 when it could not be queued.</summary>
    // The Lambda handler string in template.yaml names this method, so it keeps the name the platform expects.
#pragma warning disable VSTHRD200
    public async Task<APIGatewayHttpApiV2ProxyResponse> Handler(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var webhook = new ReceivePaymentWebhookRequest
        {
            Body = ReadBody(request),
            Signature = FindHeader(request.Headers, _signatureHeader),
        };

        using var timeout = new CancellationTokenSource(context.RemainingTime);
        try
        {
            ReceivePaymentWebhookOutcome outcome = await _handler.HandleAsync(webhook, timeout.Token);
            return ToResponse(outcome);
        }
        catch (Exception exception)
        {
            // The gateway retries on 5xx, which is exactly what we want when the queue or secret is unavailable.
            _logger.LogError(exception, "Webhook could not be processed for request {RequestId}; the gateway should retry.", context.AwsRequestId);
            return Respond(500, "{\"received\":false}");
        }
    }

#pragma warning restore VSTHRD200

    private static APIGatewayHttpApiV2ProxyResponse ToResponse(ReceivePaymentWebhookOutcome outcome)
    {
        switch (outcome)
        {
            case ReceivePaymentWebhookOutcome.Accepted:
                return Respond(200, "{\"received\":true}");
            case ReceivePaymentWebhookOutcome.PayloadInvalid:
                return Respond(400, "{\"received\":false}");
            default:
                return Respond(401, "{\"received\":false}");
        }
    }

    private static APIGatewayHttpApiV2ProxyResponse Respond(int statusCode, string body)
    {
        return new APIGatewayHttpApiV2ProxyResponse
        {
            StatusCode = statusCode,
            Body = body,
            Headers = new Dictionary<string, string> { ["Content-Type"] = ContentTypeJson },
        };
    }

    private static byte[] ReadBody(APIGatewayHttpApiV2ProxyRequest request)
    {
        if (string.IsNullOrEmpty(request.Body))
        {
            return Array.Empty<byte>();
        }

        if (request.IsBase64Encoded)
        {
            return Convert.FromBase64String(request.Body);
        }

        return Encoding.UTF8.GetBytes(request.Body);
    }

    private static string? FindHeader(IDictionary<string, string>? headers, string name)
    {
        if (headers == null)
        {
            return null;
        }

        foreach (KeyValuePair<string, string> header in headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        return null;
    }

    private static ILoggerFactory CreateLoggerFactory()
    {
        return LoggerFactory.Create(builder => builder.AddJsonConsole());
    }

    private static ReceivePaymentWebhookHandler CreateHandler(ReceiverSettings settings, ILoggerFactory loggerFactory)
    {
        IClock clock = new SystemClock();

        // The SDK clients pick up AWS_ENDPOINT_URL, so local runs against LocalStack need no code change.
        IWebhookSecretSource secrets = new SecretsManagerSecretSource(new AmazonSecretsManagerClient(), settings.SecretId, clock);
        IWebhookQueue queue = new SqsWebhookQueue(new AmazonSQSClient(), settings.QueueUrl, clock);
        var replayGuard = new ReplayGuard(clock, settings.ReplayWindow);

        return new ReceivePaymentWebhookHandler(secrets, queue, replayGuard, loggerFactory.CreateLogger<ReceivePaymentWebhookHandler>());
    }
}
