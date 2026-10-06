using EliCloud.Sdk.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EliCloud.Sdk.MainApi;

/// <summary>平台服务清单里的一项。</summary>
public sealed class PlatformServiceInfo
{
    /// <summary>服务 ID，如 <c>pdf-decrypt</c>。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>展示名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>对外路径前缀，如 <c>/pdf-decrypt</c>。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>是否在线。前端据此显示或置灰入口。</summary>
    public bool Online { get; init; }
}

/// <summary>
/// 平台核心 API（<c>main-api</c>，对外前缀 <c>/core</c>）客户端。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>该服务在平台文档里已定契约但尚未实现、尚未部署</b>
/// （<c>docs/architecture.md</c> §1 的服务表标为「规划」，§5.8 定义了
/// <c>GET /core/v1/services</c> 的响应结构）。本类按该契约实现，
/// 但请把它当作「对接预留」而不是「已验证可用」：调用时若拿到 404，
/// 说明服务还没上线，而不是 SDK 有问题。
/// </para>
/// <para>
/// 服务清单的<strong>在线判定</strong>由 main-api 负责（推荐做法是查 Caddy Admin API 的
/// 实际路由，而不是看容器是否在跑）。容器一启停，路由就变，因此清单会跟着变；
/// 前端轮询这个接口即可，平台没有 WebSocket/SSE 推送。
/// </para>
/// </remarks>
public sealed class MainApiClient
{
    private const string ServicesPath = "v1/services";

    private readonly EliCloudTransport _transport;
    private readonly Uri _baseAddress;

    /// <summary>创建客户端。</summary>
    public MainApiClient(HttpClient httpClient, EliCloudOptions options, ILogger<MainApiClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _baseAddress = options.MainApiBaseAddress;
        _transport = new EliCloudTransport(httpClient, options.Timeout, logger);
    }

    /// <summary>创建客户端（配置来自 DI 选项系统）。</summary>
    public MainApiClient(HttpClient httpClient, IOptions<EliCloudOptions> options, ILogger<MainApiClient>? logger = null)
        : this(httpClient, (options ?? throw new ArgumentNullException(nameof(options))).Value, logger)
    {
    }

    /// <summary>服务清单端点。</summary>
    public Uri ServicesEndpoint => new(_baseAddress, ServicesPath);

    /// <summary>
    /// <c>GET /core/v1/services</c>：当前哪些服务在线。
    /// </summary>
    /// <param name="accessToken">
    /// 可选 access token。契约示例里该接口没有要求认证，但一旦平台给它加上鉴权，
    /// 这里就不用改代码。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<PlatformServiceInfo>> ListServicesAsync(
        string? accessToken = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _transport
            .SendJsonAsync(HttpMethod.Get, ServicesEndpoint, null, accessToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        var envelope = response.Deserialize<ServiceListEnvelope>()
            ?? throw new EliCloudMalformedResponseException(
                $"服务清单不是合法的 JSON 对象：{ServicesEndpoint}", ServicesEndpoint, response.Body);
        return envelope.Services ?? [];
    }

    private sealed class ServiceListEnvelope
    {
        public IReadOnlyList<PlatformServiceInfo>? Services { get; init; }
    }
}
