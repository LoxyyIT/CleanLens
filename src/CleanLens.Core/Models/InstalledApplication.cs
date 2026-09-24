namespace CleanLens.Core.Models;

public sealed record InstalledApplication(
    string Id,
    string Name,
    string Publisher,
    string Version,
    DateTimeOffset? InstalledAt,
    string InstallLocation,
    string DisplayIcon,
    string UninstallCommand,
    string QuietUninstallCommand,
    string RegistryKeyPath,
    bool IsSystemComponent,
    long? EstimatedSizeKilobytes,
    string Source)
{
    public string SearchText => string.Join(' ', Name, Publisher, Version, InstallLocation, DisplayIcon, UninstallCommand);
    public string EstimatedSizeText => EstimatedSizeKilobytes is null ? "Not reported" : FormatSize(EstimatedSizeKilobytes.Value * 1024);

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
