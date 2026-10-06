namespace EliCloud.Sdk;

/// <summary>
/// 服务名：**一个服务只有一个名字，就是平台的服务名（容器名）**。
/// </summary>
/// <remarks>
/// <para>
/// 这个名字与平台自己的叫法完全一致，所以「按名字取服务」在 SDK 与平台之间是同一套：
/// </para>
/// <list type="bullet">
///   <item><description><c>sso</c> —— 账号中心，对外前缀 <c>/auth</c>。</description></item>
///   <item><description><c>mc</c> —— MC 服务器白名单，对外前缀 <c>/mc</c>。</description></item>
///   <item><description><c>main-api</c> —— 平台核心 API，对外前缀 <c>/core</c>（规划中）。</description></item>
///   <item><description><c>pdf-decrypt</c> —— PDF 解密，对外前缀 <c>/pdf-decrypt</c>（待实现，需先 <c>Register</c>）。</description></item>
/// </list>
/// <para>
/// ⚠️ <b>服务名 ≠ 对外前缀</b>，别把两者当同一件事：<c>sso</c> 挂在前缀 <c>/auth</c> 下，
/// <c>main-api</c> 挂在 <c>/core</c> 下。服务名是身份，前缀是位置。
/// </para>
/// <para>
/// 与平台接口的对应：<c>GET /core/v1/services</c>（<c>docs/architecture.md</c> §5.8）返回的
/// <c>id</c> 就是这里的服务名，<c>path</c> 才是前缀：
/// </para>
/// <code>
/// { "id": "pdf-decrypt", "name": "PDF 解密", "path": "/pdf-decrypt", "online": true }
/// </code>
/// <para>
/// 名字按**原样精确匹配**（区分大小写），不做任何折算 —— 没有别名、不补斜杠。
/// 传了个不认识的名字会抛 <see cref="KeyNotFoundException"/>，消息里会列出当前可用的全部名字。
/// </para>
/// </remarks>
public static class EliCloudServiceIds
{
    /// <summary>账号中心（对外前缀 <c>/auth</c>）。</summary>
    public const string Sso = "sso";

    /// <summary>MC 服务器白名单（对外前缀 <c>/mc</c>）。</summary>
    public const string Mc = "mc";

    /// <summary>平台核心 API（对外前缀 <c>/core</c>，规划中）。</summary>
    public const string MainApi = "main-api";

    /// <summary>PDF 解密（对外前缀 <c>/pdf-decrypt</c>，待实现；需要先 <c>Register</c> 登记位置）。</summary>
    public const string PdfDecrypt = "pdf-decrypt";
}
