namespace EliCloud.Sdk.Tokens;

/// <summary>
/// 业务服务校验 EliCloud access token 时的配置。
/// </summary>
/// <remarks>
/// <para>
/// 对应 Python 侧业务服务的环境变量：<c>SSO_ISSUER</c> → <see cref="Issuer"/>、
/// <c>SSO_JWKS_URL</c> → <see cref="JwksUri"/>、<c>JWT_AUDIENCE</c> → <see cref="Audience"/>、
/// <c>REQUIRED_SCOPE</c> → <see cref="RequiredScopes"/>（见 <c>docs/mc.md</c> §3.3）。
/// </para>
/// <para>
/// <b><see cref="Issuer"/> 是最容易配错的一项</b>：它必须与服务端 <c>PUBLIC_BASE_URL</c>
/// 逐字相同（当前是 <c>https://api.example.com/auth</c>，注意带 <c>/auth</c>）。
/// 差一个字符就会「所有令牌验签失败」，而现象只是 401，很难查。
/// </para>
/// </remarks>
public sealed class EliCloudTokenValidationOptions
{
    /// <summary>
    /// 期望的 <c>iss</c>，例如 <c>https://api.example.com/auth</c>。
    /// 域名阶段改成 <c>https://api.example.com/auth</c>，代码不动。
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// JWKS 地址；留空时按固定约定派生为 <c>{Issuer}/.well-known/jwks.json</c>。
    /// </summary>
    public string? JwksUri { get; set; }

    /// <summary>
    /// 期望的 <c>aud</c>。默认 <c>elicloud-services</c> —— 保持默认就能顺手拒掉
    /// <c>id_token</c>（它的 <c>aud</c> 是 client_id），这是平台刻意留的防线。
    /// </summary>
    public string Audience { get; set; } = EliCloudConstants.Audience;

    /// <summary>
    /// 令牌必须携带的 scope（整词匹配，如 <c>mc:whitelist</c>）。
    /// 留空表示不做 scope 校验；生产环境不建议留空。
    /// </summary>
    public IReadOnlyList<string> RequiredScopes { get; set; } = [];

    /// <summary>是否校验 <c>iss</c>。生产环境必须为 <c>true</c>。</summary>
    public bool ValidateIssuer { get; set; } = true;

    /// <summary>是否校验 <c>aud</c>。**关掉就等于允许 id_token 冒充 access token**，不要关。</summary>
    public bool ValidateAudience { get; set; } = true;

    /// <summary>是否校验 <c>exp</c>/<c>nbf</c>。</summary>
    public bool ValidateLifetime { get; set; } = true;

    /// <summary>时钟偏移容忍度，默认 30 秒（与平台业务服务一致）。</summary>
    public TimeSpan ClockSkew { get; set; } = EliCloudConstants.DefaultClockSkew;

    /// <summary>JWKS 缓存时长；服务端下发的 <c>Cache-Control: max-age</c> 优先于它。</summary>
    public TimeSpan JwksCacheDuration { get; set; } = EliCloudConstants.DefaultJwksCacheDuration;

    /// <summary>拉取 JWKS 的超时。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 允许的签名算法白名单，默认只有 <c>RS256</c>。
    /// 保持默认即可拒绝 <c>HS256</c> 与 <c>alg:none</c>；**不要**为了兼容加对称算法。
    /// </summary>
    public IReadOnlyList<string> ValidAlgorithms { get; set; } = EliCloudConstants.SupportedAlgorithms;

    /// <summary>校验配置是否自洽。</summary>
    public void Validate()
    {
        if (ValidateIssuer && string.IsNullOrWhiteSpace(Issuer))
        {
            throw new ArgumentException(
                "ValidateIssuer 为 true 时必须配置 Issuer（如 https://api.example.com/auth）。", nameof(Issuer));
        }

        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Timeout 必须为正数。", nameof(Timeout));
        }

        if (ValidAlgorithms.Count == 0)
        {
            throw new ArgumentException("ValidAlgorithms 不能为空。", nameof(ValidAlgorithms));
        }
    }

    /// <summary>解析出 JWKS 地址：显式配置优先，否则由 issuer 派生（与平台约定一致）。</summary>
    public Uri ResolveJwksUri()
    {
        if (!string.IsNullOrWhiteSpace(JwksUri))
        {
            return new Uri(JwksUri, UriKind.Absolute);
        }

        if (string.IsNullOrWhiteSpace(Issuer))
        {
            throw new ArgumentException("必须配置 Issuer 或 JwksUri 之一。", nameof(JwksUri));
        }

        return new Uri(Issuer.TrimEnd('/') + "/.well-known/jwks.json", UriKind.Absolute);
    }
}
