using System.Reflection;

namespace EliCloud.Sdk;

/// <summary>
/// 平台级常量。**这些值来自服务端实现，不要在调用方重复硬编码。**
/// </summary>
/// <remarks>
/// 依据：
/// <list type="bullet">
///   <item><description><c>docs/architecture.md</c> §2.2（路径前缀）、§3.1（issuer 由 PUBLIC_BASE_URL 派生）。</description></item>
///   <item><description><c>docs/sso-oidc.md</c> §4.1（access token 的 <c>aud</c> 是 <c>elicloud-services</c>）、§2.1（端点路径）。</description></item>
///   <item><description><c>sso/app/constants.py</c>（scope 与端点的单一真源）。</description></item>
/// </list>
/// </remarks>
public static class EliCloudConstants
{
    /// <summary>access token 的 <c>aud</c>。业务服务按它校验；<c>id_token</c> 的 <c>aud</c> 是 client_id，因此会被拒。</summary>
    public const string Audience = "elicloud-services";

    /// <summary>SSO 的对外路径前缀（容器内路径不带这个前缀，网关负责剥离/重写）。</summary>
    public const string AuthPathPrefix = "/auth";

    /// <summary>MC 白名单服务的对外路径前缀。</summary>
    public const string McPathPrefix = "/mc";

    /// <summary>平台核心 API（main-api，规划中）的对外路径前缀。</summary>
    public const string MainApiPathPrefix = "/core";

    /// <summary>access token 使用的签名算法；平台硬约束只允许 RS256。</summary>
    public const string SigningAlgorithm = "RS256";

    /// <summary>access token 的 JWT <c>typ</c> 之外的唯一合法算法集合，用于验签白名单。</summary>
    public static readonly string[] SupportedAlgorithms = [SigningAlgorithm];

    /// <summary>默认的 JWKS 缓存时长；服务端会下发 <c>Cache-Control: public, max-age=300</c>，取不到响应头时用它兜底。</summary>
    public static readonly TimeSpan DefaultJwksCacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>默认容忍的时钟偏移（服务端与 mc 服务都用 30 秒）。</summary>
    public static readonly TimeSpan DefaultClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>SDK 版本（取自程序集版本），用于 <c>User-Agent</c>。</summary>
    public static string Version { get; } =
        typeof(EliCloudConstants).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(EliCloudConstants).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>默认 <c>User-Agent</c>；服务端日志靠它区分「问题来自 SDK 哪个版本」。</summary>
    public static string UserAgent { get; } = $"EliCloud.Sdk/{Version}";
}
