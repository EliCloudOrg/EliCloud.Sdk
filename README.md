# EliCloud.Sdk（.NET 10）

EliCloud 平台的官方 .NET SDK。覆盖**当前已实现并上线的全部服务**，并把平台文档里的
硬约束（issuer 逐字一致、RS256、`aud` 区分 access/id token、scope 整词匹配、
`sub` 做数据隔离）直接编进类型与校验逻辑里，而不是留给调用方自己记。

```text
EliCloud.Sdk              客户端 SDK：SSO/OIDC 流程、MC 白名单、平台服务清单，以及 access token 校验
EliCloud.Sdk.AspNetCore   把 token 校验接进 ASP.NET Core：认证处理器 + scope 授权策略
```

- 目标框架：**net10.0**（`Directory.Build.props` 里统一固定）
- 依赖极少：`Microsoft.IdentityModel.JsonWebTokens`/`Tokens`（JWT 解析与验签）、
  `Microsoft.Extensions.DependencyInjection.Abstractions`/`Options`/`Logging.Abstractions`
- 传输层自己实现（约 200 行）：统一超时、错误信封 → 强类型异常、表单编码、日志脱敏

## 安装（NuGet）

```powershell
dotnet add package EliCloud.Sdk --prerelease             # 客户端 SDK
dotnet add package EliCloud.Sdk.AspNetCore --prerelease  # 资源服务侧（依赖上面那个）
```

当前发布版本：**1.0.0-alpha.1**（预发布）。三点须知：

1. 预发布版不会被 `dotnet add package` 默认选中，必须加 `--prerelease`，
   或写死 `--version 1.0.0-alpha.1`；等正式版发出后 `--prerelease` 就可以去掉。
2. nuget.org 上**同一版本号不可覆盖**（只能 unlist），所以每次发布都要换版本号：
   改 `Directory.Build.props` 的 `VersionSuffix`（预发布）或删掉它（正式版）。
3. 包内自带本 README，nuget.org 详情页直接渲染它 —— 但其中的**相对链接**
   （`samples/…`、`docs/…`）在 nuget.org 上点不通，看示例请在这份源码树里看。

---

## 1. 覆盖范围

| 服务 | 服务名 | 对外前缀 | 状态 | SDK 类型 |
|---|---|---|---|---|
| SSO / 账号中心 | `sso` | `/auth` | ✅ 已上线 | `SsoClient`、`SsoAdminClient`、`DeviceCodeFlow` |
| MC 服务器白名单 | `mc` | `/mc` | 代码完成，待部署 | `McClient`、`McAdminClient` |
| 平台核心 API | `main-api` | `/core` | ⚠️ 规划中，尚未上线 | `MainApiClient`（按契约实现，未线上验证） |
| 业务服务侧的令牌校验 | —— | —— | —— | `EliCloudTokenValidator`、`EliCloud.Sdk.AspNetCore` |

「服务名」就是 `GetProvider(...)` 要传的那个名字（见 §2.4）；它和对外前缀**不是**同一件事。

端点级清单：

| 方法 | 对外路径 | SDK 方法 |
|---|---|---|
| GET | `/auth/.well-known/openid-configuration` | `SsoClient.GetDiscoveryDocumentAsync` |
| GET | `/auth/.well-known/jwks.json` | `SsoClient.GetJsonWebKeySetAsync` / `GetJsonWebKeySetJsonAsync` |
| POST | `/auth/register` | `SsoClient.RegisterAsync` |
| POST | `/auth/login` | `SsoClient.LoginAsync` |
| POST | `/auth/refresh` | `SsoClient.RefreshAsync` |
| GET/POST | `/auth/userinfo` | `SsoClient.GetUserInfoAsync` |
| GET | `/auth/authorize` | `SsoClient.BuildAuthorizationUrl` |
| POST | `/auth/token`（授权码 / 刷新 / 设备码） | `ExchangeCodeAsync` / `RefreshTokenAsync` / `PollDeviceCodeAsync` |
| POST | `/auth/device_authorization` | `SsoClient.StartDeviceAuthorizationAsync`、`DeviceCodeFlow.RunAsync` |
| GET | `/auth/logout` | `SsoClient.BuildLogoutUrl` |
| GET/POST/PATCH/DELETE | `/auth/v1/clients[/{id}]`、`…/{id}/rotate-secret` | `SsoAdminClient` |
| POST/GET/DELETE | `/mc/v1/names`、`/mc/v1/names/{id}` | `McClient` |
| GET | `/mc/v1/me` | `McClient.GetIdentityAsync` |
| GET | `/mc/healthz` | `McClient.GetHealthAsync` |
| GET/DELETE/POST | `/mc/v1/admin/names`、`/audit`、`/reconcile` | `McAdminClient` |
| GET | `/core/v1/services` | `MainApiClient.ListServicesAsync`（未上线） |

