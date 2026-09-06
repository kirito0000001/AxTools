using AxTools.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxTools.Core.ViewModels;

public sealed class StorageItemViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _isSelected;

    public StorageItemViewModel(
        StorageScanItem item,
        Action selectionChanged)
    {
        Item = item;
        _selectionChanged = selectionChanged;
    }

    public StorageScanItem Item { get; }

    public string DisplayName => Item.DisplayName;

    public string ToolStableKey => Item.ToolStableKey;

    public string Path => Item.Path;

    public StorageCategory Category => Item.Category;

    public string CategoryText => Category switch
    {
        StorageCategory.SafeTemporary => "安全临时文件",
        StorageCategory.RebuildableCache => "可重建缓存",
        StorageCategory.HighCostCache => "高成本缓存",
        StorageCategory.ReleaseArtifact => "发布产物",
        _ => "其他"
    };

    public string SizeText => FormatBytes(Item.SizeBytes);

    public string GrowthText => Item.GrowthBytes switch
    {
        > 0 => $"较上次 +{FormatBytes(Item.GrowthBytes)}",
        < 0 => $"较上次 -{FormatBytes(-Item.GrowthBytes)}",
        _ when Item.PreviousSizeBytes > 0 => "较上次无变化",
        _ => "首次扫描"
    };

    public string Impact => Item.Impact;

    public bool CanClean => Item.CanClean;

    public string LastModifiedText => Item.LastModifiedAt is null
        ? "无文件"
        : $"最近修改：{Item.LastModifiedAt.Value.LocalDateTime:yyyy-MM-dd HH:mm}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!CanClean && value)
            {
                return;
            }

            if (SetProperty(ref _isSelected, value))
            {
                _selectionChanged();
            }
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(bytes, 0);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}
