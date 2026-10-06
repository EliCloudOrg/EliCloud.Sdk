using System.Security.Claims;

namespace EliCloud.Sdk.Tokens;

/// <summary>
/// 从 ClaimsPrincipal 里按 EliCloud 的 claim 约定取值。
/// </summary>
/// <remarks>
/// 业务服务的代码里到处都要写 <c>User.FindFirst("sub")?.Value</c>，
/// 而平台有两处容易踩的坑，集中在这里处理：
/// <list type="number">
///   <item><description>
///     <b>用户名有新旧两种 claim</b>：标准是 <c>preferred_username</c>，
///     早期实现用非标准的 <c>username</c>。两者都要认，但优先取标准的。
///   </description></item>
///   <item><description>
///     <b>scope 必须整词匹配</b>：<c>scope</c> 是空格分隔的字符串，
///     用 <c>Contains("pdf")</c> 会误命中 <c>pdf:read</c>，必须按整词比较。
///   </description></item>
/// </list>
/// </remarks>
public static class EliCloudClaimsPrincipalExtensions
{
    /// <summary>用户 ID（<c>sub</c>）——业务数据隔离的唯一依据。取不到返回 <c>null</c>。</summary>
    public static string? GetEliCloudSubject(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirst("sub")?.Value;
    }

    /// <summary>用户名：优先 <c>preferred_username</c>，回落 <c>username</c>。</summary>
    public static string? GetEliCloudUsername(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst("username")?.Value;
    }

    /// <summary><c>client_id</c>：审计「是哪个客户端拿到的令牌」时用。</summary>
    public static string? GetEliCloudClientId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirst("client_id")?.Value;
    }

    /// <summary>把 <c>scope</c> claim 拆成列表。</summary>
    public static IReadOnlyList<string> GetEliCloudScopes(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return EliCloudScopes.Split(principal.FindFirst("scope")?.Value);
    }

    /// <summary>令牌是否包含某个 scope（**整词匹配**）。</summary>
    public static bool HasEliCloudScope(this ClaimsPrincipal principal, string scope)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrEmpty(scope);
        return principal.GetEliCloudScopes().Contains(scope, StringComparer.Ordinal);
    }
}
