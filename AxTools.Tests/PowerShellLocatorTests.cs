using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class PowerShellLocatorTests
{
    [Fact]
    public void FindPowerShell_PrefersProgramFilesThenWindowsAppsThenPath()
    {
        var programFilesCandidate = @"C:\Program Files\PowerShell\7\pwsh.exe";
        var windowsAppsCandidate = @"C:\Users\tester\AppData\Local\Microsoft\WindowsApps\pwsh.exe";
        var pathCandidate = @"D:\Tools\pwsh.exe";

        var programFilesLocator = CreateLocator(programFilesCandidate, windowsAppsCandidate, pathCandidate);
        var windowsAppsLocator = CreateLocator(windowsAppsCandidate, windowsAppsCandidate, pathCandidate);
        var pathLocator = CreateLocator(pathCandidate, windowsAppsCandidate, pathCandidate);

        Assert.Equal(programFilesCandidate, programFilesLocator.FindPowerShell());
        Assert.Equal(windowsAppsCandidate, windowsAppsLocator.FindPowerShell());
        Assert.Equal(pathCandidate, pathLocator.FindPowerShell());
    }

    [Fact]
    public void FindPowerShell_ThrowsHelpfulErrorWhenPowerShell7IsMissing()
    {
        var locator = new PowerShellLocator(
            _ => false,
            @"C:\Program Files",
            @"C:\Users\tester\AppData\Local",
            string.Empty);

        var exception = Assert.Throws<FileNotFoundException>(() => locator.FindPowerShell());

        Assert.Contains("PowerShell 7", exception.Message);
    }

    private static PowerShellLocator CreateLocator(
        string existingPath,
        string windowsAppsCandidate,
        string pathCandidate) =>
        new(
            path => string.Equals(path, existingPath, StringComparison.OrdinalIgnoreCase),
            @"C:\Program Files",
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(windowsAppsCandidate)))!,
            Path.GetDirectoryName(pathCandidate)!);
}
