using System.Text;
using AxTools.Core.Catalog;
using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class ServerGitUpdateServiceTests
{
    /// <summary>
    /// 服务端脚本以前只在结束时写一个 JSON，所以进度条永远停在"正在准备 0%"。
    /// 这条用例锁住新的接线：脚本中途发的 ::axtools 事件要能送到 EventSink，
    /// 而且不能混进结果数据里。
    /// </summary>
    [Fact]
    public async Task ImportBuildAsync_ForwardsProtocolEventsWithoutPollutingResult()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AxTools-ServerGit-" + Guid.NewGuid().ToString("N"));
        var scriptPath = Path.Combine(
            root,
            "Scripts",
            "Servers",
            "Aliyun",
            "Invoke-AliyunServerRepo.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(
            scriptPath,
            """
            param(
                [Parameter(Mandatory)][string]$LocalRepoPath,
                [Parameter(Mandatory)][string]$Action,
                [Parameter(Mandatory)][string]$OutputPath,
                [string]$SourceDirectory = ''
            )

            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            # 真脚本由 AxTaskProtocol.psm1 直写 stdout；这里用同样的写法模拟。
            [Console]::Out.WriteLine('::axtools {"type":"stage","stage":"prepare","message":"正在检查本机仓库…"}')
            [Console]::Out.WriteLine('::axtools {"type":"progress","stage":"copy","percent":40,"message":"正在同步产物文件 40/100"}')
            [Console]::Out.WriteLine('::axtools {"type":"warning","message":"示例警告"}')
            Write-Output '普通输出行'

            $data = [ordered]@{
                sync    = [ordered]@{ addedFiles = 2; updatedFiles = 1; removedFiles = 0; removedBytes = 0 }
                changes = @([ordered]@{ kind = 'added'; code = 'A'; path = 'Content/Paks/a.uasset'; bytes = 12 })
            }
            $result = [ordered]@{ success = $true; action = $Action; message = ''; data = $data }
            [IO.File]::WriteAllText(
                [IO.Path]::GetFullPath($OutputPath),
                ($result | ConvertTo-Json -Depth 8 -Compress),
                [Text.UTF8Encoding]::new($false))
            exit 0
            """,
            new UTF8Encoding(false));

        var events = new List<AxTaskEvent>();
        var service = new ServerGitUpdateService(
            new PowerShellProcessHost(new PowerShellLocator().FindPowerShell()),
            Path.Combine(root, "Scripts"))
        {
            EventSink = taskEvent =>
            {
                lock (events)
                {
                    events.Add(taskEvent);
                }
            }
        };

        try
        {
            var result = await service
                .ImportBuildAsync(
                    ServerProgramCatalog.Get("crossingvoid"),
                    @"C:\某处\WindowsServer",
                    Path.Combine(root, "localrepo"),
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(60));

            AxTaskEvent[] captured;
            lock (events)
            {
                captured = [.. events];
            }

            Assert.Equal(3, captured.Length);
            Assert.Equal(AxTaskEventType.Stage, captured[0].Type);
            Assert.Equal("正在检查本机仓库…", captured[0].Message);
            Assert.Equal(AxTaskEventType.Progress, captured[1].Type);
            Assert.Equal(40, captured[1].Percent);
            Assert.Equal("正在同步产物文件 40/100", captured[1].Message);
            Assert.Equal(AxTaskEventType.Warning, captured[2].Type);

            // 协议行不能混进 data：sync 必须是对象，changes 必须是干净的列表。
            Assert.Equal(2, result.Sync?.AddedFiles);
            Assert.Equal(1, result.Sync?.UpdatedFiles);
            Assert.Single(result.Changes);
            Assert.Equal("Content/Paks/a.uasset", result.Changes[0].Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
