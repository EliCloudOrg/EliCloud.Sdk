using System.Text.Json.Serialization;

namespace EliCloud.Sdk.Sso;

/// <summary>
/// OIDC 发现文档（<c>GET {base}/.well-known/openid-configuration</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 线上实测形态见 <c>docs/sso-oidc.md</c> §2.2。SDK 默认**不依赖**这份文档拼端点
/// （按平台契约直接拼，省一次往返），但它是核对「对方到底是什么形态」最可靠的依据，
/// 尤其在平台从 IP 阶段切到域名阶段时。
/// </para>
/// <para>
/// 服务端有一条自我一致性测试会遍历这里声明的每个端点并断言不是 404，
/// 所以「发现文档里出现过的端点」都可以放心调用。
/// </para>
/// </remarks>
public sealed class OidcDiscoveryDocument
{
    /// <summary>issuer，必须与运行时实际对外地址逐字一致（JWT 的 <c>iss</c> 也是它）。</summary>
    public string? Issuer { get; init; }

    /// <summary>授权端点，如 <c>https://…/auth/authorize</c>。</summary>
    public string? AuthorizationEndpoint { get; init; }

    /// <summary>令牌端点，如 <c>https://…/auth/token</c>。</summary>
    public string? TokenEndpoint { get; init; }

    /// <summary>UserInfo 端点。</summary>
    /// <remarks>
    /// ⚠️ 这里必须显式标注 JSON 名：全局的 snake_case 策略会把 <c>UserInfoEndpoint</c>
    /// 转成 <c>user_info_endpoint</c>，而服务端发的是 <c>userinfo_endpoint</c>（一个词）。
    /// 这类「策略猜的名字与线缆上的名字不一致」的字段必须显式写出来，
    /// 否则会静默绑定成 <c>null</c>（线上集成测试就是为了抓这个）。
    /// </remarks>
    [JsonPropertyName("userinfo_endpoint")]
    public string? UserInfoEndpoint { get; init; }

    /// <summary>JWKS 地址，固定为 <c>{issuer}/.well-known/jwks.json</c>，匿名可访问。</summary>
    public string? JwksUri { get; init; }

    /// <summary>设备授权端点（RFC 8628 第一步）。</summary>
    public string? DeviceAuthorizationEndpoint { get; init; }

    /// <summary>RP-Initiated Logout 端点。</summary>
    public string? EndSessionEndpoint { get; init; }

    /// <summary>支持的 response_type；本平台只有 <c>code</c>。</summary>
    public IReadOnlyList<string>? ResponseTypesSupported { get; init; }

    /// <summary>支持的 response_mode；本平台只有 <c>query</c>。</summary>
    public IReadOnlyList<string>? ResponseModesSupported { get; init; }

    /// <summary>支持的 grant_type，含设备码的 URN。</summary>
    public IReadOnlyList<string>? GrantTypesSupported { get; init; }

    /// <summary>授权的 PKCE 挑战方式；本平台只有 <c>S256</c>（<c>plain</c> 被显式拒绝）。</summary>
    public IReadOnlyList<string>? CodeChallengeMethodsSupported { get; init; }

    /// <summary>令牌端点的客户端认证方式。</summary>
    public IReadOnlyList<string>? TokenEndpointAuthMethodsSupported { get; init; }

    /// <summary>id_token 的签名算法；只有 <c>RS256</c>。</summary>
    public IReadOnlyList<string>? IdTokenSigningAlgValuesSupported { get; init; }

    /// <summary>subject 类型；本平台为 <c>public</c>。</summary>
    public IReadOnlyList<string>? SubjectTypesSupported { get; init; }

    /// <summary>平台支持的 scope 全集。**不等于某个客户端的 <c>allowed_scopes</c>**。</summary>
    public IReadOnlyList<string>? ScopesSupported { get; init; }

    /// <summary>可能出现在 <c>id_token</c>/<c>userinfo</c> 里的 claim。</summary>
    public IReadOnlyList<string>? ClaimsSupported { get; init; }
}

/// <summary>平台用户信息（注册接口与登录接口里回显的 <c>user</c>）。</summary>
public sealed class EliCloudUser
{
    /// <summary>用户 ID，即 JWT 的 <c>sub</c>；一经签发永不变更。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>用户名。</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>邮箱（可空）。</summary>
    public string? Email { get; init; }

    /// <summary>创建时间（ISO8601 UTC）。</summary>
    public string? CreatedAt { get; init; }
}

/// <summary>密码直连登录的结果（<c>POST {base}/login</c>）。</summary>
public sealed class LoginResult
{
    /// <summary>access token（JWT，RS256）。</summary>
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>令牌类型，固定 <c>Bearer</c>。</summary>
    public string TokenType { get; init; } = "Bearer";

    /// <summary>access token 有效期（秒）。</summary>
    public int ExpiresIn { get; init; }

    /// <summary>refresh token（<c>rt_</c> 前缀的不透明串）。</summary>
    public string RefreshToken { get; init; } = string.Empty;

