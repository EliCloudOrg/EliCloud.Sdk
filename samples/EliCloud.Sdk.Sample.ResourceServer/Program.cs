// EliCloud.Sdk 示例 2：一个「业务服务（资源服务）」的最小实现。
//
// 它演示平台对业务服务的全部要求（docs/architecture.md §8）：
//   1. 只认 SSO 签发的 access token，用 JWKS 本地验签（不查 SSO 数据库）；
//   2. 用 aud 拒掉 id_token；
//   3. 用 scope 做授权边界，缺 scope 回 403 而不是 401；
//   4. 一切业务数据按 token 里的 sub 隔离。
//
// 运行：
//   $env:ELICLOUD_ISSUER = 'https://api.example.com/auth'
//   dotnet run --project samples/EliCloud.Sdk.Sample.ResourceServer --urls http://localhost:5080
//
// 自测（需要一个带 mc:whitelist scope 的 access token）：
//   curl http://localhost:5080/healthz
//   curl -H "Authorization: Bearer <token>" http://localhost:5080/v1/me
//   curl -H "Authorization: Bearer <token>" http://localhost:5080/v1/notes

using System.Collections.Concurrent;
using EliCloud.Sdk;
using EliCloud.Sdk.AspNetCore;
using EliCloud.Sdk.Tokens;

var builder = WebApplication.CreateBuilder(args);

// issuer 必须与服务端的 PUBLIC_BASE_URL 逐字相同（当前 IP 阶段是 …/auth）。
// 域名上线后只改这一处配置，代码不动。
var issuer = builder.Configuration["EliCloud:Issuer"]
    ?? Environment.GetEnvironmentVariable("ELICLOUD_ISSUER")
    ?? "https://api.example.com/auth";

builder.Services.AddEliCloudAuthentication(options =>
{
    options.Validation.Issuer = issuer;
    options.Validation.Audience = EliCloudConstants.Audience;

    // 本服务要求令牌必须带这个 scope（整词匹配）。
    options.Validation.RequiredScopes = [EliCloudScopes.McWhitelist];
});

// 注册「必须是 mc:whitelist」的授权策略；缺 scope 时由处理器给出 403。
builder.Services.AddEliCloudScopePolicy(EliCloudScopes.McWhitelist);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// 本示例用内存字典代替数据库，只为把「按 sub 隔离」这件事写清楚。
var notes = new ConcurrentDictionary<string, List<string>>();

// 健康检查：不需要认证。
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", issuer }));

// 自检端点：把令牌里的 claim 摊开给调用方看（前端登录后常用它确认 scope 是否齐）。
app.MapGet("/v1/me", (HttpContext context) => Results.Ok(new
{
    sub = context.User.GetEliCloudSubject(),
    username = context.User.GetEliCloudUsername(),
    clientId = context.User.GetEliCloudClientId(),
    scopes = context.User.GetEliCloudScopes(),
}))
.RequireAuthorization(EliCloudAuthenticationDefaults.ScopePolicyName(EliCloudScopes.McWhitelist));

// 业务数据读写：全部以 sub 为隔离依据。
app.MapGet("/v1/notes", (HttpContext context) =>
{
    var sub = context.User.GetEliCloudSubject();
    if (sub is null)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new { sub, notes = notes.GetValueOrDefault(sub, []) });
})
.RequireAuthorization(EliCloudAuthenticationDefaults.ScopePolicyName(EliCloudScopes.McWhitelist));

app.MapPost("/v1/notes", (HttpContext context, NoteRequest request) =>
{
    var sub = context.User.GetEliCloudSubject();
    if (sub is null)
    {
        return Results.Unauthorized();
    }

    var list = notes.GetOrAdd(sub, _ => []);
    lock (list)
    {
        list.Add(request.Text);
    }

    return Results.Created($"/v1/notes/{list.Count}", new { sub, count = list.Count });
})
.RequireAuthorization(EliCloudAuthenticationDefaults.ScopePolicyName(EliCloudScopes.McWhitelist));

app.Run();

/// <summary>请求体。</summary>
internal sealed record NoteRequest(string Text);
