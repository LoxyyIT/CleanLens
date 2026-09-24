using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using CleanLens.Core.Models;
using CleanLens.Core.Services;
using Microsoft.Win32;

namespace CleanLens.Windows;

public sealed class RegistryApplicationInventory : IApplicationInventory
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<IReadOnlyList<InstalledApplication>> ScanAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<InstalledApplication>>(() =>
        {
            var applications = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var uninstallKey = baseKey.OpenSubKey(UninstallPath, writable: false);
                        if (uninstallKey is null)
                        {
                            continue;
                        }

                        foreach (var subkeyName in uninstallKey.GetSubKeyNames())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                using var key = uninstallKey.OpenSubKey(subkeyName, writable: false);
                                if (key is null || key.GetValue("SystemComponent") is int systemComponent && systemComponent == 1)
                                {
                                    continue;
                                }

                                var name = ReadString(key, "DisplayName");
                                if (string.IsNullOrWhiteSpace(name))
                                {
                                    continue;
                                }

                                var keyPath = $"{hive} ({view})\\{UninstallPath}\\{subkeyName}";
                                var record = new InstalledApplication(
                                    CreateId(keyPath),
                                    name.Trim(),
                                    ReadString(key, "Publisher"),
                                    ReadString(key, "DisplayVersion"),
                                    ParseInstallDate(ReadString(key, "InstallDate")),
                                    ReadString(key, "InstallLocation"),
                                    ReadString(key, "DisplayIcon"),
                                    ReadString(key, "UninstallString"),
                                    ReadString(key, "QuietUninstallString"),
                                    keyPath,
                                    false,
                                    ReadLong(key, "EstimatedSize"),
                                    $"{(hive == RegistryHive.LocalMachine ? "Windows Registry · machine" : "Windows Registry · user")} · {view}");

                                var identity = $"{Normalize(record.Name)}|{Normalize(record.Publisher)}|{Normalize(record.Version)}|{Normalize(record.InstallLocation)}";
                                if (!applications.ContainsKey(identity))
                                {
                                    applications.Add(identity, record);
                                }
                            }
                            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
                            {
                            }
                        }
                    }
                    catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
                    {
                    }
                }
            }

            return applications.Values.OrderBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }, cancellationToken);
    }

    private static string ReadString(RegistryKey key, string name)
    {
        try
        {
            return Convert.ToString(key.GetValue(name, string.Empty), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return string.Empty;
        }
    }

    private static long? ReadLong(RegistryKey key, string name)
    {
        try
        {
            return long.TryParse(Convert.ToString(key.GetValue(name), CultureInfo.InvariantCulture), out var value) && value >= 0 ? value : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ParseInstallDate(string value)
    {
        return DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)
            ? new DateTimeOffset(date)
            : null;
    }

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static string CreateId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20];
}
