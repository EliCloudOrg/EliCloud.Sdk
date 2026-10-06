using System.Net;
using EliCloud.Sdk.Sso;
using EliCloud.Sdk.Tests.TestHttp;
using Xunit;

namespace EliCloud.Sdk.Tests;

/// <summary>
/// 设备授权流程（RFC 8628）的轮询状态机测试。
/// </summary>
/// <remarks>
/// 这些测试用「立即返回的 delay 委托」把等待抽掉，所以能精确断言
/// <b>客户端打算等多久</b> —— 而这正是平台要求客户端做对的地方
/// （收到 <c>slow_down</c> 要加长间隔、且不能无限增长）。
/// </remarks>
public sealed class DeviceCodeFlowTests
{
    private const string DeviceAuthorizationJson = """
        {"device_code":"dc_secret","user_code":"WDJB-MJHT",
         "verification_uri":"https://elicloud.test/auth/device",
         "verification_uri_complete":"https://elicloud.test/auth/device?user_code=WDJB-MJHT",
         "expires_in":600,"interval":5}
        """;

    private const string SuccessJson = """
        {"access_token":"at","token_type":"Bearer","expires_in":3600,"refresh_token":"rt","id_token":"idt","scope":"openid profile offline_access"}
        """;

    [Fact]
    public async Task RunAsync_ReportsPromptAndReturnsTokensAfterApproval()
    {
        var tokenCalls = 0;
        var handler = new CapturingHandler(request => request.Path switch
        {
            "/auth/device_authorization" => Stub.Json(DeviceAuthorizationJson),
            "/auth/token" => ++tokenCalls == 1
                ? Stub.Error(HttpStatusCode.BadRequest, EliCloudErrorCodes.AuthorizationPending)
                : Stub.Json(SuccessJson),
            _ => Stub.Error(HttpStatusCode.NotFound, "not_found"),
        });

        var delays = new List<TimeSpan>();
        var flow = new DeviceCodeFlow(CreateClient(handler), (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        DeviceCodePrompt? prompt = null;
        var tokens = await flow.RunAsync(
            new DeviceCodeFlowOptions { ClientId = "elicloud-cli" },
            p => prompt = p);

        Assert.NotNull(prompt);
        Assert.Equal("WDJB-MJHT", prompt!.UserCode);
        Assert.Equal("https://elicloud.test/auth/device", prompt.VerificationUri.AbsoluteUri);
        Assert.Equal("https://elicloud.test/auth/device?user_code=WDJB-MJHT", prompt.VerificationUriComplete!.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(5), prompt.Interval);
        Assert.Equal(TimeSpan.FromSeconds(600), prompt.ExpiresIn);

        // 第一次 pending 之后重试；等待间隔保持服务端给的 5 秒。
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], delays);
        Assert.Equal(2, tokenCalls);

        Assert.Equal("at", tokens.AccessToken);
        Assert.Equal("rt", tokens.RefreshToken);
        Assert.Equal("idt", tokens.IdToken);
    }

