using System.Net;
using EliCloud.Sdk.Mc;
using EliCloud.Sdk.Tests.TestHttp;
using Xunit;

namespace EliCloud.Sdk.Tests;

/// <summary>MC 白名单服务（<c>/mc/*</c>）的契约测试。</summary>
public sealed class McTests
{
    private const string EntryJson = """
        {"id":"mn_0007","name":"probetester","name_display":"ProbeTester","note":"帮朋友申请",
         "status":"active","uuid":null,"created_at":"2026-10-06T00:50:00Z","removed_at":null}
        """;

    // ------------------------------------------------------------ 用户自助端点

    [Fact]
    public async Task AddName_PostsNameAndNoteWithBearerToken()
    {
        var handler = new CapturingHandler(_ => Stub.Json(EntryJson, HttpStatusCode.Created));

        var entry = await CreateClient(handler).AddNameAsync("access-token", "ProbeTester", "帮朋友申请");

        var request = handler.Single();
        Assert.Equal("POST", request.Method);
        Assert.Equal("/mc/v1/names", request.Path);
        Assert.Equal("Bearer", request.Request.Headers.Authorization?.Scheme);
        Assert.Equal("access-token", request.Request.Headers.Authorization?.Parameter);

        var body = request.JsonFields();
        Assert.Equal("ProbeTester", body["name"]);
        Assert.Equal("帮朋友申请", body["note"]);

        // 展示写法与归一写法都在：服务端内部用小写做键，展示时回显原始写法。
        Assert.Equal("probetester", entry.Name);
        Assert.Equal("ProbeTester", entry.NameDisplay);
        Assert.Equal("mn_0007", entry.Id);
    }

    [Fact]
    public async Task AddName_UuidIsNullAndMustNotBeTreatedAsEvidence()
    {
        // 平台刻意不构造 UUID（离线模式下服务端可能写 v5 也可能是随机 v4），
        // 所以 SDK 不能把它编造成一个「看起来合理的默认值」，只能如实为 null。
        var handler = new CapturingHandler(_ => Stub.Json(EntryJson, HttpStatusCode.Created));

        var entry = await CreateClient(handler).AddNameAsync("token", "ProbeTester", "note");

        Assert.Null(entry.Uuid);
    }

    [Fact]
    public async Task AddName_IdempotentRepeatIsAlsoSuccess()
    {
        // 重复提交同名：服务端返回 200 与已有条目（不会重复执行 RCON 写入）。
        var handler = new CapturingHandler(_ => Stub.Json(EntryJson));

        var entry = await CreateClient(handler).AddNameAsync("token", "probetester", "note");

        Assert.Equal("mn_0007", entry.Id);
    }

