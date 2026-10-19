using System.Text;
using System.Text.Json;

namespace AxTools.Core.Services;

/// <summary>版本标记是怎么来的。</summary>
public enum CrossingVoidVersionMarkerAction
{
    /// <summary>源目录里已经有版本一致的标记，直接用。</summary>
    Reused,

    /// <summary>从下载站取到了这一版的标记（字节与线上清单一致）。</summary>
    Downloaded,

    /// <summary>下载站没有/取不到，按发布脚本同样的字节格式本地生成。</summary>
    Generated,
}

/// <summary>确保版本标记之后的结论（<see cref="Warning"/> 只在降级时非空）。</summary>
public sealed record CrossingVoidVersionMarkerResult(
    string Path,
    string Version,
    CrossingVoidVersionMarkerAction Action,
    string? Warning);

/// <summary>
/// <c>CrossingVoid.version.json</c>：**发布脚本写进落位目录的版本标记，打包根里本来没有**
/// （见 `Publish-GameDownloadRelease.ps1:1453-1456`）。碎片要能直接导入，必须把它补进包里：
/// 导入器按清单逐条校验，少这一个就永远停在"碎片还不齐"，安装阶段的 ready 校验也不认。
///
/// 字节格式必须和发布脚本一致：`ConvertTo-Json` 的两空格缩进 + CRLF、UTF-8 无 BOM。
/// 格式一致时本地生成的文件 sha256 与线上完全相同（0.6.0/Windows/stable 是 76 字节），
/// 所以离线也能补出正确的标记；能联网时优先直接下载线上那份，字节最稳。
/// </summary>
public static class CrossingVoidVersionMarker
{
    public const string FileName = "CrossingVoid.version.json";

    public const string WindowsPlatform = "Windows";

    public const string StableChannel = "stable";

    private const string DownloadBase = "https://dl.crossingvoid.top/games/crossingvoid";

    /// <summary>与发布脚本 <c>ConvertTo-Json</c> 输出逐字节一致的文本（CRLF、无结尾换行）。</summary>
    public static string BuildJson(string version, string platform, string channel) =>
        $"{{\r\n  \"version\": \"{version}\",\r\n  \"platform\": \"{platform}\",\r\n  \"channel\": \"{channel}\"\r\n}}";

    public static byte[] BuildBytes(string version, string platform, string channel) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(BuildJson(version, platform, channel));

    /// <summary>版本号要能安全地当 URL 路径和文件名：只允许字母数字和 . - _ +。</summary>
    public static bool IsValidVersion(string version) =>
        !string.IsNullOrWhiteSpace(version) &&
        version.All(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or '+');

    public static bool TryReadVersion(string path, out string version)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.TryGetProperty("version", out var element))
            {
                version = element.GetString()?.Trim() ?? string.Empty;
                return version.Length > 0;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // 读不到/不是合法 JSON：当成"没有版本标记"，让调用方重建。
        }

        version = string.Empty;
        return false;
    }

    /// <summary>
    /// 确保 <paramref name="gameDirectory"/> 根下有这一版的 <see cref="FileName"/>。
    /// 已经存在且版本一致就复用；否则先尝试从下载站取，取不到再按发布脚本的格式本地生成。
    /// </summary>
    public static async Task<CrossingVoidVersionMarkerResult> EnsureAsync(
        string gameDirectory,
        string version,
        string platform,
        string channel,
        HttpClient httpClient,
        CancellationToken cancellationToken = default,
        string? downloadBase = null)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            throw new DirectoryNotFoundException($"游戏包目录不存在：{gameDirectory}");
        }

        if (!IsValidVersion(version))
        {
            throw new InvalidOperationException($"版本号不能当文件名/URL 使用：{version}");
        }

        var target = Path.Combine(gameDirectory, FileName);
        if (File.Exists(target) &&
            TryReadVersion(target, out var existingVersion) &&
            string.Equals(existingVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            return new CrossingVoidVersionMarkerResult(
                target,
                version,
                CrossingVoidVersionMarkerAction.Reused,
                null);
        }

        var baseUrl = string.IsNullOrWhiteSpace(downloadBase)
            ? DownloadBase
            : downloadBase.TrimEnd('/');
        var url = $"{baseUrl}/{Uri.EscapeDataString(version)}/{FileName}";
        string? warning = null;
        try
        {
            var bytes = await httpClient.GetByteArrayAsync(url, cancellationToken);
            if (Matches(bytes, version, platform, channel))
            {
                Write(target, bytes);
                return new CrossingVoidVersionMarkerResult(
                    target,
                    version,
                    CrossingVoidVersionMarkerAction.Downloaded,
                    null);
            }

            warning = "下载站上的版本标记与当前版本/平台不符，已改用本地生成（格式与发布脚本一致）。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or OperationCanceledException or IOException)
        {
            warning = "没能从下载站取到版本标记，已改用本地生成（格式与发布脚本一致）。";
        }

        Write(target, BuildBytes(version, platform, channel));
        return new CrossingVoidVersionMarkerResult(
            target,
            version,
            CrossingVoidVersionMarkerAction.Generated,
            warning);
    }

    private static bool Matches(byte[] bytes, string version, string platform, string channel)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            return string.Equals(root.GetProperty("version").GetString(), version, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(root.GetProperty("platform").GetString(), platform, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(root.GetProperty("channel").GetString(), channel, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void Write(string path, byte[] bytes)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        File.WriteAllBytes(path, bytes);
    }
}
