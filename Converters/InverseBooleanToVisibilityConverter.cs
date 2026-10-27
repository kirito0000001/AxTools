using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace AxTools.Converters;

/// <summary>
/// true → Collapsed，false → Visible。用于“只在非游戏工具上显示”的那类元素，
/// 省得为了取反在 ViewModel 里加一堆 ShowXxx 布尔属性。
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
