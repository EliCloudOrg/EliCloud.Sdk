using EliCloud.Sdk.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EliCloud.Sdk.Sso;

/// <summary>
/// EliCloud SSO（账号中心，对外前缀 <c>/auth</c>）的客户端。
/// </summary>
/// <remarks>
/// <para>
/// 覆盖三类端点，<b>按用途而不是按新旧</b>选择：
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>标准 OIDC</b>（<see cref="BuildAuthorizationUrl"/>、<see cref="ExchangeCodeAsync"/>、
///     <see cref="StartDeviceAuthorizationAsync"/>、<see cref="PollDeviceCodeAsync"/>、
///     <see cref="RefreshTokenAsync"/>）：给 Web 前端、手机 App、CLI 用，是**推荐**路径。
///   </description></item>
///   <item><description>
///     <b>私有密码直连</b>（<see cref="LoginAsync"/>、<see cref="RefreshAsync"/>）：给自家 App
///     的开发联调，或平台内网脚本用。它绕过了浏览器与 PKCE。
///   </description></item>
///   <item><description>
///     <b>注册与 UserInfo</b>（<see cref="RegisterAsync"/>、<see cref="GetUserInfoAsync"/>）。
///   </description></item>
/// </list>
/// <para>
/// ⚠️ <b>密码直连签发的 refresh token 无法主动撤销</b>：它没有会话绑定，
/// 标准 <c>/logout</c> 找不到它（私有 <c>/v1/logout</c> 已被平台删除，
/// 见 <c>docs/sso-oidc.md</c> §1.3 与 <c>sso/README.md</c> §18.4）。
/// 需要登出语义时请改用授权码 + PKCE。
/// </para>
/// </remarks>
public sealed class SsoClient
{
    private readonly EliCloudTransport _transport;
    private readonly Uri _baseAddress;

    /// <summary>创建客户端。</summary>
    /// <param name="httpClient">HTTP 客户端；生命周期由调用方管理（SDK 不释放它）。</param>
    /// <param name="options">连接配置。</param>
    /// <param name="logger">可选日志；只会记录方法与路径，不记录查询串、请求体与令牌。</param>
    public SsoClient(HttpClient httpClient, EliCloudOptions options, ILogger<SsoClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _baseAddress = options.SsoBaseAddress;
        _transport = new EliCloudTransport(httpClient, options.Timeout, logger);
    }

    /// <summary>创建客户端（配置来自 DI 选项系统）。</summary>
    public SsoClient(HttpClient httpClient, IOptions<EliCloudOptions> options, ILogger<SsoClient>? logger = null)
        : this(httpClient, (options ?? throw new ArgumentNullException(nameof(options))).Value, logger)
    {
    }

    // ------------------------------------------------------------------ 端点

    /// <summary>OIDC 发现文档地址。</summary>
    public Uri DiscoveryEndpoint => new(_baseAddress, ".well-known/openid-configuration");

    /// <summary>JWKS 地址（匿名可访问）。</summary>
    public Uri JwksEndpoint => new(_baseAddress, ".well-known/jwks.json");

    /// <summary>授权端点。</summary>
    public Uri AuthorizationEndpoint => new(_baseAddress, "authorize");

    /// <summary>令牌端点。</summary>
    public Uri TokenEndpoint => new(_baseAddress, "token");

    /// <summary>UserInfo 端点。</summary>
    public Uri UserInfoEndpoint => new(_baseAddress, "userinfo");

    /// <summary>设备授权端点。</summary>
    public Uri DeviceAuthorizationEndpoint => new(_baseAddress, "device_authorization");

    /// <summary>登出端点（RP-Initiated Logout）。</summary>
    public Uri EndSessionEndpoint => new(_baseAddress, "logout");

    /// <summary>私有注册端点（对外形态 <c>/auth/register</c>）。</summary>
    public Uri RegisterEndpoint => new(_baseAddress, "register");

