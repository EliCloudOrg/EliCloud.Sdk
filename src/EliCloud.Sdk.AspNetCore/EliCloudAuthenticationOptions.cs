using EliCloud.Sdk.Tokens;
using Microsoft.AspNetCore.Authentication;

namespace EliCloud.Sdk.AspNetCore;

/// <summary>EliCloud 认证相关的默认值。</summary>
public static class EliCloudAuthenticationDefaults
{
    /// <summary>默认认证方案名。用 <c>[Authorize(AuthenticationSchemes = ...)]</c> 时需要它。</summary>
    public const string Scheme = "EliCloud";

    /// <summary>默认授权策略前缀（scope 策略名由它加 scope 组成）。</summary>
    public const string ScopePolicyPrefix = "EliCloudScope:";

    /// <summary>认证失败时返回的 realm。</summary>
    public const string Realm = "elicloud";

    /// <summary>scope 对应的策略名，如 <c>EliCloudScope:mc:whitelist</c>。</summary>
    public static string ScopePolicyName(string scope) => ScopePolicyPrefix + scope;
}

/// <summary>
/// ASP.NET Core 的 EliCloud 认证方案配置。
/// </summary>
public sealed class EliCloudAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>令牌校验配置（issuer / jwks / audience / 必需 scope）。</summary>
    public EliCloudTokenValidationOptions Validation { get; set; } = new();

    /// <summary>
    /// 是否在 <c>WWW-Authenticate</c> 头里带上 <c>error</c>/<c>error_description</c>
    /// （RFC 6750 §3）。默认 <c>true</c>，便于客户端排错；
    /// 不接受把失败原因暴露给未认证调用方时可以关掉。
    /// </summary>
    public bool IncludeErrorDetails { get; set; } = true;
}
