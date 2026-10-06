using EliCloud.Sdk.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EliCloud.Sdk.Mc;

/// <summary>
/// MC 白名单服务（对外前缀 <c>/mc</c>）的用户自助端点客户端。
/// </summary>
/// <remarks>
/// <para>
/// 调用前需要一个**带 <c>mc:whitelist</c> scope** 的 access token；
/// 缺这个 scope 时服务端返回 <c>403 insufficient_scope</c>
/// （<see cref="EliCloudApiException.IsInsufficientScope"/>），而不是 401 —— 身份是有效的，
/// 缺的是权限。这个区分对前端很关键：401 要重新登录，403 要让用户重新授权。
/// </para>
/// <para>
/// 本服务不是 OIDC 客户端，也不提供登录页：它的身份完全来自你传进来的令牌。
/// </para>
/// </remarks>
public sealed class McClient
{
    private const string NamesPath = "v1/names";

    private readonly EliCloudTransport _transport;
    private readonly Uri _baseAddress;

    /// <summary>创建客户端。</summary>
    public McClient(HttpClient httpClient, EliCloudOptions options, ILogger<McClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _baseAddress = options.McBaseAddress;
        _transport = new EliCloudTransport(httpClient, options.Timeout, logger);
    }

    /// <summary>创建客户端（配置来自 DI 选项系统）。</summary>
    public McClient(HttpClient httpClient, IOptions<EliCloudOptions> options, ILogger<McClient>? logger = null)
        : this(httpClient, (options ?? throw new ArgumentNullException(nameof(options))).Value, logger)
    {
    }

    /// <summary>健康检查地址（匿名可访问，**不经** <c>/v1</c>）。</summary>
    public Uri HealthEndpoint => new(_baseAddress, "healthz");

    /// <summary><c>POST /mc/v1/names</c>：提交一个 MC 用户名。</summary>
    /// <param name="accessToken">带 <c>mc:whitelist</c> scope 的 access token。</param>
    /// <param name="name">MC 用户名；必须匹配 <c>^[A-Za-z0-9_]{3,16}$</c>，服务端会归一为小写。</param>
    /// <param name="note">必填备注，1–200 字符，不能含控制字符。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <b>幂等</b>：重复提交同一个名字（大小写不敏感）时服务端返回 200 与已有条目，
    /// 不会再执行一次 RCON 写入。所以本方法在「已存在」与「新建」两种情况下都成功返回，
    /// 差别只在服务端审计里的 <c>result</c>。
    /// </remarks>
    /// <exception cref="EliCloudApiException">
    /// <c>name_taken</c>（已被别人或管理员占用）、<c>quota_exceeded</c>（名额已满）、
    /// <c>rcon_unavailable</c>（写入未被回读确认，**服务端不会落库**）、
    /// <c>invalid_request</c>（名字或备注不合法）。
    /// </exception>
    public async Task<McNameEntry> AddNameAsync(
        string accessToken,
        string name,
        string note,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(note);

        var endpoint = new Uri(_baseAddress, NamesPath);
        var response = await _transport
            .SendJsonAsync(HttpMethod.Post, endpoint, new CreateNameRequest(name, note), accessToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return Deserialize<McNameEntry>(response, endpoint);
    }

    /// <summary><c>GET /mc/v1/names</c>：本账号的条目与名额。</summary>
    /// <param name="accessToken">access token。</param>
    /// <param name="includeRemoved">是否带上已撤回的历史条目。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<McNameListResponse> ListMyNamesAsync(
        string accessToken,
        bool includeRemoved = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);

        var endpoint = EliCloudQuery.Append(
            new Uri(_baseAddress, NamesPath),
            [new KeyValuePair<string, string?>("include_removed", includeRemoved ? "true" : null)]);

        var response = await _transport
            .SendJsonAsync(HttpMethod.Get, endpoint, null, accessToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return Deserialize<McNameListResponse>(response, endpoint);
    }

    /// <summary>
    /// <c>DELETE /mc/v1/names/{id}</c>：撤回自己的条目（会立刻执行 <c>whitelist remove</c>）。
    /// </summary>
    /// <remarks>
    /// 别人的条目一律返回 <c>not_found</c>，不泄露存在性；已撤回的条目再删一次是幂等成功。
    /// </remarks>
    public async Task<McNameEntry> RemoveNameAsync(
        string accessToken,
        string entryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        ArgumentException.ThrowIfNullOrEmpty(entryId);

        var endpoint = new Uri(_baseAddress, $"{NamesPath}/{Uri.EscapeDataString(entryId)}");
        var response = await _transport
            .SendJsonAsync(HttpMethod.Delete, endpoint, null, accessToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return Deserialize<McNameEntry>(response, endpoint);
    }

    /// <summary><c>GET /mc/v1/me</c>：当前身份（前端登录后的自检）。</summary>
    public async Task<McIdentity> GetIdentityAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);

        var endpoint = new Uri(_baseAddress, "v1/me");
        var response = await _transport
            .SendJsonAsync(HttpMethod.Get, endpoint, null, accessToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return Deserialize<McIdentity>(response, endpoint);
    }

    /// <summary>
    /// <c>GET /mc/healthz</c>：健康检查（匿名）。
    /// </summary>
    /// <remarks>
    /// RCON 不可达时服务端返回 <b>503</b>，但响应体仍然是完整的健康快照 ——
    /// 所以这里**不把 503 当成异常**，而是照常解析出 <see cref="McHealthStatus"/>，
    /// 由调用方看 <see cref="McHealthStatus.IsHealthy"/>。只有其它状态码才抛。
    /// </remarks>
    public async Task<McHealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var response = await _transport
            .SendJsonAsync(HttpMethod.Get, HealthEndpoint, null, null, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is not (200 or 503))
        {
            response.EnsureSuccess();
        }

