// EliCloud.Sdk 示例 3：静态门面 —— 最省事的用法。
//
// 它只做两件事：一行配置，然后「按 id 取服务、用令牌调方法」。
// 这里刻意只用**匿名端点**，所以不需要账号也能跑通，你可以直接：
//
//   dotnet run --project samples/EliCloud.Sdk.Sample.QuickStart
//
// 想看完整登录流程见 Sample.DeviceLogin；想看服务端校验见 Sample.ResourceServer。

using EliCloud.Sdk;
using EliCloud.Sdk.Sso;

// ── 1) 一次配置 ───────────────────────────────────────────────────────────
// 不配也能用（默认就是当前 IP 阶段的入口）。阶段切到域名时只改这一行。
var baseAddress = Environment.GetEnvironmentVariable("ELICLOUD_BASE") ?? "https://api.example.com";
EliCloudService.Configure(options => options.BaseAddress = new Uri(baseAddress));

// ── 2) 取服务 ─────────────────────────────────────────────────────────────
// 服务名就是平台的服务名（容器名）：sso、mc、main-api、pdf-decrypt。
// 注意它和对外前缀不是一回事：sso 挂在前缀 /auth 下。
// 两种写法等价，任选：
//   · 便捷属性：EliCloudService.Sso
//   · 统一入口：EliCloudService.GetProvider("sso").AsSso()
var sso = EliCloudService.Sso;

Console.WriteLine($"平台入口      : {EliCloudService.Options.BaseAddress}");
Console.WriteLine($"SSO 服务基址  : {EliCloudService.GetProvider("sso").BaseAddress}");
Console.WriteLine($"MC 服务基址   : {EliCloudService.GetProvider("mc").BaseAddress}");
Console.WriteLine();

// ── 3) 匿名调用（不需要令牌）──────────────────────────────────────────────
var discovery = await sso.GetDiscoveryDocumentAsync();
Console.WriteLine($"发现文档 issuer : {discovery.Issuer}");
Console.WriteLine($"令牌端点        : {discovery.TokenEndpoint}");
Console.WriteLine($"支持 scope      : {string.Join(" ", discovery.ScopesSupported ?? [])}");

var keys = await sso.GetJsonWebKeySetAsync();
Console.WriteLine($"JWKS 公钥       : {keys.GetSigningKeys().Count} 把，kid = {string.Join(", ", keys.GetSigningKeys().Select(key => key.KeyId))}");

// ── 4) 令牌逐次传入，客户端本身无状态 ─────────────────────────────────────
// 拿真令牌只需要一句（需要账号，所以这里没执行）：
//     var login = await EliCloudService.Sso.LoginAsync("alice", "S3cret!pass");
//     var mine  = await EliCloudService.GetProvider("mc").AsMc()
//                     .ListMyNamesAsync(login.AccessToken);
//
// 换成坏令牌，看看错误是不是一眼能懂 —— 这是「按 Error 分支、不按文案分支」的依据：
try
{
    await sso.GetUserInfoAsync("not-a-real-token");
}
catch (EliCloudApiException exception)
{
    Console.WriteLine();
    Console.WriteLine($"带坏令牌调用（预期失败）: HTTP {exception.StatusCode} / {exception.Error}");
    Console.WriteLine($"  服务端说明 : {exception.ErrorDescription}");
    Console.WriteLine($"  IsInvalidToken = {exception.IsInvalidToken}   ← 调用方据此决定「重新登录」还是「申请授权」");
}

// MC 白名单服务代码已完成但尚未部署（docs/mc.md §12.5），
// 所以这里只展示它的地址，不发请求；服务上线后上面第 4 步的注释代码即可直接工作。
Console.WriteLine();
Console.WriteLine($"MC 白名单健康检查地址: {EliCloudService.Mc.HealthEndpoint}（服务待部署）");