    [Fact]
    public async Task AddName_MapsNameTakenConflict()
    {
        var handler = new CapturingHandler(_ => Stub.Error(
            HttpStatusCode.Conflict, EliCloudErrorCodes.NameTaken, "用户名 x 已被其它账号绑定"));

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).AddNameAsync("token", "taken", "note"));

        Assert.Equal(EliCloudErrorCodes.NameTaken, exception.Error);
        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public async Task AddName_MapsQuotaExceeded()
    {
        var handler = new CapturingHandler(_ => Stub.Error(
            HttpStatusCode.Conflict, EliCloudErrorCodes.QuotaExceeded, "每个账号最多绑定 2 个 MC 用户名"));

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).AddNameAsync("token", "third", "note"));

        Assert.Equal(EliCloudErrorCodes.QuotaExceeded, exception.Error);
    }

    [Fact]
    public async Task AddName_MapsRconUnavailableAs503()
    {
        // 回读未确认时服务端**不会落库**，客户端必须把 503 与 409 区分开。
        var handler = new CapturingHandler(_ => Stub.Error(
            HttpStatusCode.ServiceUnavailable, EliCloudErrorCodes.RconUnavailable, "白名单写入未被回读确认"));

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).AddNameAsync("token", "someone", "note"));

        Assert.Equal(503, exception.StatusCode);
        Assert.Equal(EliCloudErrorCodes.RconUnavailable, exception.Error);
    }

    [Fact]
    public async Task AddName_MapsMissingScopeTo403()
    {
        // 身份有效但权限不足 → 403 而不是 401：调用方据此决定「重新登录」还是「申请授权」。
        var handler = new CapturingHandler(_ => Stub.Error(
            HttpStatusCode.Forbidden, EliCloudErrorCodes.InsufficientScope, "缺少 mc:whitelist"));

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).AddNameAsync("token", "someone", "note"));

        Assert.True(exception.IsInsufficientScope);
        Assert.False(exception.IsInvalidToken);
    }

    [Fact]
    public async Task AddName_MapsInvalidTokenTo401()
    {
        var handler = new CapturingHandler(_ => Stub.Error(
            HttpStatusCode.Unauthorized, EliCloudErrorCodes.InvalidToken, "令牌无效"));

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).AddNameAsync("token", "someone", "note"));

        Assert.True(exception.IsInvalidToken);
        Assert.False(exception.IsInsufficientScope);
    }

    [Fact]
    public async Task ListMyNames_ParsesQuotaAndEntries()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            $$"""
            {"sub":"user_0001","quota":{"limit":2,"used":1,"remaining":1},"names":[{{EntryJson}}]}
            """));

        var result = await CreateClient(handler).ListMyNamesAsync("token");

        Assert.Equal("/mc/v1/names", handler.Single().Path);
        Assert.Equal("user_0001", result.Sub);
        Assert.Equal(2, result.Quota!.Limit);
        Assert.Equal(1, result.Quota.Remaining);
        Assert.Single(result.Names);
    }

    [Fact]
    public async Task ListMyNames_AddsIncludeRemovedWhenAsked()
    {
        var handler = new CapturingHandler(_ => Stub.Json("""{"sub":"u","names":[]}"""));

        await CreateClient(handler).ListMyNamesAsync("token", includeRemoved: true);

        Assert.Contains("include_removed=true", handler.Single().Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListMyNames_OmitsIncludeRemovedByDefault()
    {
        var handler = new CapturingHandler(_ => Stub.Json("""{"sub":"u","names":[]}"""));

        await CreateClient(handler).ListMyNamesAsync("token");

        Assert.Equal(string.Empty, handler.Single().Query);
    }

    [Fact]
    public async Task RemoveName_UsesEntryIdInPath()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """{"id":"mn_0007","name":"probetester","status":"removed","removed_at":"2026-10-06T01:00:00Z"}"""));

        var entry = await CreateClient(handler).RemoveNameAsync("token", "mn_0007");

        var request = handler.Single();
        Assert.Equal("DELETE", request.Method);
        Assert.Equal("/mc/v1/names/mn_0007", request.Path);
        Assert.Equal("removed", entry.Status);
    }

    [Fact]
    public async Task RemoveName_EscapesEntryId()
    {
        var handler = new CapturingHandler(_ => Stub.Json("""{"id":"a/b","status":"removed"}"""));

        await CreateClient(handler).RemoveNameAsync("token", "a/b");

        Assert.Equal("/mc/v1/names/a%2Fb", handler.Single().Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RemoveName_MapsNotFoundWithoutLeakingOwnership()
    {
        var handler = new CapturingHandler(_ => Stub.Error(HttpStatusCode.NotFound, EliCloudErrorCodes.NotFound, "条目不存在"));

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).RemoveNameAsync("token", "mn_9999"));

        Assert.Equal(EliCloudErrorCodes.NotFound, exception.Error);
    }

    [Fact]
    public async Task GetIdentity_ReturnsTokenClaimsWithoutQueryingSso()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """{"sub":"user_0001","username":"alice","issuer":"https://elicloud.test/auth","scope":"openid mc:whitelist"}"""));

        var identity = await CreateClient(handler).GetIdentityAsync("token");

        Assert.Equal("/mc/v1/me", handler.Single().Path);
        Assert.Equal("user_0001", identity.Sub);
        Assert.Equal("alice", identity.Username);
        Assert.Equal("https://elicloud.test/auth", identity.Issuer);
    }

    // ---------------------------------------------------------------- /healthz

    [Fact]
    public async Task Health_OkPayloadIsParsed()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """{"status":"ok","rcon":{"reachable":true,"endpoint":"urania-mc:25575","latency_ms":12},"whitelist_count":3,"sso_issuer":"https://elicloud.test/auth","version":"1.0.0"}"""));

        var health = await CreateClient(handler).GetHealthAsync();

        Assert.Equal("/mc/healthz", handler.Single().Path);
        Assert.True(health.IsHealthy);
        Assert.Equal(3, health.WhitelistCount);
        Assert.Equal(12, health.Rcon!.LatencyMs);
    }

    [Fact]
    public async Task Health_DegradedIs503ButStillParsedNotThrown()
    {
        // 「容器在跑但 RCON 挂了」是本服务最可能的故障模式，
        // 它用 503 表达，但响应体是完整快照 —— 当异常抛掉就丢掉了排查信息。
        var handler = new CapturingHandler(_ => Stub.Json(
            """{"status":"degraded","rcon":{"reachable":false,"endpoint":"urania-mc:25575","error":"连接被拒绝"},"whitelist_count":null,"version":"1.0.0"}""",
            HttpStatusCode.ServiceUnavailable));

        var health = await CreateClient(handler).GetHealthAsync();

        Assert.False(health.IsHealthy);
        Assert.Equal("degraded", health.Status);
        Assert.Equal("连接被拒绝", health.Rcon!.Error);
        Assert.Null(health.WhitelistCount);
    }

    [Fact]
    public async Task Health_UnexpectedStatusThrows()
    {
        var handler = new CapturingHandler(_ => Stub.Error(HttpStatusCode.Forbidden, EliCloudErrorCodes.Forbidden));

        await Assert.ThrowsAsync<EliCloudApiException>(() => CreateClient(handler).GetHealthAsync());
    }

    // -------------------------------------------------------------- 管理端点

    [Fact]
    public async Task Admin_ListNamesSendsAdminTokenAndPagination()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """{"total":1,"limit":50,"offset":10,"names":[{"id":"mn_0001","name":"a","sub":"user_0001","removed_by":null}]}"""));

        var page = await CreateAdminClient(handler).ListNamesAsync(status: "active", limit: 50, offset: 10);

        var request = handler.Single();
        Assert.Equal("/mc/v1/admin/names", request.Path);
        Assert.Equal("admin-token", request.Request.Headers.Authorization?.Parameter);
        Assert.Contains("status=active", request.Query, StringComparison.Ordinal);
        Assert.Contains("limit=50", request.Query, StringComparison.Ordinal);
        Assert.Contains("offset=10", request.Query, StringComparison.Ordinal);

        Assert.Equal(1, page.Total);
        Assert.Equal("user_0001", page.Names[0].Sub);
    }

    [Fact]
    public async Task Admin_ListAuditParsesNestedDetail()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """
            {"total":1,"limit":100,"offset":0,
             "logs":[{"id":"au_1","sub":"user_0001","action":"add","target":"probetester","result":"ok",
                      "detail":{"reply":"Added probetester to the whitelist","whitelist_size":1},
                      "request_ip":"1.2.3.4","created_at":"2026-10-06T00:50:00Z"}]}
            """));

        var page = await CreateAdminClient(handler).ListAuditAsync(userId: "user_0001", action: "add");

        Assert.Contains("user_id=user_0001", handler.Single().Query, StringComparison.Ordinal);
        Assert.Equal("ok", page.Logs[0].Result);
        Assert.Equal("Added probetester to the whitelist", page.Logs[0].Detail!.Value.GetProperty("reply").GetString());
    }

    [Fact]
    public async Task Admin_RemoveNameUsesAdminPath()
    {
        var handler = new CapturingHandler(_ => Stub.Json("""{"id":"mn_0001","status":"removed","removed_by":"admin"}"""));

        var entry = await CreateAdminClient(handler).RemoveNameAsync("mn_0001");

        Assert.Equal("/mc/v1/admin/names/mn_0001", handler.Single().Path);
        Assert.Equal("admin", entry.RemovedBy);
    }

    [Fact]
    public async Task Admin_ReconcileReportsDriftAndNeverAutoDeletesExtras()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """
            {"whitelist":["a","handmade"],"db_active":["a","b"],"missing":["b"],"extra":["handmade"],
             "applied":[],"failed":[],"apply":false}
            """));

        var report = await CreateAdminClient(handler).ReconcileAsync();

        var request = handler.Single();
        Assert.Equal("POST", request.Method);
        Assert.Equal("/mc/v1/admin/reconcile", request.Path);
        Assert.Equal("False", request.JsonFields()["apply"]);

        Assert.True(report.HasDrift);
        Assert.Equal(["b"], report.Missing);
        Assert.Equal(["handmade"], report.Extra);
        Assert.Empty(report.Applied);
    }

    [Fact]
    public async Task Admin_ReconcileApplySendsTrue()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """{"whitelist":["a","b"],"db_active":["a","b"],"missing":[],"extra":[],"applied":["b"],"failed":[],"apply":true}"""));

        var report = await CreateAdminClient(handler).ReconcileAsync(apply: true);

        Assert.Equal("True", handler.Single().JsonFields()["apply"]);
        Assert.True(report.Apply);
        Assert.Equal(["b"], report.Applied);
    }

    [Fact]
    public async Task Admin_ReconcileReportsFailuresAsStructuredItems()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """{"whitelist":[],"db_active":["b"],"missing":["b"],"extra":[],"applied":[],"failed":[{"name":"b","error":"回读未确认"}],"apply":true}"""));

        var report = await CreateAdminClient(handler).ReconcileAsync(apply: true);

        Assert.Single(report.Failed);
        Assert.Equal("b", report.Failed[0].Name);
        Assert.Equal("回读未确认", report.Failed[0].Error);
    }

    [Fact]
    public void Admin_RejectsEmptyAdminToken()
    {
        var handler = new CapturingHandler(_ => Stub.Json("{}"));

        Assert.Throws<ArgumentException>(() => new McAdminClient(
            TestOptions.HttpClient(handler), TestOptions.Create(), "  "));
    }

    // ------------------------------------------------------------------ 工具

    private static McClient CreateClient(CapturingHandler handler)
        => new(TestOptions.HttpClient(handler), TestOptions.Create());

    private static McAdminClient CreateAdminClient(CapturingHandler handler)
        => new(TestOptions.HttpClient(handler), TestOptions.Create(), "admin-token");
}

