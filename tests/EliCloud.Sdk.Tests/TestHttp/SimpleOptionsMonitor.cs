using Microsoft.Extensions.Options;

namespace EliCloud.Sdk.Tests.TestHttp;

/// <summary>测试用的 <see cref="IOptionsMonitor{T}"/>：始终返回同一个实例，不支持变更通知。</summary>
internal sealed class SimpleOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    where T : class
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
