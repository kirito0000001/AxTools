using System.Text;

namespace AxTools.Core.Services;

/// <summary>
/// <c>CrossingVoid.obb.json</c>：告诉启动器「要组装回哪个 OBB、由哪些条目组成」。
///
/// **字节必须和发布脚本生成的线上那份完全一致**：启动器导入碎片时按清单逐条校验
/// size + sha256，这份旁车也在清单里（0.6.0 是 8796 B / sha256 22cc73d4…），
/// 少一个字节都会被判成「内容对不上」，整个导入不完成。
///
/// 线上那份的格式是 PowerShell 7 <c>ConvertTo-Json</c> 的输出：
/// 2 空格缩进、CRLF、无结尾换行；entries 的顺序是 <c>Sort-Object -Property Relative -CaseSensitive</c>
/// （文化排序，不是 Ordinal —— `_Low` 会排在无后缀的前面）。这里按同样的规则手写，
/// 不依赖 <see cref="System.Text.Json"/> 的换行/排序默认值。
/// </summary>
public static class CrossingVoidAndroidObbSidecar
{
    public const string FileName = "CrossingVoid.obb.json";

    /// <summary>stable 渠道的 productKey（与发布脚本 <c>$ProductSegment-game</c> 一致）。</summary>
    public const string ProductKey = "crossingvoid-android-game";

    public const string StableChannel = "stable";

    public const string TestChannel = "test";

    /// <summary>
    /// 发布清单里的 productKey：stable 是 <c>&lt;产品段&gt;-game</c>，Test 是 <c>&lt;产品段&gt;-game-test</c>。
    /// </summary>
    public static string BuildProductKey(string productSegment, string? channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productSegment);
        var suffix = string.Equals(channel?.Trim(), TestChannel, StringComparison.OrdinalIgnoreCase)
            ? "-test"
            : string.Empty;
        return $"{productSegment.Trim()}-game{suffix}";
    }

    /// <summary>
    /// 生成旁车字节。<paramref name="entries"/> 会按线上同样的文化排序重排，
    /// 所以调用方传 OBB 的原始顺序或 Ordinal 顺序都不会影响结果。
    /// </summary>
    public static byte[] Build(
        string obbFileName,
        string version,
        IReadOnlyList<string> entries,
        string productKey = ProductKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(obbFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(productKey);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("OBB 旁车的条目不能为空。");
        }

        var sorted = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Replace('\\', '/').Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(entry => entry, StringComparer.InvariantCulture)
            .ToArray();
        if (sorted.Length == 0)
        {
            throw new InvalidOperationException("OBB 旁车的条目不能为空。");
        }

        var builder = new StringBuilder();
        builder.Append("{\r\n");
        AppendProperty(builder, "obbFileName", obbFileName.Trim(), comma: true);
        builder.Append("  \"entries\": [\r\n");
        for (var index = 0; index < sorted.Length; index++)
        {
            builder.Append("    ");
            AppendJsonString(builder, sorted[index]);
            builder.Append(index == sorted.Length - 1 ? "\r\n" : ",\r\n");
        }

        builder.Append("  ],\r\n");
        AppendProperty(builder, "version", version.Trim(), comma: true);
        AppendProperty(builder, "productKey", productKey.Trim(), comma: false);
        builder.Append('}');

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(builder.ToString());
    }

    private static void AppendProperty(StringBuilder builder, string name, string value, bool comma)
    {
        builder.Append("  \"").Append(name).Append("\": ");
        AppendJsonString(builder, value);
        builder.Append(comma ? ",\r\n" : "\r\n");
    }

    private static void AppendJsonString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < 0x20 || character > 0x7E)
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
