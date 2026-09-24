using System.Globalization;
using System.Windows.Data;
using CleanLens.Core.Localization;

namespace CleanLens.App;

public sealed class LocalizationValueConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not { } value || values[1] is not string language || parameter is not string prefix)
        {
            return Binding.DoNothing;
        }
        return LocalizationCatalog.Translate(language, prefix + value);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
