using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows;

namespace CleanLens.App;

public partial class ManualSearchRootSetting : ObservableObject
{
    [ObservableProperty]
    private bool isEnabled;

    private string displayPath;

    public string Key { get; }
    public string Path { get; }
    public string DisplayPath => string.IsNullOrWhiteSpace(Path) ? displayPath : Path;
    public bool IsBuiltIn { get; }
    public Visibility RemoveVisibility => IsBuiltIn ? Visibility.Collapsed : Visibility.Visible;

    public ManualSearchRootSetting(string key, string path, bool isEnabled, bool isBuiltIn = false, string? displayPath = null)
    {
        Key = key;
        Path = path;
        IsBuiltIn = isBuiltIn;
        this.displayPath = displayPath ?? path;
        this.isEnabled = isEnabled;
    }

    public void UpdateDisplayPath(string value)
    {
        SetProperty(ref displayPath, value, nameof(DisplayPath));
    }
}
