using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Kubarr.Identity;
using Xunit;

namespace Jellyfin.Plugin.Kubarr.Tests;

public class IdentityTokenValidatorTests
{
    private static readonly RSA Key = RSA.Create(2048);
    private static readonly RSA OtherKey = RSA.Create(2048);
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static JsonWebKey Jwk(RSA rsa, string kid = "kubarr-jwt-key")
    {
        var p = rsa.ExportParameters(false);
        return new JsonWebKey { Kty = "RSA", Kid = kid, N = B64(p.Modulus!), E = B64(p.Exponent!) };
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Token(object payload, RSA? signer = null, string alg = "RS256", string kid = "kubarr-jwt-key")
    {
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg, typ = "JWT", kid }));
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(payload));
        var sig = (signer ?? Key).SignData(Encoding.ASCII.GetBytes(header + "." + body), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + body + "." + B64(sig);
    }

    private static object Claims(string aud = "jellyfin", string iss = "kubarr", long? exp = null, bool admin = false) => new
    {
        iss,
        sub = "42",
        aud,
        iat = Now.ToUnixTimeSeconds(),
        exp = exp ?? Now.AddMinutes(5).ToUnixTimeSeconds(),
        preferred_username = "alice",
        email = "alice@example.com",
        is_admin = admin,
    };

    [Fact]
    public void AcceptsAValidToken()
    {
        var user = IdentityTokenValidator.Validate(Token(Claims(admin: true)), new[] { Jwk(Key) }, Now, out var failure);
        Assert.Null(failure);
        Assert.NotNull(user);
        Assert.Equal("42", user!.UserId);
        Assert.Equal("alice", user.Username);
        Assert.True(user.IsAdmin);
    }

    [Theory]
    [InlineData("seerr", "kubarr", "wrong audience")]
    [InlineData("jellyfin", "evil", "wrong issuer")]
    public void RejectsWrongAudienceOrIssuer(string aud, string iss, string reason)
    {
        Assert.Null(IdentityTokenValidator.Validate(Token(Claims(aud, iss)), new[] { Jwk(Key) }, Now, out var failure));
        Assert.Equal(reason, failure);
    }

    [Fact]
    public void RejectsExpiredBeyondLeeway()
    {
        var token = Token(Claims(exp: Now.AddMinutes(-1).ToUnixTimeSeconds()));
        Assert.Null(IdentityTokenValidator.Validate(token, new[] { Jwk(Key) }, Now, out var failure));
        Assert.Equal("expired", failure);

        var justExpired = Token(Claims(exp: Now.AddSeconds(-10).ToUnixTimeSeconds()));
        Assert.NotNull(IdentityTokenValidator.Validate(justExpired, new[] { Jwk(Key) }, Now, out _));
    }

    [Fact]
    public void RejectsForeignSignatureAndUnknownKey()
    {
        Assert.Null(IdentityTokenValidator.Validate(Token(Claims(), OtherKey), new[] { Jwk(Key) }, Now, out var failure));
        Assert.Equal("bad signature", failure);

        Assert.Null(IdentityTokenValidator.Validate(Token(Claims(), kid: "other"), new[] { Jwk(Key) }, Now, out failure));
        Assert.Equal("unknown signing key", failure);
    }

    [Fact]
    public void RejectsAlgNoneAndTamperedPayload()
    {
        var none = Token(Claims(), alg: "none");
        Assert.Null(IdentityTokenValidator.Validate(none, new[] { Jwk(Key) }, Now, out var failure));
        Assert.Equal("unsupported algorithm", failure);

        var parts = Token(Claims()).Split('.');
        var forged = B64(JsonSerializer.SerializeToUtf8Bytes(Claims(admin: true)));
        Assert.Null(IdentityTokenValidator.Validate($"{parts[0]}.{forged}.{parts[2]}", new[] { Jwk(Key) }, Now, out failure));
        Assert.Equal("bad signature", failure);
    }

    [Fact]
    public void RejectsGarbage()
    {
        Assert.Null(IdentityTokenValidator.Validate("not-a-jwt", new[] { Jwk(Key) }, Now, out var failure));
        Assert.Equal("malformed token", failure);
    }
}
