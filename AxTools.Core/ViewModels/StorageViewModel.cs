using System.Collections.ObjectModel;
using AxTools.Core.Models;
using AxTools.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxTools.Core.ViewModels;

public sealed class StorageViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly StorageScanService _scanService;
    private readonly StorageCleanupService _cleanupService;
    private bool _isBusy;
    private string _statusText = "尚未扫描存储空间。";
    private string _totalSizeText = "0 B";

    public StorageViewModel(
        AppSettings settings,
        StorageScanService scanService,
        StorageCleanupService cleanupService)
    {
        _settings = settings;
        _scanService = scanService;
        _cleanupService = cleanupService;
    }

    public ObservableCollection<StorageItemViewModel> Items { get; } = [];

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanScan));
                OnPropertyChanged(nameof(CanCleanSelected));
            }
        }
    }

    public bool CanScan => !IsBusy;

    public bool CanCleanSelected =>
        !IsBusy && Items.Any(item => item.IsSelected);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string TotalSizeText
    {
        get => _totalSizeText;
        private set => SetProperty(ref _totalSizeText, value);
    }

    public string SelectedSummary
    {
        get
        {
            var selected = SelectedItems;
            return selected.Count == 0
                ? "尚未选择清理项。"
                : $"已选 {selected.Count} 项，预计释放 {StorageItemViewModel.FormatBytes(selected.Sum(item => item.Item.SizeBytes))}。";
        }
    }

    public IReadOnlyList<StorageItemViewModel> SelectedItems =>
        Items.Where(item => item.IsSelected).ToArray();

    public async Task ScanAsync(
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            var result = await _scanService.ScanAsync(
                _settings,
                progress,
                cancellationToken);
            Items.Clear();
            foreach (var item in result.Items.OrderBy(item => item.Category).ThenBy(item => item.ToolStableKey))
            {
                Items.Add(new StorageItemViewModel(item, OnSelectionChanged));
            }

            TotalSizeText = StorageItemViewModel.FormatBytes(result.TotalBytes);
            StatusText = $"已扫描 {Items.Count} 项，可管理空间 {TotalSizeText}。";
            OnPropertyChanged(nameof(CanCleanSelected));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<StorageCleanupResult> CleanupSelectedAsync(
        string currentExecutablePath,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var selected = SelectedItems.Select(item => item.Item).ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException("请先选择要清理的存储项。");
        }

        IsBusy = true;
        try
        {
            var result = await _cleanupService.CleanupAsync(
                _settings,
                selected,
                currentExecutablePath,
                progress,
                cancellationToken);
            StatusText = result.Failures.Count == 0
                ? $"清理完成，实际释放 {StorageItemViewModel.FormatBytes(result.ReleasedBytes)}。"
                : $"清理完成，释放 {StorageItemViewModel.FormatBytes(result.ReleasedBytes)}，失败 {result.Failures.Count} 项。";
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(CanCleanSelected));
        OnPropertyChanged(nameof(SelectedSummary));
    }
}
