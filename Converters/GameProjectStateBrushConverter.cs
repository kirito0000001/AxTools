using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AxTools.Converters;

/// <summary>
/// 游戏项目状态点：exists 绿、missing 红、其它（未配置）灰。
/// 只做状态指示，不参与文字排版。
/// </summary>
public sealed class GameProjectStateBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Exists = new(Color.FromArgb(255, 46, 160, 67));
    private static readonly SolidColorBrush Missing = new(Color.FromArgb(255, 205, 60, 60));
    private static readonly SolidColorBrush Unknown = new(Color.FromArgb(255, 150, 150, 150));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value as string switch
        {
            "exists" => Exists,
            "missing" => Missing,
            _ => Unknown
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
