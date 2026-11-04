namespace WebhookReceiver.Common.Ports;

/// <summary>The durable hand-off to the payment service.</summary>
public interface IWebhookQueue
{
    /// <summary>Puts one verified webhook on the queue; throws if it could not be stored.</summary>
    Task EnqueueAsync(QueuedWebhook webhook, CancellationToken cancellationToken);
}