---

## 2. 快速开始

### 2.1 客户端（用 EliCloud 账号登录并调用业务 API）

```csharp
using EliCloud.Sdk;
using EliCloud.Sdk.Sso;

var options = new EliCloudOptions
{
    // ⚠️ 阶段切换只改这一行：IP 阶段是 https://api.example.com，
    //    域名可用后是 https://api.example.com。路径前缀与请求体都不变。
    BaseAddress = new Uri("https://api.example.com"),
};

using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // 超时由 SDK 按请求控制
var sso = new SsoClient(http, options);

// CLI / 桌面工具：设备授权流程（RFC 8628）
var tokens = await new DeviceCodeFlow(sso).RunAsync(
    new DeviceCodeFlowOptions { ClientId = "elicloud-cli" },
    prompt => Console.WriteLine($"请在浏览器打开 {prompt.VerificationUriComplete}"));

var me = await sso.GetUserInfoAsync(tokens.AccessToken!);
Console.WriteLine($"你好，{me.PreferredUsername}（{me.Sub}）");
```

完整可运行示例见 [`samples/EliCloud.Sdk.Sample.DeviceLogin`](samples/EliCloud.Sdk.Sample.DeviceLogin)。

### 2.2 业务服务（校验令牌 + 按 `sub` 隔离数据）

```csharp
using EliCloud.Sdk.AspNetCore;
using EliCloud.Sdk.Tokens;

builder.Services.AddEliCloudAuthentication(options =>
{
    // 必须与服务端的 PUBLIC_BASE_URL 逐字相同（当前含 /auth）
    options.Validation.Issuer = "https://api.example.com/auth";
    options.Validation.RequiredScopes = [EliCloudScopes.McWhitelist];
});
builder.Services.AddEliCloudScopePolicy(EliCloudScopes.McWhitelist);

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/v1/me", (HttpContext context) => new
{
    sub = context.User.GetEliCloudSubject(),
    username = context.User.GetEliCloudUsername(),
    scopes = context.User.GetEliCloudScopes(),
})
.RequireAuthorization(EliCloudAuthenticationDefaults.ScopePolicyName(EliCloudScopes.McWhitelist));
```

完整可运行示例见 [`samples/EliCloud.Sdk.Sample.ResourceServer`](samples/EliCloud.Sdk.Sample.ResourceServer)。

### 2.3 依赖注入（可选）

```csharp
builder.Services.AddEliCloud(o => o.BaseAddress = new Uri("https://api.example.com"));

// 管理端点（只在运维工具里用；不要发给前端/移动端）
builder.Services.AddEliCloudAdministration(ssoAdminToken, mcAdminToken);
```

`AddEliCloudAuthentication` 与 `AddEliCloud` 相互独立：资源服务只需要前者。
两者都注册 `HttpClient` 时用 `TryAdd` 语义，**不会覆盖应用自己注册的 `HttpClient`**
（自定义代理、证书、日志处理器都能生效）。

### 2.4 最省事的写法：静态门面

不想在每次调用前拼 `HttpClient` + `EliCloudOptions` 时，用静态门面：

```csharp
using EliCloud.Sdk;
using EliCloud.Sdk.Mc;            // AsMc() 扩展在这个命名空间里
using EliCloud.Sdk.Sso;           // AsSso() 扩展在这个命名空间里

// 1) 一次配置（不配也能用：默认就是当前 IP 阶段的入口）
EliCloudService.Configure(options => options.BaseAddress = new Uri("https://api.example.com"));

// 2) 拿令牌
var login = await EliCloudService.Sso.LoginAsync("alice", "S3cret!pass");
//    也可以写成：await EliCloudService.GetProvider("sso").AsSso().LoginAsync(...)

// 3) 按服务名取服务
var mc = EliCloudService.GetProvider("mc").AsMc();
//    便捷属性等价：EliCloudService.Mc

// 4) 业务调用：令牌逐次传入，客户端本身无状态
var mine = await mc.ListMyNamesAsync(login.AccessToken);
```

完整可运行示例见 [`samples/EliCloud.Sdk.Sample.QuickStart`](samples/EliCloud.Sdk.Sample.QuickStart)
（只打匿名端点，不需要账号就能跑）：

```text
平台入口      : https://api.example.com/
SSO 服务基址  : https://api.example.com/auth/
MC 服务基址   : https://api.example.com/mc/

发现文档 issuer : https://api.example.com/auth
JWKS 公钥       : 1 把，kid = 2026-01

带坏令牌调用（预期失败）: HTTP 401 / invalid_token
  服务端说明 : 令牌格式不合法
  IsInvalidToken = True   ← 调用方据此决定「重新登录」还是「申请授权」
```

#### 服务名的命名规则：一个服务只有一个名字

