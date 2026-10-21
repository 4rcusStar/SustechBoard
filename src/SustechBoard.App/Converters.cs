using System.Globalization;
using System.Windows.Data;

namespace SustechBoard.App;

/// <summary>
/// Converts the window's current width into a MaxHeight for a text block, so line counts
/// grow as the user widens the panel. Declared in XAML with LineHeight / line-count props.
/// </summary>
public sealed class LinesByWidthConverter : IValueConverter
{
    public double LineHeight { get; set; } = 15;
    public int NarrowLines { get; set; } = 1;
    public int MidLines { get; set; } = 2;
    public int WideLines { get; set; } = 3;
    public double NarrowBelow { get; set; } = 360;
    public double WideAbove { get; set; } = 560;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double width = value is double d && !double.IsNaN(d) ? d : 392;

        int lines = width < NarrowBelow ? NarrowLines
                  : width >= WideAbove ? WideLines
                  : MidLines;

        return Math.Max(LineHeight, lines * LineHeight);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
