using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using WebhookReceiver.Common.Ports;

namespace WebhookReceiver.Infrastructure;

/// <summary>Queues webhooks on the payment service's SQS queue.</summary>
public sealed class SqsWebhookQueue : IWebhookQueue
{
    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;
    private readonly IClock _clock;

    /// <summary>Creates a queue adapter for one queue.</summary>
    public SqsWebhookQueue(IAmazonSQS sqs, string queueUrl, IClock clock)
    {
        _sqs = sqs;
        _queueUrl = queueUrl;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task EnqueueAsync(QueuedWebhook webhook, CancellationToken cancellationToken)
    {
        string receivedAt = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        var message = new SendMessageRequest { QueueUrl = _queueUrl, MessageBody = webhook.Body };
        message.MessageAttributes["gatewayEventId"] = Label(webhook.GatewayEventId);
        message.MessageAttributes["receivedAt"] = Label(receivedAt);
        message.MessageAttributes["correlationId"] = Label(webhook.CorrelationId);

        await _sqs.SendMessageAsync(message, cancellationToken);
    }

    private static MessageAttributeValue Label(string value)
    {
        return new MessageAttributeValue { DataType = "String", StringValue = value };
    }
}
