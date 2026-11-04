using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using WebhookReceiver.Common.Ports;

namespace WebhookReceiver.Infrastructure;

/// <summary>
/// Reads the webhook secret from Secrets Manager and keeps it for five minutes. One Lambda container handles
/// one request at a time, so the cache needs no locking; the short lifetime is what lets a rotated secret
/// take effect without a redeploy.
/// </summary>
public sealed class SecretsManagerSecretSource : IWebhookSecretSource
{
    private const string SecretField = "webhookSecret";
    private static readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(5);

    private readonly IAmazonSecretsManager _client;
    private readonly string _secretId;
    private readonly IClock _clock;
    private string? _cachedSecret;
    private DateTimeOffset _refreshAt;

    /// <summary>Creates a source for one secret.</summary>
    public SecretsManagerSecretSource(IAmazonSecretsManager client, string secretId, IClock clock)
    {
        _client = client;
        _secretId = secretId;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<string> GetWebhookSecretAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        if (_cachedSecret != null && now < _refreshAt)
        {
            return _cachedSecret;
        }

        var request = new GetSecretValueRequest { SecretId = _secretId };
        GetSecretValueResponse response = await _client.GetSecretValueAsync(request, cancellationToken);

        _cachedSecret = ReadSecret(response.SecretString);
        _refreshAt = now + _refreshInterval;
        return _cachedSecret;
    }

    private string ReadSecret(string? secretString)
    {
        if (string.IsNullOrWhiteSpace(secretString))
        {
            throw new InvalidOperationException($"Secret '{_secretId}' has no value. Expected JSON with a '{SecretField}' field.");
        }

        using var document = JsonDocument.Parse(secretString);
        bool found = document.RootElement.TryGetProperty(SecretField, out JsonElement element);
        string? value = null;
        if (found && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
        }

        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"Secret '{_secretId}' has no '{SecretField}' field.");
        }

        return value;
    }
}
