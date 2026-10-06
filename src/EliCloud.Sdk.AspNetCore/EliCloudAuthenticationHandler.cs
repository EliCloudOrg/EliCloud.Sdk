using System.Security.Claims;
using System.Text.Encodings.Web;
using EliCloud.Sdk.Tokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace EliCloud.Sdk.AspNetCore;

/// <summary>
/// 把 EliCloud 的 <c>Authorization: Bearer &lt;access_token&gt;</c> 接进 ASP.NET Core 认证管道。
/// </summary>
/// <remarks>
/// <para>
/// 行为要点：
/// </para>
/// <list type="bullet">
///   <item><description>
///     请求**没有** <c>Authorization</c> 头时返回 <see cref="AuthenticateResult.NoResult"/>，
///     而不是失败 —— 这样匿名端点与 <c>[AllowAnonymous]</c> 照常工作，
///     也不会在匿名请求上贴一个假的「认证失败」。
///   </description></item>
///   <item><description>
///     有头但令牌无效时返回 <c>AuthenticateResult.Fail</c>，由挑战逻辑给出 401。
///   </description></item>
///   <item><description>
///     <b>401 与 403 的分工</b>：令牌无效 → 401（客户端应重新登录/刷新）；
///     令牌有效但缺 scope → 403（身份没问题，是权限不够）。后者由
///     <see cref="EliCloudScopeRequirement"/> 触发，见
///     <c>AddEliCloudScopePolicy</c>。
///   </description></item>
/// </list>
/// </remarks>
public sealed class EliCloudAuthenticationHandler : AuthenticationHandler<EliCloudAuthenticationOptions>
{
    private readonly EliCloudTokenValidator _validator;

    /// <summary>创建处理器（由 ASP.NET Core 认证管道实例化）。</summary>
    public EliCloudAuthenticationHandler(
        IOptionsMonitor<EliCloudAuthenticationOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        EliCloudTokenValidator validator)
        : base(options, loggerFactory, encoder)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderNames.Authorization, out var values))
        {
            return AuthenticateResult.NoResult();
        }

        var header = values.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return AuthenticateResult.NoResult();
        }

        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("不支持的认证方式：只接受 Bearer 令牌。");
        }

        var token = header["Bearer ".Length..].Trim();
        var result = await _validator.ValidateAccessTokenAsync(token, Context.RequestAborted).ConfigureAwait(false);
        if (!result.IsValid || result.Principal is null)
        {
            Logger.LogDebug("EliCloud 令牌校验失败：{Error} {Description}", result.ErrorCode, result.ErrorDescription);
            return AuthenticateResult.Fail(result.ErrorDescription ?? "令牌无效。");
        }

        var ticket = new AuthenticationTicket(result.Principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var details = Options.IncludeErrorDetails ? "; error=\"invalid_token\"" : string.Empty;
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers[HeaderNames.WWWAuthenticate] =
            $"Bearer realm=\"{EliCloudAuthenticationDefaults.Realm}\"{details}";
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        // 到这里的两种可能：令牌有效但缺 scope（scope 策略失败），或业务自己的授权规则拒绝。
        // 统一用 403 + insufficient_scope，让调用方能区分「重新登录」与「申请授权」。
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.Headers[HeaderNames.WWWAuthenticate] =
            $"Bearer realm=\"{EliCloudAuthenticationDefaults.Realm}\"; error=\"insufficient_scope\"";
        return Task.CompletedTask;
    }
}

/// <summary>
/// 要求当前身份携带某个 scope。
/// </summary>
public sealed class EliCloudScopeRequirement : IAuthorizationRequirement
{
    /// <summary>创建要求。</summary>
    public EliCloudScopeRequirement(string scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        Scope = scope;
    }

    /// <summary>必需的 scope。</summary>
    public string Scope { get; }
}

/// <summary>校验 scope 的授权处理器：**整词匹配** <c>scope</c> claim。</summary>
public sealed class EliCloudScopeAuthorizationHandler : AuthorizationHandler<EliCloudScopeRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        EliCloudScopeRequirement requirement)
    {
        var principal = context.User;
        if (principal?.Identity?.IsAuthenticated == true && principal.HasEliCloudScope(requirement.Scope))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// 便捷特性：<c>[EliCloudScope("mc:whitelist")]</c> 等价于
/// <c>[Authorize(Policy = "EliCloudScope:mc:whitelist")]</c>。
/// </summary>
/// <remarks>
/// 用它之前必须先调用 <c>AddEliCloudScopePolicy("mc:whitelist")</c> 注册策略，
/// 否则运行时会因找不到策略而抛异常。
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class EliCloudScopeAttribute : AuthorizeAttribute
{
    /// <summary>创建特性。</summary>
    public EliCloudScopeAttribute(string scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        Policy = EliCloudAuthenticationDefaults.ScopePolicyName(scope);
    }
}

/// <summary>把 <see cref="ClaimsPrincipal"/> 取出来用的便利扩展（ASP.NET Core 场景）。</summary>
public static class EliCloudHttpContextExtensions
{
    /// <summary>取当前请求的 <c>sub</c>（业务数据隔离的唯一依据）；未认证返回 <c>null</c>。</summary>
    public static string? GetEliCloudSubject(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.User.GetEliCloudSubject();
    }
}
