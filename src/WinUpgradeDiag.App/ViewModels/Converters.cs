using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WinUpgradeDiag.App.ViewModels
{
    /// <summary>Which part of a severity colour set a binding wants.</summary>
    public enum SeverityBrushKind
    {
        Foreground,
        Background,
        Border
    }

    /// <summary>
    /// Maps a severity name ("Critical", "Warning", "Info") to the matching brush from the theme.
    /// Keeping the mapping in one place is what stops severity colour drifting between the verdict
    /// card, the finding chips and the gap list.
    /// </summary>
    public sealed class SeverityToBrushConverter : IValueConverter
    {
        public SeverityBrushKind Kind { get; set; } = SeverityBrushKind.Foreground;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var severity = value as string ?? string.Empty;
            string key;

            switch (severity.ToLowerInvariant())
            {
                case "critical": key = "Critical"; break;
                case "warning": key = "Warning"; break;
                case "good": key = "Good"; break;
                default: key = "Info"; break;
            }

            var suffix = Kind == SeverityBrushKind.Foreground ? "Fg"
                       : Kind == SeverityBrushKind.Background ? "Bg"
                       : "Line";

            var brush = Application.Current?.TryFindResource(key + suffix) as Brush;
            return brush ?? Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Collapses when the bound boolean is true — the mirror of BooleanToVisibilityConverter.</summary>
    public sealed class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool && (bool)value ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
