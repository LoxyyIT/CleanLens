using System.Globalization;
using System.Windows.Data;

namespace CleanLens.App;

public sealed class ApplicationMonogramConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string name || string.IsNullOrWhiteSpace(name))
        {
            return "·";
        }

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length > 1)
        {
            return string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0])));
        }

        return name.Length == 1 ? name.ToUpperInvariant() : name[..2].ToUpperInvariant();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
