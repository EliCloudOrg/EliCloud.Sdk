using System.Security.Cryptography;
using System.Text;

namespace EliCloud.Sdk;

/// <summary>
/// 加密安全的随机串工具。
/// </summary>
/// <remarks>
/// 用 <see cref="RandomNumberGenerator"/> 而不是 <see cref="Random"/>：
/// 这些值会作为 PKCE <c>code_verifier</c>、OAuth <c>state</c>、<c>nonce</c> 使用，
/// 可预测的随机数会让整套防护失效。
/// </remarks>
public static class EliCloudRandom
{
    /// <summary>生成 URL 安全的随机串（base64url，无填充），默认 32 字节熵（43 字符）。</summary>
    public static string UrlSafeToken(int byteLength = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(byteLength, 16);
        return Base64Url.Encode(RandomNumberGenerator.GetBytes(byteLength));
    }

    /// <summary>生成 OAuth <c>state</c>（防 CSRF，32 字节熵）。</summary>
    public static string CreateState() => UrlSafeToken();

    /// <summary>生成 OIDC <c>nonce</c>（防 id_token 重放，32 字节熵）。</summary>
    public static string CreateNonce() => UrlSafeToken();
}

/// <summary>base64url（RFC 4648 §5，无填充）编解码。</summary>
internal static class Base64Url
{
    internal static string Encode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }

    internal static string EncodeUtf8(string value) => Encode(Encoding.UTF8.GetBytes(value));
}
