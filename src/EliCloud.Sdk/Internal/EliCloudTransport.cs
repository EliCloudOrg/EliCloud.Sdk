using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace EliCloud.Sdk.Internal;

/// <summary>
/// 一次 HTTP 往返的原始结果：状态码、响应体、解析出的平台错误。
/// </summary>
/// <remarks>
/// <b>为什么不让传输层直接抛异常</b>：设备码流程里 <c>authorization_pending</c> 与
/// <c>slow_down</c> 是**正常控制流**（RFC 8628 要求客户端轮询到用户确认为止），
/// 用异常表达会既慢又难写。因此传输层只负责「拿到结果」，
/// 「什么算失败」由各调用点通过 <see cref="EnsureSuccess"/> 决定。
/// </remarks>
internal sealed class EliCloudResponse
{
    internal EliCloudResponse(
        int statusCode,
        string? body,
        EliCloudError? error,
        Uri requestUri,
        TimeSpan? retryAfter,
        HttpResponseHeaders? headers)
    {
        StatusCode = statusCode;
        Body = body;
        Error = error;
        RequestUri = requestUri;
        RetryAfter = retryAfter;
        Headers = headers;
    }

    internal int StatusCode { get; }

    internal string? Body { get; }

    internal EliCloudError? Error { get; }

    internal Uri RequestUri { get; }

    internal TimeSpan? RetryAfter { get; }

    internal HttpResponseHeaders? Headers { get; }

    internal bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>错误码；没有结构化错误时为 <c>null</c>。</summary>
    internal string? ErrorCode => Error?.Error;

    /// <summary>
    /// 反序列化响应体。响应体不是预期的 JSON 时抛
    /// <see cref="EliCloudMalformedResponseException"/>，而不是把
    /// <see cref="System.Text.Json.JsonException"/> 原样漏给调用方 ——
    /// 「对方不是我们约定的服务」和「JSON 写错了」是两种完全不同的排错方向。
    /// </summary>
    internal T? Deserialize<T>()
    {
        try
        {
            return EliCloudJson.Deserialize<T>(Body);
        }
        catch (System.Text.Json.JsonException)
        {
            var preview = Body is { Length: > 400 } body ? body[..400] : Body;
            throw new EliCloudMalformedResponseException(
                $"响应体不是合法的 JSON（期望 {typeof(T).Name}）：{RequestUri}",
                RequestUri,
                preview);
        }
    }

    /// <summary>非 2xx 时抛 <see cref="EliCloudApiException"/>；2xx 时什么也不做。</summary>
    internal void EnsureSuccess()
    {
        if (IsSuccess)
        {
            return;
        }

        throw new EliCloudApiException(
            StatusCode,
            Error ?? new EliCloudError(null, FallbackDescription()),
            RequestUri,
            Body,
            RetryAfter);
    }

    private string FallbackDescription()
        => string.IsNullOrWhiteSpace(Body)
            ? "服务端未返回错误详情。"
            : Body.Length > 400 ? Body[..400] + "…" : Body;
}

/// <summary>
/// SDK 的 HTTP 传输层：统一超时、JSON 形态、错误解析与日志。
/// </summary>
/// <remarks>
/// <para><b>安全</b>：日志里**只记方法与路径，不记查询串、不记请求体、不记响应体**。</para>
/// <para>
/// 原因是查询串与请求体里会同时出现最敏感的三类东西：授权码（<c>/authorize</c> 回跳）、
/// 密码（登录表单）、令牌原文（刷新与设备码轮换）。路径本身足够定位问题。
/// </para>
/// </remarks>
internal sealed class EliCloudTransport
{
    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private readonly TimeSpan _timeout;

    internal EliCloudTransport(HttpClient httpClient, TimeSpan timeout, ILogger? logger)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeout = timeout;
        _logger = logger;
    }

    /// <summary>发送 JSON 请求（<c>Content-Type: application/json</c>）。</summary>
    internal async Task<EliCloudResponse> SendJsonAsync(
        HttpMethod method,
        Uri uri,
        object? payload,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (payload is not null)
        {
            var json = EliCloudJson.Serialize(payload);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (!string.IsNullOrEmpty(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发送 <c>application/x-www-form-urlencoded</c> 请求（OAuth 令牌端点用）。</summary>
    internal async Task<EliCloudResponse> SendFormAsync(
        HttpMethod method,
        Uri uri,
        IEnumerable<KeyValuePair<string, string>> form,
        string? authorizationHeader,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri)
        {
            Content = new FormUrlEncodedContent(form),
        };

        if (!string.IsNullOrEmpty(authorizationHeader))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);
        }

        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发送一个已构造好的请求；调用方负责 <c>using</c> 释放它。</summary>
    internal async Task<EliCloudResponse> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 每次请求单独计超时，而不是改 HttpClient.Timeout：
        // 后者会让共享同一个 HttpClient 的所有调用互相影响，也可能与调用方的设置冲突。
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"请求 {request.Method} {request.RequestUri?.AbsolutePath} 超过 {_timeout.TotalSeconds:0.#} 秒未完成。");
        }

        var statusCode = (int)response.StatusCode;
        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"读取 {request.Method} {request.RequestUri?.AbsolutePath} 的响应体超时。");
        }
        finally
        {
            stopwatch.Stop();
        }

        using (response)
        {
            var error = statusCode is >= 200 and < 300 ? null : EliCloudJson.TryParseError(body);
            var retryAfter = ParseRetryAfter(response.Headers);

            if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
            {
                // 只记路径：查询串里有授权码，请求体里有密码与令牌。
                _logger.LogDebug(
                    "EliCloud {Method} {Path} -> {StatusCode} ({ElapsedMs:0} ms)",
                    request.Method.Method,
                    request.RequestUri?.AbsolutePath,
                    statusCode,
                    stopwatch.Elapsed.TotalMilliseconds);
            }

            return new EliCloudResponse(
                statusCode,
                body,
                error,
                request.RequestUri ?? new Uri("about:blank"),
                retryAfter,
                response.Headers);
        }
    }

    /// <summary>解析 <c>Retry-After</c>（支持秒数与原 HTTP 日期两种写法）。</summary>
    private static TimeSpan? ParseRetryAfter(HttpResponseHeaders headers)
    {
        var retryAfter = headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// 构造 <c>client_secret_basic</c> 的 Authorization 头（RFC 6749 §2.3.1）：
    /// 先对 client_id 与 client_secret 各自做 form-urlencode，再用冒号连接并 base64。
    /// 直接把原文 base64 是常见误区，遇到含 <c>:</c> 或非 ASCII 的 secret 会认证失败。
    /// </summary>
    internal static string BuildBasicAuthorization(string clientId, string clientSecret)
    {
        var raw = $"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(clientSecret)}";
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>构造只带 client_id 的 <c>Basic</c> 头（部分工具用它表示公开客户端，平台不使用）。</summary>
    internal static string BuildBasicAuthorization(string clientId)
    {
        var raw = $"{Uri.EscapeDataString(clientId)}:";
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }
}
