using System.Net;
using System.Text.Json;
using EliCloud.Sdk.Sso;
using EliCloud.Sdk.Tests.TestHttp;
using Xunit;

namespace EliCloud.Sdk.Tests;

/// <summary>
/// SSO 私有端点（<c>/auth/register</c>、<c>/auth/login</c>、<c>/auth/refresh</c>、<c>/auth/userinfo</c>）
/// 的契约测试：请求形状、响应解析、错误映射。
/// </summary>
public sealed class SsoPrivateEndpointTests
{
    [Fact]
    public async Task Register_PostsExpectedShapeAndParsesUser()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """{"user":{"id":"user_0001","username":"alice","email":"alice@example.com","created_at":"2026-01-01T00:00:00Z"}}""",
            HttpStatusCode.Created));

        var user = await CreateClient(handler).RegisterAsync("alice", "S3cret!pass", "alice@example.com");

        var request = handler.Single();
        Assert.Equal("POST", request.Method);
        Assert.Equal("/auth/register", request.Path);

        var body = request.JsonFields();
        Assert.Equal("alice", body["username"]);
        Assert.Equal("S3cret!pass", body["password"]);
        Assert.Equal("alice@example.com", body["email"]);

        Assert.Equal("user_0001", user.Id);
        Assert.Equal("alice", user.Username);
        Assert.Equal("alice@example.com", user.Email);
    }

    [Fact]
    public async Task Register_OmitsEmailWhenNotProvided()
    {
        var handler = new CapturingHandler(_ => Stub.Json("""{"user":{"id":"user_0002","username":"bob"}}""", HttpStatusCode.Created));

        await CreateClient(handler).RegisterAsync("bob", "S3cret!pass");

        // 服务端对未知字段返回 400，所以可选字段必须真的被省略，而不是写成 null。
        Assert.DoesNotContain("email", handler.Single().JsonFields().Keys);
    }

    [Fact]
    public async Task Register_MapsConflictToApiException()
    {
        var handler = new CapturingHandler(_ => Stub.Error(
            HttpStatusCode.Conflict, EliCloudErrorCodes.UserExists, "该用户名已被占用"));

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).RegisterAsync("alice", "S3cret!pass"));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal(EliCloudErrorCodes.UserExists, exception.Error);
        Assert.Equal("该用户名已被占用", exception.ErrorDescription);
    }

    [Fact]
    public async Task Login_PostsCredentialsAndParsesTokens()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """
            {"access_token":"header.payload.signature","token_type":"Bearer","expires_in":3600,
             "refresh_token":"rt_abc","scope":"openid profile email pdf:read pdf:write mc:whitelist",
             "user":{"id":"user_0001","username":"alice"}}
            """));

        var result = await CreateClient(handler).LoginAsync("alice", "S3cret!pass");

        var request = handler.Single();
        Assert.Equal("POST", request.Method);
        Assert.Equal("/auth/login", request.Path);
        Assert.Equal("alice", request.JsonFields()["username"]);

        Assert.Equal("header.payload.signature", result.AccessToken);
        Assert.Equal("rt_abc", result.RefreshToken);
        Assert.Equal(3600, result.ExpiresIn);
        Assert.Equal("user_0001", result.User?.Id);
    }

    [Fact]
    public async Task Login_FailureDoesNotRevealWhetherUserExists()
    {
        // 服务端对「用户不存在」与「密码错误」返回逐字相同的响应；SDK 原样透传，不做任何补充判断。
        var handler = new CapturingHandler(_ => Stub.Error(
            HttpStatusCode.Unauthorized, "invalid_grant", "用户名或密码错误"));

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).LoginAsync("nobody", "whatever"));

        Assert.Equal(401, exception.StatusCode);
        Assert.Equal("用户名或密码错误", exception.ErrorDescription);
    }

    [Fact]
    public async Task RateLimited_ParsesRetryAfter()
    {
        var handler = new CapturingHandler(_ => Stub.Error(
            HttpStatusCode.TooManyRequests, "too_many_requests", "尝试过于频繁", retryAfter: TimeSpan.FromMinutes(15)));

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).LoginAsync("alice", "bad"));

        Assert.True(exception.IsRateLimited);
        Assert.Equal(TimeSpan.FromMinutes(15), exception.RetryAfter);
    }

    [Fact]
    public async Task Refresh_PostsRefreshTokenAndReturnsRotatedToken()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """{"access_token":"new.access.token","token_type":"Bearer","expires_in":3600,"refresh_token":"rt_new"}"""));

        var result = await CreateClient(handler).RefreshAsync("rt_old");

        var request = handler.Single();
        Assert.Equal("/auth/refresh", request.Path);
        Assert.Equal("rt_old", request.JsonFields()["refresh_token"]);

        // 轮换：调用方必须保存新令牌，旧令牌已失效。
        Assert.Equal("rt_new", result.RefreshToken);
        Assert.Equal("new.access.token", result.AccessToken);
    }

    [Fact]
    public async Task UserInfo_GetSendsBearerToken()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """{"sub":"user_0001","preferred_username":"alice","email":"alice@example.com","email_verified":false,"scope":"openid profile email"}"""));

        var userInfo = await CreateClient(handler).GetUserInfoAsync("access-token-value");

        var request = handler.Single();
        Assert.Equal("GET", request.Method);
        Assert.Equal("/auth/userinfo", request.Path);
        Assert.Equal("Bearer", request.Request.Headers.Authorization?.Scheme);
        Assert.Equal("access-token-value", request.Request.Headers.Authorization?.Parameter);

        Assert.Equal("user_0001", userInfo.Sub);
        Assert.Equal("alice", userInfo.PreferredUsername);
        Assert.Equal("alice@example.com", userInfo.Email);
        Assert.False(userInfo.EmailVerified);
    }

    [Fact]
    public async Task UserInfo_PostIsSupportedAndHasNoBody()
    {
        var handler = new CapturingHandler(_ => Stub.Json("""{"sub":"user_0001","scope":"openid"}"""));

        await CreateClient(handler).GetUserInfoAsync("access-token-value", usePost: true);

        var request = handler.Single();
        Assert.Equal("POST", request.Method);
        Assert.Equal("/auth/userinfo", request.Path);
        Assert.True(string.IsNullOrEmpty(request.Body));
    }

    [Fact]
    public async Task UserInfo_OmitsClaimsNotGrantedByScope()
    {
        // 只有 openid scope：既没有 preferred_username 也没有 email，SDK 不应把它们编造成空串。
        var handler = new CapturingHandler(_ => Stub.Json("""{"sub":"user_0001","scope":"openid"}"""));

        var userInfo = await CreateClient(handler).GetUserInfoAsync("token");

        Assert.Equal("user_0001", userInfo.Sub);
        Assert.Null(userInfo.PreferredUsername);
        Assert.Null(userInfo.Email);
    }

    [Fact]
    public async Task HtmlErrorPage_IsReportedWithStatusAndWithoutStructuredError()
    {
        // 被网关拦下（或代理返回 HTML）时拿不到平台的错误结构：
        // 状态码仍然是判据，但 Error 为 null —— 调用方据此知道「这不是业务错误」。
        var handler = new CapturingHandler(_ => Stub.Html("<html><body>502 Bad Gateway</body></html>"));

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).LoginAsync("alice", "S3cret!pass"));

        Assert.Equal(502, exception.StatusCode);
        Assert.Null(exception.Error);
        Assert.Contains("502 Bad Gateway", exception.ErrorDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonJsonSuccessBody_SurfacesAsMalformedResponse()
    {
        var handler = new CapturingHandler(_ => Stub.Json("<html>not json</html>"));

        await Assert.ThrowsAsync<EliCloudMalformedResponseException>(
            () => CreateClient(handler).GetUserInfoAsync("token"));
    }

    [Fact]
    public async Task ErrorWithoutStructuredBody_StillThrowsApiException()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom", System.Text.Encoding.UTF8, "text/plain"),
        });

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).LoginAsync("alice", "S3cret!pass"));

        Assert.Equal(500, exception.StatusCode);
        Assert.Null(exception.Error);
        Assert.Contains("boom", exception.ErrorDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyArguments_AreRejectedBeforeSending()
    {
        var handler = new CapturingHandler(_ => Stub.Json("{}"));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.LoginAsync("", "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RefreshAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetUserInfoAsync(""));

        Assert.Empty(handler.Captured);
    }

    [Fact]
    public async Task Discovery_ParsesLiveDocumentShape()
    {
        // 这段 JSON 逐字取自线上 https://api.example.com/auth/.well-known/openid-configuration
        const string live = """
        {"issuer":"https://api.example.com/auth",
         "authorization_endpoint":"https://api.example.com/auth/authorize",
         "token_endpoint":"https://api.example.com/auth/token",
         "userinfo_endpoint":"https://api.example.com/auth/userinfo",
         "jwks_uri":"https://api.example.com/auth/.well-known/jwks.json",
         "device_authorization_endpoint":"https://api.example.com/auth/device_authorization",
         "end_session_endpoint":"https://api.example.com/auth/logout",
         "response_types_supported":["code"],
         "response_modes_supported":["query"],
         "grant_types_supported":["authorization_code","refresh_token","urn:ietf:params:oauth:grant-type:device_code"],
         "code_challenge_methods_supported":["S256"],
         "token_endpoint_auth_methods_supported":["none","client_secret_basic","client_secret_post"],
         "id_token_signing_alg_values_supported":["RS256"],
         "subject_types_supported":["public"],
         "scopes_supported":["openid","profile","email","offline_access","pdf:read","pdf:write","mc:whitelist"],
         "claims_supported":["sub","preferred_username","email","email_verified"]}
        """;

        var handler = new CapturingHandler(_ => Stub.Json(live));

        var document = await CreateClient(handler).GetDiscoveryDocumentAsync();

        Assert.Equal("/auth/.well-known/openid-configuration", handler.Single().Path);

        // 逐字段断言：只抽查几项的话，「策略推断名与线缆名不一致」这类问题会漏掉
        // （userinfo_endpoint 就这么漏过一次，最后是线上集成测试抓出来的）。
        Assert.Equal("https://api.example.com/auth", document.Issuer);
        Assert.Equal("https://api.example.com/auth/authorize", document.AuthorizationEndpoint);
        Assert.Equal("https://api.example.com/auth/token", document.TokenEndpoint);
        Assert.Equal("https://api.example.com/auth/userinfo", document.UserInfoEndpoint);
        Assert.Equal("https://api.example.com/auth/.well-known/jwks.json", document.JwksUri);
        Assert.Equal("https://api.example.com/auth/device_authorization", document.DeviceAuthorizationEndpoint);
        Assert.Equal("https://api.example.com/auth/logout", document.EndSessionEndpoint);

        Assert.Equal(["code"], document.ResponseTypesSupported!);
        Assert.Equal(["query"], document.ResponseModesSupported!);
        Assert.Equal(
            ["authorization_code", "refresh_token", "urn:ietf:params:oauth:grant-type:device_code"],
            document.GrantTypesSupported!);
        Assert.Equal(["S256"], document.CodeChallengeMethodsSupported!);
        Assert.Equal(["none", "client_secret_basic", "client_secret_post"], document.TokenEndpointAuthMethodsSupported!);
        Assert.Equal(["RS256"], document.IdTokenSigningAlgValuesSupported!);
        Assert.Equal(["public"], document.SubjectTypesSupported!);
        Assert.Equal(
            ["openid", "profile", "email", "offline_access", "pdf:read", "pdf:write", "mc:whitelist"],
            document.ScopesSupported!);
        Assert.Equal(["sub", "preferred_username", "email", "email_verified"], document.ClaimsSupported!);
    }

    private static SsoClient CreateClient(CapturingHandler handler)
        => new(TestOptions.HttpClient(handler), TestOptions.Create());
}

/// <summary>Rediscovery：SDK 的端点地址必须与平台契约逐字一致。</summary>
public sealed class SsoEndpointAddressTests
{
    [Fact]
    public void Endpoints_MatchPlatformGatewayContract()
    {
        // 平台网关的映射规则：/auth/.well-known/* 只剥前缀；/auth/v1/* 与 /auth/* → /v1/*
        // 所以「对外地址」与「服务内地址」在这里必须分开写清楚（docs/architecture.md §2.2）。
        var handler = new CapturingHandler(_ => Stub.Json("{}"));
        var client = new SsoClient(TestOptions.HttpClient(handler), TestOptions.Create());

        Assert.Equal("https://elicloud.test/auth/.well-known/openid-configuration", client.DiscoveryEndpoint.AbsoluteUri);
        Assert.Equal("https://elicloud.test/auth/.well-known/jwks.json", client.JwksEndpoint.AbsoluteUri);
        Assert.Equal("https://elicloud.test/auth/authorize", client.AuthorizationEndpoint.AbsoluteUri);
        Assert.Equal("https://elicloud.test/auth/token", client.TokenEndpoint.AbsoluteUri);
        Assert.Equal("https://elicloud.test/auth/userinfo", client.UserInfoEndpoint.AbsoluteUri);
        Assert.Equal("https://elicloud.test/auth/device_authorization", client.DeviceAuthorizationEndpoint.AbsoluteUri);
        Assert.Equal("https://elicloud.test/auth/logout", client.EndSessionEndpoint.AbsoluteUri);
        Assert.Equal("https://elicloud.test/auth/register", client.RegisterEndpoint.AbsoluteUri);
        Assert.Equal("https://elicloud.test/auth/login", client.LoginEndpoint.AbsoluteUri);
        Assert.Equal("https://elicloud.test/auth/refresh", client.RefreshEndpoint.AbsoluteUri);
        Assert.Equal("https://elicloud.test/auth/v1/clients", client.ClientsEndpoint.AbsoluteUri);
    }

    [Fact]
    public async Task Jwks_ParsesRsaKeysWithoutTouchingReflection()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """{"keys":[{"kty":"RSA","use":"sig","alg":"RS256","kid":"2026-01","n":"AQAB","e":"AQAB"}]}"""));

        var json = await new SsoClient(TestOptions.HttpClient(handler), TestOptions.Create())
            .GetJsonWebKeySetJsonAsync();

        Assert.Contains("\"kid\":\"2026-01\"", json, StringComparison.Ordinal);
        Assert.Single(JsonDocument.Parse(json).RootElement.GetProperty("keys").EnumerateArray());
    }
}
