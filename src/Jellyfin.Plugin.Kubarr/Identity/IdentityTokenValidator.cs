using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Kubarr.Identity;

/// <summary>
/// Verifies the X-Kubarr-Identity token Kubarr's gateway attaches for a
/// signed-in user: RS256 against Kubarr's published keys, issuer "kubarr",
/// audience "jellyfin", unexpired. Hand-rolled on System.Security.Cryptography
/// so the plugin ships no JWT library that could clash with Jellyfin's.
/// </summary>
public static class IdentityTokenValidator
{
    public const string Issuer = "kubarr";
    public const string Audience = "jellyfin";

    /// <summary>Clock skew tolerated between Kubarr and Jellyfin.</summary>
    private static readonly TimeSpan Leeway = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Returns the user the token names, or null with a reason when it must
    /// not be trusted.
    /// </summary>
    public static KubarrUser? Validate(string token, IReadOnlyList<JsonWebKey> keys, DateTimeOffset now, out string? failure)
    {
        failure = null;
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            failure = "malformed token";
            return null;
        }

        JsonElement header, payload;
        try
        {
            header = JsonDocument.Parse(Base64UrlDecode(parts[0])).RootElement;
            payload = JsonDocument.Parse(Base64UrlDecode(parts[1])).RootElement;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            failure = "malformed token";
            return null;
        }

        if (!header.TryGetProperty("alg", out var alg) || alg.GetString() != "RS256")
        {
            failure = "unsupported algorithm";
            return null;
        }

        var kid = header.TryGetProperty("kid", out var k) ? k.GetString() : null;
        var candidates = keys.Where(key => key.Kty == "RSA" && (kid is null || key.Kid == kid)).ToList();
        if (candidates.Count == 0)
        {
            failure = "unknown signing key";
            return null;
        }

        byte[] signature;
        try
        {
            signature = Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            failure = "malformed token";
            return null;
        }

        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (!candidates.Any(key => Verify(key, signed, signature)))
        {
            failure = "bad signature";
            return null;
        }

        if (Str(payload, "iss") != Issuer)
        {
            failure = "wrong issuer";
            return null;
        }

        if (!HasAudience(payload, Audience))
        {
            failure = "wrong audience";
            return null;
        }

        if (!payload.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var expSeconds)
            || DateTimeOffset.FromUnixTimeSeconds(expSeconds) + Leeway < now)
        {
            failure = "expired";
            return null;
        }

        var sub = Str(payload, "sub");
        var username = Str(payload, "preferred_username");
        if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(username))
        {
            failure = "missing subject";
            return null;
        }

        return new KubarrUser
        {
            UserId = sub,
            Username = username,
            Email = Str(payload, "email") ?? string.Empty,
            IsAdmin = payload.TryGetProperty("is_admin", out var admin) && admin.ValueKind == JsonValueKind.True,
            Enabled = true,
        };
    }

    private static bool Verify(JsonWebKey key, byte[] data, byte[] signature)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = Base64UrlDecode(key.N),
                Exponent = Base64UrlDecode(key.E),
            });
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool HasAudience(JsonElement payload, string audience)
    {
        if (!payload.TryGetProperty("aud", out var aud))
        {
            return false;
        }

        return aud.ValueKind switch
        {
            JsonValueKind.String => aud.GetString() == audience,
            JsonValueKind.Array => aud.EnumerateArray().Any(a => a.GetString() == audience),
            _ => false,
        };
    }

    internal static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid base64url length"),
        };
        return Convert.FromBase64String(s);
    }
}

/// <summary>One entry of Kubarr's JWKS.</summary>
public record JsonWebKey
{
    [System.Text.Json.Serialization.JsonPropertyName("kty")]
    public string Kty { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("kid")]
    public string? Kid { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("n")]
    public string N { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("e")]
    public string E { get; init; } = string.Empty;
}

public record JsonWebKeySet
{
    [System.Text.Json.Serialization.JsonPropertyName("keys")]
    public List<JsonWebKey> Keys { get; init; } = new();
}
