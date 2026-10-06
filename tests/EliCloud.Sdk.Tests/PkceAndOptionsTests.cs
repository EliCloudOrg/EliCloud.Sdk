using EliCloud.Sdk.Sso;
using Xunit;

namespace EliCloud.Sdk.Tests;

/// <summary>
/// PKCE（RFC 7636）实现测试。
/// </summary>
/// <remarks>
/// 这里用 RFC 7636 附录 B 的**官方测试向量**，而不是自己算一个值再自己断言 ——
/// 后者只能证明「前后一致」，证明不了「符合规范」。
/// </remarks>
public sealed class PkceCodeChallengeTests
{
    /// <summary>RFC 7636 Appendix B 的已知向量。</summary>
    private const string Rfc7636Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Rfc7636Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    public void Challenge_MatchesRfc7636TestVector()
    {
        Assert.Equal(Rfc7636Challenge, PkceCodeChallenge.ComputeChallenge(Rfc7636Verifier));
    }

    [Fact]
    public void FromVerifier_DerivesSameChallenge()
    {
        var pkce = PkceCodeChallenge.FromVerifier(Rfc7636Verifier);

        Assert.Equal(Rfc7636Verifier, pkce.CodeVerifier);
        Assert.Equal(Rfc7636Challenge, pkce.CodeChallenge);
        Assert.Equal("S256", pkce.CodeChallengeMethod);
    }

    [Fact]
    public void Create_ProducesVerifierWithinRfcLimits()
    {
        var pkce = PkceCodeChallenge.Create();

        Assert.InRange(pkce.CodeVerifier.Length, 43, 128);
        Assert.Equal(64, pkce.CodeVerifier.Length);
    }

    [Fact]
    public void Create_ProducesOnlyUnreservedCharacters()
    {
        // RFC 7636 §4.1：verifier 只能含 ALPHA / DIGIT / "-" / "." / "_" / "~"
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var verifier = PkceCodeChallenge.Create().CodeVerifier;
            Assert.All(verifier, character => Assert.True(
                char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~',
                $"出现了不允许的字符「{character}」。"));
        }
    }

    [Fact]
    public void Create_ProducesDifferentValuesEachCall()
    {
        var first = PkceCodeChallenge.Create();
        var second = PkceCodeChallenge.Create();

        Assert.NotEqual(first.CodeVerifier, second.CodeVerifier);
        Assert.NotEqual(first.CodeChallenge, second.CodeChallenge);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(129)]
    [InlineData(0)]
    public void Create_RejectsLengthOutsideRfcRange(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PkceCodeChallenge.Create(length));
    }

    [Fact]
    public void Challenge_UsesBase64UrlWithoutPadding()
    {
        var challenge = PkceCodeChallenge.ComputeChallenge(Rfc7636Verifier);

        Assert.DoesNotContain('=', challenge);
        Assert.DoesNotContain('+', challenge);
        Assert.DoesNotContain('/', challenge);
    }
}

/// <summary>连接配置的地址拼装规则。</summary>
public sealed class EliCloudOptionsTests
{
    [Fact]
    public void DerivedBaseAddresses_FollowPlatformPrefixContract()
    {
        var options = TestHttp.TestOptions.Create();

        Assert.Equal("https://elicloud.test/auth/", options.SsoBaseAddress.AbsoluteUri);
        Assert.Equal("https://elicloud.test/mc/", options.McBaseAddress.AbsoluteUri);
        Assert.Equal("https://elicloud.test/core/", options.MainApiBaseAddress.AbsoluteUri);
    }

    [Fact]
    public void BaseAddress_WithTrailingSlashOrPath_IsNormalised()
    {
        var options = TestHttp.TestOptions.Create(o => o.BaseAddress = new Uri("https://elicloud.test/gateway/"));

        Assert.Equal("https://elicloud.test/gateway/auth/", options.SsoBaseAddress.AbsoluteUri);
        Assert.Equal("https://elicloud.test/gateway/mc/", options.McBaseAddress.AbsoluteUri);
    }

    [Fact]
    public void ServiceBaseAddressOverride_Wins()
    {
        var options = TestHttp.TestOptions.Create(o =>
        {
            o.McBaseAddressOverride = new Uri("https://mc.example.com/");
        });

        Assert.Equal("https://mc.example.com/", options.McBaseAddress.AbsoluteUri);
        Assert.Equal("https://elicloud.test/auth/", options.SsoBaseAddress.AbsoluteUri);
    }

    [Theory]
    [InlineData("auth")]      // 缺前导斜杠
    [InlineData("")]          // 空串
    public void Validate_RejectsMalformedPrefix(string prefix)
    {
        var options = TestHttp.TestOptions.Create(o => o.AuthPathPrefix = prefix);

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsRelativeBaseAddress()
    {
        var options = new EliCloudOptions { BaseAddress = new Uri("/auth", UriKind.Relative) };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsNonPositiveTimeout()
    {
        var options = TestHttp.TestOptions.Create(o => o.Timeout = TimeSpan.Zero);

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_AcceptsPhaseSwitchToDomain()
    {
        // IP 阶段 → 域名阶段：只改 BaseAddress，其他一切不变（平台硬约束：代码零改动）
        var options = TestHttp.TestOptions.Create(o => o.BaseAddress = new Uri("https://api.example.com"));

        options.Validate();

        Assert.Equal("https://api.example.com/auth/", options.SsoBaseAddress.AbsoluteUri);
    }
}

/// <summary>scope 工具方法。</summary>
public sealed class EliCloudScopesTests
{
    [Fact]
    public void Split_HandlesSpacesAndEmptyInput()
    {
        Assert.Equal(
            ["openid", "profile", "email"],
            EliCloudScopes.Split("openid profile email"));

        Assert.Empty(EliCloudScopes.Split("   "));
        Assert.Empty(EliCloudScopes.Split(null));
    }

    [Fact]
    public void Split_CollapsesExtraWhitespace()
    {
        Assert.Equal(["openid", "mc:whitelist"], EliCloudScopes.Split("openid   mc:whitelist "));
    }

    [Fact]
    public void Join_ProducesSpaceSeparatedScope()
    {
        Assert.Equal("openid profile", EliCloudScopes.Join([EliCloudScopes.OpenId, EliCloudScopes.Profile]));
    }
}

/// <summary>随机串生成。</summary>
public sealed class EliCloudRandomTests
{
    [Fact]
    public void UrlSafeToken_IsUrlSafeAndHighEntropy()
    {
        var first = EliCloudRandom.UrlSafeToken();
        var second = EliCloudRandom.UrlSafeToken();

        Assert.NotEqual(first, second);
        Assert.DoesNotContain('+', first);
        Assert.DoesNotContain('/', first);
        Assert.DoesNotContain('=', first);
        Assert.True(first.Length >= 43, "32 字节熵的 base64url 至少应有 43 个字符。");
    }

    [Fact]
    public void UrlSafeToken_RejectsLowEntropy()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EliCloudRandom.UrlSafeToken(8));
    }

    [Fact]
    public void StateAndNonce_AreDistinct()
    {
        Assert.NotEqual(EliCloudRandom.CreateState(), EliCloudRandom.CreateNonce());
    }
}