    /// <summary>私有登录端点（对外形态 <c>/auth/login</c>）。</summary>
    public Uri LoginEndpoint => new(_baseAddress, "login");

    /// <summary>私有刷新端点（对外形态 <c>/auth/refresh</c>）。</summary>
    public Uri RefreshEndpoint => new(_baseAddress, "refresh");

    /// <summary>客户端管理端点（对外形态 <c>/auth/v1/clients</c>）。</summary>
    public Uri ClientsEndpoint => new(_baseAddress, "v1/clients");

    // -------------------------------------------------- 发现文档与公钥（匿名）

    /// <summary>读取 OIDC 发现文档。</summary>
    /// <remarks>
    /// 这是核对「目标地址到底是不是 EliCloud SSO、处在 IP 阶段还是域名阶段」的最快手段：
    /// <see cref="OidcDiscoveryDocument.Issuer"/> 必须与你配置的对外地址一致。
    /// </remarks>
    public async Task<OidcDiscoveryDocument> GetDiscoveryDocumentAsync(CancellationToken cancellationToken = default)
    {
        var response = await _transport
            .SendJsonAsync(HttpMethod.Get, DiscoveryEndpoint, null, null, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return response.Deserialize<OidcDiscoveryDocument>()
            ?? throw new EliCloudMalformedResponseException(
                $"发现文档不是合法 JSON 对象：{DiscoveryEndpoint}", DiscoveryEndpoint, response.Body);
    }

    /// <summary>读取原始 JWKS JSON（业务服务与移动端常用它自己做缓存）。</summary>
    public async Task<string> GetJsonWebKeySetJsonAsync(CancellationToken cancellationToken = default)
    {
        var response = await _transport
            .SendJsonAsync(HttpMethod.Get, JwksEndpoint, null, null, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();
        return response.Body ?? string.Empty;
    }

    /// <summary>读取 JWKS 并解析成密钥集。</summary>
    public async Task<JsonWebKeySet> GetJsonWebKeySetAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetJsonWebKeySetJsonAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new JsonWebKeySet(json);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new EliCloudMalformedResponseException(
                $"JWKS 解析失败：{JwksEndpoint}", JwksEndpoint, json);
        }
    }

    // ------------------------------------------------------ 私有端点（密码直连）

    /// <summary>
    /// 注册账号（<c>POST {base}/register</c>）。
    /// </summary>
    /// <exception cref="EliCloudApiException">
    /// 用户名/邮箱冲突（<c>user_exists</c> / <c>email_exists</c>）、未开放自助注册
    /// （<c>registration_disabled</c>）、密码强度不足（<c>invalid_request</c>）。
    /// </exception>
    public async Task<EliCloudUser> RegisterAsync(
        string username,
        string password,
        string? email = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var payload = new RegisterRequest(username, password, email);
        var response = await _transport
            .SendJsonAsync(HttpMethod.Post, RegisterEndpoint, payload, null, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        var envelope = response.Deserialize<RegisterEnvelope>();
        return envelope?.User
            ?? throw new EliCloudMalformedResponseException(
                $"注册响应缺少 user 字段：{RegisterEndpoint}", RegisterEndpoint, response.Body);
    }

    /// <summary>
    /// 密码直连登录（<c>POST {base}/login</c>）。
    /// </summary>
    /// <remarks>
    /// 失败时服务端**不区分**「用户不存在」与「密码错误」（防账号枚举），
    /// 因此调用方无法也不应该据此判断账号是否存在。
    /// </remarks>
    public async Task<LoginResult> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var payload = new LoginRequest(username, password);
        var response = await _transport
            .SendJsonAsync(HttpMethod.Post, LoginEndpoint, payload, null, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return response.Deserialize<LoginResult>()
            ?? throw new EliCloudMalformedResponseException($"登录响应不是合法 JSON：{LoginEndpoint}", LoginEndpoint, response.Body);
    }

    /// <summary>
    /// 轮换 refresh token（<c>POST {base}/refresh</c>，私有端点）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 重放已轮换过的旧令牌会被判为窃取，**整条令牌链立刻全部撤销**，
    /// 用户必须重新登录。所以务必保存每次返回的新 refresh token。
    /// 另外：绑定了 client_id 的 OIDC 令牌**不能**在这里兑换（会被拒），要用 <see cref="RefreshTokenAsync"/>。
    /// </remarks>
    public async Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);

