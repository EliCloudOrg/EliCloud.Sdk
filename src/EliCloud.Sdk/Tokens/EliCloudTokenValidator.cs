using System.Security.Claims;
using EliCloud.Sdk.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EliCloud.Sdk.Tokens;

/// <summary>校验结果。</summary>
/// <remarks>
/// 用返回值而不是异常表达「令牌无效」：无效令牌是业务服务的**正常输入**之一
/// （客户端可能没带、可能过期），每次都抛异常既慢又难写；
/// 而服务端要把它映射成 401/403，返回值更直接。
/// </remarks>
public sealed class EliCloudTokenValidationResult
{
    private EliCloudTokenValidationResult(
        bool isValid,
        ClaimsPrincipal? principal,
        JsonWebToken? token,
        string? errorCode,
        string? errorDescription)
    {
        IsValid = isValid;
        Principal = principal;
        Token = token;
        ErrorCode = errorCode;
        ErrorDescription = errorDescription;
    }

    /// <summary>是否通过全部校验。</summary>
    public bool IsValid { get; }

    /// <summary>通过时的声明主体。</summary>
    public ClaimsPrincipal? Principal { get; }

    /// <summary>通过时的原始令牌（可读 claim）。</summary>
    public JsonWebToken? Token { get; }

    /// <summary>
    /// 失败原因码，取值与平台一致：
    /// <c>invalid_token</c>（401）或 <c>insufficient_scope</c>（403）。
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>人类可读的失败说明。</summary>
    public string? ErrorDescription { get; }

    /// <summary>用户 ID（<c>sub</c>）——业务数据隔离的唯一依据。</summary>
    public string? Subject => Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
        ?? Principal?.FindFirst("sub")?.Value;

    /// <summary>用户名（<c>preferred_username</c>，兼容早期的 <c>username</c>）。</summary>
    public string? Username =>
        Principal?.FindFirst("preferred_username")?.Value
        ?? Principal?.FindFirst("username")?.Value;

    /// <summary><c>client_id</c> claim，用于审计是哪个客户端拿到的令牌。</summary>
    public string? ClientId => Principal?.FindFirst("client_id")?.Value;

    /// <summary>令牌里的 scope 列表。</summary>
    public IReadOnlyList<string> Scopes => EliCloudScopes.Split(Principal?.FindFirst("scope")?.Value);

    /// <summary>令牌是否包含某个 scope（**整词匹配**，不是子串匹配）。</summary>
    public bool HasScope(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);

    internal static EliCloudTokenValidationResult Success(ClaimsPrincipal principal, JsonWebToken token)
        => new(true, principal, token, null, null);

    internal static EliCloudTokenValidationResult Failure(string errorCode, string errorDescription)
        => new(false, null, null, errorCode, errorDescription);
}

/// <summary>
/// EliCloud access token 校验器：业务服务（资源服务）用它验签并取出 <c>sub</c>。
/// </summary>
/// <remarks>
/// <para>
/// 它按平台契约逐条校验：算法白名单只有 <c>RS256</c>、<c>iss</c> 逐字相等、
/// <c>aud</c> 必须是 <c>elicloud-services</c>、<c>exp</c>/<c>nbf</c> 在容忍窗口内、
/// 必需的 scope 必须存在（<c>docs/mc.md</c> §6.1 那张表）。
/// </para>
/// <para>
/// <b>为什么 <c>aud</c> 校验是安全防线而不是形式</b>：<c>id_token</c> 的 <c>aud</c> 是 client_id，
/// 只有校验 <c>aud</c> 才能把它挡在业务 API 之外。关掉它等于让客户端拿「身份证明」当「通行证」。
/// </para>
/// <para>
/// 需要 ASP.NET Core 集成时用 <c>EliCloud.Sdk.AspNetCore</c> 里的
/// <c>AddEliCloudAuthentication()</c>，它内部就是本校验器。
/// </para>
/// </remarks>
public sealed class EliCloudTokenValidator : IDisposable
{
    private readonly EliCloudTokenValidationOptions _options;
    private readonly EliCloudJwksProvider _keys;
    private readonly ILogger? _logger;
    private readonly bool _ownsProvider;

    /// <summary>创建校验器。</summary>
    /// <param name="httpClient">用于拉取 JWKS 的 HTTP 客户端。</param>
    /// <param name="options">校验配置。</param>
    /// <param name="logger">可选日志。</param>
    /// <param name="jwksProvider">可复用的 JWKS 提供者；不传则内部自建。</param>
    public EliCloudTokenValidator(
        HttpClient httpClient,
        EliCloudTokenValidationOptions options,
        ILogger<EliCloudTokenValidator>? logger = null,
        EliCloudJwksProvider? jwksProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _logger = logger;
        _ownsProvider = jwksProvider is null;
        _keys = jwksProvider ?? new EliCloudJwksProvider(httpClient, options, null);
    }

