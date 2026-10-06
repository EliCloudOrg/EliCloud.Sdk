// EliCloud.Sdk 示例 1：用设备授权流程（RFC 8628）登录一个没有浏览器的客户端。
//
// 这就是 CLI / 桌面工具该走的路径：自己不开登录页，而是把用户送到浏览器里确认。
// 运行前用环境变量指定目标环境，例如：
//   $env:ELICLOUD_BASE = 'https://api.example.com'
//   $env:ELICLOUD_CLIENT_ID = 'elicloud-cli'
//   dotnet run --project samples/EliCloud.Sdk.Sample.DeviceLogin

using EliCloud.Sdk;
using EliCloud.Sdk.Mc;
using EliCloud.Sdk.Sso;
using EliCloud.Sdk.Tokens;

var baseAddress = Environment.GetEnvironmentVariable("ELICLOUD_BASE") ?? "https://api.example.com";
var clientId = Environment.GetEnvironmentVariable("ELICLOUD_CLIENT_ID") ?? "elicloud-cli";

// offline_access 才会拿到 refresh token；mc:whitelist 是调用「我的 MC 白名单」所需的 scope。
// 注意：这两项都必须在该客户端的 allowed_scopes 里注册过，否则 /authorize 会回 invalid_scope。
var scope = Environment.GetEnvironmentVariable("ELICLOUD_SCOPE")
    ?? $"{EliCloudScopes.OpenId} {EliCloudScopes.Profile} {EliCloudScopes.OfflineAccess} {EliCloudScopes.McWhitelist}";

var options = new EliCloudOptions { BaseAddress = new Uri(baseAddress) };

// 超时由 SDK 按请求控制，所以关掉 HttpClient 自己的超时，避免与设备码轮询的长流程互相干扰。
using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var sso = new SsoClient(http, options);

Console.WriteLine($"EliCloud SSO   : {options.SsoBaseAddress}");
Console.WriteLine($"MC 白名单服务  : {options.McBaseAddress}");
Console.WriteLine($"client_id      : {clientId}");
Console.WriteLine($"scope          : {scope}");
Console.WriteLine();

try
{
    // 先用发现文档做一次自检：issuer 与配置不一致是最常见的部署问题，
    // 提前发现比等到验签失败时再排查容易得多。
    var discovery = await sso.GetDiscoveryDocumentAsync();
    Console.WriteLine($"发现文档 issuer: {discovery.Issuer}");
    Console.WriteLine($"JWKS           : {discovery.JwksUri}");
    Console.WriteLine();

    var flow = new DeviceCodeFlow(sso);

    var tokens = await flow.RunAsync(
        new DeviceCodeFlowOptions { ClientId = clientId, Scope = scope },
        prompt =>
        {
            Console.WriteLine("请在浏览器里打开下面的地址并确认本次授权：");
            Console.WriteLine($"  {prompt.VerificationUriComplete ?? prompt.VerificationUri}");
            Console.WriteLine($"  设备码：{prompt.UserCode}");
            Console.WriteLine($"（{prompt.ExpiresIn.TotalMinutes:0} 分钟内有效；本程序正在轮询等待…）");
            Console.WriteLine();
        });

    Console.WriteLine("已获得令牌：");
    Console.WriteLine($"  access_token  : {Preview(tokens.AccessToken)}");
    Console.WriteLine($"  refresh_token : {Preview(tokens.RefreshToken)}");
    Console.WriteLine($"  id_token      : {Preview(tokens.IdToken)}");
    Console.WriteLine($"  scope         : {tokens.Scope}");
    Console.WriteLine($"  过期时间      : {tokens.ExpiresAt:u}（本地时钟推算）");
    Console.WriteLine();

    // 用 access token 调 SSO 的 userinfo —— 注意这里**不能**传 id_token（它的 aud 是 client_id，会被拒）。
    var userInfo = await sso.GetUserInfoAsync(tokens.AccessToken!);
    Console.WriteLine("UserInfo：");
    Console.WriteLine($"  sub                : {userInfo.Sub}");
    Console.WriteLine($"  preferred_username : {userInfo.PreferredUsername}");
    Console.WriteLine($"  email              : {userInfo.Email ?? "(未授予 email scope 或未填邮箱)"}");
    Console.WriteLine();

    // 再调一个业务服务，验证同一个令牌能跨服务使用（业务服务会自行用 JWKS 验签）。
    var whitelist = new McClient(http, options);
    var health = await whitelist.GetHealthAsync();
    Console.WriteLine($"MC 白名单服务状态：{health.Status}（RCON 可达：{health.Rcon?.Reachable}）");

    var identity = await whitelist.GetIdentityAsync(tokens.AccessToken!);
    Console.WriteLine($"MC 服务识别到的身份：{identity.Username}（{identity.Sub}）");

    var mine = await whitelist.ListMyNamesAsync(tokens.AccessToken!);
    Console.WriteLine($"我的 MC 用户名：{mine.Names.Count} / 上限 {mine.Quota?.Limit}（剩余 {mine.Quota?.Remaining}）");
    foreach (var entry in mine.Names)
    {
        Console.WriteLine($"  - {entry.NameDisplay}（{entry.Status}）备注：{entry.Note}");
    }

    Console.WriteLine();
    Console.WriteLine("完成。令牌只打印了前几位；真实客户端应把 refresh_token 存进系统凭据库。");
}
catch (EliCloudApiException exception)
{
    Console.Error.WriteLine($"EliCloud 返回错误：HTTP {exception.StatusCode} {exception.Error}");
    Console.Error.WriteLine(exception.ErrorDescription);

    if (exception.IsInsufficientScope)
    {
        Console.Error.WriteLine("提示：需要重新登录以取得带该 scope 的令牌，或让管理员把 scope 加进客户端的 allowed_scopes。");
    }

    return 1;
}
catch (TimeoutException exception)
{
    Console.Error.WriteLine($"超时：{exception.Message}");
    return 1;
}

return 0;

// 令牌原文绝不整串打印，日志与终端里只留可辨认的前缀。
static string Preview(string? value)
    => string.IsNullOrEmpty(value) ? "(无)" : value.Length <= 12 ? value : value[..12] + "…";
