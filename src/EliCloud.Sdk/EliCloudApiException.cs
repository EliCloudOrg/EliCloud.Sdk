namespace EliCloud.Sdk;

/// <summary>
/// 平台统一的错误码。
/// </summary>
/// <remarks>
/// 错误体在所有服务里都是同一形状（<c>docs/architecture.md</c> §5）：
/// <code>{ "error": "invalid_grant", "error_description": "人类可读的说明" }</code>
/// 因此调用方应当<b>按 <see cref="EliCloudApiException.Error"/> 分支</b>，
/// 而不是按 HTTP 状态码或 <c>error_description</c> 文案分支 —— 后者是给人看的，随时可能改。
/// </remarks>
public static class EliCloudErrorCodes
{
    // ---------------------------------------------------------------- 通用
    /// <summary>请求本身不合法（参数缺失、字段未知、格式错误）。</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>令牌缺失/过期/签名错/iss 或 aud 不符。业务服务用它拒绝 <c>id_token</c> 冒充 access token。</summary>
    public const string InvalidToken = "invalid_token";

    /// <summary>身份有效但权限不足（管理接口令牌错误等）。</summary>
    public const string Forbidden = "forbidden";

    /// <summary>令牌有效但缺少该服务要求的 scope（如 <c>mc:whitelist</c>）。</summary>
    public const string InsufficientScope = "insufficient_scope";

    /// <summary>资源不存在，**或存在但不属于当前用户**（服务端刻意不区分，避免泄露存在性）。</summary>
    public const string NotFound = "not_found";

    /// <summary>触发限流；通常会带 <c>Retry-After</c>。</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>服务端内部错误。</summary>
    public const string ServerError = "server_error";

    // ------------------------------------------------------------ OAuth/OIDC
    /// <summary>客户端认证失败（secret 错、认证方式与注册值不一致）。</summary>
    public const string InvalidClient = "invalid_client";

    /// <summary>授权码 / refresh token / 设备码不可用（不存在、已用、过期、绑定不匹配）。</summary>
    public const string InvalidGrant = "invalid_grant";

    /// <summary>该客户端未被允许使用这个 grant_type。</summary>
    public const string UnauthorizedClient = "unauthorized_client";

    /// <summary>服务端不支持这个 grant_type。</summary>
    public const string UnsupportedGrantType = "unsupported_grant_type";

    /// <summary>请求的 scope 未注册或超出客户端的 <c>allowed_scopes</c>；刷新时也用于拒绝提权。</summary>
    public const string InvalidScope = "invalid_scope";

    /// <summary>设备流程：用户尚未确认。客户端应继续按 interval 轮询。</summary>
    public const string AuthorizationPending = "authorization_pending";

    /// <summary>设备流程：轮询过快。客户端应把 interval +5 秒（服务端上限 60 秒）后重试。</summary>
    public const string SlowDown = "slow_down";

    /// <summary>用户拒绝，或授权码流程被拒。</summary>
    public const string AccessDenied = "access_denied";

    /// <summary>设备码已过期，流程终结。</summary>
    public const string ExpiredToken = "expired_token";

    /// <summary><c>prompt=none</c> 但没有有效会话；客户端应改走交互式登录。</summary>
    public const string LoginRequired = "login_required";

    /// <summary><c>response_type</c> 不是受支持的值（本平台只支持 <c>code</c>）。</summary>
    public const string UnsupportedResponseType = "unsupported_response_type";

    // -------------------------------------------------------------- 私有端点
    /// <summary>注册：用户名已存在。</summary>
    public const string UserExists = "user_exists";

    /// <summary>注册：邮箱已存在。</summary>
    public const string EmailExists = "email_exists";

    /// <summary>注册：平台关闭了自助注册。</summary>
    public const string RegistrationDisabled = "registration_disabled";

    /// <summary>限流（SSO 私有端点的写法，语义等同 <see cref="RateLimited"/>）。</summary>
    public const string TooManyRequests = "too_many_requests";

    // ---------------------------------------------------------- MC 白名单服务
    /// <summary>MC 白名单：该 MC 用户名已被别的账号绑定，或已被管理员手工加入白名单。</summary>
    public const string NameTaken = "name_taken";

    /// <summary>MC 白名单：本账号已达绑定名额上限（默认 2 个）。</summary>
    public const string QuotaExceeded = "quota_exceeded";

    /// <summary>MC 白名单：RCON 不可达，或写入未被回读确认（服务端**不会**落库）。</summary>
    public const string RconUnavailable = "rcon_unavailable";
}

/// <summary>
/// EliCloud 服务返回的结构化错误。
/// </summary>
/// <param name="Error">错误码，见 <see cref="EliCloudErrorCodes"/>。</param>
/// <param name="ErrorDescription">人类可读说明；**不要**用它做逻辑分支。</param>
public readonly record struct EliCloudError(string? Error, string? ErrorDescription)
{
    /// <summary>把错误渲染成便于日志与异常消息的单行文本。</summary>
    public override string ToString()
        => Error is null and null
            ? "(无错误体)"
            : $"{Error}: {ErrorDescription}";
}

/// <summary>
/// EliCloud 服务返回了非 2xx 响应时抛出的异常。
/// </summary>
/// <remarks>
/// 命名与 <see cref="HttpRequestException"/> 保持一致，但语义更窄：**只要拿到了服务端的结构化错误，
/// 就是本异常**。网络层失败（连不上、TLS 失败、超时）仍然是 <see cref="HttpRequestException"/> /
/// <see cref="TaskCanceledException"/>。
/// </remarks>
public sealed class EliCloudApiException : Exception
{
    /// <summary>创建异常。</summary>
    public EliCloudApiException(
        int statusCode,
        EliCloudError error,
        Uri? requestUri = null,
        string? responseBody = null,
        TimeSpan? retryAfter = null)
        : base(BuildMessage(statusCode, error, requestUri))
    {
        StatusCode = statusCode;
        Error = error.Error;
        ErrorDescription = error.ErrorDescription;
        RequestUri = requestUri;
        ResponseBody = responseBody;
        RetryAfter = retryAfter;
    }

    /// <summary>HTTP 状态码。</summary>
    public int StatusCode { get; }

    /// <summary>平台错误码（可能为 <c>null</c>：响应体不是平台的错误结构时）。</summary>
    public string? Error { get; }

    /// <summary>人类可读的说明。</summary>
    public string? ErrorDescription { get; }

    /// <summary>出错的请求地址。</summary>
    public Uri? RequestUri { get; }

    /// <summary>原始响应体；仅用于诊断，**不要**在日志里原样打印（可能含敏感信息）。</summary>
    public string? ResponseBody { get; }

    /// <summary><c>Retry-After</c> 头解析出的等待时长（限流场景）。</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>是否为「缺少 scope」——业务服务最常见的可编程处理分支。</summary>
    public bool IsInsufficientScope => Error == EliCloudErrorCodes.InsufficientScope;

    /// <summary>是否为「令牌无效」——通常意味着需要重新登录或刷新。</summary>
    public bool IsInvalidToken => Error == EliCloudErrorCodes.InvalidToken;

    /// <summary>是否为限流。</summary>
    public bool IsRateLimited =>
        Error is EliCloudErrorCodes.RateLimited or EliCloudErrorCodes.TooManyRequests || StatusCode == 429;

    private static string BuildMessage(int statusCode, EliCloudError error, Uri? requestUri)
    {
        var target = requestUri is null ? string.Empty : $" ({requestUri})";
        return $"EliCloud 请求失败：HTTP {statusCode}{target} → {error}";
    }
}
