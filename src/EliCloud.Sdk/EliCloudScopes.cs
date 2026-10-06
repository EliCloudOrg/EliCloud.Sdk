namespace EliCloud.Sdk;

/// <summary>
/// 平台支持的 OAuth 2.0 / OIDC scope 常量。
/// </summary>
/// <remarks>
/// <para>
/// scope 的单一真源在服务端（<c>sso/app/constants.py::SUPPORTED_SCOPES</c>），
/// 发现文档的 <c>scopes_supported</c> 也从那里派生。这里的常量只是为了在客户端避免手写字符串拼错。
/// </para>
/// <para>
/// <b>「平台支持」不等于「这个客户端被允许」</b>：每个 OIDC 客户端注册时还有一份
/// <c>allowed_scopes</c> 上限，请求了没注册的 scope 会在 <c>/authorize</c> 被拒（<c>invalid_scope</c>）。
/// </para>
/// </remarks>
public static class EliCloudScopes
{
    /// <summary>OIDC 必须项；只有带它才会签发 <c>id_token</c>。</summary>
    public const string OpenId = "openid";

    /// <summary>让 <c>userinfo</c>/<c>id_token</c> 返回 <c>preferred_username</c>。</summary>
    public const string Profile = "profile";

    /// <summary>让 <c>userinfo</c>/<c>id_token</c> 返回 <c>email</c> 与 <c>email_verified</c>。</summary>
    public const string Email = "email";

    /// <summary>
    /// 请求 refresh token。
    /// </summary>
    /// <remarks>
    /// 服务端**只有请求了该 scope 且客户端注册了 <c>refresh_token</c> 授权类型**时才签发 refresh token
    /// （OIDC 惯例，见 <c>docs/sso-oidc.md</c> §15.2）。浏览器类客户端靠会话 cookie 静默续期，
    /// 通常不需要它；CLI / App 需要长期访问时才带。
    /// </remarks>
    public const string OfflineAccess = "offline_access";

    /// <summary>PDF 解密服务的读权限（服务规划中）。</summary>
    public const string PdfRead = "pdf:read";

    /// <summary>PDF 解密服务的写权限（服务规划中）。</summary>
    public const string PdfWrite = "pdf:write";

    /// <summary>MC 白名单服务的必需 scope；缺失时该服务返回 <c>403 insufficient_scope</c>。</summary>
    public const string McWhitelist = "mc:whitelist";

    /// <summary>把若干 scope 拼成协议要求的空格分隔形式。</summary>
    public static string Join(IEnumerable<string> scopes) => string.Join(' ', scopes);

    /// <summary>把空格分隔的 scope 串拆成集合（忽略空白项）。</summary>
    public static IReadOnlyList<string> Split(string? scope)
        => string.IsNullOrWhiteSpace(scope)
            ? []
            : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
