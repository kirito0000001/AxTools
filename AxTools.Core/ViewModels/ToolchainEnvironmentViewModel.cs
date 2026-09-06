using System.Collections.ObjectModel;
using AxTools.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxTools.Core.ViewModels;

public sealed class ToolchainEnvironmentViewModel : ObservableObject
{
    private string _changeSummary = "尚未建立环境快照。";
    private string _scanStatus = "尚未扫描环境。";
    private bool _isScanning;

    public ToolchainEnvironmentViewModel(
        ToolchainSettings settings,
        IReadOnlyList<ToolchainStatusItem>? statuses = null,
        string? changeSummary = null)
    {
        Settings = settings;
        ReplaceItems(statuses ?? [], changeSummary ?? "尚未建立环境快照。");
    }

    public ToolchainSettings Settings { get; }

    public ObservableCollection<ToolchainStatusItem> Items { get; } = [];

    public string ChangeSummary
    {
        get => _changeSummary;
        private set => SetProperty(ref _changeSummary, value);
    }

    public string ScanStatus
    {
        get => _scanStatus;
        private set => SetProperty(ref _scanStatus, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        internal set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(CanRescan));
            }
        }
    }

    public bool CanRescan => !IsScanning;

    public void ReplaceItems(
        IReadOnlyList<ToolchainStatusItem> items,
        string changeSummary)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        ChangeSummary = changeSummary;
        ScanStatus = $"已扫描 {Items.Count} 个环境组件，时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}。";
    }
}
