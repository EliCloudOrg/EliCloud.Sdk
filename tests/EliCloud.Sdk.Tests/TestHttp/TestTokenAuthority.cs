using System.Security.Cryptography;
using System.Text;
using EliCloud.Sdk.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EliCloud.Sdk.Tests.TestHttp;

/// <summary>
/// 测试用的「令牌签发机构」：一把 RSA 私钥 + 对应的 JWKS，用来生成真实签名的令牌。
/// </summary>
/// <remarks>
/// 这里刻意用真实的非对称签名与真实的 JWKS JSON，而不是打桩验签逻辑：
/// 验签是这套 SDK 里最不能出错的一段，测「我们有没有正确地把令牌和公钥对起来」
/// 才有意义。
/// </remarks>
internal sealed class TestTokenAuthority
{
    private readonly Dictionary<string, RSA> _keys = new(StringComparer.Ordinal);

    internal const string Issuer = "https://elicloud.test/auth";
    internal const string Audience = "elicloud-services";
    internal const string DefaultKid = "2026-01";

    internal TestTokenAuthority(string kid = DefaultKid)
    {
        AddKey(kid);
        DefaultKeyId = kid;
    }

    internal string DefaultKeyId { get; }

    /// <summary>新增一把密钥（模拟服务端密钥轮换：新旧公钥并存）。</summary>
    internal void AddKey(string kid)
    {
        var rsa = RSA.Create(2048);
        _keys[kid] = rsa;
    }

    /// <summary>生成当前 JWKS 文档（只含指定 kid，不指定则全部）。</summary>
    internal string JwksJson(params string[] kids)
    {
        var selected = kids.Length == 0 ? _keys.Keys.ToArray() : kids;
        var entries = selected.Select(kid =>
        {
            var parameters = _keys[kid].ExportParameters(includePrivateParameters: false);
            var modulus = Base64UrlEncoder.Encode(parameters.Modulus);
            var exponent = Base64UrlEncoder.Encode(parameters.Exponent);
            return $$"""{"kty":"RSA","use":"sig","alg":"RS256","kid":"{{kid}}","n":"{{modulus}}","e":"{{exponent}}"}""";
        });

        return $$"""{"keys":[{{string.Join(",", entries)}}]}""";
    }

    /// <summary>用指定密钥签发一个 RS256 access token。</summary>
    internal string CreateAccessToken(
        string? issuer = Issuer,
        string? audience = Audience,
        string subject = "user_0001",
        string username = "alice",
        string scope = "openid mc:whitelist",
        string? kid = null,
        DateTime? expires = null,
        DateTime? notBefore = null,
        string? clientId = null)
    {
        var keyId = kid ?? DefaultKeyId;
        var signingKey = new RsaSecurityKey(_keys[keyId]) { KeyId = keyId };

        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["username"] = username,
            ["scope"] = scope,
            ["jti"] = Guid.NewGuid().ToString("N"),
        };

        if (clientId is not null)
        {
            claims["client_id"] = clientId;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            NotBefore = notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>用对称密钥签发的 HS256 令牌（平台必须拒绝）。</summary>
    internal static string CreateHs256Token(string? issuer = Issuer, string? audience = Audience)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = new Dictionary<string, object> { ["sub"] = "user_0001", ["scope"] = "openid" },
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>手写一个 <c>alg=none</c> 的令牌（无签名）。</summary>
    internal static string CreateAlgNoneToken(string? issuer = Issuer, string? audience = Audience)
    {
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(
            $$"""
            {"iss":"{{issuer}}","aud":"{{audience}}","sub":"user_0001","scope":"openid",
             "exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}
            """);

        return $"{header}.{payload}.";
    }

    /// <summary>篡改签名：把最后一段换掉。</summary>
    internal static string TamperSignature(string token)
    {
        var parts = token.Split('.');
        var signature = Base64UrlEncoder.DecodeBytes(parts[2]);
        signature[0] ^= 0xFF;
        parts[2] = Base64UrlEncoder.Encode(signature);
        return string.Join('.', parts);
    }
}