规则只有一条：**用平台的服务名（容器名）**。

| 服务 | 服务名（= 容器名） | 对外前缀 |
|---|---|---|
| 账号中心 | `sso` | `/auth` |
| MC 服务器白名单 | `mc` | `/mc` |
| 平台核心 API | `main-api` | `/core` |
| PDF 解密（待实现） | `pdf-decrypt` | `/pdf-decrypt` |

> ⚠️ **服务名 ≠ 对外前缀**，别把两者当同一件事：`sso` 挂在前缀 `/auth` 下，
> `main-api` 挂在 `/core` 下。服务名是身份，前缀是位置。
>
> 因此 `GetProvider("auth")` / `GetProvider("core")` 会抛 `KeyNotFoundException` ——
> 它们是对外前缀，不是服务名。测试里有专门一条锁住这个行为。

名字按**原样精确匹配**：区分大小写，不补斜杠，没有别名折算。
传了不认识的名字会抛 `KeyNotFoundException`，消息里列出当前可用的全部名字。

平台的服务清单（`GET /core/v1/services`，`docs/architecture.md` §5.8）返回的 `id` 就是服务名，
所以清单可以直接喂进来（**用 `id`，不是 `path`**）：

```csharp
foreach (var service in await EliCloudService.MainApi.ListServicesAsync())
{
    var provider = EliCloudService.GetProvider(service.Id);   // "mc" / "pdf-decrypt" …
}
```

#### 按服务名取服务是怎么组织的

`GetProvider(服务名)` 返回的是**一个抽象句柄** `IEliCloudService`（只有 `ServiceId`、`BaseAddress`、
`HttpClient`、`Options`），它不认识任何业务类型；具体客户端由**各服务自己命名空间里的扩展方法**给出：

```csharp
// EliCloud.Sdk.Sso
public static SsoClient AsSso(this IEliCloudService service);

// EliCloud.Sdk.Mc
public static McClient AsMc(this IEliCloudService service);

// EliCloud.Sdk.MainApi
public static MainApiClient AsMainApi(this IEliCloudService service);
```

这样设计换来的是：**加服务不用改门面**。新服务只要在自己命名空间里补一个扩展方法即可，
既有「按服务名取服务」的统一入口，又有编译期类型检查与 IntelliSense ——
不需要 `dynamic`（拼错方法名要到运行时才炸）。未来服务照抄这个形状：

```csharp
// 1) 告诉 SDK 它挂在哪
EliCloudService.Register(EliCloudServiceIds.PdfDecrypt, "/pdf-decrypt");

// 2) 在它自己的命名空间里写扩展方法
namespace EliCloud.Sdk.PdfDecrypt;

public static class PdfDecryptServiceExtensions
{
    public static PdfDecryptClient AsPdfDecrypt(this IEliCloudService service)
    {
        service.RequireService(EliCloudServiceIds.PdfDecrypt);   // id 对不上就抛带指引的异常
        return new PdfDecryptClient(service.HttpClient, service.Options);
    }
}
```

`RequireService` 这层校验不是装饰：没有它，`GetProvider("pdf-decrypt").AsMc()`
会安静地把请求发到错误地址，报错信息也会指向一个完全无关的地方。

#### 门面管什么、不管什么

| 只管 | 不管 |
|---|---|
| 入口地址与各服务基址的派生 | 令牌缓存与否 |
| 共享 `HttpClient` 的创建与注入 | 重试策略 |
| 「id → 服务句柄 → 强类型客户端」的解析 | 刷新时机、并发调度 |

后面这些都需要状态，交给调用方或 `Microsoft.Extensions.Http` 那类设施更合适。

**静态状态的代价（请知情）**：一个进程只有一套默认配置。要同时连两个环境
（IP 阶段 + 域名阶段，或本地 mock + 生产）就必须回到 `2.1`/`2.3` 的实例与 DI 用法 ——
门面是**额外加的一层**，不是替换。测试里记得用 `EliCloudService.Reset()` 互不污染。

```csharp
// 也可以指定外部 HttpClient（代理、证书、日志处理器、测试替身）
EliCloudService.UseHttpClient(myHttpClient);

// 直接写 `Sso.GetTokenAsync(...)` 这种短名，可以静态导入：
using static EliCloud.Sdk.EliCloudService;
```

---

## 3. 解决方案结构

