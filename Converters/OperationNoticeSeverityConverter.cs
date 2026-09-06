using AxTools.Core.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace AxTools.Converters;

public sealed class OperationNoticeSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            OperationNoticeSeverity.Success => InfoBarSeverity.Success,
            OperationNoticeSeverity.Warning => InfoBarSeverity.Warning,
            OperationNoticeSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
