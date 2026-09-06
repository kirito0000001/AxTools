using System.Diagnostics;
using System.Text;
using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class PowerShellProcessHostTests
{
    [Fact]
    public async Task RunAsync_PassesSelectedVisualStudioEnvironmentWithoutChangingMachineEnvironment()
    {
        var powerShellPath = new PowerShellLocator().FindPowerShell();
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "AxTools-PowerShellProcessHost-" + Guid.NewGuid().ToString("N"));
        var scriptPath = Path.Combine(testRoot, "show-native-toolchain.ps1");
        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(
            scriptPath,
            "Write-Output \"$env:AXTOOLS_VISUAL_STUDIO_ROOT|$env:AXTOOLS_MSVC_VERSION\"",
            new UTF8Encoding(false));
        var machineVisualStudioRoot = Environment.GetEnvironmentVariable(
            "AXTOOLS_VISUAL_STUDIO_ROOT");

        try
        {
            var output = new List<string>();
            var host = new PowerShellProcessHost(
                powerShellPath,
                toolchains: new ToolchainSettings
                {
                    VisualStudioRoot = @"C:\Program Files\Microsoft Visual Studio\18\Insiders",
                    MsvcVersion = "14.44.35207"
                });

            var result = await host.RunAsync(
                new AxTaskDefinition("native-env", "原生工具链环境", scriptPath, []),
                (_, line) =>
                {
                    output.Add(line);
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(
                @"C:\Program Files\Microsoft Visual Studio\18\Insiders|14.44.35207",
                output);
            Assert.Equal(
                machineVisualStudioRoot,
                Environment.GetEnvironmentVariable("AXTOOLS_VISUAL_STUDIO_ROOT"));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_DoesNotWaitForLaunchedChildToCloseInheritedOutput()
    {
        var powerShellPath = new PowerShellLocator().FindPowerShell();
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "AxTools-PowerShellProcessHost-" + Guid.NewGuid().ToString("N"));
        var scriptPath = Path.Combine(testRoot, "start-child.ps1");
        var childPidPath = Path.Combine(testRoot, "child.pid");
        Directory.CreateDirectory(testRoot);

        Task<AxProcessResult>? runTask = null;
        try
        {
            await File.WriteAllTextAsync(
                scriptPath,
                """
                param([Parameter(Mandatory=$true)][string]$ChildPidPath)
                $ErrorActionPreference = 'Stop'
                $childInfo = [Diagnostics.ProcessStartInfo]::new()
                $childInfo.FileName = Join-Path $PSHOME 'pwsh.exe'
                $childInfo.UseShellExecute = $false
                $childInfo.CreateNoWindow = $true
                foreach($argument in @('-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 15')) {
                    $childInfo.ArgumentList.Add($argument)
                }
                $child = [Diagnostics.Process]::Start($childInfo)
                [IO.File]::WriteAllText($ChildPidPath, $child.Id.ToString(), [Text.UTF8Encoding]::new($false))
                Write-Output '::axtools {"type":"result","status":"success","exitCode":0}'
                exit 0
                """,
                new UTF8Encoding(false));

            var output = new List<string>();
            var host = new PowerShellProcessHost(
                powerShellPath,
                outputDrainTimeout: TimeSpan.FromMilliseconds(250));
            runTask = host.RunAsync(
                new AxTaskDefinition(
                    "inherited-output",
                    "继承输出句柄测试",
                    scriptPath,
                    new[] { "-ChildPidPath", childPidPath }),
                (_, line) =>
                {
                    output.Add(line);
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None);

            var result = await runTask.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.CancellationRequested);
            Assert.Contains(output, line => line.Contains("\"status\":\"success\"", StringComparison.Ordinal));
        }
        finally
        {
            KillRecordedProcess(childPidPath);
            if (runTask is not null)
            {
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // The original assertion reports the failure; cleanup must not replace it.
                }
            }

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void KillRecordedProcess(string childPidPath)
    {
        if (!File.Exists(childPidPath) ||
            !int.TryParse(File.ReadAllText(childPidPath), out var childPid))
        {
            return;
        }

        try
        {
            using var child = Process.GetProcessById(childPid);
            child.Kill(entireProcessTree: true);
            child.WaitForExit(2000);
        }
        catch (ArgumentException)
        {
            // The test child has already exited.
        }
        catch (InvalidOperationException)
        {
            // The test child has already exited.
        }
    }
}
