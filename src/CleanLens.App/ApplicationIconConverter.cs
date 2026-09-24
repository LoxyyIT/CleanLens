using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace CleanLens.App;

public sealed class ApplicationIconConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, BitmapSource> cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string displayIcon || string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var path = ResolvePath(displayIcon);
        if (path is null)
        {
            return null;
        }

        if (cache.TryGetValue(path, out var cached))
        {
            return cached;
        }
        var extracted = ExtractIcon(path);
        if (extracted is not null)
        {
            cache.TryAdd(path, extracted);
        }
        return extracted;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    internal static string? ResolvePath(string displayIcon)
    {
        var value = Environment.ExpandEnvironmentVariables(displayIcon.Trim());
        if (value.StartsWith('"'))
        {
            var endQuote = value.IndexOf('"', 1);
            if (endQuote > 1)
            {
                value = value[1..endQuote];
            }
        }
        else
        {
            var comma = value.LastIndexOf(',');
            if (comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out _))
            {
                value = value[..comma].Trim();
            }
        }

        if (!Path.IsPathFullyQualified(value) || value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(value);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static BitmapSource? ExtractIcon(string path)
    {
        var info = new ShellFileInfo();
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x000000100 | 0x000000000);
        if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            DestroyIcon(info.Icon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
