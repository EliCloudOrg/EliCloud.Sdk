using EliCloud.Sdk.Mc;
using EliCloud.Sdk.MainApi;
using EliCloud.Sdk.Sso;
using EliCloud.Sdk.Tests.TestHttp;
using Xunit;

namespace EliCloud.Sdk.Tests;

/// <summary>
/// 静态门面（<see cref="EliCloudService"/>）与服务扩展方法的测试。
/// </summary>
/// <remarks>
/// 门面用的是**进程级静态状态**，所以每个测试都要先 <see cref="EliCloudService.Reset"/>：
/// xunit 对每个测试方法新建一个实例并调用 <see cref="Dispose"/>，
/// 因此把重置放在构造与释放两处，就能保证测试之间互不污染。
/// 构造函数里顺便配好入口地址——门面没有默认入口，不配就用不了。
/// </remarks>
public sealed class EliCloudServiceFacadeTests : IDisposable
{
    public EliCloudServiceFacadeTests()
    {
        EliCloudService.Reset();
        ConfigureEntryAddress();
    }

    public void Dispose() => EliCloudService.Reset();

    // ------------------------------------------------ 入口地址必须显式配置

    [Fact]
    public void GetProvider_WithoutBaseAddress_ThrowsTeachingError()
    {
        // 门面不提供默认入口：没配置就报错，而不是悄悄连到某个写死的地址。
        EliCloudService.Reset();

        var exception = Assert.Throws<InvalidOperationException>(
            () => EliCloudService.GetProvider(EliCloudServiceIds.Mc));

        Assert.Contains("BaseAddress", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortcutProperty_WithoutBaseAddress_ThrowsToo()
    {
        EliCloudService.Reset();

        Assert.Throws<InvalidOperationException>(() => _ = EliCloudService.Sso);
    }

    [Fact]
    public void GetProvider_WorksAfterConfiguration()
    {
        ConfigureEntryAddress();

        var service = EliCloudService.GetProvider(EliCloudServiceIds.Mc);

        Assert.Equal("mc", service.ServiceId);
        Assert.Equal("https://elicloud.test/mc/", service.BaseAddress.AbsoluteUri);
    }

    [Fact]
    public void AsMc_ProducesClientPointingAtThatService()
    {
        ConfigureEntryAddress();

        var client = EliCloudService.GetProvider("mc").AsMc();

        Assert.Equal("https://elicloud.test/mc/healthz", client.HealthEndpoint.AbsoluteUri);
    }

    [Fact]
    public void AsSso_ProducesClientPointingAtAuthPrefix()
    {
        ConfigureEntryAddress();

        var client = EliCloudService.GetProvider("sso").AsSso();

        Assert.Equal("https://elicloud.test/auth/token", client.TokenEndpoint.AbsoluteUri);
        Assert.Equal("https://elicloud.test/auth/v1/clients", client.ClientsEndpoint.AbsoluteUri);
    }

    [Fact]
    public void AsMainApi_ProducesClientPointingAtCorePrefix()
    {
        ConfigureEntryAddress();

        var client = EliCloudService.GetProvider("main-api").AsMainApi();

        Assert.Equal("https://elicloud.test/core/v1/services", client.ServicesEndpoint.AbsoluteUri);
    }

    private static void ConfigureEntryAddress() =>
        EliCloudService.Configure(options => options.BaseAddress = new Uri(TestOptions.BaseAddress));

    // ------------------------------------------- 一个服务只有一个名字：服务名

    [Theory]
    [InlineData("sso")]
    [InlineData("mc")]
    [InlineData("main-api")]
    [InlineData("pdf-decrypt-demo")]
    public void GetProvider_AcceptsTheServiceNameAsIs(string serviceName)
    {
        if (serviceName == "pdf-decrypt-demo")
        {
            EliCloudService.Register(serviceName, "/pdf-decrypt-demo");
        }

        var service = EliCloudService.GetProvider(serviceName);

        Assert.Equal(serviceName, service.ServiceId);
    }

    [Fact]
    public void GetProvider_DoesNotAcceptFormerOrPrefixNames()
    {
        // 服务名就是平台的服务名（容器名）。以下都**不是**服务名，一律拒绝：
        //   · mc-whitelist —— 改名前的旧名
        //   · auth / core   —— 对外前缀，不是服务名（sso 挂 /auth、main-api 挂 /core）
        const string formerName = "mc-whitelist";
        const string authPrefix = "auth";
        const string corePrefix = "core";

        Assert.Throws<KeyNotFoundException>(() => EliCloudService.GetProvider(formerName));
        Assert.Throws<KeyNotFoundException>(() => EliCloudService.GetProvider(authPrefix));
        Assert.Throws<KeyNotFoundException>(() => EliCloudService.GetProvider(corePrefix));
    }

    [Fact]
    public void GetProvider_IsCaseSensitiveAndDoesNotPatchUpInput()
    {
        // 没有别名折算，也没有大小写/斜杠的容错：名字按原样精确匹配。
        Assert.Throws<KeyNotFoundException>(() => EliCloudService.GetProvider("MC"));
        Assert.Throws<KeyNotFoundException>(() => EliCloudService.GetProvider("/mc"));
        Assert.Throws<ArgumentException>(() => EliCloudService.GetProvider("   "));
    }

    // ------------------------------------------------------------ 配置与地址

    [Fact]
    public void Configure_ChangesEveryDerivedAddress()
    {
        EliCloudService.Configure(options => options.BaseAddress = new Uri("https://elicloud.test"));

        Assert.Equal("https://elicloud.test/auth/", EliCloudService.GetProvider("sso").BaseAddress.AbsoluteUri);
        Assert.Equal("https://elicloud.test/mc/", EliCloudService.GetProvider("mc").BaseAddress.AbsoluteUri);
        Assert.Equal("https://elicloud.test/core/", EliCloudService.GetProvider("main-api").BaseAddress.AbsoluteUri);
    }

    [Fact]
    public void Configure_AppliesPathPrefixOverrides()
    {
        EliCloudService.Configure(options =>
        {
            options.BaseAddress = new Uri("https://elicloud.test");
            options.McPathPrefix = "/minecraft";
        });

        Assert.Equal("https://elicloud.test/minecraft/", EliCloudService.GetProvider("mc").BaseAddress.AbsoluteUri);
    }

    [Fact]
    public void Configure_RejectsInvalidConfiguration()
    {
        // 校验失败必须当场抛出，而不是等到第一次发请求才以奇怪的方式失败。
        Assert.Throws<ArgumentException>(() =>
            EliCloudService.Configure(options => options.AuthPathPrefix = "auth"));
    }

    [Fact]
    public void ConfigureWithWholeOptionsInstance_IsApplied()
    {
        EliCloudService.Configure(new EliCloudOptions { BaseAddress = new Uri("https://elicloud.example") });

        Assert.Equal("https://elicloud.example/auth/", EliCloudService.GetProvider("sso").BaseAddress.AbsoluteUri);
    }

    // ------------------------------------------------------ 注入的 HttpClient

    [Fact]
    public async Task UseHttpClient_RoutesRequestsThroughInjectedClient()
    {
        var handler = new CapturingHandler(_ => Stub.Json("""{"services":[]}"""));
        EliCloudService.UseHttpClient(TestOptions.HttpClient(handler));

        await EliCloudService.GetProvider("main-api").AsMainApi().ListServicesAsync();

        // 请求确实走了注入的客户端（而不是门面自建的那个）。
        Assert.Single(handler.Captured);
        Assert.Equal("/core/v1/services", handler.Single().Path);
    }

    [Fact]
    public async Task Configure_KeepsInjectedHttpClient()
    {
        var handler = new CapturingHandler(_ => Stub.Json("""{"services":[]}"""));
        EliCloudService.UseHttpClient(TestOptions.HttpClient(handler));

        // 换地址不等于换传输层：代理/证书/拦截器还得继续用。
        EliCloudService.Configure(options => options.BaseAddress = new Uri("https://elicloud.example"));

        await EliCloudService.GetProvider("main-api").AsMainApi().ListServicesAsync();

        var request = handler.Single();
        Assert.Equal("/core/v1/services", request.Path);
        Assert.Equal("elicloud.example", request.Request.RequestUri!.Host);
    }

    // ---------------------------------------------------------- 未知与不匹配

    [Fact]
    public void GetProvider_UnknownIdThrowsWithGuidance()
    {
        var exception = Assert.Throws<KeyNotFoundException>(() => EliCloudService.GetProvider("pdf-decrypt"));

        Assert.Contains("pdf-decrypt", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Register", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AsMc_OnAnotherServiceThrowsWithGuidance()
    {
        EliCloudService.Register(EliCloudServiceIds.PdfDecrypt, "/pdf-decrypt");

        var service = EliCloudService.GetProvider(EliCloudServiceIds.PdfDecrypt);

        // 不做这层校验的话，请求会静默发到错误地址，报错也会指向无关的地方。
        var exception = Assert.Throws<InvalidOperationException>(() => service.AsMc());

        Assert.Contains("pdf-decrypt", exception.Message, StringComparison.Ordinal);
        Assert.Contains("mc", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsService_ReflectsTheHandleIdentity()
    {
        var service = EliCloudService.GetProvider("mc");

        Assert.True(service.IsService(EliCloudServiceIds.Mc));
        Assert.False(service.IsService(EliCloudServiceIds.Sso));
    }

    [Fact]
    public void AdminClients_RequireAnAdminToken()
    {
        var sso = EliCloudService.GetProvider("sso");
        var whitelist = EliCloudService.GetProvider("mc");

        Assert.Throws<ArgumentException>(() => sso.AsSsoAdmin("   "));
        Assert.Throws<ArgumentException>(() => whitelist.AsMcAdmin(""));

        // 令牌合法时能正常构造。
        Assert.NotNull(sso.AsSsoAdmin("admin-token"));
        Assert.NotNull(whitelist.AsMcAdmin("admin-token"));
    }

    // ------------------------------------------------------ 自定义服务（扩展点）

    [Fact]
    public void RegisterWithPathPrefix_DerivesAddressFromBaseAddress()
    {
        EliCloudService.Configure(options => options.BaseAddress = new Uri("https://elicloud.test"));
        EliCloudService.Register(EliCloudServiceIds.PdfDecrypt, "/pdf-decrypt");

        var service = EliCloudService.GetProvider(EliCloudServiceIds.PdfDecrypt);

        Assert.Equal("https://elicloud.test/pdf-decrypt/", service.BaseAddress.AbsoluteUri);
        Assert.True(service.IsService(EliCloudServiceIds.PdfDecrypt));
    }

    [Fact]
    public void RegisterWithAbsoluteAddress_SupportsServiceOnItsOwnHost()
    {
        EliCloudService.Register(EliCloudServiceIds.PdfDecrypt, new Uri("https://pdf.example.com/"));

        Assert.Equal("https://pdf.example.com/", EliCloudService.GetProvider("pdf-decrypt").BaseAddress.AbsoluteUri);
    }

    [Fact]
    public void Register_ThenCustomExtensionResolvesClient()
    {
        // 这就是「新服务只加一个扩展类、门面一行不用改」的用法：
        // 扩展方法写在服务自己的命名空间里，靠 RequireService 保证 id 对得上。
        EliCloudService.Register(EliCloudServiceIds.PdfDecrypt, "/pdf-decrypt");

        var baseAddress = EliCloudService.GetProvider("pdf-decrypt").AsFakePdfDecrypt();

        Assert.Equal("https://elicloud.test/pdf-decrypt/", baseAddress.AbsoluteUri);
    }

    [Fact]
    public void Register_RejectsMalformedInput()
    {
        Assert.Throws<ArgumentException>(() => EliCloudService.Register("x", "no-leading-slash"));
        Assert.Throws<ArgumentException>(() => EliCloudService.Register("x", new Uri("/relative", UriKind.Relative)));
    }

    [Fact]
    public void Unregister_RemovesTheService()
    {
        EliCloudService.Register(EliCloudServiceIds.PdfDecrypt, "/pdf-decrypt");
        Assert.True(EliCloudService.Unregister(EliCloudServiceIds.PdfDecrypt));

        Assert.False(EliCloudService.Unregister(EliCloudServiceIds.PdfDecrypt));
        Assert.Throws<KeyNotFoundException>(() => EliCloudService.GetProvider(EliCloudServiceIds.PdfDecrypt));
    }

    [Fact]
    public void RegisteredServiceIds_ListsBuiltInsAndCustomOnes()
    {
        Assert.Equal(
            [EliCloudServiceIds.Sso, EliCloudServiceIds.Mc, EliCloudServiceIds.MainApi],
            EliCloudService.BuiltInServiceIds);

        EliCloudService.Register(EliCloudServiceIds.PdfDecrypt, "/pdf-decrypt");

        Assert.Contains(EliCloudServiceIds.PdfDecrypt, EliCloudService.RegisteredServiceIds);
        Assert.Contains(EliCloudServiceIds.Sso, EliCloudService.RegisteredServiceIds);
    }

    // ------------------------------------------------------------- 便捷属性

    [Fact]
    public async Task ShortcutProperties_MatchTheProviderPath()
    {
        ConfigureEntryAddress();
        var handler = new CapturingHandler(_ => Stub.Json("""{"sub":"user_0001","names":[]}"""));
        EliCloudService.UseHttpClient(TestOptions.HttpClient(handler));

        await EliCloudService.Mc.ListMyNamesAsync("token");
        await EliCloudService.GetProvider("mc").AsMc().ListMyNamesAsync("token");

        Assert.Equal(2, handler.Captured.Count);
        Assert.All(handler.Captured, request => Assert.Equal("/mc/v1/names", request.Path));
    }

    [Fact]
    public void ShortcutProperties_ReturnClientsForEachService()
    {
        ConfigureEntryAddress();

        Assert.Equal("https://elicloud.test/auth/login", EliCloudService.Sso.LoginEndpoint.AbsoluteUri);
        Assert.Equal("https://elicloud.test/mc/healthz", EliCloudService.Mc.HealthEndpoint.AbsoluteUri);
        Assert.Equal("https://elicloud.test/core/v1/services", EliCloudService.MainApi.ServicesEndpoint.AbsoluteUri);
    }

    // ---------------------------------------------------------------- Reset

    [Fact]
    public void Reset_ClearsEntryAddressAndRegistrations()
    {
        EliCloudService.Configure(options => options.BaseAddress = new Uri("https://elicloud.example"));
        EliCloudService.Register(EliCloudServiceIds.PdfDecrypt, "/pdf-decrypt");

        EliCloudService.Reset();

        // Reset 之后回到「入口未配置」的初始状态，而不是某个默认地址。
        Assert.Null(EliCloudService.Options.BaseAddress);
        Assert.Throws<InvalidOperationException>(() => EliCloudService.GetProvider(EliCloudServiceIds.Sso));
        Assert.Throws<KeyNotFoundException>(() => EliCloudService.GetProvider(EliCloudServiceIds.PdfDecrypt));
    }

    [Fact]
    public void Options_ExposesTheEffectiveConfiguration()
    {
        EliCloudService.Configure(options => options.BaseAddress = new Uri("https://elicloud.example"));

        // Uri 会为「只有 authority」的地址补上结尾斜杠，这里如实断言规范化后的形式。
        Assert.Equal("https://elicloud.example/", EliCloudService.Options.BaseAddress?.AbsoluteUri);
    }
}

/// <summary>
/// 「PDF 解密服务」在**它自己的命名空间**里的扩展方法 —— 演示新增服务的接入方式。
/// 真实服务照这个形状写一个就够了，门面不需要任何改动。
/// </summary>
internal static class FakePdfDecryptServiceExtensions
{
    internal static Uri AsFakePdfDecrypt(this IEliCloudService service)
    {
        service.RequireService(EliCloudServiceIds.PdfDecrypt);
        return service.BaseAddress;
    }
}
