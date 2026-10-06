using EliCloud.Sdk.Mc;
using EliCloud.Sdk.MainApi;
using EliCloud.Sdk.Sso;

namespace EliCloud.Sdk;

/// <summary>
/// 静态门面：一行配置，然后「按 id 取服务」。
/// </summary>
/// <remarks>
/// <para>
/// 这一层是**纯粹新增的便利层**：所有具体客户端（<c>SsoClient</c>、<c>McClient</c>…）
/// 的构造与语义一个字都没变，门面只是替你把
/// 「<c>new HttpClient</c> + <c>new EliCloudOptions</c> + <c>new XxxClient(...)</c>」这三步收起来。
/// 需要多环境并存、要做单元测试、或要接 DI 时，继续直接用实例 API 即可。
/// </para>
/// <para>典型用法：</para>
/// <code>
/// // 1) 一次配置（不配就用默认的当前 IP 阶段地址）
/// EliCloudService.Configure(options => options.BaseAddress = new Uri("https://api.example.com"));
///
/// // 2) 拿令牌
/// var login = await EliCloudService.Sso.LoginAsync("alice", "S3cret!pass");
///
/// // 3) 按服务名取服务，再用它自己的扩展方法拿到强类型客户端
/// var mc = EliCloudService.GetProvider("mc").AsMc();
///
/// // 4) 业务调用：令牌逐次传入，客户端本身无状态
/// var mine = await mc.ListMyNamesAsync(login.AccessToken);
/// </code>
/// <para>
/// <b>它管什么、不管什么</b>：门面只负责「地址 + HttpClient + 客户端实例」这一层，
/// 不管令牌缓存、不管重试策略、不管刷新时机 —— 那些都是有状态的东西，
/// 交给调用方或 <c>Microsoft.Extensions.Http</c> 那类设施更合适。
/// </para>
/// <para>
/// <b>静态状态的代价（请知情）</b>：一个进程只有一套默认配置。
/// 要同时连两个环境（例如 IP 阶段 + 域名阶段，或本地 mock + 生产）就必须回到实例 API。
/// </para>
/// </remarks>
public static class EliCloudService
{
    private static readonly Lock Gate = new();

    private static EliCloudOptions _options = new();
    private static HttpClient? _httpClient;
    private static bool _ownsHttpClient;
    private static Dictionary<string, Uri> _registered = new(StringComparer.Ordinal);

    /// <summary>当前生效的连接配置。</summary>
    /// <remarks>
    /// 返回的是门面正在使用的那一个实例。配置请在启动阶段用 <see cref="Configure(Action{EliCloudOptions})"/>
    /// 完成；运行期再去改这里的字段不会通知门面重建，行为不确定。
    /// </remarks>
    public static EliCloudOptions Options
    {
        get
        {
            lock (Gate)
            {
                return _options;
            }
        }
    }

    /// <summary>账号中心的客户端（等价于 <c>GetProvider("sso").AsSso()</c>）。</summary>
    public static SsoClient Sso => GetProvider(EliCloudServiceIds.Sso).AsSso();

    /// <summary>MC 服务器白名单的客户端（等价于 <c>GetProvider("mc").AsMc()</c>）。</summary>
    public static McClient Mc => GetProvider(EliCloudServiceIds.Mc).AsMc();

    /// <summary>平台核心 API 的客户端（等价于 <c>GetProvider("main-api").AsMainApi()</c>）。</summary>
    public static MainApiClient MainApi => GetProvider(EliCloudServiceIds.MainApi).AsMainApi();

    /// <summary>SDK 内置支持的服务名（基址由路径前缀派生，无需 <see cref="Register(string, string)"/>）。</summary>
    public static IReadOnlyList<string> BuiltInServiceIds { get; } =
    [
        EliCloudServiceIds.Sso,
        EliCloudServiceIds.Mc,
        EliCloudServiceIds.MainApi,
    ];

    /// <summary>当前可用的服务名：内置的三个 + 已登记的自定义名字。</summary>
    public static IReadOnlyCollection<string> RegisteredServiceIds
    {
        get
        {
            lock (Gate)
            {
                return [.. BuiltInServiceIds, .. _registered.Keys];
            }
        }
    }

    /// <summary>
    /// 配置连接方式。
    /// </summary>
    /// <param name="configure">对当前配置的修改动作（在原实例上改，便于只改一处）。</param>
    /// <exception cref="ArgumentException">配置不合法（地址不是绝对 URI、前缀不以「/」开头等）。</exception>
    /// <remarks>
    /// 配置完会重建 HttpClient（旧的自建客户端会被释放），因此请在启动阶段调用。
    /// </remarks>
    public static void Configure(Action<EliCloudOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        lock (Gate)
        {
            configure(_options);
            _options.Validate();
            RebuildHttpClient();
        }
    }

    /// <summary>用一整份配置替换当前配置。</summary>
    /// <param name="options">新的连接配置。</param>
    public static void Configure(EliCloudOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        lock (Gate)
        {
            _options = options;
            RebuildHttpClient();
        }
    }

    /// <summary>
    /// 指定门面使用的外部 <see cref="HttpClient"/>（自定义代理、证书、日志处理器、测试替身）。
    /// </summary>
    /// <param name="httpClient">要使用的客户端；门面**不**负责释放它。</param>
    public static void UseHttpClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        lock (Gate)
        {
            ClearHttpClient();
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
    }

