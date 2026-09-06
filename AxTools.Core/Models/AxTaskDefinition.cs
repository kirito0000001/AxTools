namespace AxTools.Core.Models;

public sealed record AxTaskDefinition(
    string Id,
    string Title,
    string ScriptPath,
    IReadOnlyList<string> Arguments,
    bool IsHeavy = true,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);
