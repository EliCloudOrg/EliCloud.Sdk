using System.Text;

namespace EliCloud.Sdk.Internal;

/// <summary>查询串拼装（RFC 3986 百分号编码，空格编码为 <c>%20</c>）。</summary>
internal static class EliCloudQuery
{
    /// <summary>把参数追加到地址上；<c>null</c> 或空串的参数会被跳过。</summary>
    internal static Uri Append(Uri endpoint, IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        var builder = new StringBuilder(endpoint.AbsoluteUri);
        var separator = endpoint.AbsoluteUri.Contains('?') ? '&' : '?';
        var appended = 0;

        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            builder.Append(appended == 0 ? separator : '&');
            builder.Append(Uri.EscapeDataString(key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value));
            appended++;
        }

        return appended == 0 ? endpoint : new Uri(builder.ToString());
    }
}
