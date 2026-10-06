using EliCloud.Sdk.Tokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EliCloud.Sdk.AspNetCore;

/// <summary>
/// 把 EliCloud 令牌校验接进 ASP.NET Core 的注册扩展。
/// </summary>
/// <remarks>
/// <para>典型用法（业务服务 = 资源服务）：</para>
/// <code>
/// builder.Services.AddEliCloudAuthentication(options =>
/// {
///     options.Validation.Issuer = "https://api.example.com/auth";   // 必须与服务端 PUBLIC_BASE_URL 逐字相同
///     options.Validation.RequiredScopes = ["mc:whitelist"];
/// });
///
/// app.UseAuthentication();
/// app.UseAuthorization();
///
/// app.MapPost("/v1/names", (HttpContext context) => context.GetEliCloudSubject());
/// </code>
/// <para>
/// 它**不依赖** <c>Microsoft.AspNetCore.Authentication.JwtBearer</c>：
/// 认证处理器直接调用 <see cref="EliCloudTokenValidator"/>，
/// 因此 JWKS 缓存、算法白名单、scope 整词匹配这些平台规则只有一份实现。
/// </para>
/// </remarks>
public static class EliCloudAuthenticationExtensions
{
    /// <summary>
    /// 注册 EliCloud 认证方案。
    /// </summary>
    /// <param name="services">服务容器。</param>
    /// <param name="configure">认证方案配置（其中 <c>Validation</c> 至少要设置 <c>Issuer</c>）。</param>
    /// <param name="scheme">方案名；默认 <c>EliCloud</c>。</param>
    /// <param name="setAsDefault">
    /// 是否把它设为默认认证方案。单认证方案的应用保持 <c>true</c> 即可；
    /// 已经用其它方案（Cookie 等）的应用应传 <c>false</c>，再用
    /// <c>[Authorize(AuthenticationSchemes = "EliCloud")]</c> 指定。
    /// </param>
    public static AuthenticationBuilder AddEliCloudAuthentication(
        this IServiceCollection services,
        Action<EliCloudAuthenticationOptions> configure,
        string scheme = EliCloudAuthenticationDefaults.Scheme,
        bool setAsDefault = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentException.ThrowIfNullOrEmpty(scheme);

        services.AddOptions<EliCloudAuthenticationOptions>().Configure(configure);

        // 拉 JWKS 用的 HttpClient。若应用已经调用过 AddEliCloud()，复用它注册的那一个。
        services.TryAddSingleton(_ => BuildJwksHttpClient());

        // 校验器必须是单例：它的 JWKS 缓存要跨请求复用，
        // 否则每个请求都会去拉一次公钥。
        services.TryAddSingleton(provider =>
        {
            var authenticationOptions = provider.GetRequiredService<IOptions<EliCloudAuthenticationOptions>>().Value;
            authenticationOptions.Validation.Validate();

            return new EliCloudTokenValidator(
                provider.GetRequiredService<HttpClient>(),
                authenticationOptions.Validation,
                provider.GetService<ILogger<EliCloudTokenValidator>>());
        });

        var builder = setAsDefault ? services.AddAuthentication(scheme) : services.AddAuthentication();
        builder.AddScheme<EliCloudAuthenticationOptions, EliCloudAuthenticationHandler>(scheme, configureOptions: null);
        return builder;
    }

    /// <summary>
    /// 注册「必须携带某个 scope」的授权策略，并注册处理它的授权处理器。
    /// </summary>
    /// <param name="services">服务容器。</param>
    /// <param name="scope">必需的 scope，如 <c>mc:whitelist</c>。</param>
    /// <remarks>
    /// 注册后可以用 <c>EliCloudAuthenticationDefaults.ScopePolicyName(scope)</c> 拿到策略名，
    /// 或者直接用 <see cref="EliCloudScopeAttribute"/>。
    /// </remarks>
    public static IServiceCollection AddEliCloudScopePolicy(this IServiceCollection services, string scope)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(scope);

        var policyName = EliCloudAuthenticationDefaults.ScopePolicyName(scope);
        services.AddAuthorization(options => options.AddPolicy(policyName, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new EliCloudScopeRequirement(scope));
        }));

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAuthorizationHandler, EliCloudScopeAuthorizationHandler>());

        return services;
    }

    /// <summary>一次注册多个 scope 策略。</summary>
    public static IServiceCollection AddEliCloudScopePolicies(this IServiceCollection services, params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(scopes);

        foreach (var scope in scopes)
        {
            services.AddEliCloudScopePolicy(scope);
        }

        return services;
    }

    private static HttpClient BuildJwksHttpClient()
    {
        var client = new HttpClient
        {
            // 超时由 SDK 的传输层按请求控制，见 EliCloudTransport。
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", EliCloudConstants.UserAgent);
        return client;
    }
}
