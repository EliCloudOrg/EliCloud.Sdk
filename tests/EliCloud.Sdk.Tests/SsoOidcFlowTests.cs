using System.Net;
using System.Text;
using EliCloud.Sdk.Sso;
using EliCloud.Sdk.Tests.TestHttp;
using Xunit;

namespace EliCloud.Sdk.Tests;

/// <summary>标准 OIDC 端点（<c>/authorize</c>、<c>/token</c>、<c>/device_authorization</c>、<c>/logout</c>）的契约测试。</summary>
public sealed class SsoOidcFlowTests
{
    private const string RedirectUri = "elipese://callback";

    // ------------------------------------------------------------- /authorize

    [Fact]
    public void BuildAuthorizationUrl_IncludesAllRequiredParameters()
    {
        var client = CreateClient(new CapturingHandler(_ => Stub.Json("{}")));
        var pkce = PkceCodeChallenge.Create();

        var url = client.BuildAuthorizationUrl(new AuthorizationRequest
        {
            ClientId = "elipese-app",
            RedirectUri = RedirectUri,
            Scope = "openid profile offline_access",
            State = "state-123",
            Nonce = "nonce-456",
            CodeChallenge = pkce.CodeChallenge,
        });

        var query = ParseQuery(url.Query);

        Assert.Equal("https://elicloud.test/auth/authorize", url.GetLeftPart(UriPartial.Path));
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("elipese-app", query["client_id"]);
        Assert.Equal(RedirectUri, query["redirect_uri"]);
        Assert.Equal("openid profile offline_access", query["scope"]);
        Assert.Equal("state-123", query["state"]);
        Assert.Equal("nonce-456", query["nonce"]);
        Assert.Equal(pkce.CodeChallenge, query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);

        // 平台只接受 S256；SDK 不应允许调用方悄悄降级到 plain。
        Assert.DoesNotContain("prompt", query.Keys);
    }

    [Fact]
    public void BuildAuthorizationUrl_OmitsChallengeMethodWhenNoChallenge()
    {
        var client = CreateClient(new CapturingHandler(_ => Stub.Json("{}")));

        var url = client.BuildAuthorizationUrl(new AuthorizationRequest
        {
            ClientId = "elipese-web",
            RedirectUri = "https://example.com/callback",
        });

        var query = ParseQuery(url.Query);

        Assert.Equal("openid", query["scope"]);
        Assert.DoesNotContain("code_challenge", query.Keys);
        Assert.DoesNotContain("code_challenge_method", query.Keys);
    }

    [Fact]
    public void BuildAuthorizationUrl_PercentEncodesRedirectUri()
    {
        var client = CreateClient(new CapturingHandler(_ => Stub.Json("{}")));

        var url = client.BuildAuthorizationUrl(new AuthorizationRequest
        {
            ClientId = "elipese-web",
            RedirectUri = "https://example.com/callback?tenant=a b",
            State = "a&b=c",
        });

        // 查询串必须整体编码：未编码的 & 会把 state 拆成另一个参数。
        Assert.Contains("redirect_uri=https%3A%2F%2Fexample.com%2Fcallback%3Ftenant%3Da%20b", url.Query, StringComparison.Ordinal);
        Assert.Contains("state=a%26b%3Dc", url.Query, StringComparison.Ordinal);
        Assert.Equal("a&b=c", ParseQuery(url.Query)["state"]);
    }

    [Fact]
    public void BuildAuthorizationUrl_SupportsPromptNoneForSilentRenewal()
    {
        var client = CreateClient(new CapturingHandler(_ => Stub.Json("{}")));

        var url = client.BuildAuthorizationUrl(new AuthorizationRequest
        {
            ClientId = "elipese-web",
            RedirectUri = "https://example.com/callback",
            Prompt = "none",
        });

        Assert.Equal("none", ParseQuery(url.Query)["prompt"]);
    }

    [Fact]
    public void BuildAuthorizationUrl_RejectsEmptyScope()
    {
        var client = CreateClient(new CapturingHandler(_ => Stub.Json("{}")));

        Assert.Throws<ArgumentException>(() => client.BuildAuthorizationUrl(new AuthorizationRequest
        {
            ClientId = "elipese-web",
            RedirectUri = "https://example.com/callback",
            Scope = "   ",
        }));
    }