```text
EliCloud.Sdk/
├── .github/workflows/
│   ├── ci.yml                     # Linux + Windows 双平台：构建、测试、打包校验
│   └── publish.yml                # tag 或手动触发 → Trusted Publishing 发布到 nuget.org
├── .gitattributes                 # 行尾规范化（.ps1 固定 LF）
├── EliCloud.Sdk.slnx              # 用 `dotnet new sln` + `dotnet sln add` 生成
├── Directory.Build.props          # net10.0 / nullable / warnings-as-errors / 版本与包元数据
├── Directory.Build.targets        # 只对可打包项目生效：README 入包 + .snupkg 符号包
├── Directory.Packages.props       # 中央包版本管理（CPM）
├── LICENSE                        # MIT
├── eng/
│   ├── build.ps1                  # 生成整个解决方案（-Pack 产出 NuGet 包）
│   ├── test.ps1                   # 跑测试（-Runner xunit / -Live）
│   └── publish.ps1                # 发布：build + pack（-Push 则推送，见 §4.2）
├── src/
│   ├── EliCloud.Sdk/
│   │   ├── EliCloudService.cs             # ★ 静态门面：一行配置 + 按服务名取服务
│   │   ├── IEliCloudService.cs            # ★ 服务句柄抽象 + 服务名校验扩展
│   │   ├── EliCloudServiceIds.cs          # ★ 服务名常量（sso / mc / main-api / pdf-decrypt）
│   │   ├── EliCloudOptions.cs             # 连接配置：入口地址 + 各服务前缀
│   │   ├── EliCloudConstants.cs           # aud、前缀、算法、缓存与时钟偏移默认值
│   │   ├── EliCloudScopes.cs              # scope 常量与整词工具
│   │   ├── EliCloudApiException.cs        # 错误码 + 统一异常
│   │   ├── EliCloudRandom.cs              # PKCE verifier / state / nonce 的随机源
│   │   ├── ServiceCollectionExtensions.cs # DI 注册
│   │   ├── Internal/                      # JSON 约定、HTTP 传输层、查询串
│   │   ├── Sso/                           # SsoClient / SsoAdminClient / PKCE / 设备流程
│   │   │   └── SsoServiceExtensions.cs    # ★ AsSso() / AsSsoAdmin()
│   │   ├── Mc/                            # MC 服务器白名单
│   │   │   ├── McClient.cs                #   McClient / McAdminClient
│   │   │   ├── McModels.cs                #   条目 / 名额 / 审计 / 对账 / 健康快照
│   │   │   └── McServiceExtensions.cs     # ★ AsMc() / AsMcAdmin()
│   │   ├── MainApi/                       # 平台核心 API（未上线）
│   │   │   ├── MainApiClient.cs
│   │   │   └── MainApiServiceExtensions.cs # ★ AsMainApi()
│   │   └── Tokens/                        # JWKS 缓存 + access token 校验 + claim 工具
│   └── EliCloud.Sdk.AspNetCore/
│       ├── EliCloudAuthenticationOptions.cs
│       ├── EliCloudAuthenticationHandler.cs   # 认证处理器 + scope 授权 + [EliCloudScope]
│       └── EliCloudAuthenticationExtensions.cs
├── tests/EliCloud.Sdk.Tests/       # 173 个单元/契约测试 + 6 个线上集成测试
└── samples/
    ├── EliCloud.Sdk.Sample.QuickStart/     # 静态门面：最短用法（只打匿名端点，无需账号）
    ├── EliCloud.Sdk.Sample.DeviceLogin/    # 设备授权流程登录
    └── EliCloud.Sdk.Sample.ResourceServer/ # 业务服务侧：校验令牌 + 按 sub 隔离
```

---

## 4. 构建与测试

```powershell
pwsh -File eng/build.ps1                 # 生成整个解决方案
pwsh -File eng/build.ps1 -Pack           # 另外产出 NuGet 包
pwsh -File eng/test.ps1                  # 跑测试（标准 dotnet test）
pwsh -File eng/test.ps1 -Live            # 额外跑打线上环境的集成测试
pwsh -File eng/test.ps1 -Runner xunit    # 用 xunit 控制台运行器（见下）
```

当前结果：**179 个测试全绿**（173 个单元/契约 + 6 个线上冒烟，`-Live` 时）。

### 4.1 两个本机环境注意事项（不是代码问题）

1. **`dotnet test` 在某些受限宿主里会崩**：VSTest 的 testhost 启动时会获取**父进程句柄**
   来监听父进程退出，被拒绝时报 `Win32Exception (5): 拒绝访问`，一条测试都跑不了。
   xunit 控制台运行器不做这件事，所以提供了 `-Runner xunit`。
   （脚本默认的 `dotnet test` 在正常开发机上工作正常。）

2. **`eng/*.ps1` 保持纯 ASCII**：部分宿主会用系统 ANSI 代码页读取无 BOM 的 `.ps1`，
   中文注释会被解码坏，进而报出莫名其妙的「字符串缺少终止符」。
   脚本里的说明因此用英文；中文说明放在本 README 与源码注释里。

### 4.2 发布到 NuGet

