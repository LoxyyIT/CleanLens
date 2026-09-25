using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using CleanLens.Core.Models;
using CleanLens.Core.Services;
using Microsoft.Win32;

namespace CleanLens.Windows;

public sealed class RegistryApplicationInventory : IApplicationInventory
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public string? LastScanWarning { get; private set; }

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

            LastScanWarning = null;
            try
            {
                foreach (var package in ScanAppxPackages(cancellationToken))
                {
                    var identity = $"appx|{package.PackageFullName}";
                    applications[identity] = package;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or OperationCanceledException)
            {
                if (ex is OperationCanceledException) throw;
                LastScanWarning = ex.Message;
            }

            return applications.Values.OrderBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }, cancellationToken);
    }

    private static IReadOnlyList<InstalledApplication> ScanAppxPackages(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; Get-AppxPackage -PackageTypeFilter Main | Where-Object { -not $_.IsFramework } | Select-Object Name,PackageFullName,PackageFamilyName,Publisher,Version,InstallLocation,IsNonRemovable,Logo | ConvertTo-Json -Depth 3 -Compress");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows PowerShell did not start.");
        using var registration = cancellationToken.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        process.WaitForExit();
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "AppX inventory failed." : error.Trim());
        if (string.IsNullOrWhiteSpace(output)) return [];
        using var document = JsonDocument.Parse(output);
        var packages = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : new[] { document.RootElement };
        return packages.Select(package =>
        {
            var fullName = JsonString(package, "PackageFullName");
            var name = JsonString(package, "Name");
            if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(name)) return null;
            return new InstalledApplication(
                CreateId("AppX\\" + fullName), name, JsonString(package, "Publisher"), JsonString(package, "Version"), null,
                JsonString(package, "InstallLocation"), JsonString(package, "Logo"), string.Empty, string.Empty,
                "Current-user AppX package", false, null, "Windows AppX · current user", fullName,
                JsonBoolean(package, "IsNonRemovable"), JsonString(package, "Logo"), JsonString(package, "PackageFamilyName"));
        }).Where(record => record is not null).Cast<InstalledApplication>().ToArray();
    }

    private static string JsonString(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ToString() : string.Empty;
    private static bool JsonBoolean(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

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
