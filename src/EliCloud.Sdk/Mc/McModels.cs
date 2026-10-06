using System.Text.Json;

namespace EliCloud.Sdk.Mc;

/// <summary>条目状态。</summary>
public static class McNameStatus
{
    /// <summary>生效中（已写入 MC 白名单）。</summary>
    public const string Active = "active";

    /// <summary>已撤回。</summary>
    public const string Removed = "removed";
}

/// <summary>
/// 一条「MC 用户名 ↔ SSO 账号」的绑定。
/// </summary>
/// <remarks>
/// 同一个类型同时表示用户视角与管理视角的返回：管理接口会多带
/// <see cref="Sub"/>、<see cref="RequestIp"/>、<see cref="UserAgent"/>、<see cref="RemovedBy"/>，
/// 用户接口里这些为 <c>null</c>。
/// </remarks>
public sealed class McNameEntry
{
    /// <summary>条目 ID，形如 <c>mn_0007</c>。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>归一后的 MC 用户名（**小写**，服务内部一律用它做键）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>用户提交时的原始写法，仅用于展示。</summary>
    public string NameDisplay { get; init; } = string.Empty;

    /// <summary>必填备注（1–200 字符）。</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary><see cref="McNameStatus"/>。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// MC 服务端写入的 UUID。
    /// </summary>
    /// <remarks>
    /// <b>恒为 <c>null</c></b>：服务只走 RCON，而 <c>whitelist list</c> 不返回 UUID，
    /// 平台也刻意不自己构造它（离线模式下服务器可能写 v5 也可能是随机 v4，见
    /// <c>docs/mc.md</c> §5.2）。所以不要把它当作判定依据。
    /// </remarks>
    public string? Uuid { get; init; }

    /// <summary>创建时间（ISO8601 UTC）。</summary>
    public string? CreatedAt { get; init; }

    /// <summary>撤回时间；未撤回为 <c>null</c>。</summary>
    public string? RemovedAt { get; init; }

    /// <summary>所属用户（SSO 的 <c>sub</c>）；仅管理接口返回。</summary>
    public string? Sub { get; init; }

    /// <summary>提交来源 IP；仅管理接口返回。</summary>
    public string? RequestIp { get; init; }

    /// <summary>提交时的 User-Agent；仅管理接口返回。</summary>
    public string? UserAgent { get; init; }

    /// <summary>撤回者：<c>self</c> / <c>admin</c> / <c>reconcile</c>；仅管理接口返回。</summary>
    public string? RemovedBy { get; init; }
}

/// <summary>名额状态。</summary>
public sealed class McNameQuota
{
    /// <summary>上限（默认 2）。</summary>
    public int Limit { get; init; }

    /// <summary>已用。</summary>
    public int Used { get; init; }

    /// <summary>剩余。</summary>
    public int Remaining { get; init; }
}

/// <summary><c>GET /mc/v1/names</c> 的返回。</summary>
public sealed class McNameListResponse
{
    /// <summary>当前用户。</summary>
    public string Sub { get; init; } = string.Empty;

    /// <summary>名额状态。</summary>
    public McNameQuota? Quota { get; init; }

    /// <summary>条目列表（默认只有 active；带 <c>include_removed=true</c> 时含历史）。</summary>
    public IReadOnlyList<McNameEntry> Names { get; init; } = [];
}

/// <summary><c>GET /mc/v1/me</c> 的返回：前端登录后自检身份用。</summary>
public sealed class McIdentity
{
    /// <summary>用户 ID。</summary>
    public string Sub { get; init; } = string.Empty;

    /// <summary>用户名（来自令牌 claim，**不查 SSO 数据库**）。</summary>
    public string? Username { get; init; }

    /// <summary>平台 issuer。</summary>
    public string? Issuer { get; init; }

    /// <summary>令牌 scope。</summary>
    public string? Scope { get; init; }
}

/// <summary>RCON 连通性。</summary>
public sealed class McRconStatus
{
    /// <summary>是否可达。</summary>
    public bool Reachable { get; init; }

    /// <summary>RCON 端点（<c>host:port</c>）。</summary>
    public string? Endpoint { get; init; }

    /// <summary>一次 <c>whitelist list</c> 的往返毫秒数。</summary>
    public int? LatencyMs { get; init; }