发布走 **GitHub Actions + Trusted Publishing（OIDC）**，仓库与 CI 里都不存在任何长期 NuGet
密钥：nuget.org 已停发长期 API key（旧 key 于 2025-11-01 起失效），改为由 CI 拿 GitHub 的
OIDC 令牌换取**一次性、1 小时有效**的临时 key，用完即废。

```text
push tag v1.0.0-alpha.1 ─┐
Actions 页面手动触发     ─┴─→ 跑测试 → NuGet/login@v1 换临时 key → eng/publish.ps1 -Push
```

正常发版：

```powershell
# 1) 改版本号：Directory.Build.props 里的 VersionPrefix / VersionSuffix
# 2) 提交，然后打 tag —— tag 必须与打包出的版本一致，
#    否则 workflow 会在推送前就失败（不会出现 tag v1.2.3 却推 1.0.0）
git tag v1.0.0-alpha.2
git push origin main --tags
```

本地只想要产物：

```powershell
pwsh -File eng/publish.ps1                        # 构建 + 打包，不推
pwsh -File eng/publish.ps1 -VersionSuffix beta.1  # 换个预发布号试打包
```

`-Push` 是应急通道（CI 挂了要手推时用）：key 按 `-ApiKey` → `NUGET_API_KEY` 环境变量 →
`NuGet.Config` 的 `<apikeys>` 段取。注意 nuget.org **不再签发长期 key**，所以这条路
通常只在推私有源（`-Source <自己的源>`）时还有意义。

几条不变的约定：

- 产物固定落在 `artifacts/packages/`：两个 `.nupkg` + 两个 `.snupkg`（符号包）。
- 打包前脚本会**清空** `artifacts/packages/`。`dotnet pack` 是增量的：输出已存在且比输入新时
  整个 PackTask 会被跳过，「按时间戳挑本次产物」会挑空、甚至误推上一次的包。清空之后
  「目录里的一切」必然等于「本次的产物」。
- `-ExpectedVersion` 让版本对不上时**在推送前**失败（见上面的 tag 约定）。
- 先推主包再推符号包：不会出现符号包上线而主包没上线。
- 版本号写在 `Directory.Build.props`；nuget.org 上同一版本号**不可覆盖**（只能 unlist），
  所以每次发布都要换版本号。

---

## 5. 使用指南

### 5.1 连接配置与「IP 阶段 → 域名阶段」

平台把「对外地址」做成环境变量，代码里零硬编码。SDK 侧同样如此：

```csharp
new EliCloudOptions
{
    BaseAddress = new Uri("https://api.example.com"),   // 域名阶段改成 https://api.example.com
    // 各服务前缀默认 /auth、/mc、/core，一般不用改
}
```

派生结果：`SsoBaseAddress = {BaseAddress}/auth/`、`McBaseAddress = {BaseAddress}/mc/`、
`MainApiBaseAddress = {BaseAddress}/core/`。

将来某个服务被拆到独立主机（例如 `mc.example.com`），用
`McBaseAddressOverride` 单点覆盖即可，不必动全局地址。

> **业务服务侧的 `Issuer` 也必须跟着改**（`EliCloudTokenValidationOptions.Issuer`）。
> 切换 issuer 会让旧令牌全部失效，用户需重新登录一次 —— 这是无状态 JWT 的固有代价，
> 平台文档 §0.9 已经写明。

### 5.2 三种登录流程怎么选

| 场景 | 用哪个 | 说明 |
|---|---|---|
| Web 前端 | 授权码 + PKCE | `BuildAuthorizationUrl` → 回跳拿 code → `ExchangeCodeAsync`。前端注册为 public 客户端，**必须**带 PKCE。 |
| 手机 App（系统浏览器 / WebView） | 授权码 + PKCE | 自定义 scheme 回跳（如 `elipese://callback`），`redirect_uri` 必须与注册值**逐字相同**。 |
| CLI / 桌面工具 | **设备授权流程** | `DeviceCodeFlow.RunAsync`，用户在浏览器里输入短码。 |
| 自家 App 开发联调 | 密码直连 | `LoginAsync`。⚠️ 见下方取舍。 |

**授权码 + PKCE 示例**：

```csharp
using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var sso = new SsoClient(http, options);

var pkce = PkceCodeChallenge.Create();
var state = EliCloudRandom.CreateState();
var nonce = EliCloudRandom.CreateNonce();

var authorizeUrl = sso.BuildAuthorizationUrl(new AuthorizationRequest
{
    ClientId = "elipese-web",
    RedirectUri = "https://example.com/callback",
    Scope = $"{EliCloudScopes.OpenId} {EliCloudScopes.Profile} {EliCloudScopes.Email} {EliCloudScopes.McWhitelist}",
    State = state,
    Nonce = nonce,
    CodeChallenge = pkce.CodeChallenge,        // 只接受 S256
});
// 交给浏览器；回跳后校验 state 原样一致，再用同一个 pkce 实例换令牌
var tokens = await sso.ExchangeCodeAsync(new AuthorizationCodeTokenRequest
{
    Code = code,
    CodeVerifier = pkce.CodeVerifier,
    RedirectUri = "https://example.com/callback",
    ClientId = "elipese-web",
});
```

