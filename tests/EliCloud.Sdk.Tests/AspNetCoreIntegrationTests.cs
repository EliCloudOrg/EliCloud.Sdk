using System.Security.Claims;
using System.Text.Encodings.Web;
using EliCloud.Sdk.AspNetCore;
using EliCloud.Sdk.Mc;
using EliCloud.Sdk.MainApi;
using EliCloud.Sdk.Sso;
using EliCloud.Sdk.Tests.TestHttp;
using EliCloud.Sdk.Tokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EliCloud.Sdk.Tests;

/// <summary>ASP.NET Core 集成：认证处理器、scope 授权、DI 注册。</summary>
public sealed class AspNetCoreIntegrationTests
{
    // ------------------------------------------------------------ 认证处理器

    [Fact]
    public async Task Handler_NoAuthorizationHeaderYieldsNoResult()
    {
        // 匿名端点与 [AllowAnonymous] 必须照常工作，不能在匿名请求上贴一个假的「认证失败」。
        var (handler, context) = await CreateHandlerAsync(new TestTokenAuthority().CreateAccessToken(), headerOverride: string.Empty);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.None);
        Assert.Null(result.Failure);
        Assert.Equal(200, context.Response.StatusCode); // 没有触发挑战，响应不该被改动
    }

    [Fact]
    public async Task Handler_ValidTokenSucceedsAndPopulatesPrincipal()
    {
        var authority = new TestTokenAuthority();
        var token = authority.CreateAccessToken(clientId: "elipese-web");
        var (handler, _) = await CreateHandlerAsync(token, authority);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("user_0001", result.Principal!.GetEliCloudSubject());
        Assert.Equal("alice", result.Principal.GetEliCloudUsername());
        Assert.Equal("EliCloud", result.Ticket!.AuthenticationScheme);
    }

    [Fact]
    public async Task Handler_InvalidTokenFails()
    {
        var authority = new TestTokenAuthority();
        var token = authority.CreateAccessToken(issuer: "https://attacker.example/auth");
        var (handler, _) = await CreateHandlerAsync(token, authority);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Handler_RejectsNonBearerScheme()
    {
        var authority = new TestTokenAuthority();
        var (handler, _) = await CreateHandlerAsync(authority.CreateAccessToken(), authority, headerOverride: "Basic abc");

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Bearer", result.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handler_ChallengeReturns401WithBearerRealm()
    {
        var authority = new TestTokenAuthority();
        var (handler, context) = await CreateHandlerAsync(authority.CreateAccessToken(), authority);

        await ((IAuthenticationHandler)handler).ChallengeAsync(new AuthenticationProperties());

        Assert.Equal(401, context.Response.StatusCode);
        var header = context.Response.Headers.WWWAuthenticate.ToString();
        Assert.Contains("Bearer", header, StringComparison.Ordinal);
        Assert.Contains("elicloud", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handler_ForbiddenReturns403WithInsufficientScope()
    {
        var authority = new TestTokenAuthority();
        var (handler, context) = await CreateHandlerAsync(authority.CreateAccessToken(), authority);

        // IAuthenticationHandler 接口只有 Initialize/Authenticate/Challenge 三个成员，
        // 403 是 AuthenticationHandler<T> 的受保护钩子（处理器本身 sealed），所以这里用反射调用它，
        // 以便把「403 + insufficient_scope」这段契约锁住。
        var forbidden = typeof(EliCloudAuthenticationHandler).GetMethod(
            "HandleForbiddenAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(forbidden);

        await (Task)forbidden!.Invoke(handler, [new AuthenticationProperties()])!;

        Assert.Equal(403, context.Response.StatusCode);
        Assert.Contains(
            "insufficient_scope",
            context.Response.Headers.WWWAuthenticate.ToString(),
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- scope 授权

    [Fact]
    public async Task ScopeAuthorization_SucceedsWhenScopePresent()
    {
        var requirement = new EliCloudScopeRequirement("mc:whitelist");
        var context = CreateAuthorizationContext("openid profile mc:whitelist", requirement);

        await new EliCloudScopeAuthorizationHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task ScopeAuthorization_FailsWhenScopeMissing()
    {
        var requirement = new EliCloudScopeRequirement("mc:whitelist");
        var context = CreateAuthorizationContext("openid profile", requirement);

        await new EliCloudScopeAuthorizationHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task ScopeAuthorization_UsesWholeWordMatching()
    {
        var requirement = new EliCloudScopeRequirement("pdf");
        var context = CreateAuthorizationContext("openid pdf:read", requirement);

        await new EliCloudScopeAuthorizationHandler().HandleAsync(context);

        // 子串匹配会误放行 pdf:read，这里必须不通过。
        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task ScopeAuthorization_FailsForAnonymousPrincipal()
    {
        var requirement = new EliCloudScopeRequirement("mc:whitelist");
        var context = new AuthorizationHandlerContext(
            [requirement],
            new ClaimsPrincipal(new ClaimsIdentity()),
            resource: null);

        await new EliCloudScopeAuthorizationHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public void ScopeAttribute_MapsToRegisteredPolicyName()
    {
        var attribute = new EliCloudScopeAttribute("mc:whitelist");

        Assert.Equal("EliCloudScope:mc:whitelist", attribute.Policy);
        Assert.Equal(attribute.Policy, EliCloudAuthenticationDefaults.ScopePolicyName("mc:whitelist"));
    }

    // ------------------------------------------------------------------- DI

    [Fact]
    public void AddEliCloud_RegistersAllClientsAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddEliCloud(options => options.BaseAddress = new Uri(TestOptions.BaseAddress));

        using var provider = services.BuildServiceProvider();

        var sso = provider.GetRequiredService<SsoClient>();
        var whitelist = provider.GetRequiredService<McClient>();
        var platform = provider.GetRequiredService<MainApiClient>();

        Assert.Same(sso, provider.GetRequiredService<SsoClient>());
        Assert.Equal("https://elicloud.test/auth/token", sso.TokenEndpoint.AbsoluteUri);
        Assert.Equal("https://elicloud.test/mc/healthz", whitelist.HealthEndpoint.AbsoluteUri);
        Assert.Equal("https://elicloud.test/core/v1/services", platform.ServicesEndpoint.AbsoluteUri);
    }

    [Fact]
    public void AddEliCloud_RejectsInvalidConfigurationLazily()
    {
        var services = new ServiceCollection();
        services.AddEliCloud(options => options.AuthPathPrefix = "auth"); // 缺前导斜杠

        using var provider = services.BuildServiceProvider();

        // Options 校验在取值时触发，错误信息必须点明是哪一类配置问题。
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<SsoClient>());
    }

    [Fact]
    public void AddEliCloudAdministration_RegistersAdminClients()
    {
        var services = new ServiceCollection();
        services.AddEliCloud(options => options.BaseAddress = new Uri(TestOptions.BaseAddress));
        services.AddEliCloudAdministration("sso-admin", "mc-admin");

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<SsoAdminClient>());
        Assert.NotNull(provider.GetRequiredService<McAdminClient>());
    }

    [Fact]
    public async Task AddEliCloudAuthentication_WiresValidatorThroughDi()
    {
        var authority = new TestTokenAuthority();
        var httpHandler = new CapturingHandler(_ => Stub.Json(authority.JwksJson(), cacheControl: "public, max-age=300"));

        var services = new ServiceCollection();
        services.AddSingleton<HttpClient>(TestOptions.HttpClient(httpHandler));
        services.AddEliCloudAuthentication(options =>
        {
            options.Validation.Issuer = TestTokenAuthority.Issuer;
            options.Validation.RequiredScopes = ["mc:whitelist"];
        });
        services.AddEliCloudScopePolicy("mc:whitelist");

        using var provider = services.BuildServiceProvider();

        var validator = provider.GetRequiredService<EliCloudTokenValidator>();
        Assert.Same(validator, provider.GetRequiredService<EliCloudTokenValidator>());

        var result = await validator.ValidateAccessTokenAsync(authority.CreateAccessToken());
        Assert.True(result.IsValid, result.ErrorDescription);

        // scope 策略注册了对应的授权处理器，否则策略永远无法满足。
        var authorizationHandlers = provider.GetServices<IAuthorizationHandler>().ToList();
        Assert.Contains(authorizationHandlers, handler => handler is EliCloudScopeAuthorizationHandler);

        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(EliCloudAuthenticationDefaults.ScopePolicyName("mc:whitelist"));
        Assert.NotNull(policy);
        Assert.Contains(policy!.Requirements, requirement =>
            requirement is EliCloudScopeRequirement scopeRequirement && scopeRequirement.Scope == "mc:whitelist");
    }

    [Fact]
    public void AddEliCloudAuthentication_DoesNotOverrideExistingHttpClient()
    {
        var marker = new HttpClient();
        var services = new ServiceCollection();
        services.AddSingleton(marker);
        services.AddEliCloudAuthentication(options => options.Validation.Issuer = TestTokenAuthority.Issuer);

        using var provider = services.BuildServiceProvider();

        // 应用自己注册的 HttpClient（带代理/证书/处理器）不能被 SDK 覆盖。
        Assert.Same(marker, provider.GetRequiredService<HttpClient>());
    }

    [Fact]
    public void Authentication_WorksWithoutEliCloudClientRegistration()
    {
        // 资源服务只需要校验令牌，不必注册客户端（也不应被迫注册 EliCloudOptions）。
        var services = new ServiceCollection();
        services.AddEliCloudAuthentication(options => options.Validation.Issuer = TestTokenAuthority.Issuer);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<EliCloudTokenValidator>());
    }

    // ------------------------------------------------------------------ 工具

    private static AuthorizationHandlerContext CreateAuthorizationContext(string scope, IAuthorizationRequirement requirement)
    {
        var identity = new ClaimsIdentity([new Claim("scope", scope)], "EliCloud");
        return new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(identity), resource: null);
    }

    private static async Task<(EliCloudAuthenticationHandler Handler, HttpContext Context)> CreateHandlerAsync(
        string token,
        TestTokenAuthority? authority = null,
        string? headerOverride = null)
    {
        authority ??= new TestTokenAuthority();
        var httpHandler = new CapturingHandler(_ => Stub.Json(authority.JwksJson(), cacheControl: "public, max-age=300"));

        var options = new EliCloudAuthenticationOptions
        {
            Validation = new EliCloudTokenValidationOptions
            {
                Issuer = TestTokenAuthority.Issuer,
                Audience = TestTokenAuthority.Audience,
            },
        };

        var validator = new EliCloudTokenValidator(TestOptions.HttpClient(httpHandler), options.Validation);
        var monitor = new SimpleOptionsMonitor<EliCloudAuthenticationOptions>(options);
        var handler = new EliCloudAuthenticationHandler(monitor, NullLoggerFactory.Instance, UrlEncoder.Default, validator);

        var context = new DefaultHttpContext();
        if (headerOverride is not null)
        {
            context.Request.Headers.Authorization = headerOverride;
        }
        else if (token is not null)
        {
            context.Request.Headers.Authorization = "Bearer " + token;
        }

        var scheme = new AuthenticationScheme(
            EliCloudAuthenticationDefaults.Scheme,
            EliCloudAuthenticationDefaults.Scheme,
            typeof(EliCloudAuthenticationHandler));

        await handler.InitializeAsync(scheme, context);
        return (handler, context);
    }
}
