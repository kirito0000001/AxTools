using System.Text.RegularExpressions;

namespace AxTools.Core.Services;

public static partial class PublishVersionComparer
{
    public static int Compare(string candidate, string online)
    {
        var left = Parse(candidate);
        var right = Parse(online);
        for (var index = 0; index < 4; index++)
        {
            var comparison = left.Core[index].CompareTo(right.Core[index]);
            if (comparison != 0) { return comparison; }
        }
        if (left.Prerelease.Length == 0 && right.Prerelease.Length == 0) { return 0; }
        if (left.Prerelease.Length == 0) { return 1; }
        if (right.Prerelease.Length == 0) { return -1; }
        return ComparePrerelease(left.Prerelease, right.Prerelease);
    }

    private static ParsedVersion Parse(string value)
    {
        var match = VersionPattern().Match(value?.Trim() ?? string.Empty);
        if (!match.Success) { throw new FormatException($"无效的发布版本：{value}"); }
        var parts = match.Groups["core"].Value.Split('.').Select(int.Parse).ToList();
        while (parts.Count < 4) { parts.Add(0); }
        return new ParsedVersion(parts.ToArray(), match.Groups["pre"].Value);
    }

    private static int ComparePrerelease(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            if (index >= leftParts.Length) { return -1; }
            if (index >= rightParts.Length) { return 1; }
            var leftNumeric = int.TryParse(leftParts[index], out var leftNumber);
            var rightNumeric = int.TryParse(rightParts[index], out var rightNumber);
            var comparison = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : leftNumeric
                    ? -1
                    : rightNumeric
                        ? 1
                        : string.Compare(leftParts[index], rightParts[index], StringComparison.OrdinalIgnoreCase);
            if (comparison != 0) { return comparison; }
        }
        return 0;
    }

    [GeneratedRegex(@"^[vV]?(?<core>\d+\.\d+\.\d+(?:\.\d+)?)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$")]
    private static partial Regex VersionPattern();

    private sealed record ParsedVersion(int[] Core, string Prerelease);
}