        var payload = new RefreshRequest(refreshToken);
        var response = await _transport
            .SendJsonAsync(HttpMethod.Post, RefreshEndpoint, payload, null, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return response.Deserialize<RefreshResult>()
            ?? throw new EliCloudMalformedResponseException($"刷新响应不是合法 JSON：{RefreshEndpoint}", RefreshEndpoint, response.Body);
    }

    /// <summary>
    /// 读取当前用户信息（<c>GET|POST {base}/userinfo</c>）。
    /// </summary>
    /// <param name="accessToken">access token；**不能**传 id_token（其 <c>aud</c> 是 client_id，会被拒）。</param>
    /// <param name="usePost">用 POST 而不是 GET（OIDC 要求两种都支持；代理对 GET 缓存不当时用 POST）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<UserInfoResponse> GetUserInfoAsync(
        string accessToken,
        bool usePost = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);

        var response = await _transport
            .SendJsonAsync(usePost ? HttpMethod.Post : HttpMethod.Get, UserInfoEndpoint, null, accessToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return response.Deserialize<UserInfoResponse>()
            ?? throw new EliCloudMalformedResponseException($"UserInfo 响应不是合法 JSON：{UserInfoEndpoint}", UserInfoEndpoint, response.Body);
    }

    // ------------------------------------------------------------ 授权码 + PKCE

    /// <summary>
    /// 构造授权 URL（<c>GET {base}/authorize</c>），把它交给浏览器即可。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 公开客户端必须先在 <see cref="AuthorizationRequest.CodeChallenge"/> 里放
    /// <see cref="PkceCodeChallenge.CodeChallenge"/>，并保存同一个实例的
    /// <see cref="PkceCodeChallenge.CodeVerifier"/> 供换令牌时使用。
    /// </para>
    /// <para>
    /// <see cref="AuthorizationRequest.State"/> 与 <see cref="AuthorizationRequest.Nonce"/> 建议都提供：
    /// state 防 CSRF，nonce 让 <c>id_token</c> 能被校验为「本次请求的产物」。
    /// 用 <see cref="EliCloudRandom.CreateState"/> 与 <see cref="EliCloudRandom.CreateNonce"/> 生成。
    /// </para>
    /// </remarks>
    public Uri BuildAuthorizationUrl(AuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.ClientId);
        ArgumentException.ThrowIfNullOrEmpty(request.RedirectUri);

        if (EliCloudScopes.Split(request.Scope).Count == 0)
        {
            throw new ArgumentException("scope 不能为空；OIDC 授权码流程至少要含 openid。", nameof(request));
        }

        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", request.ClientId),
            new("redirect_uri", request.RedirectUri),
            new("scope", request.Scope),
        };

        AddIfPresent(query, "state", request.State);
        AddIfPresent(query, "nonce", request.Nonce);
        AddIfPresent(query, "code_challenge", request.CodeChallenge);
        if (!string.IsNullOrEmpty(request.CodeChallenge))
        {
            AddIfPresent(query, "code_challenge_method", request.CodeChallengeMethod);
        }

        AddIfPresent(query, "prompt", request.Prompt);

        return AppendQuery(AuthorizationEndpoint, query);
    }

    /// <summary>
    /// 用授权码换取令牌（<c>POST {base}/token</c>，<c>grant_type=authorization_code</c>）。
    /// </summary>
    /// <remarks>
    /// 服务端会逐条校验：码存在/未用过/未过期、码归属的 client_id、<c>redirect_uri</c> 逐字一致、
    /// PKCE 摘要匹配。任一条不过都是 <c>invalid_grant</c>。
    /// </remarks>
    public async Task<TokenResponse> ExchangeCodeAsync(
        AuthorizationCodeTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (form, authorization) = BuildClientAuthentication(
            request.ClientId, request.ClientSecret, request.AuthenticationMethod);

        form.Add(new("grant_type", "authorization_code"));
        form.Add(new("code", request.Code));
        form.Add(new("redirect_uri", request.RedirectUri));
        AddIfPresent(form, "code_verifier", request.CodeVerifier);

        return await SendTokenRequestAsync(form, authorization, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 刷新令牌（<c>POST {base}/token</c>，<c>grant_type=refresh_token</c>）。
    /// </summary>
    /// <remarks>
    /// 与私有 <see cref="RefreshAsync"/> 的差别：**强制客户端绑定** ——
    /// 令牌只能由签发它的那个 client_id 兑换，且 <c>scope</c> 只能收窄不能扩大。
    /// </remarks>
    public async Task<TokenResponse> RefreshTokenAsync(
        RefreshTokenGrantRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (form, authorization) = BuildClientAuthentication(
            request.ClientId, request.ClientSecret, request.AuthenticationMethod);

        form.Add(new("grant_type", "refresh_token"));
        form.Add(new("refresh_token", request.RefreshToken));
        AddIfPresent(form, "scope", request.Scope);

        return await SendTokenRequestAsync(form, authorization, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------- 设备授权（CLI）

    /// <summary>
    /// 设备流程第一步（<c>POST {base}/device_authorization</c>）：拿到设备码与人读短码。
    /// </summary>
    /// <remarks>
    /// 把 <see cref="DeviceAuthorizationResponse.VerificationUriComplete"/> 显示给用户、
    /// 并按 <see cref="DeviceAuthorizationResponse.Interval"/> 轮询
    /// <see cref="PollDeviceCodeAsync"/>（或直接用 <see cref="DeviceCodeFlow"/> 走完整个流程）。
    /// </remarks>
    public async Task<DeviceAuthorizationResponse> StartDeviceAuthorizationAsync(
        string clientId,
        string scope,
        string? clientSecret = null,
        ClientAuthenticationMethod? authenticationMethod = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentException.ThrowIfNullOrEmpty(scope);

        var (form, authorization) = BuildClientAuthentication(clientId, clientSecret, authenticationMethod);
        form.Add(new("scope", scope));

        var response = await _transport
            .SendFormAsync(HttpMethod.Post, DeviceAuthorizationEndpoint, form, authorization, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return response.Deserialize<DeviceAuthorizationResponse>()
            ?? throw new EliCloudMalformedResponseException(
                $"设备授权响应不是合法 JSON：{DeviceAuthorizationEndpoint}", DeviceAuthorizationEndpoint, response.Body);
    }

    /// <summary>
    /// 设备流程轮询（<c>POST {base}/token</c>，<c>grant_type=device_code</c>）。
    /// </summary>
    /// <remarks>
    /// <b>本方法不抛「用户还没确认」这类协议错误</b>：<c>authorization_pending</c> 与
    /// <c>slow_down</c> 是 RFC 8628 定义的正常轮询状态，用异常表达既慢又难写。
    /// 它们会体现在返回值的 <see cref="DeviceCodePollResult.Error"/> 上。
    /// 只有真正的异常（网络失败、5xx、无法识别的 4xx）才会抛。
    /// </remarks>
    public async Task<DeviceCodePollResult> PollDeviceCodeAsync(
        string deviceCode,
        string clientId,
        string? clientSecret = null,
        ClientAuthenticationMethod? authenticationMethod = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceCode);
        ArgumentException.ThrowIfNullOrEmpty(clientId);

        var (form, authorization) = BuildClientAuthentication(clientId, clientSecret, authenticationMethod);
        form.Add(new("grant_type", EliCloudGrantTypes.DeviceCode));
        form.Add(new("device_code", deviceCode));

        var response = await _transport
            .SendFormAsync(HttpMethod.Post, TokenEndpoint, form, authorization, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsSuccess)
        {
            var tokens = response.Deserialize<TokenResponse>()
                ?? throw new EliCloudMalformedResponseException(
                    $"令牌响应不是合法 JSON：{TokenEndpoint}", TokenEndpoint, response.Body);
            return DeviceCodePollResult.Success(tokens);
        }

        if (DeviceCodePollResult.IsProtocolError(response.ErrorCode))
        {
            return DeviceCodePollResult.Failure(response.ErrorCode!, response.Error?.ErrorDescription);
        }

        // 其余情况（5xx、无法识别的 4xx、非 JSON 响应）说明不是「流程还没走完」，而是真的出错了。
        response.EnsureSuccess();
        throw new EliCloudMalformedResponseException(
            $"设备码轮询遇到无法识别的错误响应：HTTP {response.StatusCode}", TokenEndpoint, response.Body);
    }

    // ------------------------------------------------------------------ 登出

    /// <summary>
    /// 构造 RP-Initiated Logout 的 URL（<c>GET {base}/logout</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 登出是**浏览器跳转语义**，不是 Bearer 调用：它会清会话 cookie、撤销该会话派生的
    /// refresh 链，然后按注册值决定是否回跳。所以这里只负责拼 URL，由前端做跳转。
    /// </para>
    /// <para>
    /// <see cref="LogoutRequest.PostLogoutRedirectUri"/> 必须在客户端注册的
    /// <c>post_logout_redirect_uris</c> 里，否则服务端**不会跳转**（防开放重定向）。
    /// </para>
    /// </remarks>
    public Uri BuildLogoutUrl(LogoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var query = new List<KeyValuePair<string, string>>();
        AddIfPresent(query, "post_logout_redirect_uri", request.PostLogoutRedirectUri);
        AddIfPresent(query, "id_token_hint", request.IdTokenHint);
        AddIfPresent(query, "client_id", request.ClientId);
        AddIfPresent(query, "state", request.State);

        return AppendQuery(EndSessionEndpoint, query);
    }

    // ---------------------------------------------------------------- 内部实现

    private async Task<TokenResponse> SendTokenRequestAsync(
        List<KeyValuePair<string, string>> form,
        string? authorization,
        CancellationToken cancellationToken)
    {
        var response = await _transport
            .SendFormAsync(HttpMethod.Post, TokenEndpoint, form, authorization, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return response.Deserialize<TokenResponse>()
            ?? throw new EliCloudMalformedResponseException(
                $"令牌响应不是合法 JSON：{TokenEndpoint}", TokenEndpoint, response.Body);
    }

    /// <summary>
    /// 组装客户端认证。三选一，且**不混用**：
    /// 同时用 Basic 与表单字段会被服务端按 RFC 6749 §2.3.1 判为 <c>invalid_request</c>。
    /// </summary>
    private static (List<KeyValuePair<string, string>> Form, string? Authorization) BuildClientAuthentication(
        string clientId,
        string? clientSecret,
        ClientAuthenticationMethod? method)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);

        var effective = method ?? (string.IsNullOrEmpty(clientSecret)
            ? ClientAuthenticationMethod.None
            : ClientAuthenticationMethod.ClientSecretBasic);

        var form = new List<KeyValuePair<string, string>>();
        switch (effective)
        {
            case ClientAuthenticationMethod.None:
                if (!string.IsNullOrEmpty(clientSecret))
                {
                    throw new ArgumentException(
                        "认证方式为 none（公开客户端）时不应提供 client_secret。", nameof(method));
                }

                form.Add(new("client_id", clientId));
                return (form, null);

            case ClientAuthenticationMethod.ClientSecretBasic:
                RequireSecret(clientSecret, nameof(method));
                return (form, EliCloudTransport.BuildBasicAuthorization(clientId, clientSecret!));

            case ClientAuthenticationMethod.ClientSecretPost:
                RequireSecret(clientSecret, nameof(method));
                form.Add(new("client_id", clientId));
                form.Add(new("client_secret", clientSecret!));
                return (form, null);

            default:
                throw new ArgumentOutOfRangeException(nameof(method), effective, "未知的客户端认证方式。");
        }
    }

    private static void RequireSecret(string? clientSecret, string parameterName)
    {
        if (string.IsNullOrEmpty(clientSecret))
        {
            throw new ArgumentException("该认证方式需要 client_secret。", parameterName);
        }
    }

    private static void AddIfPresent(List<KeyValuePair<string, string>> target, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            target.Add(new(key, value));
        }
    }

    /// <summary>
    /// 拼查询串。编码规则集中在 <see cref="EliCloudQuery"/>，避免授权 URL 与登出 URL
    /// 各自实现一份、慢慢漂移出不一致的转义行为。
    /// </summary>
    private static Uri AppendQuery(Uri endpoint, List<KeyValuePair<string, string>> query)
        => EliCloudQuery.Append(
            endpoint,
            query.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)));

    // ------------------------------------------------------------- 请求体模型
    // 字段名靠 EliCloudJson 的 snake_case 策略统一映射，这里不逐个标 [JsonPropertyName]：
    // 策略式映射是整体一致的，逐字段标注则漏标一个就静默失配。

    private sealed record RegisterRequest(string Username, string Password, string? Email);

    private sealed record LoginRequest(string Username, string Password);

    private sealed record RefreshRequest(string RefreshToken);

    private sealed class RegisterEnvelope
    {
        public EliCloudUser? User { get; init; }
    }
}