    // ---------------------------------------------------------- /token 授权码

    [Fact]
    public async Task ExchangeCode_PublicClientSendsVerifierAndNoSecret()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """{"access_token":"at","token_type":"Bearer","expires_in":3600,"refresh_token":"rt","id_token":"idt","scope":"openid profile offline_access"}"""));

        var response = await CreateClient(handler).ExchangeCodeAsync(new AuthorizationCodeTokenRequest
        {
            Code = "code-abc",
            CodeVerifier = "verifier-value",
            RedirectUri = RedirectUri,
            ClientId = "elipese-app",
        });

        var request = handler.Single();
        Assert.Equal("POST", request.Method);
        Assert.Equal("/auth/token", request.Path);
        Assert.Equal("application/x-www-form-urlencoded", request.Request.Content?.Headers.ContentType?.MediaType);

        var form = request.Form();
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("code-abc", form["code"]);
        Assert.Equal(RedirectUri, form["redirect_uri"]);
        Assert.Equal("verifier-value", form["code_verifier"]);
        Assert.Equal("elipese-app", form["client_id"]);
        Assert.DoesNotContain("client_secret", form.Keys);

        // 公开客户端不能带 Authorization 头：服务端把「同时用多种认证方式」判为 invalid_request。
        Assert.Null(request.Request.Headers.Authorization);

        Assert.Equal("at", response.AccessToken);
        Assert.Equal("idt", response.IdToken);
        Assert.Equal("rt", response.RefreshToken);
    }

    [Fact]
    public async Task ExchangeCode_ConfidentialClientUsesBasicWithoutMixingForms()
    {
        var handler = new CapturingHandler(_ => Stub.Json("""{"access_token":"at","expires_in":3600}"""));

        await CreateClient(handler).ExchangeCodeAsync(new AuthorizationCodeTokenRequest
        {
            Code = "code-abc",
            RedirectUri = RedirectUri,
            ClientId = "backend",
            ClientSecret = "s3cret",
        });

        var request = handler.Single();
        var authorization = request.Request.Headers.Authorization;

        Assert.Equal("Basic", authorization?.Scheme);

        // RFC 6749 §2.3.1：Basic 的凭据要先各自 form-urlencode 再 base64。
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization!.Parameter!));
        Assert.Equal("backend:s3cret", decoded);

        // 不得同时在表单里出现客户端凭据。
        var form = request.Form();
        Assert.DoesNotContain("client_id", form.Keys);
        Assert.DoesNotContain("client_secret", form.Keys);
    }

    [Fact]
    public async Task ExchangeCode_BasicAuthUrlEncodesSpecialCharactersInSecret()
    {
        var handler = new CapturingHandler(_ => Stub.Json("""{"access_token":"at","expires_in":3600}"""));

        await CreateClient(handler).ExchangeCodeAsync(new AuthorizationCodeTokenRequest
        {
            Code = "code",
            RedirectUri = RedirectUri,
            ClientId = "backend",
            ClientSecret = "p@ss word:1/+",
        });

        var authorization = handler.Single().Request.Headers.Authorization!;
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization.Parameter!));

        // 直接 base64("id:secret") 是常见错误：含 ':' 或非 ASCII 的 secret 会认证失败。
        Assert.Equal("backend:p%40ss%20word%3A1%2F%2B", decoded);
    }

    [Fact]
    public async Task ExchangeCode_ClientSecretPostPutsCredentialsInForm()
    {
        var handler = new CapturingHandler(_ => Stub.Json("""{"access_token":"at","expires_in":3600}"""));

        await CreateClient(handler).ExchangeCodeAsync(new AuthorizationCodeTokenRequest
        {
            Code = "code",
            RedirectUri = RedirectUri,
            ClientId = "backend",
            ClientSecret = "s3cret",
            AuthenticationMethod = ClientAuthenticationMethod.ClientSecretPost,
        });

        var request = handler.Single();
        Assert.Null(request.Request.Headers.Authorization);

        var form = request.Form();
        Assert.Equal("backend", form["client_id"]);
        Assert.Equal("s3cret", form["client_secret"]);
    }

    [Fact]
    public async Task ExchangeCode_MapsInvalidGrant()
    {
        var handler = new CapturingHandler(_ => Stub.Error(
            HttpStatusCode.BadRequest, EliCloudErrorCodes.InvalidGrant, "授权码无效或已过期"));

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).ExchangeCodeAsync(new AuthorizationCodeTokenRequest
            {
                Code = "used",
                RedirectUri = RedirectUri,
                ClientId = "elipese-app",
            }));

        Assert.Equal(EliCloudErrorCodes.InvalidGrant, exception.Error);
        Assert.Equal(400, exception.StatusCode);
    }

    // ------------------------------------------------------------- /token 刷新

    [Fact]
    public async Task RefreshTokenGrant_SendsClientIdAndNarrowedScope()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """{"access_token":"at2","expires_in":3600,"refresh_token":"rt2","scope":"openid profile"}"""));

        var response = await CreateClient(handler).RefreshTokenAsync(new RefreshTokenGrantRequest
        {
            RefreshToken = "rt1",
            ClientId = "elipese-app",
            Scope = "openid profile",
        });

        var form = handler.Single().Form();
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("rt1", form["refresh_token"]);
        Assert.Equal("elipese-app", form["client_id"]);
        Assert.Equal("openid profile", form["scope"]);

        Assert.Equal("rt2", response.RefreshToken);
    }

    [Fact]
    public async Task RefreshTokenGrant_MapsScopeEscalationRejection()
    {
        var handler = new CapturingHandler(_ => Stub.Error(
            HttpStatusCode.BadRequest, EliCloudErrorCodes.InvalidScope, "不得扩大 scope"));

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).RefreshTokenAsync(new RefreshTokenGrantRequest
            {
                RefreshToken = "rt",
                ClientId = "elipese-app",
                Scope = "openid mc:whitelist admin:everything",
            }));

        Assert.Equal(EliCloudErrorCodes.InvalidScope, exception.Error);
    }

    // --------------------------------------------------- /device_authorization

    [Fact]
    public async Task StartDeviceAuthorization_ReturnsUserCodeAndVerificationUris()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """
            {"device_code":"dc_secret","user_code":"WDJB-MJHT",
             "verification_uri":"https://elicloud.test/auth/device",
             "verification_uri_complete":"https://elicloud.test/auth/device?user_code=WDJB-MJHT",
             "expires_in":600,"interval":5}
            """));

        var authorization = await CreateClient(handler).StartDeviceAuthorizationAsync(
            "elicloud-cli", "openid profile offline_access");

        var request = handler.Single();
        Assert.Equal("/auth/device_authorization", request.Path);

        var form = request.Form();
        Assert.Equal("elicloud-cli", form["client_id"]);
        Assert.Equal("openid profile offline_access", form["scope"]);

        Assert.Equal("WDJB-MJHT", authorization.UserCode);
        Assert.Equal("dc_secret", authorization.DeviceCode);
        Assert.Equal(600, authorization.ExpiresIn);
        Assert.Equal(5, authorization.Interval);
        Assert.Contains("user_code=WDJB-MJHT", authorization.VerificationUriComplete, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartDeviceAuthorization_RejectsInvalidClient()
    {
        var handler = new CapturingHandler(_ => Stub.Error(
            HttpStatusCode.BadRequest, EliCloudErrorCodes.InvalidClient, "客户端不存在"));

        var exception = await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).StartDeviceAuthorizationAsync("nope", "openid"));

        Assert.Equal(EliCloudErrorCodes.InvalidClient, exception.Error);
    }

    // ------------------------------------------------------------ /token 轮询

    [Fact]
    public async Task PollDeviceCode_PendingIsNotAnException()
    {
        var handler = new CapturingHandler(_ => Stub.Error(
            HttpStatusCode.BadRequest, EliCloudErrorCodes.AuthorizationPending, "用户尚未确认"));

        var result = await CreateClient(handler).PollDeviceCodeAsync("dc", "elicloud-cli");

        Assert.False(result.IsSuccess);
        Assert.True(result.IsPending);
        Assert.False(result.IsTerminal);

        var form = handler.Single().Form();
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", form["grant_type"]);
        Assert.Equal("dc", form["device_code"]);
    }

    [Fact]
    public async Task PollDeviceCode_SlowDownIsNotAnException()
    {
        var handler = new CapturingHandler(_ => Stub.Error(HttpStatusCode.BadRequest, EliCloudErrorCodes.SlowDown));

        var result = await CreateClient(handler).PollDeviceCodeAsync("dc", "elicloud-cli");

        Assert.True(result.IsSlowDown);
        Assert.False(result.IsTerminal);
    }

    [Theory]
    [InlineData(EliCloudErrorCodes.AccessDenied)]
    [InlineData(EliCloudErrorCodes.ExpiredToken)]
    [InlineData(EliCloudErrorCodes.InvalidGrant)]
    public async Task PollDeviceCode_TerminalErrorsAreReported(string errorCode)
    {
        var handler = new CapturingHandler(_ => Stub.Error(HttpStatusCode.BadRequest, errorCode));

        var result = await CreateClient(handler).PollDeviceCodeAsync("dc", "elicloud-cli");

        Assert.True(result.IsTerminal);
        Assert.Equal(errorCode, result.Error);
    }

    [Fact]
    public async Task PollDeviceCode_SuccessReturnsTokenSet()
    {
        var handler = new CapturingHandler(_ => Stub.Json(
            """{"access_token":"at","expires_in":3600,"refresh_token":"rt","id_token":"idt","scope":"openid offline_access"}"""));

        var result = await CreateClient(handler).PollDeviceCodeAsync("dc", "elicloud-cli");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Tokens);
        Assert.Equal("at", result.Tokens!.AccessToken);
        Assert.Equal("rt", result.Tokens.RefreshToken);
    }

    [Fact]
    public async Task PollDeviceCode_UnknownErrorStillThrows()
    {
        // 「不是已知的轮询状态」意味着真的出错了，不能静默当成 pending，
        // 否则客户端会一直转圈到超时。
        var handler = new CapturingHandler(_ => Stub.Error(
            HttpStatusCode.ServiceUnavailable, EliCloudErrorCodes.RconUnavailable));

        await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).PollDeviceCodeAsync("dc", "elicloud-cli"));
    }

    [Fact]
    public async Task PollDeviceCode_ServerErrorThrows()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        await Assert.ThrowsAsync<EliCloudApiException>(
            () => CreateClient(handler).PollDeviceCodeAsync("dc", "elicloud-cli"));
    }

    // ------------------------------------------------------------------ /logout

    [Fact]
    public void BuildLogoutUrl_IncludesRegisteredRedirectAndState()
    {
        var client = CreateClient(new CapturingHandler(_ => Stub.Json("{}")));

        var url = client.BuildLogoutUrl(new LogoutRequest
        {
            PostLogoutRedirectUri = "https://example.com/signed-out",
            IdTokenHint = "id.token.hint",
            State = "logout-state",
        });

        var query = ParseQuery(url.Query);

        Assert.Equal("https://elicloud.test/auth/logout", url.GetLeftPart(UriPartial.Path));
        Assert.Equal("https://example.com/signed-out", query["post_logout_redirect_uri"]);
        Assert.Equal("id.token.hint", query["id_token_hint"]);
        Assert.Equal("logout-state", query["state"]);
    }

    [Fact]
    public void BuildLogoutUrl_WithoutParametersIsJustTheEndpoint()
    {
        var client = CreateClient(new CapturingHandler(_ => Stub.Json("{}")));

        var url = client.BuildLogoutUrl(new LogoutRequest());

        Assert.Equal("https://elicloud.test/auth/logout", url.AbsoluteUri);
    }

    [Fact]
    public void BuildLogoutUrl_SupportsClientIdInsteadOfIdTokenHint()
    {
        // 平台允许直接给 client_id（给不保留 id_token 的客户端用），同样要精确匹配注册值。
        var client = CreateClient(new CapturingHandler(_ => Stub.Json("{}")));

        var url = client.BuildLogoutUrl(new LogoutRequest
        {
            PostLogoutRedirectUri = "https://example.com/bye",
            ClientId = "elipese-web",
        });

        var query = ParseQuery(url.Query);
        Assert.Equal("elipese-web", query["client_id"]);
        Assert.DoesNotContain("id_token_hint", query.Keys);
    }

    // ------------------------------------------------------------------ 工具

    private static SsoClient CreateClient(CapturingHandler handler)
        => new(TestOptions.HttpClient(handler), TestOptions.Create());

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            result[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return result;
    }
}
