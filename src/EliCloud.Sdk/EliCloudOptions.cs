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
/// <b><see cref="BaseAddress"/> 没有默认值</b>：平台入口属于部署信息，SDK 不替调用方决定
/// （早期版本给过一个具体地址作默认值，等于把某一次部署写死进程序集，已移除）。
/// 换环境——IP 阶段 → 域名阶段、测试 → 生产——只改这一处，路径前缀、请求体、响应体都不变。
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
    /// <para>
    /// <b>必须显式配置，没有默认值。</b>平台本身就不硬编码入口：issuer、jwks_uri、各端点 URL
    /// 都由服务端的环境变量派生，所以客户端也只能把它当成配置而不是常量。
    /// </para>
    /// <para>未配置就使用会抛 <see cref="InvalidOperationException"/>，异常信息里带正确写法。</para>
    /// </remarks>
    public Uri? BaseAddress { get; set; }

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

    /// <summary>SSO 的服务基址（派生结果，只读）。未配置入口且无覆盖时抛异常。</summary>
    public Uri SsoBaseAddress => SsoBaseAddressOverride ?? Combine(RequireBaseAddress(), AuthPathPrefix);

    /// <summary>MC 白名单服务的基址（派生结果，只读）。未配置入口且无覆盖时抛异常。</summary>
    public Uri McBaseAddress => McBaseAddressOverride ?? Combine(RequireBaseAddress(), McPathPrefix);

    /// <summary>平台核心 API 的基址（派生结果，只读）。未配置入口且无覆盖时抛异常。</summary>
    public Uri MainApiBaseAddress => MainApiBaseAddressOverride ?? Combine(RequireBaseAddress(), MainApiPathPrefix);

    /// <summary>校验配置是否可用；不合法时抛 <see cref="ArgumentException"/>。</summary>
    public void Validate()
    {
        if (BaseAddress is not { } baseAddress)
        {
            throw new ArgumentException(BaseAddressNotConfigured, nameof(BaseAddress));
        }

        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException($"BaseAddress 必须是绝对 URI，当前为「{baseAddress}」。", nameof(BaseAddress));
        }

        ValidatePrefix(AuthPathPrefix, nameof(AuthPathPrefix));
        ValidatePrefix(McPathPrefix, nameof(McPathPrefix));
        ValidatePrefix(MainApiPathPrefix, nameof(MainApiPathPrefix));

        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Timeout 必须为正数。", nameof(Timeout));
        }
    }

    /// <summary>取已配置的入口地址；未配置时抛出带正确写法的异常。</summary>
    internal Uri RequireBaseAddress() =>
        BaseAddress ?? throw new InvalidOperationException(BaseAddressNotConfigured);

    private const string BaseAddressNotConfigured =
        "必须配置 EliCloudOptions.BaseAddress（平台入口地址），SDK 不提供默认入口。"
        + "例如：new EliCloudOptions { BaseAddress = new Uri(\"https://api.example.com\") }；"
        + "若只有某个服务部署在别处，也可以只设 SsoBaseAddressOverride / McBaseAddressOverride 之类的服务级覆盖。";

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