    /// <summary>
    /// 按服务名取一个服务句柄，再用该服务的 <c>AsXxx()</c> 扩展方法拿到强类型客户端。
    /// </summary>
    /// <param name="serviceId">
    /// 服务名，见 <see cref="EliCloudServiceIds"/>（<c>sso</c>、<c>mc</c>、<c>main-api</c>…），
    /// 或你 <see cref="Register(string, string)"/> 登记过的自定义名字。
    /// 按**原样精确匹配**：区分大小写，不接受别名或带斜杠的前缀写法。
    /// </param>
    /// <exception cref="KeyNotFoundException">
    /// 这个名字既不是内置服务、也没有 <see cref="Register(string, string)"/> 登记过。
    /// </exception>
    public static IEliCloudService GetProvider(string serviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        lock (Gate)
        {
            var baseAddress = ResolveBaseAddress(serviceId);
            return new ServiceHandle(serviceId, baseAddress, GetHttpClient(), _options);
        }
    }

    /// <summary>
    /// 登记一个自定义服务的挂载位置（相对平台入口的路径前缀）。
    /// </summary>
    /// <param name="serviceId">服务名，如 <c>pdf-decrypt</c>。</param>
    /// <param name="pathPrefix">对外路径前缀，如 <c>/pdf-decrypt</c>。</param>
    /// <remarks>
    /// 登记之后 <see cref="GetProvider"/> 就能解析它的地址；具体客户端仍由该服务自己的
    /// 命名空间里的 <c>AsXxx()</c> 扩展提供 —— 门面不认识任何业务类型。
    /// </remarks>
    public static void Register(string serviceId, string pathPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathPrefix);
        if (!pathPrefix.StartsWith('/'))
        {
            throw new ArgumentException($"pathPrefix 必须以「/」开头，当前为「{pathPrefix}」。", nameof(pathPrefix));
        }

        Register(serviceId, EliCloudOptions.Combine(Options.BaseAddress, pathPrefix));
    }

    /// <summary>
    /// 登记一个自定义服务的绝对基址（该服务不在平台入口的路径前缀下时用它）。
    /// </summary>
    /// <param name="serviceId">服务名。</param>
    /// <param name="baseAddress">服务的绝对基址，如 <c>https://mc.example.com/</c>。</param>
    public static void Register(string serviceId, Uri baseAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException($"baseAddress 必须是绝对 URI，当前为「{baseAddress}」。", nameof(baseAddress));
        }

        lock (Gate)
        {
            _registered[serviceId] = baseAddress;
        }
    }

    /// <summary>取消一个自定义服务的登记；返回它此前是否存在。</summary>
    /// <param name="serviceId">服务名。</param>
    public static bool Unregister(string serviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        lock (Gate)
        {
            return _registered.Remove(serviceId);
        }
    }

    /// <summary>
    /// 恢复到初始状态：默认配置、自建 HttpClient、清空自定义登记。
    /// </summary>
    /// <remarks>
    /// 主要为测试与「同一进程里换环境」提供；正常应用不需要调用它。
    /// </remarks>
    public static void Reset()
    {
        lock (Gate)
        {
            ClearHttpClient();
            _options = new EliCloudOptions();
            _registered = new Dictionary<string, Uri>(StringComparer.Ordinal);
        }
    }

    // ---------------------------------------------------------------- 内部实现

    private static Uri ResolveBaseAddress(string serviceId) => serviceId switch
    {
        EliCloudServiceIds.Sso => _options.SsoBaseAddress,
        EliCloudServiceIds.Mc => _options.McBaseAddress,
        EliCloudServiceIds.MainApi => _options.MainApiBaseAddress,
        _ => _registered.TryGetValue(serviceId, out var address)
            ? address
            : throw new KeyNotFoundException(
                $"未知的服务名「{serviceId}」。当前可用：{string.Join("、", RegisteredServiceIds)}。" +
                $"如果是你自己的服务，先登记它的位置：EliCloudService.Register(\"{serviceId}\", \"/它的前缀\")。"),
    };

    private static HttpClient GetHttpClient()
    {
        if (_httpClient is not null)
        {
            return _httpClient;
        }

        _httpClient = BuildHttpClient(_options);
        _ownsHttpClient = true;
        return _httpClient;
    }

    private static void RebuildHttpClient()
    {
        // 只丢弃**我们自己创建**的客户端。外部注入的（UseHttpClient）必须留着 ——
        // 换地址不等于换传输层：代理、证书、日志处理器通常还得继续用。
        if (!_ownsHttpClient)
        {
            return;
        }

        _httpClient?.Dispose();
        _httpClient = null;
        _ownsHttpClient = false;
    }

    /// <summary>丢弃当前 HttpClient（自建的先释放，外部注入的只解除引用）。</summary>
    private static void ClearHttpClient()
    {
        if (_ownsHttpClient)
        {
            _httpClient?.Dispose();
        }

        _httpClient = null;
        _ownsHttpClient = false;
    }

    private static HttpClient BuildHttpClient(EliCloudOptions options)
    {
        var client = new HttpClient
        {
            // 超时由 SDK 的传输层按请求控制（见 EliCloudTransport），
            // 关掉 HttpClient 自己的超时，免得两者叠加出难查的现象。
            Timeout = Timeout.InfiniteTimeSpan,
        };

        if (!string.IsNullOrWhiteSpace(options.UserAgent))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
        }

        return client;
    }

    /// <summary>服务句柄：只是个不可变的数据袋子，客户端由扩展方法照着它构造。</summary>
    private sealed class ServiceHandle(
        string serviceId,
        Uri baseAddress,
        HttpClient httpClient,
        EliCloudOptions options) : IEliCloudService
    {
        public string ServiceId { get; } = serviceId;

        public Uri BaseAddress { get; } = baseAddress;

        public HttpClient HttpClient { get; } = httpClient;

        public EliCloudOptions Options { get; } = options;
    }
}
