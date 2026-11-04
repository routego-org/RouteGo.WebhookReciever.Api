namespace WebhookReceiver.Common.Ports;

/// <summary>Provides the secret the payment gateway signs its webhooks with.</summary>
public interface IWebhookSecretSource
{
    /// <summary>Returns the current webhook signing secret.</summary>
    Task<string> GetWebhookSecretAsync(CancellationToken cancellationToken);
}
