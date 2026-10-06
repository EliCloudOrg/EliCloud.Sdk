namespace EliCloud.Sdk.MainApi;

/// <summary>
/// 把 <see cref="IEliCloudService"/> 句柄转成平台核心 API 客户端的扩展方法。
/// </summary>
/// <remarks>
/// 用法：<c>EliCloudService.GetProvider("main-api").AsMainApi()</c>。
/// 注意该服务尚未上线，见 <see cref="MainApiClient"/> 的说明。
/// </remarks>
public static class MainApiServiceExtensions
{
    /// <summary>取出平台核心 API 客户端。</summary>
    /// <param name="service">由 <c>EliCloudService.GetProvider(...)</c> 返回的服务句柄。</param>
    /// <exception cref="InvalidOperationException">句柄指向的不是平台核心 API。</exception>
    public static MainApiClient AsMainApi(this IEliCloudService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        service.RequireService(EliCloudServiceIds.MainApi);

        return new MainApiClient(service.HttpClient, service.Options);
    }
}
