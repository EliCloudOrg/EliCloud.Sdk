using System.Net;
using EliCloud.Sdk.Sso;
using Xunit;

namespace EliCloud.Sdk.Tests;

/// <summary>
/// 「打真实环境」的集成测试开关。
/// </summary>
/// <remarks>
/// 默认**不跑**：单元/契约测试已经覆盖了请求形状与解析逻辑，而这一组要求能直连线上地址
/// （当前是 <c>https://api.example.com/auth</c>）。它在 CI 或受限网络里会变成假失败，
/// 所以设计成显式开启：
/// <code>ELICLOUD_LIVE_TESTS=1 ELICLOUD_LIVE_BASE=https://api.example.com</code>
/// </remarks>
internal static class LiveEnvironment
{
    internal static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("ELICLOUD_LIVE_TESTS"), "1", StringComparison.Ordinal);

    internal static Uri BaseAddress => new(
        Environment.GetEnvironmentVariable("ELICLOUD_LIVE_BASE") ?? "https://api.example.com");

    internal static HttpClient CreateHttpClient(int timeoutSeconds = 30) => new()
    {
        Timeout = TimeSpan.FromSeconds(timeoutSeconds),
    };

    internal static EliCloudOptions CreateOptions() => new()
    {
        BaseAddress = BaseAddress,
        Timeout = TimeSpan.FromSeconds(30),
    };
}

/// <summary>只在开启 <c>ELICLOUD_LIVE_TESTS=1</c> 时才执行的测试。</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    /// <summary>创建特性；未开启时把测试标记为跳过。</summary>
    public LiveFactAttribute()
    {
        if (!LiveEnvironment.Enabled)
        {
            Skip = "线上集成测试默认跳过；设置 ELICLOUD_LIVE_TESTS=1 后运行（需要能直连 " + LiveEnvironment.BaseAddress + "）。";
        }
    }
}

/// <summary>
/// 线上端到端冒烟：发现文档 ↔ 网关 ↔ 实现三者一致，且 JWKS 真的可用。
/// </summary>
/// <remarks>
/// 它与平台自带的 <c>sso/scripts/e2e_endpoints.py</c> 思路一致，
/// 但用的是本 SDK 自己的代码路径 —— 也就是说它同时验证了「SDK 能连上真实服务」。
/// 这些接口全部匿名可访问，不需要账号。
/// </remarks>
public sealed class LiveEndpointTests
{
    [LiveFact]
    public async Task DiscoveryDocument_MatchesConfiguredBaseAddress()
    {
        using var http = LiveEnvironment.CreateHttpClient();
        var client = new SsoClient(http, LiveEnvironment.CreateOptions());

        var document = await client.GetDiscoveryDocumentAsync();

        Assert.Equal(client.DiscoveryEndpoint.AbsoluteUri, document.Issuer + "/.well-known/openid-configuration");
        Assert.Equal(LiveEnvironment.BaseAddress.AbsoluteUri.TrimEnd('/') + "/auth", document.Issuer);
        Assert.Equal(document.Issuer + "/.well-known/jwks.json", document.JwksUri);
        Assert.Equal(document.Issuer + "/token", document.TokenEndpoint);
        Assert.Contains("authorization_code", document.GrantTypesSupported!);
        Assert.Contains("urn:ietf:params:oauth:grant-type:device_code", document.GrantTypesSupported!);
        Assert.Contains(EliCloudScopes.McWhitelist, document.ScopesSupported!);
        Assert.DoesNotContain("plain", document.CodeChallengeMethodsSupported!);
        Assert.Equal(["RS256"], document.IdTokenSigningAlgValuesSupported!);
    }

    [LiveFact]
    public async Task Jwks_IsAnonymouslyAccessibleAndPublishesRs256Keys()
    {
        using var http = LiveEnvironment.CreateHttpClient();
        var client = new SsoClient(http, LiveEnvironment.CreateOptions());

        var keySet = await client.GetJsonWebKeySetAsync();
        var signingKeys = keySet.GetSigningKeys();

        Assert.NotEmpty(signingKeys);
        Assert.All(signingKeys, key => Assert.False(string.IsNullOrEmpty(key.KeyId)));
        Assert.All(keySet.Keys, key => Assert.Equal("RSA", key.Kty));
        Assert.Contains(keySet.Keys, key => key.Alg == EliCloudConstants.SigningAlgorithm);
    }

    [LiveFact]
    public async Task EveryDeclaredEndpoint_IsRoutedByTheGateway()
    {
        // 平台有过一次「发现文档声明了 /authorize，但服务端没实现」的事故，
        // 所以它自己有一条测试遍历所有声明的端点断言不是 404。这里用 SDK 的地址再验一遍。
        using var http = LiveEnvironment.CreateHttpClient();
        var client = new SsoClient(http, LiveEnvironment.CreateOptions());
        var document = await client.GetDiscoveryDocumentAsync();

        var endpoints = new[]
        {
            document.AuthorizationEndpoint,
            document.TokenEndpoint,
            document.UserInfoEndpoint,
            document.DeviceAuthorizationEndpoint,
            document.EndSessionEndpoint,
            document.JwksUri,
        };

        foreach (var endpoint in endpoints)
        {
            Assert.False(string.IsNullOrEmpty(endpoint), "发现文档里出现了空的端点地址。");

            using var response = await http.GetAsync(endpoint);

            // GET 打到只支持 POST 的端点会得到 405，缺参数的 /authorize 会得到 400，
            // 无令牌的 /userinfo 会得到 401 —— 这些都是「路由通了」的证据。
            Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [LiveFact]
    public async Task JwksResponse_AdvertisesCacheControl()
    {
        // 业务服务按这个头缓存公钥；丢了它会导致每次校验都来拉一次 JWKS。
        using var http = LiveEnvironment.CreateHttpClient();
        using var response = await http.GetAsync(new Uri(LiveEnvironment.BaseAddress, "/auth/.well-known/jwks.json"));

        response.EnsureSuccessStatusCode();
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl!.MaxAge > TimeSpan.Zero);
    }

    [LiveFact]
    public async Task UnknownClient_IsRejectedWithoutRedirecting()
    {
        // 开放重定向防线：client_id 不可信时只渲染错误页，绝不 302。
        using var http = LiveEnvironment.CreateHttpClient();
        var client = new SsoClient(http, LiveEnvironment.CreateOptions());

        var url = client.BuildAuthorizationUrl(new AuthorizationRequest
        {
            ClientId = "definitely-not-registered",
            RedirectUri = "https://evil.example/callback",
            State = "state",
        });

        using var response = await http.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [LiveFact]
    public async Task PasswordLoginWithWrongCredentials_ReturnsUnauthorizedWithoutEnumeration()
    {
        using var http = LiveEnvironment.CreateHttpClient();
        var client = new SsoClient(http, LiveEnvironment.CreateOptions());

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => client.LoginAsync("definitely-not-a-user-xyz", "not-a-real-password"));

        Assert.Equal(401, exception.StatusCode);
        Assert.Equal(EliCloudErrorCodes.InvalidGrant, exception.Error);
        Assert.False(string.IsNullOrWhiteSpace(exception.ErrorDescription));
    }
}