    [Fact]
    public async Task RunAsync_IncreasesIntervalOnSlowDown()
    {
        // 服务端每次 slow_down 都要求客户端把间隔 +5 秒（RFC 8628 §3.5）。
        var tokenCalls = 0;
        var handler = new CapturingHandler(request => request.Path switch
        {
            "/auth/device_authorization" => Stub.Json(DeviceAuthorizationJson),
            "/auth/token" => ++tokenCalls <= 2
                ? Stub.Error(HttpStatusCode.BadRequest, EliCloudErrorCodes.SlowDown)
                : Stub.Json(SuccessJson),
            _ => Stub.Error(HttpStatusCode.NotFound, "not_found"),
        });

        var delays = new List<TimeSpan>();
        var flow = new DeviceCodeFlow(CreateClient(handler), (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        await flow.RunAsync(new DeviceCodeFlowOptions { ClientId = "elicloud-cli" });

        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)],
            delays);
    }

    [Fact]
    public async Task RunAsync_CapsIntervalAtSixtySeconds()
    {
        // 上限是必须的：没有它，一个持续过快轮询的客户端会把间隔顶到几百秒并永远追不上，
        // 等于把自己锁死（服务端实现期实测到过 110 秒）。
        var tokenCalls = 0;
        var handler = new CapturingHandler(request => request.Path switch
        {
            "/auth/device_authorization" => Stub.Json(DeviceAuthorizationJson),
            "/auth/token" => ++tokenCalls <= 20
                ? Stub.Error(HttpStatusCode.BadRequest, EliCloudErrorCodes.SlowDown)
                : Stub.Json(SuccessJson),
            _ => Stub.Error(HttpStatusCode.NotFound, "not_found"),
        });

        var delays = new List<TimeSpan>();
        var flow = new DeviceCodeFlow(CreateClient(handler), (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        await flow.RunAsync(new DeviceCodeFlowOptions { ClientId = "elicloud-cli" });

        Assert.Equal(21, delays.Count);
        Assert.All(delays, delay => Assert.True(
            delay <= TimeSpan.FromSeconds(DeviceCodeFlow.MaximumPollIntervalSeconds),
            $"间隔 {delay} 超过了 {DeviceCodeFlow.MaximumPollIntervalSeconds} 秒上限。"));

        Assert.Equal(TimeSpan.FromSeconds(5), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(60), delays[^1]);
        Assert.Equal(TimeSpan.FromSeconds(60), delays[^2]);
    }

    [Fact]
    public async Task RunAsync_HonoursMinimumIntervalWhenServerAsksForLess()
    {
        var handler = new CapturingHandler(request => request.Path switch
        {
            "/auth/device_authorization" => Stub.Json(
                """{"device_code":"dc","user_code":"AAAA-BBBB","verification_uri":"https://elicloud.test/auth/device","expires_in":600,"interval":1}"""),
            "/auth/token" => Stub.Json(SuccessJson),
            _ => Stub.Error(HttpStatusCode.NotFound, "not_found"),
        });

        var delays = new List<TimeSpan>();
        var flow = new DeviceCodeFlow(CreateClient(handler), (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        await flow.RunAsync(new DeviceCodeFlowOptions
        {
            ClientId = "elicloud-cli",
            MinimumPollInterval = TimeSpan.FromSeconds(5),
        });

        Assert.Equal([TimeSpan.FromSeconds(5)], delays);
    }

    [Fact]
    public async Task RunAsync_ThrowsOnUserDenial()
    {
        var handler = new CapturingHandler(request => request.Path switch
        {
            "/auth/device_authorization" => Stub.Json(DeviceAuthorizationJson),
            "/auth/token" => Stub.Error(HttpStatusCode.BadRequest, EliCloudErrorCodes.AccessDenied, "用户已拒绝"),
            _ => Stub.Error(HttpStatusCode.NotFound, "not_found"),
        });

        var flow = new DeviceCodeFlow(CreateClient(handler), (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => flow.RunAsync(new DeviceCodeFlowOptions { ClientId = "elicloud-cli" }));

        Assert.Equal(EliCloudErrorCodes.AccessDenied, exception.Error);
        Assert.Equal("用户已拒绝", exception.ErrorDescription);
    }

    [Fact]
    public async Task RunAsync_ThrowsWhenDeviceCodeAlreadyExpired()
    {
        // expires_in=0：设备码一拿到就过期，客户端不应再发起任何轮询。
        var handler = new CapturingHandler(request => request.Path switch
        {
            "/auth/device_authorization" => Stub.Json(
                """{"device_code":"dc","user_code":"AAAA-BBBB","verification_uri":"https://elicloud.test/auth/device","expires_in":0,"interval":5}"""),
            _ => Stub.Json(SuccessJson),
        });

        var flow = new DeviceCodeFlow(CreateClient(handler), (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => flow.RunAsync(new DeviceCodeFlowOptions { ClientId = "elicloud-cli" }));

        Assert.Equal(EliCloudErrorCodes.ExpiredToken, exception.Error);
        Assert.Single(handler.Captured);
    }

    [Fact]
    public async Task RunAsync_StopsPollingAfterTerminalError()
    {
        var tokenCalls = 0;
        var handler = new CapturingHandler(request => request.Path switch
        {
            "/auth/device_authorization" => Stub.Json(DeviceAuthorizationJson),
            "/auth/token" => ++tokenCalls == 1
                ? Stub.Error(HttpStatusCode.BadRequest, EliCloudErrorCodes.AuthorizationPending)
                : Stub.Error(HttpStatusCode.BadRequest, EliCloudErrorCodes.ExpiredToken),
            _ => Stub.Error(HttpStatusCode.NotFound, "not_found"),
        });

        var flow = new DeviceCodeFlow(CreateClient(handler), (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<EliCloudApiException>(
            () => flow.RunAsync(new DeviceCodeFlowOptions { ClientId = "elicloud-cli" }));

        // 终态之后绝不能再轮询：否则流程已经结束还在空转。
        Assert.Equal(2, tokenCalls);
    }

    [Fact]
    public async Task RunAsync_PropagatesCancellation()
    {
        var handler = new CapturingHandler(request => request.Path switch
        {
            "/auth/device_authorization" => Stub.Json(DeviceAuthorizationJson),
            _ => Stub.Error(HttpStatusCode.BadRequest, EliCloudErrorCodes.AuthorizationPending),
        });

        using var cancellation = new CancellationTokenSource();
        var flow = new DeviceCodeFlow(CreateClient(handler), (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => flow.RunAsync(new DeviceCodeFlowOptions { ClientId = "elicloud-cli" }, cancellationToken: cancellation.Token));
    }

    private static SsoClient CreateClient(CapturingHandler handler)
        => new(TestOptions.HttpClient(handler), TestOptions.Create());
}