    /// <summary>本次令牌的 scope（空格分隔）。</summary>
    public string Scope { get; init; } = string.Empty;

    /// <summary>登录用户。</summary>
    public EliCloudUser? User { get; init; }
}

/// <summary>私有刷新接口的结果（<c>POST {base}/refresh</c>）。</summary>
public sealed class RefreshResult
{
    /// <summary>新的 access token。</summary>
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>令牌类型。</summary>
    public string TokenType { get; init; } = "Bearer";

    /// <summary>有效期（秒）。</summary>
    public int ExpiresIn { get; init; }

    /// <summary>轮换后的 refresh token（旧令牌立即失效）。</summary>
    public string RefreshToken { get; init; } = string.Empty;
}

/// <summary>
/// UserInfo 响应（<c>GET|POST {base}/userinfo</c>）。
/// </summary>
/// <remarks>
/// claim 按 access token 的 scope 过滤：<c>sub</c> 恒有；<c>profile</c> 才有
/// <see cref="PreferredUsername"/>；<c>email</c> 才有 <see cref="Email"/>。
/// <see cref="Scope"/> 不是用户 claim，保留它是为了调用方自检。
/// </remarks>
public sealed class UserInfoResponse
{
    /// <summary>用户 ID。</summary>
    public string Sub { get; init; } = string.Empty;

    /// <summary>标准用户名字段；平台**不再**返回非标准的 <c>username</c>。</summary>
    public string? PreferredUsername { get; init; }

    /// <summary>邮箱（需要 <c>email</c> scope，且账号确实填了邮箱）。</summary>
    public string? Email { get; init; }

    /// <summary>邮箱是否已验证。平台没有验证流程，恒为 <c>false</c>。</summary>
    public bool? EmailVerified { get; init; }

    /// <summary>显示名（平台当前不返回，保留以兼容未来）。</summary>
    public string? Name { get; init; }

    /// <summary>本次令牌的 scope。</summary>
    public string? Scope { get; init; }
}

/// <summary>OIDC 令牌端点的响应（授权码 / 刷新 / 设备码三种 grant 共用）。</summary>
public sealed class TokenResponse
{
    /// <summary>access token；<c>aud</c> 固定为 <c>elicloud-services</c>，用于访问业务 API。</summary>
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>令牌类型，固定 <c>Bearer</c>。</summary>
    public string TokenType { get; init; } = "Bearer";

    /// <summary>有效期（秒）。</summary>
    public int ExpiresIn { get; init; }

    /// <summary>refresh token；**只有请求了 <c>offline_access</c> 才会出现**。</summary>
    public string? RefreshToken { get; init; }

    /// <summary>id_token；<c>aud</c> 是 client_id，**不可用于访问 API**。</summary>
    public string? IdToken { get; init; }

    /// <summary>本次授予的 scope（空格分隔）。</summary>
    public string? Scope { get; init; }

    /// <summary>access token 的绝对过期时间（按本地时钟推算，仅用于提前刷新）。</summary>
    public DateTimeOffset GetAccessTokenExpiresAt(DateTimeOffset? issuedAt = null)
        => (issuedAt ?? DateTimeOffset.UtcNow).AddSeconds(ExpiresIn);

    /// <summary>把三个令牌收成一个便于持久化的对象。</summary>
    public TokenSet ToTokenSet() => new()
    {
        AccessToken = AccessToken,
        RefreshToken = RefreshToken,
        IdToken = IdToken,
        TokenType = TokenType,
        Scope = Scope,
        ExpiresIn = ExpiresIn,
        IssuedAt = DateTimeOffset.UtcNow,
    };
}

/// <summary>
/// 一组令牌 + 签发时间，便于客户端持久化与判断是否该刷新。
/// </summary>
public sealed class TokenSet
{
    /// <summary>access token。</summary>
    public string? AccessToken { get; init; }

    /// <summary>refresh token（可能没有）。</summary>
    public string? RefreshToken { get; init; }

    /// <summary>id_token（可能没有）。</summary>
    public string? IdToken { get; init; }

    /// <summary>令牌类型。</summary>
    public string TokenType { get; init; } = "Bearer";

    /// <summary>scope（空格分隔）。</summary>
    public string? Scope { get; init; }

    /// <summary>有效期（秒）。</summary>
    public int ExpiresIn { get; init; }

    /// <summary>本地记录的签发时刻。</summary>
    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>access token 过期时刻。</summary>
    public DateTimeOffset ExpiresAt => IssuedAt.AddSeconds(ExpiresIn);

    /// <summary>是否已过期（<paramref name="skew"/> 用来提前刷新，默认提前 60 秒）。</summary>
    public bool IsAccessTokenExpired(TimeSpan? skew = null)
        => DateTimeOffset.UtcNow >= ExpiresAt - (skew ?? TimeSpan.FromSeconds(60));
}

/// <summary>设备授权响应（<c>POST {base}/device_authorization</c>）。</summary>
public sealed class DeviceAuthorizationResponse
{
    /// <summary>设备码：**只给设备**，用于轮询令牌端点。高熵随机串。</summary>
    public string DeviceCode { get; init; } = string.Empty;

