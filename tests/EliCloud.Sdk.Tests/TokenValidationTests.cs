using System.Net;
using EliCloud.Sdk.Tests.TestHttp;
using EliCloud.Sdk.Tokens;
using Xunit;

namespace EliCloud.Sdk.Tests;

/// <summary>
/// access token 校验（业务服务侧）测试。
/// </summary>
/// <remarks>
/// 这一组是安全边界测试，逐条对应 <c>docs/mc.md</c> §6.1 的校验表与
/// <c>docs/sso-oidc.md</c> §7 的安全清单。任何一条放松都会让「业务服务只认 SSO 签发的
/// access token」这个前提失效。
/// </remarks>
public sealed class TokenValidationTests
{
    // -------------------------------------------------------------- 正常路径

    [Fact]
    public async Task ValidToken_YieldsSubjectUsernameAndScopes()
    {
        var authority = new TestTokenAuthority();
        var validator = CreateValidator(authority, requiredScopes: ["mc:whitelist"]);

        var result = await validator.ValidateAccessTokenAsync(authority.CreateAccessToken(clientId: "elipese-web"));

        Assert.True(result.IsValid, result.ErrorDescription);
        Assert.Equal("user_0001", result.Subject);
        Assert.Equal("alice", result.Username);
        Assert.Equal("elipese-web", result.ClientId);
        Assert.True(result.HasScope("mc:whitelist"));
        Assert.Contains("openid", result.Scopes);
    }

    [Fact]
    public async Task ValidToken_ExposesClaimsPrincipalForAspNetCore()
    {
        var authority = new TestTokenAuthority();
        var validator = CreateValidator(authority);

        var result = await validator.ValidateAccessTokenAsync(authority.CreateAccessToken());

        Assert.NotNull(result.Principal);
        Assert.Equal("user_0001", result.Principal!.GetEliCloudSubject());
        Assert.Equal("alice", result.Principal.GetEliCloudUsername());
        Assert.True(result.Principal.HasEliCloudScope("mc:whitelist"));
        Assert.False(result.Principal.HasEliCloudScope("pdf:read"));
    }

    // ------------------------------------------------------------ 拒绝的令牌

    [Fact]
    public async Task WrongIssuer_IsRejected()
    {
        var authority = new TestTokenAuthority();
        var validator = CreateValidator(authority);

        var result = await validator.ValidateAccessTokenAsync(
            authority.CreateAccessToken(issuer: "https://attacker.example/auth"));

        Assert.False(result.IsValid);
        Assert.Equal(EliCloudErrorCodes.InvalidToken, result.ErrorCode);
    }

    [Fact]
    public async Task IdTokenMisuse_IsRejectedBecauseAudienceDiffers()
    {
        // id_token 的 aud 是 client_id。只有校验 aud，才能把它挡在业务 API 之外。
        var authority = new TestTokenAuthority();
        var validator = CreateValidator(authority);

        var result = await validator.ValidateAccessTokenAsync(
            authority.CreateAccessToken(audience: "elipese-web"));

        Assert.False(result.IsValid);
        Assert.Equal(EliCloudErrorCodes.InvalidToken, result.ErrorCode);
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        var authority = new TestTokenAuthority();
        var validator = CreateValidator(authority);

        var result = await validator.ValidateAccessTokenAsync(
            authority.CreateAccessToken(
                notBefore: DateTime.UtcNow.AddHours(-2),
                expires: DateTime.UtcNow.AddHours(-1)));

        Assert.False(result.IsValid);
        Assert.Equal(EliCloudErrorCodes.InvalidToken, result.ErrorCode);
    }

    [Fact]
    public async Task ClockSkew_IsToleratedWithinThirtySeconds()
    {
        var authority = new TestTokenAuthority();
        var validator = CreateValidator(authority);

        // 刚过期 10 秒 —— 平台业务服务统一给 30 秒容忍，避免服务器之间的小幅时钟漂移造成假失败。
        var result = await validator.ValidateAccessTokenAsync(
            authority.CreateAccessToken(
                notBefore: DateTime.UtcNow.AddHours(-2),
                expires: DateTime.UtcNow.AddSeconds(-10)));

        Assert.True(result.IsValid, result.ErrorDescription);
    }

    [Fact]
    public async Task Hs256Token_IsRejectedEvenIfCorrectlySigned()
    {
        // 业务服务只应持有公钥，绝不能接受对称算法 —— 否则「拿到公钥」就等于「能签发令牌」。
        var authority = new TestTokenAuthority();
        var validator = CreateValidator(authority);

        var result = await validator.ValidateAccessTokenAsync(TestTokenAuthority.CreateHs256Token());

        Assert.False(result.IsValid);
        Assert.Equal(EliCloudErrorCodes.InvalidToken, result.ErrorCode);
    }

    [Fact]
    public async Task AlgNoneToken_IsRejected()
    {
        var authority = new TestTokenAuthority();
        var validator = CreateValidator(authority);

        var result = await validator.ValidateAccessTokenAsync(TestTokenAuthority.CreateAlgNoneToken());

        Assert.False(result.IsValid);
        Assert.Equal(EliCloudErrorCodes.InvalidToken, result.ErrorCode);
    }

