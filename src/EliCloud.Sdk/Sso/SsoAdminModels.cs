namespace EliCloud.Sdk.Sso;

/// <summary>OIDC 客户端类型。</summary>
public static class EliCloudClientTypes
{
    /// <summary>公开客户端：无 secret，安全性由 PKCE(S256) 保证。手机 App、CLI、桌面工具、SPA。</summary>
    public const string Public = "public";

    /// <summary>保密客户端：有 secret，用 <c>client_secret_basic</c> 或 <c>client_secret_post</c> 认证。</summary>
    public const string Confidential = "confidential";
}

/// <summary>令牌端点的客户端认证方式（注册值）。</summary>
public static class EliCloudTokenEndpointAuthMethods
{
    /// <summary>无认证（公开客户端）。</summary>
    public const string None = "none";

    /// <summary>HTTP Basic。</summary>
    public const string ClientSecretBasic = "client_secret_basic";

    /// <summary>表单字段。</summary>
    public const string ClientSecretPost = "client_secret_post";
}

/// <summary>已注册的 OIDC 客户端信息（**永不包含 secret**）。</summary>
public sealed class OAuthClientInfo
{
    /// <summary>客户端 ID。</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary><c>public</c> 或 <c>confidential</c>。</summary>
    public string ClientType { get; init; } = string.Empty;

    /// <summary>显示名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>允许的回跳地址；校验是**精确字符串匹配**。</summary>
    public IReadOnlyList<string> RedirectUris { get; init; } = [];

    /// <summary>允许的登出后回跳地址。</summary>
    public IReadOnlyList<string> PostLogoutRedirectUris { get; init; } = [];

    /// <summary>该客户端被允许请求的 scope 上限（平台支持集合的子集）。</summary>
    public IReadOnlyList<string> AllowedScopes { get; init; } = [];

    /// <summary>该客户端被允许使用的 grant_type。</summary>
    public IReadOnlyList<string> AllowedGrantTypes { get; init; } = [];

    /// <summary>注册的令牌端点认证方式。</summary>
    public string TokenEndpointAuthMethod { get; init; } = string.Empty;

    /// <summary>创建时间（ISO8601）。</summary>
    public string? CreatedAt { get; init; }

    /// <summary>最近修改时间（ISO8601）。</summary>
    public string? UpdatedAt { get; init; }
}

/// <summary>创建客户端后的一次性返回：**只有这一次**能看到明文 secret。</summary>
public sealed class CreatedOAuthClientInfo
{
    /// <summary>客户端 ID。</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary><c>public</c> 或 <c>confidential</c>。</summary>
    public string ClientType { get; init; } = string.Empty;

    /// <summary>显示名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 明文 <c>client_secret</c>；公开客户端为 <c>null</c>。
    /// 服务端只存哈希，**之后任何接口都不再回显**，请立即落盘到安全位置。
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>注册的回跳地址。</summary>
    public IReadOnlyList<string> RedirectUris { get; init; } = [];

    /// <summary>注册的登出回跳地址。</summary>
    public IReadOnlyList<string> PostLogoutRedirectUris { get; init; } = [];

    /// <summary>允许的 scope。</summary>
    public IReadOnlyList<string> AllowedScopes { get; init; } = [];

    /// <summary>允许的 grant_type。</summary>
    public IReadOnlyList<string> AllowedGrantTypes { get; init; } = [];

    /// <summary>认证方式。</summary>
    public string TokenEndpointAuthMethod { get; init; } = string.Empty;
}

/// <summary>创建 OIDC 客户端的请求（<c>POST /auth/v1/clients</c>）。</summary>
public sealed class CreateOAuthClientRequest
{
    /// <summary>客户端 ID：2–64 位，字母/数字开头，仅含字母、数字与 <c>_ . : -</c>。</summary>
    public required string ClientId { get; init; }

    /// <summary>显示名。</summary>
    public required string Name { get; init; }

    /// <summary>客户端类型，默认公开客户端。</summary>
    public string ClientType { get; init; } = EliCloudClientTypes.Public;

    /// <summary>回跳地址；含 <c>authorization_code</c> 的客户端必填。自定义 scheme（如 <c>elipese://callback</c>）允许。</summary>
    public IReadOnlyList<string> RedirectUris { get; init; } = [];

    /// <summary>登出后回跳地址。</summary>
    public IReadOnlyList<string> PostLogoutRedirectUris { get; init; } = [];

    /// <summary>
    /// 允许的 scope 上限。**平台新增 scope 后（如 <c>mc:whitelist</c>）这里不追加，
    /// 该客户端请求时就会被拒**，见 <c>docs/mc.md</c> §12.4 的提醒。
    /// </summary>
    public IReadOnlyList<string> AllowedScopes { get; init; } = [];

    /// <summary>允许的 grant_type；留空由服务端取默认（<c>authorization_code</c> + <c>refresh_token</c>）。</summary>
    public IReadOnlyList<string>? AllowedGrantTypes { get; init; }

    /// <summary>认证方式；留空由服务端按客户端类型推断。</summary>
    public string? TokenEndpointAuthMethod { get; init; }
}

/// <summary>
/// 修改 OIDC 客户端的请求（<c>PATCH /auth/v1/clients/{id}</c>）。
/// </summary>
/// <remarks>
/// <c>client_type</c> 与 <c>token_endpoint_auth_method</c> **刻意不可修改**：
/// 它们决定认证强度，要改就删除重建，免得把公开客户端悄悄改成保密客户端。
/// 另外服务端对未知字段返回 400，所以这里只暴露允许修改的字段。
/// </remarks>
public sealed class UpdateOAuthClientRequest
{
    /// <summary>新显示名；<c>null</c> 表示不改。</summary>
    public string? Name { get; init; }

    /// <summary>新的回跳地址集合；<c>null</c> 表示不改。</summary>
    public IReadOnlyList<string>? RedirectUris { get; init; }

    /// <summary>新的登出回跳地址集合；<c>null</c> 表示不改。</summary>
    public IReadOnlyList<string>? PostLogoutRedirectUris { get; init; }

    /// <summary>新的 scope 上限；<c>null</c> 表示不改。</summary>
    public IReadOnlyList<string>? AllowedScopes { get; init; }

    /// <summary>新的 grant_type 集合；<c>null</c> 表示不改。</summary>
    public IReadOnlyList<string>? AllowedGrantTypes { get; init; }
}

/// <summary>轮换 secret 的结果：同样是**一次性**回显明文。</summary>
public sealed class RotatedClientSecret
{
    /// <summary>客户端 ID。</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>新的明文 secret；旧 secret 立即失效。</summary>
    public string ClientSecret { get; init; } = string.Empty;
}
