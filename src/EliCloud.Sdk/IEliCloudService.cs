namespace EliCloud.Sdk;

/// <summary>
/// 「一个 EliCloud 服务」的抽象句柄，由 <c>EliCloudService.GetProvider(id)</c> 返回。
/// </summary>
/// <remarks>
/// <para>
/// 它刻意**只描述「是哪个服务、挂在哪、用哪个 HttpClient」**，不描述任何具体业务操作 ——
/// 具体客户端由各服务自己命名空间里的扩展方法提供：
/// </para>
/// <code>
/// var mc = EliCloudService.GetProvider("mc").AsMc();
/// </code>
/// <para>
/// 这样设计的好处是**加服务不用改门面**：新服务只要在自己的命名空间里写一个扩展方法
/// （<c>public static XxxClient AsXxx(this IEliCloudService service)</c>）即可，
/// 既保留了「按 key 取服务」的多态入口，又有编译期类型检查与 IntelliSense ——
/// 不需要 <c>dynamic</c> 那种「拼错方法名要到运行时才炸」的代价。
/// </para>
/// <para>
/// 未来服务若不在 SDK 内置的三个前缀里，先登记它的位置：
/// </para>
/// <code>
/// EliCloudService.Register(EliCloudServiceIds.PdfDecrypt, "/pdf-decrypt");
/// var pdf = EliCloudService.GetProvider(EliCloudServiceIds.PdfDecrypt).AsPdfDecrypt();
/// </code>
/// </remarks>
public interface IEliCloudService
{
    /// <summary>平台的服务名，如 <c>mc</c>（见 <see cref="EliCloudServiceIds"/>）。</summary>
    string ServiceId { get; }

    /// <summary>该服务的基址（带结尾斜杠），如 <c>https://host/mc/</c>。</summary>
    Uri BaseAddress { get; }

    /// <summary>共享的 <see cref="System.Net.Http.HttpClient"/>（超时由 SDK 按请求控制）。</summary>
    HttpClient HttpClient { get; }

    /// <summary>生效的连接配置。</summary>
    EliCloudOptions Options { get; }
}

/// <summary><see cref="IEliCloudService"/> 的通用扩展。</summary>
public static class EliCloudServiceExtensions
{
    /// <summary>
    /// 断言这个句柄确实是某个服务；不是时抛 <see cref="InvalidOperationException"/>，
    /// 并在消息里给出正确的写法。
    /// </summary>
    /// <remarks>
    /// 各服务的 <c>AsXxx()</c> 扩展都会先调它。没有这层校验的话，
    /// <c>GetProvider("pdf-decrypt").AsMc()</c> 会安静地发请求到错误的地址，
    /// 报错信息也会指向一个完全无关的地方。
    /// </remarks>
    /// <param name="service">服务句柄。</param>
    /// <param name="expectedServiceId">期望的服务名（见 <see cref="EliCloudServiceIds"/>）。</param>
    public static IEliCloudService RequireService(this IEliCloudService service, string expectedServiceId)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedServiceId);

        if (!string.Equals(service.ServiceId, expectedServiceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"服务句柄的 id 是「{service.ServiceId}」，不能在它上面取「{expectedServiceId}」的客户端。" +
                $"请改用 EliCloudService.GetProvider(\"{expectedServiceId}\")；" +
                $"如果「{service.ServiceId}」是你自己的服务，请在它的命名空间里加一个扩展方法" +
                $"（public static XxxClient AsXxx(this IEliCloudService service)）。");
        }

        return service;
    }

    /// <summary>判断这个句柄是不是某个服务（不抛异常的版本）。</summary>
    public static bool IsService(this IEliCloudService service, string serviceId)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        return string.Equals(service.ServiceId, serviceId, StringComparison.Ordinal);
    }
}
