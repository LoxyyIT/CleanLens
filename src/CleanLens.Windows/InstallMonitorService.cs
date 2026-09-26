using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CleanLens.Core.Models;
using CleanLens.Core.Services;
using Microsoft.Win32;

namespace CleanLens.Windows;

public sealed record InstallMonitorReport(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, IReadOnlyList<string> AddedApplications, IReadOnlyList<string> RemovedApplications, IReadOnlyList<string> SystemEntryChanges, IReadOnlyList<string> FileEvents, bool IsIncomplete, string? ApplicationId = null, string? ApplicationName = null)
{
    public string DisplayName => $"{FinishedAt.ToLocalTime():g} · {ApplicationName ?? "General installation"}";
    public override string ToString() => DisplayName;
}

public sealed class InstallMonitorService : IDisposable
{
    private const int MaxEvents = 5000;
    private readonly IApplicationInventory inventory;
    private readonly string historyPath;
    private readonly ConcurrentDictionary<string, byte> events = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> watchers = [];
    private Dictionary<string, string> baselineApps = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> baselineSystemEntries = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset startedAt;
    private string? monitoredApplicationId;
    private string? monitoredApplicationName;
    private int overflowed;
    private bool inventoryIncomplete;
    private bool systemSnapshotIncomplete;
    public bool IsRunning { get; private set; }

    public InstallMonitorService(IApplicationInventory inventory, string localDataPath)
    {
        this.inventory = inventory;
        historyPath = Path.Combine(localDataPath, "install-monitor-history.json");
    }

    public async Task StartAsync(IEnumerable<string> additionalRoots, CancellationToken cancellationToken = default, string? applicationId = null, string? applicationName = null)
    {
        if (IsRunning) throw new InvalidOperationException("An install-monitor session is already running.");
        var inventoryBefore = await inventory.ScanAsync(cancellationToken);
        inventoryIncomplete = inventory.LastScanWarning is { Length: > 0 };
        baselineApps = inventoryBefore.ToDictionary(app => app.Id, app => $"{app.Name} · {app.Publisher}", StringComparer.OrdinalIgnoreCase);
        var initialSystemSnapshot = await Task.Run(CaptureSystemEntries, cancellationToken);
        baselineSystemEntries = initialSystemSnapshot.Entries;
        systemSnapshotIncomplete = initialSystemSnapshot.IsIncomplete;
        events.Clear();
        overflowed = 0;
        startedAt = DateTimeOffset.Now;
        monitoredApplicationId = applicationId;
        monitoredApplicationName = applicationName;
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        }.Concat(additionalRoots).Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks"))
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var root in roots)
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 32 * 1024,
                    EnableRaisingEvents = false
                };
                watcher.Created += (_, e) => AddEvent("Created", e.FullPath);
                watcher.Changed += (_, e) => AddEvent("Changed", e.FullPath);
                watcher.Deleted += (_, e) => AddEvent("Deleted", e.FullPath);
                watcher.Renamed += (_, e) => AddEvent("Renamed", $"{e.OldFullPath} → {e.FullPath}");
                watcher.Error += (_, _) => Interlocked.Exchange(ref overflowed, 1);
                watchers.Add(watcher);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                Interlocked.Exchange(ref overflowed, 1);
            }
        }
        if (watchers.Count == 0) throw new InvalidOperationException("No supported folders were available to monitor.");
        IsRunning = true;
    }

    public async Task<InstallMonitorReport> StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning) throw new InvalidOperationException("No install-monitor session is running.");
        foreach (var watcher in watchers) watcher.EnableRaisingEvents = false;
        var inventoryAfter = await inventory.ScanAsync(cancellationToken);
        inventoryIncomplete |= inventory.LastScanWarning is { Length: > 0 };
        var finalSystemSnapshot = await Task.Run(CaptureSystemEntries, cancellationToken);
        var systemEntriesAfter = finalSystemSnapshot.Entries;
        var after = inventoryAfter.ToDictionary(app => app.Id, StringComparer.OrdinalIgnoreCase);
        var before = baselineApps;
        var added = after.Values.Where(app => !before.ContainsKey(app.Id)).Select(app => $"{app.Name} · {app.Publisher}").OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var removed = before.Where(pair => !after.ContainsKey(pair.Key)).Select(pair => pair.Value).ToArray();
        var systemChanges = baselineSystemEntries.Where(pair => !systemEntriesAfter.TryGetValue(pair.Key, out var newValue) || !pair.Value.Equals(newValue, StringComparison.Ordinal))
            .Select(pair => $"Removed or changed: {pair.Key}")
            .Concat(systemEntriesAfter.Where(pair => !baselineSystemEntries.TryGetValue(pair.Key, out var oldValue) || !pair.Value.Equals(oldValue, StringComparison.Ordinal)).Select(pair => $"Added or changed: {pair.Key}"))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var report = new InstallMonitorReport(startedAt, DateTimeOffset.Now, added, removed, systemChanges, events.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(), Volatile.Read(ref overflowed) != 0 || inventoryIncomplete || systemSnapshotIncomplete || finalSystemSnapshot.IsIncomplete, monitoredApplicationId, monitoredApplicationName);
        Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
        var history = File.Exists(historyPath) ? await File.ReadAllTextAsync(historyPath, cancellationToken) : "[]";
        var previous = System.Text.Json.JsonSerializer.Deserialize<List<InstallMonitorReport>>(history) ?? [];
        previous.Add(report);
        await File.WriteAllTextAsync(historyPath, System.Text.Json.JsonSerializer.Serialize(previous.TakeLast(50).ToArray(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        IsRunning = false;
        DisposeWatchers();
        return report;
    }

    public async Task<IReadOnlyList<InstallMonitorReport>> GetReportsAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(historyPath)) return [];
        var json = await File.ReadAllTextAsync(historyPath, cancellationToken);
        return (System.Text.Json.JsonSerializer.Deserialize<List<InstallMonitorReport>>(json) ?? [])
            .OrderByDescending(report => report.FinishedAt).ToArray();
    }

    private void AddEvent(string kind, string path)
    {
        if (events.Count >= MaxEvents)
        {
            Interlocked.Exchange(ref overflowed, 1);
            return;
        }
        events.TryAdd($"{kind}: {path}", 0);
    }

    private static SystemEntrySnapshot CaptureSystemEntries()
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var incomplete = false;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var services = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
                foreach (var serviceName in services?.GetSubKeyNames() ?? [])
                {
                    using var service = services!.OpenSubKey(serviceName);
                    foreach (var valueName in new[] { "ImagePath", "DisplayName", "Start", "Type" })
                    {
                        var value = service?.GetValue(valueName);
                        if (value is not null) AddEntry($"Service [{view}] {serviceName} · {valueName}", value);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { incomplete = true; }

            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    foreach (var runPath in new[]
                    {
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
                        @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\RunOnce"
                    })
                    {
                        using var run = root.OpenSubKey(runPath);
                        foreach (var name in run?.GetValueNames() ?? [])
                            AddEntry($"Startup [{hive} {view}] {runPath} · {name}", run?.GetValue(name));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { incomplete = true; }
            }
        }
        return new SystemEntrySnapshot(entries, incomplete);

        void AddEntry(string label, object? value)
        {
            if (value is null) return;
            var text = value is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            entries[label] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }
    }

    private sealed record SystemEntrySnapshot(Dictionary<string, string> Entries, bool IsIncomplete);

    private void DisposeWatchers()
    {
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
    }

    public void Dispose() => DisposeWatchers();
}
