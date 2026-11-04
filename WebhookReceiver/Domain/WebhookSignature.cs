using System.Security.Cryptography;
using System.Text;

namespace WebhookReceiver.Domain;

/// <summary>The gateway's signing rule: HMAC-SHA256 of the raw body, in hex, optionally prefixed "sha256=".</summary>
public static class WebhookSignature
{
    private const string Prefix = "sha256=";

    /// <summary>Returns true only when the header carries the correct signature for this body and secret.</summary>
    public static bool IsValid(byte[] body, string? header, string secret)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        string hex = header.Trim();
        if (hex.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            hex = hex.Substring(Prefix.Length);
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);

        // Fixed-time so response timing never reveals how many leading bytes matched.
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