两个容易踩的点，SDK 已经在类型层面处理：

- **只有请求了 `offline_access` 才会拿到 `refresh_token`**（OIDC 惯例）。浏览器类客户端
  通常靠会话 cookie 静默重跑 `/authorize` 续期，不需要长期凭据。
- **`id_token` 不能用来访问 API**：它的 `aud` 是 client_id。业务服务按
  `elicloud-services` 校验 `aud`，所以拿 id_token 调业务 API 会得到 401。

**设备流程**：`DeviceCodeFlow` 实现了服务端明确要求的两条规则 ——
收到 `slow_down` 时把轮询间隔 **+5 秒**（而不是固定间隔硬轮询），并**封顶 60 秒**。
不这么做，客户端会把间隔一路顶到几百秒并永远追不上，等于把自己锁死。

**密码直连的已知缺口**：私有 `POST /v1/logout` 已被平台删除，而密码直连签发的
refresh token 没有会话绑定，**因此无法主动撤销**，只能等 TTL（默认 30 天）自然过期，
或改用授权码流程（`sso/README.md` §18.4）。所以密码直连只适合开发联调。

### 5.3 登出（RP-Initiated Logout）

登出是**浏览器跳转语义**，不是 Bearer 调用：

```csharp
var logoutUrl = sso.BuildLogoutUrl(new LogoutRequest
{
    PostLogoutRedirectUri = "https://example.com/signed-out",  // 必须在客户端注册值内
    IdTokenHint = tokens.IdToken,
    State = "logout-state",
});
```

服务端会清会话 cookie、撤销**该会话派生的整条 refresh 链**（同账号其它设备不受影响）。
`post_logout_redirect_uri` 未注册时**不跳转**（防开放重定向）。
注意：注销后已签发的 access token 在 `exp` 之前仍然有效，这是无状态 JWT 的固有取舍 ——
平台用「短 TTL + refresh 轮换」压缩风险窗口。

### 5.4 令牌校验（业务服务）

```csharp
var validator = new EliCloudTokenValidator(http, new EliCloudTokenValidationOptions
{
    Issuer = "https://api.example.com/auth",       // 必须与服务端 PUBLIC_BASE_URL 逐字相同
    Audience = EliCloudConstants.Audience,        // elicloud-services：顺手拒掉 id_token
    RequiredScopes = [EliCloudScopes.McWhitelist],
});

var result = await validator.ValidateAccessTokenAsync(bearerToken);
if (!result.IsValid)
{
    // invalid_token → 401（重新登录/刷新）；insufficient_scope → 403（申请授权）
    return result.ErrorCode == EliCloudErrorCodes.InsufficientScope
        ? Results.StatusCode(403)
        : Results.Unauthorized();
}

var sub = result.Subject;   // 业务数据隔离的唯一依据
```

逐条对应平台的自查清单（`docs/mc.md` §6.1）：

| 校验 | 行为 |
|---|---|
| 算法 | 白名单只有 `RS256`；`HS256`、`alg:none` 一律拒绝 |
| 签名 | 按 `kid` 从 JWKS 取公钥验签；未知 `kid` 触发一次受限的强制刷新 |
| `iss` | 逐字等于配置的 Issuer |
| `aud` | 必须是 `elicloud-services`（因此 `id_token` 会被拒） |
| `exp`/`nbf` | 标准校验，容忍 30 秒时钟偏移 |
| `scope` | 必需 scope **整词匹配**（`pdf:read` 不会满足 `pdf`） |

**JWKS 缓存**：按服务端 `Cache-Control: max-age` 缓存（默认 300 秒）。
遇到未知 `kid` 会强制刷新一次，但**两次强制刷新之间有最小间隔** ——
否则攻击者只要持续提交带随机 `kid` 的令牌，就能把业务服务变成打向 SSO 的 JWKS 放大器。

**401 与 403 的分工**：令牌无效 → 401；令牌有效但缺 scope → 403。
前者要求重新登录，后者要求申请授权，前端/客户端需要区别对待。

### 5.5 MC 白名单服务

```csharp
var whitelist = new McClient(http, options);

// 提交（幂等：重复提交同名返回已有条目，不会重复写 RCON）
var entry = await whitelist.AddNameAsync(accessToken, "ProbeTester", "帮朋友申请");

// 我的条目与名额
var mine = await whitelist.ListMyNamesAsync(accessToken);
Console.WriteLine($"{mine.Names.Count}/{mine.Quota!.Limit}，剩余 {mine.Quota.Remaining}");

// 撤回（别人的条目一律 404，不泄露存在性）
await whitelist.RemoveNameAsync(accessToken, entry.Id);
```

