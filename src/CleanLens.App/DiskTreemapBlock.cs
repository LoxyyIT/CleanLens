using System.Windows;

namespace CleanLens.App;

public sealed record DiskTreemapBlock(
    string Path,
    string Label,
    string Tooltip,
    string Color,
    double X,
    double Y,
    double Width,
    double Height,
    bool IsOther,
    bool ShowLabel)
{
    public Visibility LabelVisibility => ShowLabel ? Visibility.Visible : Visibility.Collapsed;
}