        return Deserialize<McHealthStatus>(response, HealthEndpoint);
    }

    private static T Deserialize<T>(EliCloudResponse response, Uri endpoint)
        => response.Deserialize<T>()
           ?? throw new EliCloudMalformedResponseException(
               $"响应不是合法的 JSON 对象：{endpoint}", endpoint, response.Body);

    private sealed record CreateNameRequest(string Name, string Note);
}

/// <summary>
/// MC 白名单服务的管理端点客户端（<c>/mc/v1/admin/*</c>，fail closed）。
/// </summary>
/// <remarks>
/// 服务端未配置 <c>ADMIN_TOKEN</c> 时整组接口返回 403，因此本客户端只应出现在运维工具里，
/// **不要**把它发给浏览器或移动端。
/// </remarks>
public sealed class McAdminClient
{
    private const string AdminNamesPath = "v1/admin/names";
    private const string AdminAuditPath = "v1/admin/audit";
    private const string AdminReconcilePath = "v1/admin/reconcile";

    private readonly EliCloudTransport _transport;
    private readonly Uri _baseAddress;
    private readonly string _adminToken;

    /// <summary>创建管理客户端。</summary>
    /// <param name="httpClient">HTTP 客户端。</param>
    /// <param name="options">连接配置。</param>
    /// <param name="adminToken">服务端的 <c>ADMIN_TOKEN</c>；空值立刻抛异常。</param>
    /// <param name="logger">可选日志。</param>
    public McAdminClient(
        HttpClient httpClient,
        EliCloudOptions options,
        string adminToken,
        ILogger<McAdminClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminToken, nameof(adminToken));
        options.Validate();

        _baseAddress = options.McBaseAddress;
        _adminToken = adminToken;
        _transport = new EliCloudTransport(httpClient, options.Timeout, logger);
    }

    /// <summary>创建管理客户端（配置来自 DI 选项系统）。</summary>
    public McAdminClient(
        HttpClient httpClient,
        IOptions<EliCloudOptions> options,
        string adminToken,
        ILogger<McAdminClient>? logger = null)
        : this(httpClient, (options ?? throw new ArgumentNullException(nameof(options))).Value, adminToken, logger)
    {
    }

    /// <summary><c>GET /mc/v1/admin/names</c>：列出全部绑定（含备注全文）。</summary>
    /// <param name="status">按 <c>active</c> / <c>removed</c> 过滤；<c>null</c> 为全部。</param>
    /// <param name="limit">本页上限（1–1000）。</param>
    /// <param name="offset">偏移。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<McNamePage> ListNamesAsync(
        string? status = null,
        int limit = 200,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var endpoint = EliCloudQuery.Append(
            new Uri(_baseAddress, AdminNamesPath),
            [
                new KeyValuePair<string, string?>("status", status),
                new KeyValuePair<string, string?>("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string?>("offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ]);

        var response = await _transport
            .SendJsonAsync(HttpMethod.Get, endpoint, null, _adminToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return Deserialize<McNamePage>(response, endpoint);
    }

    /// <summary><c>GET /mc/v1/admin/audit</c>：审计日志（分页 + 过滤）。</summary>
    /// <param name="userId">按 <c>sub</c> 过滤。</param>
    /// <param name="action">按 <c>add</c> / <c>remove</c> / <c>reconcile</c> / <c>deny</c> 过滤。</param>
    /// <param name="limit">本页上限。</param>
    /// <param name="offset">偏移。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<McAuditPage> ListAuditAsync(
        string? userId = null,
        string? action = null,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var endpoint = EliCloudQuery.Append(
            new Uri(_baseAddress, AdminAuditPath),
            [
                new KeyValuePair<string, string?>("user_id", userId),
                new KeyValuePair<string, string?>("action", action),
                new KeyValuePair<string, string?>("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string?>("offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ]);

        var response = await _transport
            .SendJsonAsync(HttpMethod.Get, endpoint, null, _adminToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return Deserialize<McAuditPage>(response, endpoint);
    }

    /// <summary><c>DELETE /mc/v1/admin/names/{id}</c>：管理员强制移除（<c>removed_by=admin</c>）。</summary>
    public async Task<McNameEntry> RemoveNameAsync(string entryId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryId);

        var endpoint = new Uri(_baseAddress, $"{AdminNamesPath}/{Uri.EscapeDataString(entryId)}");
        var response = await _transport
            .SendJsonAsync(HttpMethod.Delete, endpoint, null, _adminToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return Deserialize<McNameEntry>(response, endpoint);
    }

    /// <summary>
    /// <c>POST /mc/v1/admin/reconcile</c>：用 MC 的 <c>whitelist list</c> 与库对账。
    /// </summary>
    /// <param name="apply">
    /// <c>true</c> 时把「库有、白名单无」的漂移补回去；
    /// 「白名单有、库无」**只会报告，绝不自动删除**（可能是管理员手工加的）。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<McReconcileReport> ReconcileAsync(bool apply = false, CancellationToken cancellationToken = default)
    {
        var endpoint = new Uri(_baseAddress, AdminReconcilePath);
        var response = await _transport
            .SendJsonAsync(HttpMethod.Post, endpoint, new ReconcileRequest(apply), _adminToken, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccess();

        return Deserialize<McReconcileReport>(response, endpoint);
    }

    private static T Deserialize<T>(EliCloudResponse response, Uri endpoint)
        => response.Deserialize<T>()
           ?? throw new EliCloudMalformedResponseException(
               $"响应不是合法的 JSON 对象：{endpoint}", endpoint, response.Body);

    private sealed record ReconcileRequest(bool Apply);
}
