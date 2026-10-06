using System.Net.Http.Headers;
using EliCloud.Sdk.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace EliCloud.Sdk.Tokens;

/// <summary>
/// 带缓存的 JWKS 提供者：按 <c>kid</c> 解析公钥，并支持密钥轮换。
/// </summary>
/// <remarks>
/// <para>
/// 服务端对 JWKS 下发 <c>Cache-Control: public, max-age=300</c>，并要求业务服务
/// <b>尊重该缓存头</b>（<c>docs/architecture.md</c> §0.4 硬约束 2、§5.6）。本类按响应头里的
/// <c>max-age</c> 决定缓存时长，取不到就退回 <see cref="EliCloudTokenValidationOptions.JwksCacheDuration"/>。
/// </para>
/// <para>
/// <b>轮换</b>：服务端轮换密钥时新旧公钥并存，所以正常缓存不会导致验签失败。
/// 遇到「<c>kid</c> 不在缓存里」时本类会强制刷新一次 —— 但要限流：
/// 否则攻击者只要持续提交带随机 <c>kid</c> 的令牌，就能把服务端当成免费的 JWKS 放大器。
/// 因此两次强制刷新之间有最小间隔（<see cref="MinimumRefreshInterval"/>）。
/// </para>
/// </remarks>
public sealed class EliCloudJwksProvider : IDisposable
{
    /// <summary>两次「因未知 kid 而强制刷新」之间的最小间隔。</summary>
    public static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly EliCloudTransport _transport;
    private readonly Uri _jwksUri;
    private readonly TimeSpan _defaultCacheDuration;
    private readonly TimeSpan _minimumRefreshInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private JsonWebKeySet? _cached;
    private DateTimeOffset _cachedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastForcedRefresh = DateTimeOffset.MinValue;

    /// <summary>创建提供者。</summary>
    public EliCloudJwksProvider(
        HttpClient httpClient,
        EliCloudTokenValidationOptions options,
        ILogger<EliCloudJwksProvider>? logger = null,
        TimeSpan? minimumRefreshInterval = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _jwksUri = options.ResolveJwksUri();
        _defaultCacheDuration = options.JwksCacheDuration;
        _minimumRefreshInterval = minimumRefreshInterval ?? MinimumRefreshInterval;
        _transport = new EliCloudTransport(httpClient, options.Timeout, logger);
    }

    /// <summary>当前缓存里的密钥集地址。</summary>
    public Uri JwksUri => _jwksUri;

    /// <summary>取密钥集：缓存有效时直接返回，否则重新拉取。</summary>
    public async Task<JsonWebKeySet> GetAsync(CancellationToken cancellationToken = default)
    {
        var cached = Volatile.Read(ref _cached);
        if (cached is not null && DateTimeOffset.UtcNow < _cachedUntil)
        {
            return cached;
        }

        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>强制重新拉取 JWKS 并更新缓存。</summary>
    public Task<JsonWebKeySet> RefreshAsync(CancellationToken cancellationToken = default)
        => RefreshCoreAsync(cancellationToken, forced: false);

    private async Task<JsonWebKeySet> RefreshCoreAsync(CancellationToken cancellationToken, bool forced)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 双重检查：等锁期间别人可能已经刷新好了。
            // forced 时不能复用缓存 —— 调用方正是因为「缓存里没有这个 kid」才要求刷新的。
            var cached = Volatile.Read(ref _cached);
            if (!forced && cached is not null && DateTimeOffset.UtcNow < _cachedUntil)
            {
                return cached;
            }

            var response = await _transport
                .SendJsonAsync(HttpMethod.Get, _jwksUri, null, null, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccess();

            var keySet = ParseKeySet(response.Body);
            Volatile.Write(ref _cached, keySet);
            _cachedUntil = DateTimeOffset.UtcNow + ResolveCacheDuration(response.Headers);

            // 只给「因未知 kid 触发的刷新」打限流时间戳。
            // 如果连常规的缓存刷新也打，那么缓存刚建好时收到的第一个未知 kid 就会被无谓地拒绝。
            if (forced)
            {
                _lastForcedRefresh = DateTimeOffset.UtcNow;
            }

            return keySet;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 按 <c>kid</c> 解析签名公钥；缓存里没有时会尝试强制刷新一次（受最小间隔限制）。
    /// </summary>
    /// <returns>找不到时返回 <c>null</c>（调用方应据此判定令牌无效，而不是抛异常）。</returns>
    public async Task<SecurityKey?> ResolveAsync(string? kid, CancellationToken cancellationToken = default)
    {
        var keySet = await GetAsync(cancellationToken).ConfigureAwait(false);
        var key = FindKey(keySet, kid);
        if (key is not null)
        {
            return key;
        }

        // 未知 kid：可能是服务端刚轮换过密钥。限流后再强制刷新一次。
        if (DateTimeOffset.UtcNow - _lastForcedRefresh < _minimumRefreshInterval)
        {
            return null;
        }

        var refreshed = await RefreshCoreAsync(cancellationToken, forced: true).ConfigureAwait(false);
        return FindKey(refreshed, kid);
    }

    private static SecurityKey? FindKey(JsonWebKeySet keySet, string? kid)
    {
        var keys = keySet.GetSigningKeys();
        if (keys.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrEmpty(kid))
        {
            // 没有 kid 时只有在密钥唯一的情况下才能安全推断（RFC 7515 §4.1.4）。
            return keys.Count == 1 ? keys[0] : null;
        }

        foreach (var key in keys)
        {
            if (string.Equals(key.KeyId, kid, StringComparison.Ordinal))
            {
                return key;
            }
        }

        return null;
    }

    private JsonWebKeySet ParseKeySet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new EliCloudMalformedResponseException($"JWKS 响应为空：{_jwksUri}", _jwksUri, body);
        }

        try
        {
            var keySet = new JsonWebKeySet(body);
            if (keySet.GetSigningKeys().Count == 0)
            {
                throw new EliCloudMalformedResponseException($"JWKS 里没有可用的签名密钥：{_jwksUri}", _jwksUri, body);
            }

            return keySet;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new EliCloudMalformedResponseException($"JWKS 解析失败：{_jwksUri}", _jwksUri, body);
        }
    }

    private TimeSpan ResolveCacheDuration(HttpResponseHeaders? headers)
    {
        var maxAge = headers?.CacheControl?.MaxAge;
        if (maxAge is { } duration && duration > TimeSpan.Zero)
        {
            return duration;
        }

        return _defaultCacheDuration;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
