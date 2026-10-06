using System.Security.Cryptography;

namespace EliCloud.Sdk.Sso;

/// <summary>
/// PKCE（RFC 7636）的 <c>code_verifier</c> 与 <c>code_challenge</c>。
/// </summary>
/// <remarks>
/// <para>
/// 平台对公开客户端**强制** PKCE，且只接受 <c>S256</c>（<c>plain</c> 会被直接拒绝，
/// 见 <c>docs/sso-oidc.md</c> §7.2）。所以「用授权码流程」= 「必须用本类」。
/// </para>
/// <para>
/// 正确用法是<b>同一个实例</b>走完两步：授权时把 <see cref="CodeChallenge"/> 发给
/// <c>/authorize</c>，换令牌时把 <see cref="CodeVerifier"/> 发给 <c>/token</c>。
/// 中途重新生成会让服务端算出的摘要对不上，得到 <c>invalid_grant</c>。
/// </para>
/// </remarks>
public sealed class PkceCodeChallenge
{
    private const int MinimumVerifierLength = 43;
    private const int MaximumVerifierLength = 128;

    private PkceCodeChallenge(string codeVerifier)
    {
        CodeVerifier = codeVerifier;
        CodeChallenge = ComputeChallenge(codeVerifier);
    }

    /// <summary><c>code_verifier</c> 原文（43–128 个 unreserved 字符）。</summary>
    public string CodeVerifier { get; }

    /// <summary><c>code_challenge</c> = BASE64URL(SHA256(ASCII(code_verifier)))。</summary>
    public string CodeChallenge { get; }

    /// <summary>挑战方式，固定 <c>S256</c>。</summary>
    public string CodeChallengeMethod => "S256";

    /// <summary>
    /// 生成一对新的 verifier / challenge。
    /// </summary>
    /// <param name="verifierLength">
    /// verifier 长度，默认 64。RFC 7636 允许 43–128；越短熵越低，不建议低于 43。
    /// </param>
    public static PkceCodeChallenge Create(int verifierLength = 64)
    {
        if (verifierLength is < MinimumVerifierLength or > MaximumVerifierLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(verifierLength),
                verifierLength,
                $"code_verifier 长度必须在 {MinimumVerifierLength}–{MaximumVerifierLength} 之间（RFC 7636 §4.1）。");
        }

        // base64url 的字母表（A-Za-z0-9-_）是 RFC 7636 所要求的 unreserved 字符集的子集，
        // 所以可以直接截断使用，不需要再做过滤。
        var verifier = Base64Url.Encode(RandomNumberGenerator.GetBytes(96))[..verifierLength];
        return new PkceCodeChallenge(verifier);
    }

    /// <summary>用已有的 verifier 构造（例如把 verifier 存到了别处要复用）。</summary>
    public static PkceCodeChallenge FromVerifier(string codeVerifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(codeVerifier);
        return new PkceCodeChallenge(codeVerifier);
    }

    /// <summary>按 RFC 7636 §4.2 计算 <c>code_challenge</c>。</summary>
    public static string ComputeChallenge(string codeVerifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(codeVerifier);
        var digest = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(codeVerifier));
        return Base64Url.Encode(digest);
    }
}
