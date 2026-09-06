using AxTools.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace AxTools.Converters;

public sealed class LogKindToStyleConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        string language)
    {
        var kind = value is LogKind logKind ? logKind : LogKind.Info;
        var suffix = string.Equals(parameter?.ToString(), "Block", StringComparison.Ordinal)
            ? "BlockStyle"
            : "TextStyle";
        var prefix = kind switch
        {
            LogKind.User => "LogUser",
            LogKind.Warning => "LogWarning",
            LogKind.Error => "LogError",
            _ => "LogDefault"
        };
        return Application.Current.Resources[prefix + suffix];
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        string language) =>
        throw new NotSupportedException();
}
