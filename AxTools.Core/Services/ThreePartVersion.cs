namespace AxTools.Core.Services;

public readonly record struct ThreePartVersion(int Major, int Feature, int BugFix)
{
    public static ThreePartVersion ParseOrDefault(string? value)
    {
        var normalized = value?.Trim().TrimStart('v', 'V') ?? string.Empty;
        var suffixIndex = normalized.IndexOf('-');
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        var parts = normalized.Split('.');
        return parts.Length >= 3 &&
               int.TryParse(parts[0], out var major) && major >= 0 &&
               int.TryParse(parts[1], out var feature) && feature >= 0 &&
               int.TryParse(parts[2], out var bugFix) && bugFix >= 0
            ? new ThreePartVersion(major, feature, bugFix)
            : new ThreePartVersion(0, 0, 0);
    }

    public ThreePartVersion WithMajor(double value) => this with { Major = Normalize(value) };

    public ThreePartVersion WithFeature(double value) => this with { Feature = Normalize(value) };

    public ThreePartVersion WithBugFix(double value) => this with { BugFix = Normalize(value) };

    public override string ToString() => $"{Major}.{Feature}.{BugFix}";

    private static int Normalize(double value) =>
        double.IsFinite(value) ? Math.Max(0, (int)Math.Round(value)) : 0;
}
