using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

public sealed record AuthTokenResult(string Token, DateTimeOffset ExpiresAt);
public sealed record ValidatedAuthToken(int UserId, DateTimeOffset ExpiresAt);

public class AuthTokenService
{
    private readonly IConfiguration _config;

    public AuthTokenService(IConfiguration config)
    {
        _config = config;
    }

    public AuthTokenResult IssueToken(AppUser user)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(GetTokenHours());
        var payload = new AuthTokenPayload(user.Id, expiresAt.ToUnixTimeSeconds());
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var payloadSegment = Base64UrlEncode(payloadBytes);
        var signatureSegment = Sign(payloadSegment);

        return new AuthTokenResult($"{payloadSegment}.{signatureSegment}", expiresAt);
    }

    public ValidatedAuthToken? ValidateToken(string token)
    {
        var segments = token.Split('.', 2);
        if (segments.Length != 2)
            return null;

        var expectedSignature = Sign(segments[0]);
        if (!FixedTimeEquals(expectedSignature, segments[1]))
            return null;

        AuthTokenPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<AuthTokenPayload>(Base64UrlDecode(segments[0]))
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.Exp);
        if (expiresAt <= DateTimeOffset.UtcNow)
            return null;

        return new ValidatedAuthToken(payload.UserId, expiresAt);
    }

    private int GetTokenHours()
    {
        var value = _config["AUTH_TOKEN_HOURS"];
        return int.TryParse(value, out var hours) && hours > 0 ? hours : 12;
    }

    private string Sign(string payloadSegment)
    {
        using var hmac = new HMACSHA256(GetSecret());
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadSegment)));
    }

    private byte[] GetSecret()
    {
        var secret = _config["AUTH_TOKEN_SECRET"];
        if (string.IsNullOrWhiteSpace(secret))
            secret = "dev-auth-token-secret-change-before-production";

        return SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        return Convert.FromBase64String(base64);
    }

    private sealed record AuthTokenPayload(int UserId, long Exp);
}
