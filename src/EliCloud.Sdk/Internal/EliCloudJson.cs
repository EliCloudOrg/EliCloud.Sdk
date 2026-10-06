using System.Text.Json;
using System.Text.Json.Serialization;

namespace EliCloud.Sdk.Internal;

/// <summary>
/// SDK 内部统一的 JSON 约定。
/// </summary>
/// <remarks>
/// <para>
/// 服务端是 FastAPI + Pydantic，字段一律 <c>snake_case</c>（<c>error_description</c>、
/// <c>access_token</c>、<c>name_display</c>…）。这里用
/// <see cref="JsonNamingPolicy.SnakeCaseLower"/> 做双向映射，
/// C# 侧才能保持 <c>PascalCase</c> 的常规命名。
/// </para>
/// <para>
/// <b>例外</b>：少数字段的「策略推断名」与线缆上的实际名字不一致，必须用
/// <c>[JsonPropertyName]</c> 显式钉住。已经踩到的例子是发现文档里的
/// <c>userinfo_endpoint</c> —— 策略会把 <c>UserInfoEndpoint</c> 猜成
/// <c>user_info_endpoint</c>，于是该字段静默变成 <c>null</c>。
/// 判断方法很简单：名字里出现的英文缩写/复合词是否是服务端写作一个词。
/// </para>
/// </remarks>
internal static class EliCloudJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>序列化请求体。</summary>
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// 反序列化响应体。空体或 <c>null</c> 字面量返回 <c>default</c>；
    /// 解析失败时抛 <see cref="JsonException"/>（调用方决定包成哪种异常）。
    /// </summary>
    internal static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(json, Options);
    }

    /// <summary>从错误响应体里提取平台的统一错误结构；不是该结构时返回 <c>null</c>。</summary>
    internal static EliCloudError? TryParseError(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = document.RootElement;
            if (!root.TryGetProperty("error", out var errorElement))
            {
                return null;
            }

            var error = errorElement.ValueKind == JsonValueKind.String ? errorElement.GetString() : null;
            string? description = null;
            if (root.TryGetProperty("error_description", out var descriptionElement)
                && descriptionElement.ValueKind == JsonValueKind.String)
            {
                description = descriptionElement.GetString();
            }

            return new EliCloudError(error, description);
        }
        catch (JsonException)
        {
            // 网关的 HTML 错误页 / 空体：交给上层按「无结构化错误」处理
            return null;
        }
    }
}