    /// <summary>不可达时的原因。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// <c>GET /mc/healthz</c> 的返回。
/// </summary>
/// <remarks>
/// RCON 不可达时服务端返回 <b>503</b> 但进程仍在跑、只读接口仍可用 ——
/// 所以这个接口即使「不健康」也会带完整信息，不是错误响应。
/// </remarks>
public sealed class McHealthStatus
{
    /// <summary><c>ok</c> 或 <c>degraded</c>。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>RCON 状态。</summary>
    public McRconStatus? Rcon { get; init; }

    /// <summary>白名单里的名字数量；RCON 不可达时为 <c>null</c>。</summary>
    public int? WhitelistCount { get; init; }

    /// <summary>本服务配置的 SSO issuer，用于核对与平台是否一致。</summary>
    public string? SsoIssuer { get; init; }

    /// <summary>服务版本。</summary>
    public string? Version { get; init; }

    /// <summary>是否健康（等价于 <see cref="Rcon"/> 可达）。</summary>
    public bool IsHealthy => Rcon?.Reachable == true;
}

/// <summary>一条审计记录。</summary>
public sealed class McAuditLog
{
    /// <summary>记录 ID，形如 <c>au_1a2b…</c>。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>用户；未认证的失败请求为 <c>null</c>。</summary>
    public string? Sub { get; init; }

    /// <summary>动作：<c>add</c> / <c>remove</c> / <c>reconcile</c> / <c>deny</c>。</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>目标（MC 用户名或条目 ID）。</summary>
    public string? Target { get; init; }

    /// <summary>结果：<c>ok</c> / <c>idempotent</c> / <c>conflict</c> / <c>quota_exceeded</c> / <c>rcon_error</c> / …</summary>
    public string Result { get; init; } = string.Empty;

    /// <summary>附加详情（RCON 原文、失败原因等）。**不含令牌原文**。</summary>
    public JsonElement? Detail { get; init; }

    /// <summary>来源 IP。</summary>
    public string? RequestIp { get; init; }

    /// <summary>时间（ISO8601 UTC）。</summary>
    public string? CreatedAt { get; init; }
}

/// <summary>分页元信息（管理接口通用）。</summary>
public abstract class McPageInfo
{
    /// <summary>总数。</summary>
    public int Total { get; init; }

    /// <summary>本页请求的上限。</summary>
    public int Limit { get; init; }

    /// <summary>本页偏移。</summary>
    public int Offset { get; init; }
}

/// <summary><c>GET /mc/v1/admin/names</c> 的返回。</summary>
public sealed class McNamePage : McPageInfo
{
    /// <summary>条目列表。</summary>
    public IReadOnlyList<McNameEntry> Names { get; init; } = [];
}

/// <summary><c>GET /mc/v1/admin/audit</c> 的返回。</summary>
public sealed class McAuditPage : McPageInfo
{
    /// <summary>审计记录。</summary>
    public IReadOnlyList<McAuditLog> Logs { get; init; } = [];
}

/// <summary>对账失败的一项。</summary>
public sealed class McReconcileFailure
{
    /// <summary>MC 用户名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>失败原因。</summary>
    public string Error { get; init; } = string.Empty;
}

/// <summary><c>POST /mc/v1/admin/reconcile</c> 的对账报告。</summary>
public sealed class McReconcileReport
{
    /// <summary>MC 白名单里的名字（**唯一真源**）。</summary>
    public IReadOnlyList<string> Whitelist { get; init; } = [];

    /// <summary>平台库里 active 的名字。</summary>
    public IReadOnlyList<string> DbActive { get; init; } = [];

    /// <summary>库里有、白名单里没有（漂移，<c>apply=true</c> 时会补回）。</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>白名单里有、库里没有（可能是管理员手工加的，**绝不自动删除**）。</summary>
    public IReadOnlyList<string> Extra { get; init; } = [];

    /// <summary>本次补回成功的名字。</summary>
    public IReadOnlyList<string> Applied { get; init; } = [];

    /// <summary>本次补回失败的名字。</summary>
    public IReadOnlyList<McReconcileFailure> Failed { get; init; } = [];

    /// <summary>本次是否真的写入了白名单。</summary>
    public bool Apply { get; init; }

    /// <summary>是否存在漂移。</summary>
    public bool HasDrift => Missing.Count > 0 || Extra.Count > 0;
}