需要**带 `mc:whitelist` scope** 的令牌，否则拿到 `403 insufficient_scope`
（用 `EliCloudApiException.IsInsufficientScope` 判断）。错误码语义：

| 错误码 | 含义 | 处理建议 |
|---|---|---|
| `name_taken` | 该 MC 用户名已被别人（或管理员手工加入白名单）占用 | 换个名字或联系管理员 |
| `quota_exceeded` | 名额已满（默认 2 个） | 先撤回一个 |
| `rcon_unavailable` | RCON 不可达或写入未被回读确认，**服务端不会落库** | 稍后重试 |
| `insufficient_scope` | 令牌缺 scope | 重新走授权 |

`GetHealthAsync()` 在 RCON 挂掉时会拿到 **503**，但响应体是完整快照 ——
SDK 把它照常解析成 `McHealthStatus`（而不是抛异常），因为「容器在跑但 RCON 挂了」
正是这个服务最可能的故障模式，排查信息不该被丢掉。

### 5.6 客户端静态注册（管理接口）

```csharp
var admin = new SsoAdminClient(http, options, adminToken);

var created = await admin.CreateClientAsync(new CreateOAuthClientRequest
{
    ClientId = "my-tool",
    Name = "我的工具",
    ClientType = EliCloudClientTypes.Public,
    RedirectUris = ["http://127.0.0.1:5173/callback"],
    AllowedScopes = [EliCloudScopes.OpenId, EliCloudScopes.Profile, EliCloudScopes.McWhitelist],
});

// ⚠️ client_secret 只在这两处出现：创建时与轮换时。库里只有哈希，之后无法再取回。
Console.WriteLine(created.ClientSecret);
```

服务端只存哈希；`client_type` 与 `token_endpoint_auth_method` **刻意不可修改**
（要改就删除重建，避免把公开客户端悄悄改强/改弱）。
新增平台 scope 后，**必须给已有客户端 PATCH `allowed_scopes`**，否则它们请求该 scope
会在 `/authorize` 被 `invalid_scope` 拒掉（`docs/mc.md` §12.4 记录过这个跨服务前置条件）。

### 5.7 错误处理

```csharp
try
{
    await sso.LoginAsync(username, password);
}
catch (EliCloudApiException exception) when (exception.IsRateLimited)
{
    await Task.Delay(exception.RetryAfter ?? TimeSpan.FromMinutes(1));
}
catch (EliCloudApiException exception)
{
    logger.LogWarning("EliCloud 拒绝请求：{Status} {Error} {Description}",
        exception.StatusCode, exception.Error, exception.ErrorDescription);
}
catch (EliCloudMalformedResponseException exception)
{
    // 成功状态码 + 非预期响应体：通常是网关/代理返回了 HTML，说明打错了地址
}
```

- `EliCloudApiException` —— 拿到了服务端的结构化错误。**按 `Error` 分支，不要按文案分支。**
- `EliCloudMalformedResponseException` —— 响应体不是约定结构（打错地址、被代理拦下）。
- 网络层失败（连不上、TLS、超时）仍是 `HttpRequestException` / `TimeoutException`。

设备码轮询里的 `authorization_pending` 与 `slow_down` **不抛异常**：
它们是 RFC 8628 定义的正常轮询状态，体现在 `DeviceCodePollResult` 上。

---

## 6. 设计决策

