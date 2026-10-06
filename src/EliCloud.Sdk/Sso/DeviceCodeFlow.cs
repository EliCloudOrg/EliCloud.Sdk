namespace EliCloud.Sdk.Sso;

/// <summary>设备授权流程的输入参数。</summary>
public sealed class DeviceCodeFlowOptions
{
    /// <summary>客户端 ID（如平台预置的 <c>elicloud-cli</c>）。</summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// 请求的 scope。默认 <c>openid profile offline_access</c>：
    /// <c>offline_access</c> 才会拿到 refresh token，CLI 一般都需要。
    /// </summary>
    public string Scope { get; init; } = $"{EliCloudScopes.OpenId} {EliCloudScopes.Profile} {EliCloudScopes.OfflineAccess}";

    /// <summary>保密客户端的 secret。</summary>
    public string? ClientSecret { get; init; }

    /// <summary>客户端认证方式；留空时按是否提供 secret 自动推断。</summary>
    public ClientAuthenticationMethod? AuthenticationMethod { get; init; }

    /// <summary>
    /// 轮询间隔下限；实际间隔取「服务端返回的 interval」与它的**较大者**。
    /// 服务端会对过快轮询回 <c>slow_down</c>，所以不建议调小。
    /// </summary>
    public TimeSpan MinimumPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>整个流程的总超时；默认用设备码自身的 <c>expires_in</c>。</summary>
    public TimeSpan? OverallTimeout { get; init; }
}

/// <summary>需要展示给用户的信息（终端打印或弹窗都行）。</summary>
public sealed class DeviceCodePrompt
{
    /// <summary>人读短码，形如 <c>WDJB-MJHT</c>。</summary>
    public required string UserCode { get; init; }

    /// <summary>用户要打开的地址。</summary>
    public required Uri VerificationUri { get; init; }

    /// <summary>预填短码的地址；显示它最省事。</summary>
    public Uri? VerificationUriComplete { get; init; }

    /// <summary>服务端要求的轮询间隔。</summary>
    public TimeSpan Interval { get; init; }

    /// <summary>设备码有效期。</summary>
    public TimeSpan ExpiresIn { get; init; }
}

/// <summary>
/// 走完 RFC 8628 设备授权流程（CLI / 桌面工具的标准做法）。
/// </summary>
/// <remarks>
/// <para>
/// 它把「轮询到用户确认为止」这件繁琐事封装掉，并实现了服务端明确要求的两条规则：
/// </para>
/// <list type="number">
///   <item><description>
///     收到 <c>slow_down</c> 后把间隔 <b>+5 秒</b>（RFC 8628 §3.5），
///     而不是继续用固定间隔硬轮询 —— 后者会把间隔逼到上限，最终自己把自己锁死；
///   </description></item>
///   <item><description>间隔封顶 <b>60 秒</b>，与服务端的 <c>DEVICE_MAX_POLL_INTERVAL</c> 对齐。</description></item>
/// </list>
/// <para>
/// <b>为什么要给 <c>delay</c> 留一个注入点</b>：真正的等待会让单元测试变成秒级慢测。
/// 生产代码不要传它；测试传一个立即完成的委托，就能把整个状态机跑得又快又确定。
/// </para>
/// </remarks>
public sealed class DeviceCodeFlow
{
    /// <summary>轮询间隔上限（秒），与服务端的 <c>DEVICE_MAX_POLL_INTERVAL</c> 一致。</summary>
    public const int MaximumPollIntervalSeconds = 60;

    /// <summary>收到 <c>slow_down</c> 时每次递增的秒数（RFC 8628 §3.5）。</summary>
    public const int SlowDownStepSeconds = 5;

