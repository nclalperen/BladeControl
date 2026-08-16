using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BladeControl.UI.ViewModels;

namespace BladeControl.UI.Converters;

public sealed class StatusToneBrushConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        StatusTone tone = value switch
        {
            StatusTone actual => actual,
            bool isError => isError ? StatusTone.Danger : StatusTone.Good,
            _ => StatusTone.Neutral
        };
        bool background = string.Equals(
            parameter as string,
            "Background",
            StringComparison.OrdinalIgnoreCase);
        string key = $"{tone}{(background ? "Background" : string.Empty)}Brush";
        return Application.Current?.TryFindResource(key) as Brush ??
            (background ? Brushes.Transparent : Brushes.Gray);
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) => Binding.DoNothing;
}
