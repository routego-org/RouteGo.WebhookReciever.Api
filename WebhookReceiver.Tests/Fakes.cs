using WebhookReceiver.Common.Ports;

namespace WebhookReceiver.Tests;

internal sealed class FixedClock : IClock
{
    private readonly DateTimeOffset _now;

    public FixedClock(DateTimeOffset now)
    {
        _now = now;
    }

    public DateTimeOffset GetUtcNow()
    {
        return _now;
    }
}

internal sealed class FixedSecretSource : IWebhookSecretSource
{
    private readonly string _secret;

    public FixedSecretSource(string secret)
    {
        _secret = secret;
    }

    public Task<string> GetWebhookSecretAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(_secret);
    }
}

internal sealed class RecordingQueue : IWebhookQueue
{
    public List<QueuedWebhook> Sent { get; } = new List<QueuedWebhook>();

    public bool Fails { get; set; }

    public Task EnqueueAsync(QueuedWebhook webhook, CancellationToken cancellationToken)
    {
        if (Fails)
        {
            throw new InvalidOperationException("Access denied to the queue.");
        }

        Sent.Add(webhook);
        return Task.CompletedTask;
    }
}