    /// <summary>创建校验器（配置来自 DI 选项系统）。</summary>
    public EliCloudTokenValidator(
        HttpClient httpClient,
        IOptions<EliCloudTokenValidationOptions> options,
        ILogger<EliCloudTokenValidator>? logger = null,
        EliCloudJwksProvider? jwksProvider = null)
        : this(httpClient, (options ?? throw new ArgumentNullException(nameof(options))).Value, logger, jwksProvider)
    {
    }

    /// <summary>期望的 issuer。</summary>
    public string? Issuer => _options.Issuer;

    /// <summary>期望的 audience。</summary>
    public string Audience => _options.Audience;

    /// <summary>令牌必须携带的 scope。</summary>
    public IReadOnlyList<string> RequiredScopes => _options.RequiredScopes;

    /// <summary>JWKS 地址。</summary>
    public Uri JwksUri => _keys.JwksUri;

    /// <summary>
    /// 校验一个 access token。**不抛异常**：任何失败都体现在返回值的
    /// <see cref="EliCloudTokenValidationResult.ErrorCode"/> 上。
    /// </summary>
    public async Task<EliCloudTokenValidationResult> ValidateAccessTokenAsync(
        string? token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return EliCloudTokenValidationResult.Failure(EliCloudErrorCodes.InvalidToken, "缺少访问令牌。");
        }

        JsonWebToken jwt;
        try
        {
            jwt = new JsonWebTokenHandler().ReadJsonWebToken(token);
        }
        catch (Exception exception) when (exception is ArgumentException or SecurityTokenException)
        {
            return EliCloudTokenValidationResult.Failure(EliCloudErrorCodes.InvalidToken, "令牌格式不正确。");
        }

        // 显式先判算法：即使在库层面也白名单限制，这里多一层，
        // 是为了让「拒绝对称算法 / alg:none」成为可读、可测试的一条明确规则。
        if (!_options.ValidAlgorithms.Contains(jwt.Alg, StringComparer.OrdinalIgnoreCase))
        {
            return EliCloudTokenValidationResult.Failure(
                EliCloudErrorCodes.InvalidToken,
                $"不接受的签名算法「{jwt.Alg}」，只允许 {string.Join('/', _options.ValidAlgorithms)}。");
        }

        SecurityKey? key;
        try
        {
            key = await _keys.ResolveAsync(jwt.Kid, cancellationToken).ConfigureAwait(false);
        }
        catch (EliCloudApiException exception)
        {
            _logger?.LogWarning(exception, "拉取 JWKS 失败：{JwksUri}", _keys.JwksUri);
            return EliCloudTokenValidationResult.Failure(EliCloudErrorCodes.InvalidToken, "无法获取签名公钥。");
        }

        if (key is null)
        {
            return EliCloudTokenValidationResult.Failure(
                EliCloudErrorCodes.InvalidToken,
                $"找不到 kid「{jwt.Kid}」对应的签名公钥。");
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = _options.ValidateIssuer,
            ValidIssuer = _options.Issuer,
            ValidateAudience = _options.ValidateAudience,
            ValidAudience = _options.Audience,
            ValidateLifetime = _options.ValidateLifetime,
            ClockSkew = _options.ClockSkew,
            IssuerSigningKey = key,
            TryAllIssuerSigningKeys = false,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidAlgorithms = _options.ValidAlgorithms,
        };

        var validation = await new JsonWebTokenHandler()
            .ValidateTokenAsync(token, parameters)
            .ConfigureAwait(false);

        if (!validation.IsValid)
        {
            var reason = validation.Exception?.Message ?? "令牌未通过校验。";
            return EliCloudTokenValidationResult.Failure(EliCloudErrorCodes.InvalidToken, reason);
        }

        var principal = new ClaimsPrincipal(validation.ClaimsIdentity);

        // scope 是授权边界：身份有效但权限不足要用 403 而不是 401，
        // 这样调用方能区分「重新登录」与「申请授权」（与 mc 服务的语义一致）。
        var granted = EliCloudScopes.Split(principal.FindFirst("scope")?.Value);
        foreach (var required in _options.RequiredScopes)
        {
            if (!granted.Contains(required, StringComparer.Ordinal))
            {
                return EliCloudTokenValidationResult.Failure(
                    EliCloudErrorCodes.InsufficientScope,
                    $"令牌缺少所需的 scope「{required}」。");
            }
        }

        return EliCloudTokenValidationResult.Success(principal, jwt);
    }

    /// <summary>便利方法：只关心「是否有效」时用它。</summary>
    public async Task<bool> IsValidAsync(string? token, CancellationToken cancellationToken = default)
        => (await ValidateAccessTokenAsync(token, cancellationToken).ConfigureAwait(false)).IsValid;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsProvider)
        {
            _keys.Dispose();
        }
    }
}