| 决策 | 理由 |
|---|---|
| **传输层自己实现**（不引入 Refit/RestSharp/Flurl） | 这个 SDK 要暴露的是**强类型对象 API**，不是 HTTP 客户端的薄包装。而下面三块正好是通用 REST 库帮不上的：OAuth 表单 + 三种客户端认证方式、设备码轮询的「非异常控制流」、自定义错误信封 → 强类型异常。引入库只会多一套并行的 HTTP 路径。 |
| **不依赖 `Microsoft.AspNetCore.Authentication.JwtBearer`** | 该包**不在** ASP.NET Core 共享框架里，而且它内部会再实现一遍 JWKS 获取与校验。自己写一个约 150 行的 `AuthenticationHandler`，可以让 JWKS 缓存、算法白名单、scope 整词匹配只有**一份**实现（同时被非 Web 场景的 `EliCloudTokenValidator` 复用）。 |
| **JSON 用全局 snake_case 策略，个别字段显式标注** | 服务端字段是 `snake_case`，策略式映射让 C# 侧保持 PascalCase。但策略会猜错少数字段（`UserInfoEndpoint` → `user_info_endpoint`，而服务端发的是 `userinfo_endpoint`），这类必须用 `[JsonPropertyName]` 钉住 —— **这个 bug 就是线上集成测试抓出来的**。 |
| **超时按请求控制，不改 `HttpClient.Timeout`** | 改共享 `HttpClient` 的超时会影响所有调用方，也可能与应用自己的设置冲突。SDK 用每次请求的 `CancellationTokenSource` 计超时，所以一个 `HttpClient` 可以被所有服务客户端共享。 |
| **日志只记方法与路径** | 查询串里有授权码，请求体里有密码与令牌原文。路径足够定位问题，其余一律不记。 |
| **`EliCloudApiException` 而非「HTTP 状态码 + 手工解析」** | 平台所有服务的错误体形状统一，直接映射成可分支的 `Error` 属性最省事，也避免各调用方各写一套解析。 |
| **`aud` 校验默认开启** | 它是「拿 id_token 当通行证」的唯一防线。测试里专门锁住了「关掉 `iss` 校验不会连带关掉 `aud`」。 |
| **便捷门面是「额外一层」，不动原有类型** | `EliCloudService` 只用现有公开 API 组装，客户端类的构造与语义一个字没改。因此实例 API / DI 用法完全不受影响，需要多环境并存或细粒度控制时随时回到它们。 |
| **`AsXxx()` 用扩展方法而不是 `As<T>()` 或 `dynamic`** | 扩展方法可以放在**服务自己的命名空间**里，于是「加服务」不需要改门面，且仍是编译期检查 + IntelliSense。`dynamic` 会把拼错方法名的代价推到运行时；`As<T>()` 则要求门面预先知道所有服务类型。 |
| **类型名与命名空间跟服务名一致** | `EliCloud.Sdk.Mc` + `McClient`/`McAdminClient`、`EliCloud.Sdk.MainApi` + `MainApiClient`、`EliCloud.Sdk.Sso` + `SsoClient`。一眼就能从类型看出它属于哪个服务，也让「按服务名取服务」的门面与类型体系对齐。 |

---

## 7. 已知限制

1. **`MainApiClient`（`/core/v1/services`）未经验证**：该服务在平台文档里有契约但尚未实现、
   尚未部署。SDK 按契约实现并标注清楚，调用时若拿到 404，说明服务没上线，不是 SDK 的问题。
2. **`/pdf-decrypt/*` 未实现**：平台文档只给了路径草图（没有请求/响应契约），
   因此 SDK 未包含该服务。契约定稿后再按 `McClient` 的形状加一个客户端即可。
3. **登录页与设备确认页不在 SDK 范围内**：`/authorize` 与 `/device` 是浏览器页面，
   SDK 只负责拼 URL 与处理 302。
4. **限流器是服务端进程内的**，SDK 侧只做 `Retry-After` 的表面配合。
5. **`Microsoft.IdentityModel.*` 固定 7.1.2**：这是同时存在于 nuget.org 与本机离线镜像的
   最新版本，且已含 CVE-2024-21319（JsonWebTokens 的 DoS）修复。
   要升到 8.x 时改 `Directory.Packages.props` 一处即可（需要联网还原）。

---

## 8. 与平台文档的对应关系

| 平台文档 | 本 SDK 对应 |
|---|---|
| `docs/architecture.md` §0.4/§3.1（issuer 逐字一致、`PUBLIC_BASE_URL` 单一真源） | `EliCloudTokenValidationOptions.Issuer`、`EliCloudOptions.BaseAddress` |
| §2.2（路径前缀与网关重写） | `EliCloudOptions` 的各前缀与派生地址；`SsoEndpointAddressTests` 逐条锁定 |
| §4.1（access token 的 `aud`） | `EliCloudConstants.Audience`、`EliCloudTokenValidator` |
| §5（错误结构与状态码） | `EliCloudApiException`、`EliCloudErrorCodes` |
| §5.6（JWKS 匿名、`kid`、缓存头） | `EliCloudJwksProvider`（尊重 `Cache-Control`）、`LiveEndpointTests` |
| `docs/sso-oidc.md` §2.1/§2.4（端点契约、三种 grant） | `SsoClient`、`EliCloudGrantTypes` |
| §2.5/§3.2/§7.9（设备流程与轮询规则） | `DeviceCodeFlow`（`slow_down` +5、封顶 60） |
| §4.2（`id_token` 的 `aud`） | 由 `aud` 校验拒绝，测试 `IdTokenMisuse_IsRejectedBecauseAudienceDiffers` |
| §13（客户端静态注册） | `SsoAdminClient` |
| §18（RP-Initiated Logout、§18.4 的缺口） | `SsoClient.BuildLogoutUrl`，README §5.2 的取舍说明 |
| `docs/mc.md` §6.1/§6.2（鉴权与端点） | `McClient`、`McAdminClient` |
| §5.7（健康检查语义：503 也算有信息） | `McClient.GetHealthAsync` |
| §9.1（测试清单） | `TokenValidationTests`、`McTests` |