/// <summary>平台服务清单（<c>/core/v1/services</c>，服务尚未上线）。</summary>
public sealed class MainApiClientTests
{
    [Fact]
    public async Task ListServices_ParsesOnlineFlags()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """
            {"services":[{"id":"pdf-decrypt","name":"PDF 解密","path":"/pdf-decrypt","online":true},
                         {"id":"chatroom","name":"聊天室","path":"/chatroom","online":false}]}
            """));

        var services = await new Sdk.MainApi.MainApiClient(
            TestOptions.HttpClient(handler), TestOptions.Create()).ListServicesAsync();

        Assert.Equal("/core/v1/services", handler.Single().Path);
        Assert.Equal(2, services.Count);
        Assert.True(services[0].Online);
        Assert.False(services[1].Online);
        Assert.Equal("/pdf-decrypt", services[0].Path);
    }

    [Fact]
    public async Task ListServices_ForwardsOptionalAccessToken()
    {
        var handler = new CapturingHandler(_ => Stub.Json("""{"services":[]}"""));

        await new Sdk.MainApi.MainApiClient(TestOptions.HttpClient(handler), TestOptions.Create())
            .ListServicesAsync("token");

        Assert.Equal("Bearer", handler.Single().Request.Headers.Authorization?.Scheme);
    }
}
