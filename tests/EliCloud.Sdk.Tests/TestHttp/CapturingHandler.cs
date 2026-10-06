using System.Net;
using System.Text;
using Xunit;

namespace EliCloud.Sdk.Tests.TestHttp;

/// <summary>一次被捕获的请求。</summary>
internal sealed record CapturedRequest(HttpRequestMessage Request, string? Body)
{
    internal string Method => Request.Method.Method;

    internal string Path => Request.RequestUri!.AbsolutePath;

    internal string Query => Request.RequestUri!.Query;

    /// <summary>把 <c>application/x-www-form-urlencoded</c> 请求体解析成字典。</summary>
    internal Dictionary<string, string> Form()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(Body))
        {
            return result;
        }

        foreach (var pair in Body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            result[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return result;
    }

    /// <summary>把 JSON 请求体反序列化成字典（只用于断言字段，不做强类型绑定）。</summary>
    internal Dictionary<string, string?> JsonFields()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(Body))
        {
            return result;
        }

        using var document = System.Text.Json.JsonDocument.Parse(Body);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind == System.Text.Json.JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString();
        }

        return result;
    }
}

/// <summary>
/// 可编程的 <see cref="HttpMessageHandler"/>：记录每个请求，并按注入的路由函数返回响应。
/// </summary>
/// <remarks>
/// 用它而不是起真实服务器：测试聚焦在「SDK 发出了什么、怎么解释响应」，
/// 不需要 TCP、端口与并发。真正的端到端验证由可选的线上集成测试负责。
/// </remarks>
internal sealed class CapturingHandler : HttpMessageHandler
{
    private readonly Func<CapturedRequest, HttpResponseMessage> _router;

    internal CapturingHandler(Func<CapturedRequest, HttpResponseMessage> router)
    {
        _router = router;
    }

    internal List<CapturedRequest> Captured { get; } = [];

    internal CapturedRequest Single()
    {
        Assert.Single(Captured);
        return Captured[0];
    }

    internal CapturedRequest Last() => Captured[^1];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var captured = new CapturedRequest(request, body);
        Captured.Add(captured);
        return _router(captured);
    }
}

/// <summary>构造响应的小工具。</summary>
internal static class Stub
{
    internal static HttpResponseMessage Json(
        string json,
        HttpStatusCode status = HttpStatusCode.OK,
        string? cacheControl = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (cacheControl is not null)
        {
            response.Headers.TryAddWithoutValidation("Cache-Control", cacheControl);
        }

        return response;
    }

    /// <summary>平台的统一错误结构。</summary>
    internal static HttpResponseMessage Error(
        HttpStatusCode status,
        string error,
        string? description = null,
        TimeSpan? retryAfter = null)
    {
        var body = description is null
            ? $$"""{"error":"{{error}}"}"""
            : $$"""{"error":"{{error}}","error_description":"{{description}}"}""";

        var response = Json(body, status);
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
        }

        return response;
    }

    /// <summary>非 JSON 的响应体（例如网关返回的 HTML 错误页）。</summary>
    internal static HttpResponseMessage Html(string html, HttpStatusCode status = HttpStatusCode.BadGateway)
        => new(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    internal static HttpResponseMessage NoContent()
        => new(HttpStatusCode.NoContent);
}

/// <summary>测试用的 EliCloud 连接配置。</summary>
internal static class TestOptions
{
    internal const string BaseAddress = "https://elicloud.test";

    internal static EliCloudOptions Create(Action<EliCloudOptions>? configure = null)
    {
        var options = new EliCloudOptions
        {
            BaseAddress = new Uri(BaseAddress),
            Timeout = TimeSpan.FromSeconds(5),
        };

        configure?.Invoke(options);
        return options;
    }

    internal static HttpClient HttpClient(CapturingHandler handler) => new(handler)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
