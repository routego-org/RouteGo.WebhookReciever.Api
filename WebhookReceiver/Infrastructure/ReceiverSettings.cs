using System.Globalization;

namespace WebhookReceiver.Infrastructure;

/// <summary>Configuration, read once at cold start from environment variables.</summary>
public sealed class ReceiverSettings
{
    private const string SecretIdKey = "SECRET_ID";
    private const string QueueUrlKey = "QUEUE_URL";
    private const string SignatureHeaderKey = "SIGNATURE_HEADER";
    private const string ReplayWindowKey = "REPLAY_WINDOW_SECONDS";
    private const string DefaultSignatureHeader = "X-Signature";

    /// <summary>Secrets Manager id or ARN of the secret holding the webhook signing key.</summary>
    public required string SecretId { get; init; }

    /// <summary>URL of the queue the verified webhooks go to.</summary>
    public required string QueueUrl { get; init; }

    /// <summary>Name of the header the gateway puts its signature in.</summary>
    public required string SignatureHeader { get; init; }

    /// <summary>How old an event may be before it is refused; zero means no age check.</summary>
    public required TimeSpan ReplayWindow { get; init; }

    /// <summary>Reads the settings, failing at once and naming every missing or invalid key.</summary>
    public static ReceiverSettings FromEnvironment()
    {
        string? secretId = Environment.GetEnvironmentVariable(SecretIdKey);
        string? queueUrl = Environment.GetEnvironmentVariable(QueueUrlKey);
        if (string.IsNullOrWhiteSpace(secretId) || string.IsNullOrWhiteSpace(queueUrl))
        {
            throw new InvalidOperationException($"Missing configuration: both {SecretIdKey} and {QueueUrlKey} must be set.");
        }

        return new ReceiverSettings
        {
            SecretId = secretId,
            QueueUrl = queueUrl,
            SignatureHeader = ReadSignatureHeader(),
            ReplayWindow = ReadReplayWindow(),
        };
    }

    private static string ReadSignatureHeader()
    {
        string? configured = Environment.GetEnvironmentVariable(SignatureHeaderKey);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultSignatureHeader;
        }

        return configured;
    }

    private static TimeSpan ReadReplayWindow()
    {
        string? configured = Environment.GetEnvironmentVariable(ReplayWindowKey);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return TimeSpan.Zero;
        }

        bool parsed = int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds);
        if (!parsed)
        {
            throw new InvalidOperationException($"{ReplayWindowKey} must be a whole number of seconds, but was '{configured}'.");
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
