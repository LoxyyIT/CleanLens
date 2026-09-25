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
    string Source,
    string PackageFullName = "",
    bool IsNonRemovablePackage = false,
    string PackageLogo = "",
    string PackageFamilyName = "")
{
    public bool IsAppxPackage => !string.IsNullOrWhiteSpace(PackageFullName);
    public string SearchText => string.Join(' ', Name, Publisher, Version, InstallLocation, DisplayIcon, UninstallCommand, PackageFullName, PackageFamilyName);
    public string EstimatedSizeText => EstimatedSizeKilobytes is null ? string.Empty : FormatSize(EstimatedSizeKilobytes.Value * 1024);

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
