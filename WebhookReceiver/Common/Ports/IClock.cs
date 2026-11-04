namespace WebhookReceiver.Common.Ports;

/// <summary>Source of the current time, so tests can control it.</summary>
public interface IClock
{
    /// <summary>Returns the current moment in UTC.</summary>
    DateTimeOffset GetUtcNow();
}
