using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AxTools.Converters;

/// <summary>
/// 服务端实例状态点：healthy / recovered 绿，warning 黄，maintenance 灰，error 红，其余（离线）浅灰。
/// 与旧服务器页的配色保持一致。
/// </summary>
public sealed class ServerInstanceStateBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Healthy = new(Color.FromArgb(255, 46, 160, 67));
    private static readonly SolidColorBrush Warning = new(Color.FromArgb(255, 209, 139, 23));
    private static readonly SolidColorBrush Maintenance = new(Color.FromArgb(255, 120, 120, 120));
    private static readonly SolidColorBrush Error = new(Color.FromArgb(255, 205, 60, 60));
    private static readonly SolidColorBrush Offline = new(Color.FromArgb(255, 150, 150, 150));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value as string switch
        {
            "healthy" or "recovered" => Healthy,
            "warning" => Warning,
            "maintenance" => Maintenance,
            "error" => Error,
            _ => Offline
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
