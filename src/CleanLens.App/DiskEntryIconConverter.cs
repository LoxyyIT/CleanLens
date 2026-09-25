using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CleanLens.Windows;

namespace CleanLens.App;

public sealed class DiskEntryIconConverter : IValueConverter
{
    private const int MaximumCachedIcons = 512;
    private static readonly object sync = new();
    private static readonly Dictionary<string, BitmapSource> cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> insertionOrder = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DiskScanEntry entry || string.IsNullOrWhiteSpace(entry.Path)) return null;
        var key = GetCacheKey(entry);
        lock (sync)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
        }

        var icon = ApplicationIconConverter.ExtractIcon(entry.Path);
        if (icon is null) return null;
        lock (sync)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
            while (cache.Count >= MaximumCachedIcons && insertionOrder.TryDequeue(out var oldest)) cache.Remove(oldest);
            cache[key] = icon;
            insertionOrder.Enqueue(key);
        }
        return icon;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    private static string GetCacheKey(DiskScanEntry entry)
    {
        if (entry.IsDirectory) return $"directory:{Path.GetFullPath(entry.Path)}";
        var extension = entry.Extension.Trim();
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".url", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".scr", StringComparison.OrdinalIgnoreCase))
            return $"file:{Path.GetFullPath(entry.Path)}";
        return string.IsNullOrWhiteSpace(extension) ? "file:generic" : $"extension:{extension}";
    }
}
