namespace AxTools.Core.Models;

public sealed class BootstrapSettings
{
    public int SchemaVersion { get; set; } = 1;

    public string ProjectRootPath { get; set; } = string.Empty;
}
