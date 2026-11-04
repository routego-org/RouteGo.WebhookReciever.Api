using WebhookReceiver.Common.Ports;

namespace WebhookReceiver.Common;

/// <summary>
/// Refuses events whose own timestamp is older than the configured window, so a captured request cannot be
/// replayed later. A window of zero turns the check off: gateways retry for hours, and a stale-but-genuine
/// payment must not be dropped unless the operator chose that trade-off.
/// </summary>
public sealed class ReplayGuard
{
    private readonly IClock _clock;
    private readonly TimeSpan _window;

    /// <summary>Creates a guard; <paramref name="window"/> of zero or less disables it.</summary>
    public ReplayGuard(IClock clock, TimeSpan window)
    {
        _clock = clock;
        _window = window;
    }

    /// <summary>Returns true when the event is older than the window.</summary>
    public bool IsStale(DateTimeOffset occurredAt)
    {
        if (_window <= TimeSpan.Zero)
        {
            return false;
        }

        TimeSpan age = _clock.GetUtcNow() - occurredAt;
        return age > _window;
    }
}