    [Fact]
    public async Task TamperedSignature_IsRejected()
    {
        var authority = new TestTokenAuthority();
        var validator = CreateValidator(authority);
        var token = authority.CreateAccessToken();

        var result = await validator.ValidateAccessTokenAsync(TestTokenAuthority.TamperSignature(token));

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task UnknownKid_IsRejectedAfterOneRefreshAttempt()
    {
        var authority = new TestTokenAuthority("2026-01");
        authority.AddKey("2026-07");

        var jwksCalls = 0;
        var handler = new CapturingHandler(_ =>
        {
            jwksCalls++;

            // 只发布 2026-07 的公钥；令牌声称的 kid 是 2026-01（伪造或来自别的环境）。
            return Stub.Json(authority.JwksJson("2026-07"), cacheControl: "public, max-age=300");
        });

        var validator = CreateValidator(handler, out _);
        var token = authority.CreateAccessToken(kid: "2026-01");

        var result = await validator.ValidateAccessTokenAsync(token);

        Assert.False(result.IsValid);
        Assert.Equal(EliCloudErrorCodes.InvalidToken, result.ErrorCode);
        Assert.Equal(2, jwksCalls); // 首次取缓存 + 一次因未知 kid 的强制刷新
    }

    [Fact]
    public async Task UnknownKid_RefreshIsThrottledAgainstJwksAmplification()
    {
        // 没有限流的话，攻击者只要持续提交带随机 kid 的令牌，
        // 就能把业务服务变成打向 SSO 的 JWKS 放大器。
        var authority = new TestTokenAuthority("2026-01");
        authority.AddKey("2026-07");

        var jwksCalls = 0;
        var handler = new CapturingHandler(_ =>
        {
            jwksCalls++;
            return Stub.Json(authority.JwksJson("2026-07"), cacheControl: "public, max-age=300");
        });

        var validator = CreateValidator(handler, out _);
        var token = authority.CreateAccessToken(kid: "2026-01");

        for (var iteration = 0; iteration < 5; iteration++)
        {
            Assert.False((await validator.ValidateAccessTokenAsync(token)).IsValid);
        }

        // 第一次未知 kid 触发一次强制刷新，之后被最小间隔挡住，不再拉取。
        Assert.Equal(2, jwksCalls);
    }

    // ------------------------------------------------------------ 密钥轮换

    [Fact]
    public async Task KeyRotation_RefreshesJwksAndAcceptsNewKid()
    {
        var authority = new TestTokenAuthority("2026-01");
        authority.AddKey("2026-07");

        var jwksCalls = 0;
        var handler = new CapturingHandler(_ =>
        {
            jwksCalls++;
            // 第一次只发布旧公钥，模拟「服务端刚轮换、业务服务缓存还是旧的」
            return Stub.Json(
                jwksCalls == 1 ? authority.JwksJson("2026-01") : authority.JwksJson(),
                cacheControl: "public, max-age=300");
        });

        var validator = CreateValidator(handler, out _);

        // 先用旧 kid 的令牌把缓存填上
        var oldToken = authority.CreateAccessToken(kid: "2026-01");
        Assert.True((await validator.ValidateAccessTokenAsync(oldToken)).IsValid);

        // 轮换后用新 kid 签发的令牌：缓存里没有，必须自动刷新并验签通过
        var newToken = authority.CreateAccessToken(kid: "2026-07");
        var result = await validator.ValidateAccessTokenAsync(newToken);

        Assert.True(result.IsValid, result.ErrorDescription);
        Assert.Equal(2, jwksCalls);

        // 刷新后新旧令牌都还能验签（平台要求轮换时新旧公钥并存）
        Assert.True((await validator.ValidateAccessTokenAsync(oldToken)).IsValid);
        Assert.Equal(2, jwksCalls); // 缓存已含两把密钥，不再拉取
    }

    [Fact]
    public async Task JwksCache_IsReusedAcrossValidations()
    {
        var authority = new TestTokenAuthority();
        var jwksCalls = 0;
        var handler = new CapturingHandler(_ =>
        {
            jwksCalls++;
            return Stub.Json(authority.JwksJson(), cacheControl: "public, max-age=300");
        });

        var validator = CreateValidator(handler, out _);
        var token = authority.CreateAccessToken();

        for (var iteration = 0; iteration < 5; iteration++)
        {
            Assert.True((await validator.ValidateAccessTokenAsync(token)).IsValid);
        }

        // 尊重服务端的 Cache-Control：5 次校验只应拉一次公钥。
        Assert.Equal(1, jwksCalls);
    }

    // ---------------------------------------------------------------- scope

    [Fact]
    public async Task MissingRequiredScope_YieldsInsufficientScopeNotInvalidToken()
    {
        var authority = new TestTokenAuthority();
        var validator = CreateValidator(authority, requiredScopes: ["mc:whitelist"]);

        var result = await validator.ValidateAccessTokenAsync(
            authority.CreateAccessToken(scope: "openid profile email"));

        Assert.False(result.IsValid);
        Assert.Equal(EliCloudErrorCodes.InsufficientScope, result.ErrorCode);
    }

    [Fact]
    public async Task ScopeMatchingIsWholeWordNotSubstring()
    {
        var authority = new TestTokenAuthority();
        var validator = CreateValidator(authority, requiredScopes: ["pdf"]);

        // scope 串里是 pdf:read，不包含独立的 pdf —— 用子串匹配就会误放行。
        var result = await validator.ValidateAccessTokenAsync(
            authority.CreateAccessToken(scope: "openid pdf:read pdf:write"));

        Assert.False(result.IsValid);
        Assert.Equal(EliCloudErrorCodes.InsufficientScope, result.ErrorCode);
    }

    [Fact]
    public async Task AllRequiredScopesMustBePresent()
    {
        var authority = new TestTokenAuthority();
        var validator = CreateValidator(authority, requiredScopes: ["openid", "mc:whitelist"]);

        Assert.True((await validator.ValidateAccessTokenAsync(
            authority.CreateAccessToken(scope: "openid profile mc:whitelist"))).IsValid);

        Assert.False((await validator.ValidateAccessTokenAsync(
            authority.CreateAccessToken(scope: "openid profile"))).IsValid);
    }

    // ------------------------------------------------------------ 异常与边界

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    public async Task MalformedInput_IsRejectedWithoutThrowing(string? token)
    {
        var authority = new TestTokenAuthority();
        var validator = CreateValidator(authority);

        var result = await validator.ValidateAccessTokenAsync(token);

        Assert.False(result.IsValid);
        Assert.Equal(EliCloudErrorCodes.InvalidToken, result.ErrorCode);
    }

    [Fact]
    public async Task JwksFetchFailure_IsReportedAsInvalidTokenNotCrash()
    {
        var handler = new CapturingHandler(_ => Stub.Html("<html>502</html>", HttpStatusCode.BadGateway));
        var validator = CreateValidator(handler, out _);
        var authority = new TestTokenAuthority();

        var result = await validator.ValidateAccessTokenAsync(authority.CreateAccessToken());

        Assert.False(result.IsValid);
        Assert.Equal(EliCloudErrorCodes.InvalidToken, result.ErrorCode);
    }

    [Fact]
    public void Validate_RequiresIssuerWhenIssuerValidationEnabled()
    {
        var options = new EliCloudTokenValidationOptions
        {
            ValidateIssuer = true,
            Issuer = null,
        };

        var exception = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Contains("Issuer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JwksUri_DerivesFromIssuerByPlatformConvention()
    {
        var options = new EliCloudTokenValidationOptions { Issuer = "https://elicloud.test/auth/" };

        Assert.Equal("https://elicloud.test/auth/.well-known/jwks.json", options.ResolveJwksUri().AbsoluteUri);
    }

    [Fact]
    public void JwksUri_ExplicitValueWins()
    {
        var options = new EliCloudTokenValidationOptions
        {
            Issuer = "https://elicloud.test/auth",
            JwksUri = "https://other.example/jwks.json",
        };

        Assert.Equal("https://other.example/jwks.json", options.ResolveJwksUri().AbsoluteUri);
    }

    [Fact]
    public async Task IssuerValidationCanBeDisabledButAudienceStillApplies()
    {
        // 平台没有任何场景需要关掉 iss 校验；这里只是锁住「关掉它不会连带关掉 aud」，
        // 免得将来有人为了兼容临时关掉 iss，结果把 id_token 也放进来了。
        var authority = new TestTokenAuthority();
        var handler = new CapturingHandler(_ => Stub.Json(authority.JwksJson()));
        var options = new EliCloudTokenValidationOptions
        {
            Issuer = TestTokenAuthority.Issuer,
            ValidateIssuer = false,
        };

        using var validator = new EliCloudTokenValidator(TestOptions.HttpClient(handler), options);

        Assert.True((await validator.ValidateAccessTokenAsync(
            authority.CreateAccessToken(issuer: "https://whatever.example"))).IsValid);

        Assert.False((await validator.ValidateAccessTokenAsync(
            authority.CreateAccessToken(audience: "elipese-web"))).IsValid);
    }

    // ------------------------------------------------------------------ 工具

    private static EliCloudTokenValidator CreateValidator(
        TestTokenAuthority authority,
        string[]? requiredScopes = null)
    {
        var handler = new CapturingHandler(_ => Stub.Json(authority.JwksJson(), cacheControl: "public, max-age=300"));
        return CreateValidator(handler, out _, requiredScopes);
    }

    private static EliCloudTokenValidator CreateValidator(
        CapturingHandler handler,
        out EliCloudTokenValidationOptions options,
        string[]? requiredScopes = null)
    {
        options = new EliCloudTokenValidationOptions
        {
            Issuer = TestTokenAuthority.Issuer,
            Audience = TestTokenAuthority.Audience,
            RequiredScopes = requiredScopes ?? [],
        };

        return new EliCloudTokenValidator(TestOptions.HttpClient(handler), options);
    }
}
