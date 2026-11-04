using WebhookReceiver.Common.Ports;

namespace WebhookReceiver.Infrastructure;

/// <summary>The real clock.</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset GetUtcNow()
    {
        return TimeProvider.System.GetUtcNow();
    }
}
