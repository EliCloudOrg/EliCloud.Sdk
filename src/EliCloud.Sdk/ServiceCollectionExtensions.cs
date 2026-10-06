using EliCloud.Sdk.Mc;
using EliCloud.Sdk.MainApi;
using EliCloud.Sdk.Sso;
using EliCloud.Sdk.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EliCloud.Sdk;

/// <summary>
/// <see cref="IServiceCollection"/> 上的 EliCloud SDK 注册扩展。
/// </summary>
/// <remarks>
/// <para>
/// 所有客户端都注册为**单例**，共用一个 <see cref="HttpClient"/>。
/// 这是安全的，因为每次请求的超时由 SDK 自己的传输层控制
/// （见 <c>EliCloudTransport</c>），而不是靠 <see cref="HttpClient.Timeout"/>；
/// 所以不存在「一个客户端改了超时影响其它客户端」的问题。
/// </para>
/// <para>
/// 需要自定义代理、证书、日志处理器时，请先自己注册一个 <see cref="HttpClient"/> 单例，
/// 本扩展用 <c>TryAdd</c> 语义，不会覆盖你的注册。
/// </para>
/// </remarks>
public static class EliCloudServiceCollectionExtensions
{
    /// <summary>
    /// 注册 EliCloud 客户端（SSO / MC 白名单 / 平台核心 API）。
    /// </summary>
    /// <param name="services">服务容器。</param>
    /// <param name="configure">连接配置，至少要有正确的 <see cref="EliCloudOptions.BaseAddress"/>。</param>
    public static IServiceCollection AddEliCloud(
        this IServiceCollection services,
        Action<EliCloudOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<EliCloudOptions>()
            .Configure(configure)
            .Validate(
                options =>
                {
                    try
                    {
                        options.Validate();
                        return true;
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                },
                "EliCloudOptions 配置不合法：必须提供绝对的 BaseAddress（SDK 不提供默认入口），路径前缀必须以「/」开头。");

        services.TryAddSingleton(CreateHttpClient);

        services.TryAddSingleton(provider => new SsoClient(
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<IOptions<EliCloudOptions>>(),
            provider.GetService<ILogger<SsoClient>>()));

        services.TryAddSingleton(provider => new McClient(
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<IOptions<EliCloudOptions>>(),
            provider.GetService<ILogger<McClient>>()));

        services.TryAddSingleton(provider => new MainApiClient(
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<IOptions<EliCloudOptions>>(),
            provider.GetService<ILogger<MainApiClient>>()));

        return services;
    }

    /// <summary>
    /// 注册管理员客户端（<see cref="SsoAdminClient"/> 与 <see cref="McAdminClient"/>）。
    /// </summary>
    /// <param name="services">服务容器。</param>
    /// <param name="adminToken">
    /// 服务端的 <c>ADMIN_TOKEN</c>。两个服务各有一份，通常配成不同的值；
    /// 若相同可以只调一次本方法，不同则分别调用并指定 <paramref name="whitelistAdminToken"/>。
    /// </param>
    /// <param name="whitelistAdminToken">MC 白名单服务的管理员令牌；<c>null</c> 表示与 <paramref name="adminToken"/> 相同。</param>
    public static IServiceCollection AddEliCloudAdministration(
        this IServiceCollection services,
        string adminToken,
        string? whitelistAdminToken = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(adminToken);

        services.TryAddSingleton(provider => new SsoAdminClient(
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<IOptions<EliCloudOptions>>(),
            adminToken,
            provider.GetService<ILogger<SsoAdminClient>>()));

        services.TryAddSingleton(provider => new McAdminClient(
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<IOptions<EliCloudOptions>>(),
            whitelistAdminToken ?? adminToken,
            provider.GetService<ILogger<McAdminClient>>()));

        return services;
    }

    /// <summary>
    /// 注册 access token 校验器（业务服务/资源服务用）。
    /// </summary>
    /// <param name="services">服务容器。</param>
    /// <param name="configure">
    /// 校验配置。至少要设置 <see cref="EliCloudTokenValidationOptions.Issuer"/>，
    /// 且必须与服务端的 <c>PUBLIC_BASE_URL</c> 逐字相同。
    /// </param>
    /// <remarks>
    /// 校验器是**单例**：它内部的 JWKS 缓存必须跨请求复用，
    /// 否则每个请求都会去拉一次公钥（既慢，又会给 SSO 制造无谓压力）。
    /// </remarks>
    public static IServiceCollection AddEliCloudTokenValidation(
        this IServiceCollection services,
        Action<EliCloudTokenValidationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<EliCloudTokenValidationOptions>()
            .Configure(configure)
            .Validate(
                options =>
                {
                    try
                    {
                        options.Validate();
                        return true;
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                },
                "EliCloudTokenValidationOptions 配置不合法：开启 issuer 校验时必须提供 Issuer。");

        services.TryAddSingleton(CreateValidationHttpClient);
        services.TryAddSingleton(provider => new EliCloudTokenValidator(
            provider.GetRequiredService<EliCloudValidationHttpClient>().HttpClient,
            provider.GetRequiredService<IOptions<EliCloudTokenValidationOptions>>(),
            provider.GetService<ILogger<EliCloudTokenValidator>>()));

        return services;
    }

    private static HttpClient CreateHttpClient(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<EliCloudOptions>>().Value;
        return BuildHttpClient(options.UserAgent);
    }

    private static EliCloudValidationHttpClient CreateValidationHttpClient(IServiceProvider provider)
    {
        _ = provider;
        return new EliCloudValidationHttpClient(BuildHttpClient(EliCloudConstants.UserAgent));
    }

    private static HttpClient BuildHttpClient(string? userAgent)
    {
        var client = new HttpClient
        {
            // 超时由 EliCloudTransport 按请求控制；这里关掉 HttpClient 自己的超时，
            // 免得两者叠加后出现「还没到 SDK 的超时就被底层掐断」这种难查的现象。
            Timeout = Timeout.InfiniteTimeSpan,
        };

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        }

        return client;
    }

    /// <summary>
    /// 给校验器用的独立 <see cref="HttpClient"/> 包装。
    /// </summary>
    /// <remarks>
    /// 单独包一层是为了避免「业务自己的 HttpClient」与「拉 JWKS 用的 HttpClient」互相覆盖：
    /// 应用可能已经因为别的原因注册过 <see cref="HttpClient"/> 单例。
    /// </remarks>
    private sealed class EliCloudValidationHttpClient(HttpClient httpClient)
    {
        internal HttpClient HttpClient { get; } = httpClient;
    }
}