    /// <summary>人读短码，形如 <c>WDJB-MJHT</c>（去掉了易混淆的 0/O/1/I/L）。</summary>
    public string UserCode { get; init; } = string.Empty;

    /// <summary>用户需要在浏览器里打开的地址。</summary>
    public string VerificationUri { get; init; } = string.Empty;

    /// <summary>预填短码的完整地址；把它显示给用户最省事。</summary>
    public string? VerificationUriComplete { get; init; }

    /// <summary>设备码有效期（秒），默认 600。</summary>
    public int ExpiresIn { get; init; }

    /// <summary>服务端要求的轮询间隔（秒），默认 5。</summary>
    public int Interval { get; init; } = 5;
}

/// <summary>授权码换取令牌所需的参数（<c>grant_type=authorization_code</c>）。</summary>
public sealed class AuthorizationCodeTokenRequest
{
    /// <summary><c>/authorize</c> 回跳带回来的授权码。</summary>
    public required string Code { get; init; }

    /// <summary>PKCE 的 <c>code_verifier</c> 原文（公开客户端必填）。</summary>
    public string? CodeVerifier { get; init; }

    /// <summary>与授权请求**逐字相同**的回跳地址。</summary>
    public required string RedirectUri { get; init; }

    /// <summary>客户端 ID。</summary>
    public required string ClientId { get; init; }

    /// <summary>保密客户端的 secret；公开客户端留空。</summary>
    public string? ClientSecret { get; init; }

    /// <summary>客户端认证方式；留空时按是否给了 secret 自动推断。</summary>
    public ClientAuthenticationMethod? AuthenticationMethod { get; init; }
}

/// <summary>刷新令牌所需的参数（<c>grant_type=refresh_token</c>）。</summary>
public sealed class RefreshTokenGrantRequest
{
    /// <summary>refresh token。</summary>
    public required string RefreshToken { get; init; }

    /// <summary>客户端 ID。</summary>
    public required string ClientId { get; init; }

    /// <summary>保密客户端的 secret。</summary>
    public string? ClientSecret { get; init; }

    /// <summary>客户端认证方式；留空时自动推断。</summary>
    public ClientAuthenticationMethod? AuthenticationMethod { get; init; }

    /// <summary>可选：收窄 scope（**只能收窄，不能扩大**）。</summary>
    public string? Scope { get; init; }
}

/// <summary>客户端在令牌端点的认证方式。</summary>
public enum ClientAuthenticationMethod
{
    /// <summary>公开客户端：只带 <c>client_id</c>，安全性由 PKCE 保证。</summary>
    None,

    /// <summary><c>Authorization: Basic base64(urlencode(id):urlencode(secret))</c>。</summary>
    ClientSecretBasic,

    /// <summary>表单字段 <c>client_id</c> + <c>client_secret</c>。</summary>
    ClientSecretPost,
}

/// <summary>构造授权 URL 所需的参数（<c>GET {base}/authorize</c>）。</summary>
public sealed class AuthorizationRequest
{
    /// <summary>客户端 ID（必须已在 SSO 静态注册）。</summary>
    public required string ClientId { get; init; }

    /// <summary>回跳地址；必须与注册值**精确字符串匹配**（不做前缀或通配匹配）。</summary>
    public required string RedirectUri { get; init; }

    /// <summary>请求的 scope，必须含 <c>openid</c>。</summary>
    public string Scope { get; init; } = EliCloudScopes.OpenId;

    /// <summary>CSRF 用的 state，服务端原样回传。</summary>
    public string? State { get; init; }

    /// <summary>随机数，会被写进 <c>id_token</c> 供客户端校验（防重放）。</summary>
    public string? Nonce { get; init; }

    /// <summary>PKCE 的 <c>code_challenge</c>；公开客户端必填。</summary>
    public string? CodeChallenge { get; init; }

    /// <summary>PKCE 挑战方式；本平台只接受 <c>S256</c>。</summary>
    public string CodeChallengeMethod { get; init; } = "S256";

    /// <summary><c>login</c> 强制重新认证；<c>none</c> 在无会话时不渲染登录页而是回 <c>login_required</c>。</summary>
    public string? Prompt { get; init; }
}

/// <summary>构造登出 URL 所需的参数（RP-Initiated Logout）。</summary>
public sealed class LogoutRequest
{
    /// <summary>登出后的回跳地址；必须在客户端注册的 <c>post_logout_redirect_uris</c> 内，否则不会跳转。</summary>
    public string? PostLogoutRedirectUri { get; init; }

    /// <summary><c>id_token</c> 提示；服务端只校验签名与 <c>iss</c>（允许已过期）。</summary>
    public string? IdTokenHint { get; init; }

    /// <summary>不保留 id_token 时可直接给 <c>client_id</c>（服务端的便利扩展）。</summary>
    public string? ClientId { get; init; }

    /// <summary>原样回传的 state。</summary>
    public string? State { get; init; }
}
