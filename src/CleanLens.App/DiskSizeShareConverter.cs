using System.Globalization;
using System.Windows.Data;

namespace CleanLens.App;

public sealed class DiskSizeShareConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not long sizeBytes || values[1] is not long totalBytes || totalBytes <= 0)
            return 0d;

        return Math.Clamp((double)sizeBytes / totalBytes * 100d, 0d, 100d);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        [Binding.DoNothing, Binding.DoNothing];
}
