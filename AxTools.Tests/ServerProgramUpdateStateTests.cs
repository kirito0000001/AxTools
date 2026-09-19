using AxTools.Core.Catalog;
using AxTools.Core.Models;
using Xunit;

namespace AxTools.Tests;

/// <summary>
/// 「上一版」那一行的文案。回退之后 previous 会和 deployed 撞成同一个提交，
/// 以前会原样印两遍同一个版本号，看着像"上一版＝当前版"（2026-09-20 用户反馈）。
/// </summary>
public sealed class ServerProgramUpdateStateTests
{
    private static ServerGitCommitInfo Commit(string sha, string subject) =>
        new()
        {
            Sha = sha,
            Short = sha[..8],
            Subject = subject,
            Date = DateTimeOffset.Now
        };

    private static ServerProgramUpdateState State(
        ServerGitCommitInfo deployed,
        ServerGitCommitInfo? previous,
        ServerGitCommitInfo? main,
        bool canSwitchLatest,
        bool rollbackPossible) =>
        new()
        {
            Profile = ServerProgramCatalog.Get("crossingvoid"),
            Remote = new ServerGitRemoteStatus
            {
                Initialized = true,
                Deployed = deployed,
                Previous = previous,
                Main = main,
                CanSwitchLatest = canSwitchLatest,
                RollbackPossible = rollbackPossible
            }
        };

    [Fact]
    public void PreviousText_AfterRollback_SaysWhereYouActuallyAre()
    {
        var old = Commit("ff9c34280b96cf0671aa756b141b550ab822eb38", "初始化：零境交错 服务端（线上现状快照）");
        var state = State(
            deployed: old,
            previous: old,
            main: Commit("22d8ef5d1234567890abcdef1234567890abcdef", "零境交错 服务端 2026-09-20 00:28 · 导入新构建"),
            canSwitchLatest: true,
            rollbackPossible: false);

        Assert.Equal("—（当前就停在这一版上，可切回最新版）", state.PreviousText);
    }

    [Fact]
    public void PreviousText_AfterRollbackWithoutNewerVersion_SaysNothingToGoBackTo()
    {
        var old = Commit("ff9c34280b96cf0671aa756b141b550ab822eb38", "初始化");
        var state = State(
            deployed: old,
            previous: old,
            main: old,
            canSwitchLatest: false,
            rollbackPossible: false);

        Assert.Equal("—（当前就停在这一版上，没有更旧的版本可退）", state.PreviousText);
    }

    [Fact]
    public void PreviousText_NormalCase_DescribesPreviousCommit()
    {
        var old = Commit("ff9c34280b96cf0671aa756b141b550ab822eb38", "初始化");
        var current = Commit("22d8ef5d1234567890abcdef1234567890abcdef", "导入新构建");
        var state = State(
            deployed: current,
            previous: old,
            main: current,
            canSwitchLatest: false,
            rollbackPossible: true);

        Assert.Equal(old.Describe(), state.PreviousText);
    }

    [Fact]
    public void PreviousText_NoPreviousCommit_KeepsOldWording()
    {
        var current = Commit("22d8ef5d1234567890abcdef1234567890abcdef", "导入新构建");
        var state = State(
            deployed: current,
            previous: null,
            main: current,
            canSwitchLatest: false,
            rollbackPossible: false);

        Assert.Equal("没有可回退的版本", state.PreviousText);
    }
}