    private readonly SsoClient _client;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>创建流程驱动器。</summary>
    /// <param name="client">SSO 客户端。</param>
    /// <param name="delay">等待实现；默认 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>，仅为测试预留。</param>
    public DeviceCodeFlow(SsoClient client, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    /// <summary>
    /// 执行完整流程：申请设备码 → 提示用户 → 轮询到拿到令牌。
    /// </summary>
    /// <param name="options">流程参数。</param>
    /// <param name="prompt">
    /// 提示回调：把短码与验证地址展示给用户。命令行里就是 <c>Console.WriteLine</c>。
    /// </param>
    /// <param name="cancellationToken">取消令牌（用户按 Ctrl+C 时传入）。</param>
    /// <returns>成功时返回包含 access/refresh/id token 的令牌组。</returns>
    /// <exception cref="EliCloudApiException">
    /// 流程被终结时抛出，<see cref="EliCloudApiException.Error"/> 为
    /// <c>access_denied</c>（用户拒绝）、<c>expired_token</c>（设备码过期）或 <c>invalid_grant</c>（已被兑换）。
    /// </exception>
    /// <exception cref="TimeoutException">超过 <see cref="DeviceCodeFlowOptions.OverallTimeout"/> 仍未完成。</exception>
    public async Task<TokenSet> RunAsync(
        DeviceCodeFlowOptions options,
        Action<DeviceCodePrompt>? prompt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var authorization = await _client
            .StartDeviceAuthorizationAsync(
                options.ClientId,
                options.Scope,
                options.ClientSecret,
                options.AuthenticationMethod,
                cancellationToken)
            .ConfigureAwait(false);

        var interval = MaxInterval(options.MinimumPollInterval, TimeSpan.FromSeconds(EffectiveInterval(authorization.Interval)));
        var promptInfo = new DeviceCodePrompt
        {
            UserCode = authorization.UserCode,
            VerificationUri = new Uri(authorization.VerificationUri, UriKind.Absolute),
            VerificationUriComplete = string.IsNullOrEmpty(authorization.VerificationUriComplete)
                ? null
                : new Uri(authorization.VerificationUriComplete, UriKind.Absolute),
            Interval = interval,
            ExpiresIn = TimeSpan.FromSeconds(authorization.ExpiresIn),
        };

        prompt?.Invoke(promptInfo);

        var deviceCodeLifetime = TimeSpan.FromSeconds(authorization.ExpiresIn);
        var deadline = DateTimeOffset.UtcNow
            + (options.OverallTimeout is { } overall && overall < deviceCodeLifetime ? overall : deviceCodeLifetime);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new EliCloudApiException(
                    400,
                    new EliCloudError(EliCloudErrorCodes.ExpiredToken, "设备码已过期，请重新发起登录。"),
                    _client.TokenEndpoint);
            }

            await _delay(interval, cancellationToken).ConfigureAwait(false);

            var result = await _client
                .PollDeviceCodeAsync(
                    authorization.DeviceCode,
                    options.ClientId,
                    options.ClientSecret,
                    options.AuthenticationMethod,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.IsSuccess && result.Tokens is not null)
            {
                return result.Tokens.ToTokenSet();
            }

            if (result.IsSlowDown)
            {
                // RFC 8628 §3.5：把间隔 +5 秒，并且**封顶**。
                // 注意是「加上去再封顶」，不是「直接封到上限」——
                // 后者会让一次 slow_down 就把间隔从 5 秒顶到 60 秒。
                interval = MinInterval(
                    interval + TimeSpan.FromSeconds(SlowDownStepSeconds),
                    TimeSpan.FromSeconds(MaximumPollIntervalSeconds));
                continue;
            }

            if (result.IsPending)
            {
                continue;
            }

            // 终态：用户拒绝、设备码过期、设备码已被兑换。继续轮询只会一直拿到同样的答案。
            throw new EliCloudApiException(
                400,
                new EliCloudError(result.Error, result.ErrorDescription),
                _client.TokenEndpoint);
        }
    }

    private static TimeSpan MaxInterval(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private static TimeSpan MinInterval(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static int EffectiveInterval(int intervalSeconds) => intervalSeconds > 0 ? intervalSeconds : 5;
}
