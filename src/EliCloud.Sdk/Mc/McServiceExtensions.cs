namespace EliCloud.Sdk.Mc;

/// <summary>
/// 把 <see cref="IEliCloudService"/> 句柄转成 MC 白名单客户端的扩展方法。
/// </summary>
/// <remarks>
/// 用法：<c>EliCloudService.GetProvider("mc").AsMc()</c>。
/// </remarks>
public static class McServiceExtensions
{
    /// <summary>取出 MC 白名单服务的用户自助客户端。</summary>
    /// <param name="service">由 <c>EliCloudService.GetProvider(...)</c> 返回的服务句柄。</param>
    /// <exception cref="InvalidOperationException">句柄指向的不是 MC 白名单服务。</exception>
    public static McClient AsMc(this IEliCloudService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        service.RequireService(EliCloudServiceIds.Mc);

        return new McClient(service.HttpClient, service.Options);
    }

    /// <summary>取出 MC 白名单服务的管理客户端（<c>/mc/v1/admin/*</c>）。</summary>
    /// <param name="service">由 <c>EliCloudService.GetProvider(...)</c> 返回的服务句柄。</param>
    /// <param name="adminToken">该服务容器里的 <c>ADMIN_TOKEN</c>（与 SSO 的那份通常不同值）。</param>
    /// <exception cref="InvalidOperationException">句柄指向的不是 MC 白名单服务。</exception>
    public static McAdminClient AsMcAdmin(this IEliCloudService service, string adminToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        service.RequireService(EliCloudServiceIds.Mc);

        return new McAdminClient(service.HttpClient, service.Options, adminToken);
    }
}
