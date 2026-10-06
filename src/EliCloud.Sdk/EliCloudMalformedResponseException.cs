namespace EliCloud.Sdk;

/// <summary>
/// 服务端返回了成功状态码，但响应体不是 SDK 期望的结构。
/// </summary>
/// <remarks>
/// 与 <see cref="EliCloudApiException"/> 的区别：后者表示「请求被服务端拒绝」，
/// 本异常表示「请求成功了，但对方不是我们约定的那个服务」——
/// 典型场景是请求被网关/代理拦下并返回了 HTML 页面。
/// </remarks>
public sealed class EliCloudMalformedResponseException : Exception
{
    /// <summary>创建异常。</summary>
    public EliCloudMalformedResponseException(string message, Uri? requestUri = null, string? responseBody = null)
        : base(message)
    {
        RequestUri = requestUri;
        ResponseBody = responseBody;
    }

    /// <summary>出错的请求地址。</summary>
    public Uri? RequestUri { get; }

    /// <summary>原始响应体；仅用于诊断。</summary>
    public string? ResponseBody { get; }
}
