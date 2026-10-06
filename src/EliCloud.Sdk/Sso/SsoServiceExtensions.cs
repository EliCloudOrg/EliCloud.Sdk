namespace EliCloud.Sdk.Sso;

/// <summary>
/// 把 <see cref="IEliCloudService"/> 句柄转成 SSO 客户端的扩展方法。
/// </summary>
/// <remarks>
/// <para>
/// 扩展方法放在**服务自己的命名空间**里，这样 <c>using EliCloud.Sdk.Sso;</c> 就自然带出了
/// <see cref="AsSso"/>，而门面（<c>EliCloud.Sdk</c>）不需要认识任何业务类型。
/// 将来新增服务时照抄这个文件的形状即可，一行都不用改门面。
/// </para>
/// <para>
/// 用法：<c>EliCloudService.GetProvider("sso").AsSso()</c>。
/// </para>
/// </remarks>
public static class SsoServiceExtensions
{
    /// <summary>
    /// 取出 SSO 客户端。
    /// </summary>
    /// <param name="service">由 <c>EliCloudService.GetProvider(...)</c> 返回的服务句柄。</param>
    /// <exception cref="InvalidOperationException">句柄指向的不是 SSO。</exception>
    /// <remarks>
    /// 返回的客户端是无状态的，令牌逐次传给各方法，因此可以随手创建、不必缓存。
    /// </remarks>
    public static SsoClient AsSso(this IEliCloudService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        service.RequireService(EliCloudServiceIds.Sso);

        return new SsoClient(service.HttpClient, service.Options);
    }

    /// <summary>
    /// 取出 SSO 的客户端管理客户端（<c>/auth/v1/clients</c>）。
    /// </summary>
    /// <param name="service">由 <c>EliCloudService.GetProvider(...)</c> 返回的服务句柄。</param>
    /// <param name="adminToken">服务端的 <c>ADMIN_TOKEN</c>。</param>
    /// <exception cref="InvalidOperationException">句柄指向的不是 SSO。</exception>
    /// <remarks>
    /// 管理员令牌只能来自服务端配置，因此它必须显式传入 —— 门面刻意不提供「全局管理员令牌」，
    /// 免得把平台级凭据悄悄带进普通调用路径。
    /// </remarks>
    public static SsoAdminClient AsSsoAdmin(this IEliCloudService service, string adminToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        service.RequireService(EliCloudServiceIds.Sso);

        return new SsoAdminClient(service.HttpClient, service.Options, adminToken);
    }
}
