namespace EliCloud.Sdk;

/// <summary>
/// EliCloud 客户端 SDK 的连接配置。
/// </summary>
/// <remarks>
/// <para>
/// 平台的核心约定是「一个入口主机 + 每个服务一个路径前缀」（<c>docs/architecture.md</c> §2.2）：
/// 网关按前缀把请求分派到各服务容器，服务内部看不到这个前缀。因此默认只需要配置
/// <see cref="BaseAddress"/>，各服务地址由它加前缀派生。
/// </para>
/// <para>
/// <b>阶段切换（IP → 域名）只改这一处的 <see cref="BaseAddress"/></b>：
/// 现在是 <c>https://api.example.com</c>，域名可用后是 <c>https://api.example.com</c>。
/// 路径前缀、请求体、响应体都不变。
/// </para>
/// <para>
/// 某个服务将来被拆到独立主机时（例如 <c>mc.example.com</c>），
/// 用 <see cref="SsoBaseAddressOverride"/> 之类的覆盖属性单点覆盖即可，
/// 不必为了一个服务改掉全局 <see cref="BaseAddress"/>。
/// </para>
/// </remarks>
public sealed class EliCloudOptions
{
    /// <summary>
    /// 平台入口地址（含 scheme，可含基础路径；**不要**带服务前缀，例如不要写成 <c>https://host/auth</c>）。
    /// </summary>
    /// <remarks>
    /// 平台**没有硬编码域名的设计**：issuer、jwks_uri、各端点 URL 都由服务端的环境变量派生，
    /// 所以客户端也必须把它当成配置而不是常量。默认值是当前 IP 阶段的实际入口。
    /// </remarks>
    public Uri BaseAddress { get; set; } = new("https://api.example.com");

    /// <summary>SSO 服务的路径前缀，默认 <c>/auth</c>。</summary>
    public string AuthPathPrefix { get; set; } = EliCloudConstants.AuthPathPrefix;

    /// <summary>MC 白名单服务的路径前缀，默认 <c>/mc</c>。</summary>
    public string McPathPrefix { get; set; } = EliCloudConstants.McPathPrefix;

    /// <summary>平台核心 API（main-api，规划中）的路径前缀，默认 <c>/core</c>。</summary>
    public string MainApiPathPrefix { get; set; } = EliCloudConstants.MainApiPathPrefix;

    /// <summary>显式指定 SSO 的服务基址，覆盖「<see cref="BaseAddress"/> + 前缀」的派生结果。</summary>
    public Uri? SsoBaseAddressOverride { get; set; }

    /// <summary>显式指定 MC 白名单服务的服务基址。</summary>
    public Uri? McBaseAddressOverride { get; set; }

    /// <summary>显式指定平台核心 API 的服务基址。</summary>
    public Uri? MainApiBaseAddressOverride { get; set; }

    /// <summary>
    /// 单次 HTTP 请求的超时。默认 30 秒。
    /// </summary>
    /// <remarks>
    /// 注意这是 <see cref="HttpClient.Timeout"/> 级别的整体超时；
    /// 设备码轮询这类长流程请用 <see cref="System.Threading.CancellationToken"/> 控制，不要靠调大超时。
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary><c>User-Agent</c> 头；<c>null</c> 表示不设置。默认带 SDK 名称与版本，便于服务端归因问题。</summary>
    public string? UserAgent { get; set; } = EliCloudConstants.UserAgent;

    /// <summary>SSO 的服务基址（派生结果，只读）。</summary>
    public Uri SsoBaseAddress => SsoBaseAddressOverride ?? Combine(BaseAddress, AuthPathPrefix);

    /// <summary>MC 白名单服务的基址（派生结果，只读）。</summary>
    public Uri McBaseAddress => McBaseAddressOverride ?? Combine(BaseAddress, McPathPrefix);

    /// <summary>平台核心 API 的基址（派生结果，只读）。</summary>
    public Uri MainApiBaseAddress => MainApiBaseAddressOverride ?? Combine(BaseAddress, MainApiPathPrefix);

    /// <summary>校验配置是否可用；不合法时抛 <see cref="ArgumentException"/>。</summary>
    public void Validate()
    {
        if (!BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException($"BaseAddress 必须是绝对 URI，当前为「{BaseAddress}」。", nameof(BaseAddress));
        }

        ValidatePrefix(AuthPathPrefix, nameof(AuthPathPrefix));
        ValidatePrefix(McPathPrefix, nameof(McPathPrefix));
        ValidatePrefix(MainApiPathPrefix, nameof(MainApiPathPrefix));

        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Timeout 必须为正数。", nameof(Timeout));
        }
    }

    private static void ValidatePrefix(string prefix, string name)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException($"{name} 不能为空；若该服务不启用可留空串以外的占位。", name);
        }

        if (!prefix.StartsWith('/'))
        {
            throw new ArgumentException($"{name} 必须以「/」开头，当前为「{prefix}」。", name);
        }
    }

    /// <summary>把「入口地址 + 服务前缀」拼成一个带结尾斜杠的服务基址。</summary>
    internal static Uri Combine(Uri baseAddress, string pathPrefix)
    {
        var prefix = pathPrefix.Trim();
        if (!prefix.StartsWith('/'))
        {
            prefix = "/" + prefix;
        }

        prefix = prefix.TrimEnd('/');

        var authority = baseAddress.GetLeftPart(UriPartial.Authority);
        var basePath = baseAddress.AbsolutePath.TrimEnd('/');
        return new Uri(authority + basePath + prefix + "/", UriKind.Absolute);
    }
}