/// <summary>设备流程轮询的一次结果。</summary>
public sealed class DeviceCodePollResult
{
    private DeviceCodePollResult(bool isSuccess, TokenResponse? tokens, string? error, string? errorDescription)
    {
        IsSuccess = isSuccess;
        Tokens = tokens;
        Error = error;
        ErrorDescription = errorDescription;
    }

    /// <summary>是否已经拿到令牌。</summary>
    public bool IsSuccess { get; }

    /// <summary>成功时的令牌组。</summary>
    public TokenResponse? Tokens { get; }

    /// <summary>失败时的协议错误码；<c>null</c> 表示成功。</summary>
    public string? Error { get; }

    /// <summary>人类可读的说明。</summary>
    public string? ErrorDescription { get; }

    /// <summary>用户还没确认，应继续按 interval 轮询。</summary>
    public bool IsPending => Error == EliCloudErrorCodes.AuthorizationPending;

    /// <summary>轮询过快，应把 interval +5 秒（服务端上限 60 秒）后重试。</summary>
    public bool IsSlowDown => Error == EliCloudErrorCodes.SlowDown;

    /// <summary>流程已终结（用户拒绝 / 设备码过期 / 设备码已被兑换），不应再轮询。</summary>
    public bool IsTerminal => Error is EliCloudErrorCodes.AccessDenied
        or EliCloudErrorCodes.ExpiredToken
        or EliCloudErrorCodes.InvalidGrant;

    internal static DeviceCodePollResult Success(TokenResponse tokens) => new(true, tokens, null, null);

    internal static DeviceCodePollResult Failure(string error, string? description) => new(false, null, error, description);

    internal static bool IsProtocolError(string? error) => error is
        EliCloudErrorCodes.AuthorizationPending
        or EliCloudErrorCodes.SlowDown
        or EliCloudErrorCodes.AccessDenied
        or EliCloudErrorCodes.ExpiredToken
        or EliCloudErrorCodes.InvalidGrant;
}

/// <summary>平台支持的 grant_type 常量。</summary>
public static class EliCloudGrantTypes
{
    /// <summary>授权码。</summary>
    public const string AuthorizationCode = "authorization_code";

    /// <summary>刷新令牌。</summary>
    public const string RefreshToken = "refresh_token";

    /// <summary>设备码（RFC 8628）。</summary>
    public const string DeviceCode = "urn:ietf:params:oauth:grant-type:device_code";
}
