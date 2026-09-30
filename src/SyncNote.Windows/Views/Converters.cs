using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SyncNote.Windows.Views;

// Инверсия bool -> Visibility для empty-state («нет данных»).
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
