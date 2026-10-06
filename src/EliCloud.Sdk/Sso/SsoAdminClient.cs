using EliCloud.Sdk.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EliCloud.Sdk.Sso;

/// <summary>
/// SSO 的 OIDC 客户端注册管理客户端（<c>/auth/v1/clients</c>，需要管理员令牌）。
/// </summary>
/// <remarks>
/// <para>
/// 平台采用**静态注册**（不做 RFC 7591 动态注册），有两条等价路径：
/// 容器内 CLI（<c>python -m app.cli create-client</c>）与这个 HTTP 管理接口。本类是后者。
/// </para>
/// <para>
/// <b>fail closed</b>：服务端没配置 <c>ADMIN_TOKEN</c> 时整组接口返回 403，
/// 所以这个客户端只在确实需要时有意义 —— 但即使这样，用错令牌也只会得到 403 而不是误操作。
/// </para>
/// <para>
/// ⚠️ 管理员令牌是**平台级凭据**：不要把它发给前端或移动端，只在服务端/运维脚本里使用。
/// </para>
/// </remarks>
public sealed class SsoAdminClient
{
    private readonly EliCloudTransport _transport;
    private readonly Uri _clientsEndpoint;
    private readonly string _adminToken;

    /// <summary>创建管理客户端。</summary>
    /// <param name="httpClient">HTTP 客户端；生命周期由调用方管理。</param>
    /// <param name="options">连接配置。</param>
    /// <param name="adminToken">服务端的 <c>ADMIN_TOKEN</c>。空值会立刻抛异常，避免发出必然 403 的请求。</param>
    /// <param name="logger">可选日志。</param>
    public SsoAdminClient(
        HttpClient httpClient,
        EliCloudOptions options,
        string adminToken,
        ILogger<SsoAdminClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminToken, nameof(adminToken));
        options.Validate();

        _clientsEndpoint = new Uri(options.SsoBaseAddress, "v1/clients");
        _adminToken = adminToken;
        _transport = new EliCloudTransport(httpClient, options.Timeout, logger);
    }

    /// <summary>创建管理客户端（配置来自 DI 选项系统）。</summary>
    public SsoAdminClient(
        HttpClient httpClient,
        IOptions<EliCloudOptions> options,
        string adminToken,
        ILogger<SsoAdminClient>? logger = null)
        : this(httpClient, (options ?? throw new ArgumentNullException(nameof(options))).Value, adminToken, logger)
    {
    }

    /// <summary>列出全部客户端（不回显 secret）。</summary>
    public async Task<IReadOnlyList<OAuthClientInfo>> ListClientsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _transport
            .SendJsonAsync(HttpMethod.Get, _clientsEndpoint, null, _adminToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        var envelope = response.Deserialize<ClientListEnvelope>()
            ?? throw new EliCloudMalformedResponseException(
                $"客户端列表不是合法 JSON：{_clientsEndpoint}", _clientsEndpoint, response.Body);
        return envelope.Clients ?? [];
    }

    /// <summary>读取单个客户端。</summary>
    public async Task<OAuthClientInfo> GetClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);

        var endpoint = new Uri(_clientsEndpoint, Uri.EscapeDataString(clientId));
        var response = await _transport
            .SendJsonAsync(HttpMethod.Get, endpoint, null, _adminToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return response.Deserialize<OAuthClientInfo>()
            ?? throw new EliCloudMalformedResponseException($"客户端详情不是合法 JSON：{endpoint}", endpoint, response.Body);
    }

    /// <summary>
    /// 创建客户端。返回体里的 <see cref="CreatedOAuthClientInfo.ClientSecret"/> **只出现这一次**。
    /// </summary>
    public async Task<CreatedOAuthClientInfo> CreateClientAsync(
        CreateOAuthClientRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await _transport
            .SendJsonAsync(HttpMethod.Post, _clientsEndpoint, request, _adminToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return response.Deserialize<CreatedOAuthClientInfo>()
            ?? throw new EliCloudMalformedResponseException(
                $"创建客户端响应不是合法 JSON：{_clientsEndpoint}", _clientsEndpoint, response.Body);
    }

    /// <summary>修改客户端的名称 / 回跳地址 / scope / grant_type。</summary>
    public async Task<OAuthClientInfo> UpdateClientAsync(
        string clientId,
        UpdateOAuthClientRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = new Uri(_clientsEndpoint, Uri.EscapeDataString(clientId));
        var response = await _transport
            .SendJsonAsync(HttpMethod.Patch, endpoint, request, _adminToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return response.Deserialize<OAuthClientInfo>()
            ?? throw new EliCloudMalformedResponseException($"修改客户端响应不是合法 JSON：{endpoint}", endpoint, response.Body);
    }

    /// <summary>
    /// 删除客户端。服务端会同时撤销它的 refresh 链、清掉授权码/设备码/同意记录。
    /// </summary>
    public async Task DeleteClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);

        var endpoint = new Uri(_clientsEndpoint, Uri.EscapeDataString(clientId));
        var response = await _transport
            .SendJsonAsync(HttpMethod.Delete, endpoint, null, _adminToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();
    }

    /// <summary>轮换 secret；旧 secret 立即失效，新明文只在此回显一次。</summary>
    public async Task<RotatedClientSecret> RotateClientSecretAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);

        var endpoint = new Uri(_clientsEndpoint, $"{Uri.EscapeDataString(clientId)}/rotate-secret");
        var response = await _transport
            .SendJsonAsync(HttpMethod.Post, endpoint, null, _adminToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return response.Deserialize<RotatedClientSecret>()
            ?? throw new EliCloudMalformedResponseException($"轮换 secret 响应不是合法 JSON：{endpoint}", endpoint, response.Body);
    }

    private sealed class ClientListEnvelope
    {
        public IReadOnlyList<OAuthClientInfo>? Clients { get; init; }
    }
}
