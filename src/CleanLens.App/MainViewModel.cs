using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CleanLens.Core.Models;
using CleanLens.Core.Services;
using CleanLens.Data;
using CleanLens.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CleanLens.App;

public partial class MainViewModel : ObservableObject
{
    private readonly IApplicationInventory inventory;
    private readonly LeftoverScanner leftoverScanner;
    private readonly CleanLensDatabase database;
    private readonly QuarantineService quarantineService;
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanLens", "settings.json");
    private bool scanned;
    private bool leftoversScanned;
    private InstalledApplication? pendingUninstallApplication;
    private string? pendingUninstallApplicationId;
    private bool uninstallRemovalVerified;
    private string searchText = string.Empty;
    private int filterIndex;

    [ObservableProperty]
    private InstalledApplication? selectedApplication;

    [ObservableProperty]
    private LeftoverCandidate? selectedLeftover;

    [ObservableProperty]
    private QuarantineEntry? selectedQuarantine;

    [ObservableProperty]
    private bool safetyAccepted;

    [ObservableProperty]
    private string statusText = "Scan applications to read the Windows uninstall registry.";

    [ObservableProperty]
    private string pageTitle = "Overview";

    [ObservableProperty]
    private string pageSubtitle = "See what is installed. Review every cleanup candidate before moving it.";

    [ObservableProperty]
    private Visibility disclaimerVisibility = Visibility.Visible;

    public ObservableCollection<InstalledApplication> Applications { get; } = [];
    public ObservableCollection<LeftoverCandidate> Leftovers { get; } = [];
    public ObservableCollection<HistoryEntry> HistoryEntries { get; } = [];
    public ObservableCollection<QuarantineEntry> QuarantineEntries { get; } = [];
    public ObservableCollection<InstalledApplication> VisibleApplications { get; } = [];
    public bool HasReviewApplication => SelectedApplication is not null || pendingUninstallApplication is not null;

    public string AppCountText => scanned ? Applications.Count.ToString() : "Not scanned yet";
    public string TotalSizeText => scanned ? FormatBytes(Applications.Sum(application => application.EstimatedSizeKilobytes.GetValueOrDefault() * 1024)) : "Not scanned yet";
    public string LeftoversText => leftoversScanned ? Leftovers.Count.ToString() : "Not scanned yet";
    public string DetailVersion => SelectedApplication is null ? "Select an application to inspect its registered details." : $"Version {Fallback(SelectedApplication.Version)} · {FormatInstallDate(SelectedApplication.InstalledAt)}";
    public string DetailLocation => SelectedApplication is null ? string.Empty : $"Install location: {Fallback(SelectedApplication.InstallLocation)}";
    public string DetailSource => SelectedApplication is null ? string.Empty : $"{SelectedApplication.Source} · {FormatEstimate(SelectedApplication.EstimatedSizeKilobytes)}";

    public MainViewModel(IApplicationInventory inventory, LeftoverScanner leftoverScanner, CleanLensDatabase database, QuarantineService quarantineService)
    {
        this.inventory = inventory;
        this.leftoverScanner = leftoverScanner;
        this.database = database;
        this.quarantineService = quarantineService;
        SafetyAccepted = LoadSafetyAcceptance();
        DisclaimerVisibility = SafetyAccepted ? Visibility.Collapsed : Visibility.Visible;
        _ = LoadLocalRecordsAsync();
    }

    partial void OnSelectedApplicationChanged(InstalledApplication? value)
    {
        Leftovers.Clear();
        leftoversScanned = false;
        OnPropertyChanged(nameof(LeftoversText));
        OnPropertyChanged(nameof(DetailVersion));
        OnPropertyChanged(nameof(DetailLocation));
        OnPropertyChanged(nameof(DetailSource));
    }

    partial void OnSafetyAcceptedChanged(bool value)
    {
        DisclaimerVisibility = value ? Visibility.Collapsed : Visibility.Visible;
    }

    public void UpdateSearch(string value)
    {
        searchText = value.Trim();
        RefreshVisibleApplications();
    }

    public void UpdateFilter(int value)
    {
        filterIndex = value;
        RefreshVisibleApplications();
    }

    public void SetPage(string page)
    {
        PageTitle = page;
        PageSubtitle = page switch
        {
            "Applications" => "Installed applications reported by the Windows uninstall registry.",
            "Leftover review" => uninstallRemovalVerified && pendingUninstallApplication is not null
                ? $"{pendingUninstallApplication.Name} is no longer registered. Review every candidate before moving it."
                : "Candidate review is available after CleanLens starts an uninstaller and a fresh scan confirms the app is no longer registered.",
            "History" => "Local actions recorded by CleanLens on this device.",
            "Quarantine" => "Restore moved folders while their original paths remain available.",
            _ => "See what is installed. Review every cleanup candidate before moving it."
        };
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        try
        {
            StatusText = "Scanning registered Windows applications…";
            var results = await inventory.ScanAsync();
            Applications.Clear();
            foreach (var application in results)
            {
                Applications.Add(application);
            }
            scanned = true;
            RefreshVisibleApplications();
            OnPropertyChanged(nameof(AppCountText));
            OnPropertyChanged(nameof(TotalSizeText));
            OnPropertyChanged(nameof(LeftoversText));
            if (pendingUninstallApplicationId is not null)
            {
                uninstallRemovalVerified = results.All(application => application.Id != pendingUninstallApplicationId);
                StatusText = uninstallRemovalVerified
                    ? $"Scan complete · {pendingUninstallApplication?.Name ?? "The selected application"} no longer appears in registered uninstall entries. Associated folder review is available."
                    : $"Scan complete · {results.Count} applications found. The selected application is still registered, so cleanup candidates remain unavailable.";
            }
            else
            {
                StatusText = $"Scan complete · {results.Count} applications read from registered uninstall entries.";
            }
            await database.RecordOperationAsync("All applications", "Inventory scan", $"Read {results.Count} registered uninstall entries.");
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ScanLeftoversAsync()
    {
        var application = SelectedApplication ?? pendingUninstallApplication;
        if (application is null)
        {
            StatusText = "Select an application first.";
            return;
        }
        if (application.Id != pendingUninstallApplicationId || !uninstallRemovalVerified)
        {
            StatusText = "Run the registered uninstaller, then scan applications again. Candidate review is available only after the selected app is no longer registered.";
            return;
        }

        try
        {
            StatusText = "Checking exact publisher/product application-data paths…";
            var results = await leftoverScanner.ScanAsync(application);
            Leftovers.Clear();
            foreach (var candidate in results)
            {
                Leftovers.Add(candidate);
            }
            leftoversScanned = true;
            OnPropertyChanged(nameof(LeftoversText));
            StatusText = results.Count == 0
                ? "No exact application-data candidates matched. This is not a full system or registry scan."
                : $"{results.Count} candidate folder(s) found. None are selected automatically.";
        }
        catch (Exception ex)
        {
            StatusText = $"Leftover scan failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void AcceptSafety()
    {
        if (!SafetyAccepted)
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new UserSettings(true)));
    }

    public async Task<string> QuarantineSelectedAsync()
    {
        if (!SafetyAccepted)
        {
            throw new InvalidOperationException("Accept the safety notice before continuing.");
        }
        var application = SelectedApplication ?? pendingUninstallApplication;
        if (SelectedLeftover is null || application is null || application.Id != pendingUninstallApplicationId || !uninstallRemovalVerified)
        {
            throw new InvalidOperationException("Select a reviewed leftover folder first.");
        }
        var operationId = await quarantineService.MoveAsync(SelectedLeftover.Path, application.Name);
        Leftovers.Remove(SelectedLeftover);
        SelectedLeftover = null;
        await RefreshLocalRecordsAsync();
        OnPropertyChanged(nameof(LeftoversText));
        StatusText = "Folder moved to local quarantine. Its contents were not deleted.";
        return operationId;
    }

    public async Task RestoreSelectedAsync()
    {
        if (!SafetyAccepted || SelectedQuarantine is null)
        {
            throw new InvalidOperationException("Accept the safety notice and select a quarantine record first.");
        }
        await quarantineService.RestoreAsync(SelectedQuarantine);
        await RefreshLocalRecordsAsync();
        StatusText = "Folder restored to its original path.";
    }

    public async Task RecordUninstallAsync(InstalledApplication application, string result)
    {
        pendingUninstallApplication = application;
        pendingUninstallApplicationId = application.Id;
        uninstallRemovalVerified = false;
        await database.RecordOperationAsync(application.Name, "Official uninstall started", result);
        await RefreshLocalRecordsAsync();
    }

    public Task RefreshLocalRecordsFromUiAsync() => RefreshLocalRecordsAsync();

    private async Task LoadLocalRecordsAsync()
    {
        try
        {
            await RefreshLocalRecordsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Local history is unavailable: {ex.Message}";
        }
    }

    private async Task RefreshLocalRecordsAsync()
    {
        var history = await database.GetHistoryAsync();
        HistoryEntries.Clear();
        foreach (var entry in history)
        {
            HistoryEntries.Add(entry);
        }
        var quarantine = await database.GetQuarantineAsync();
        QuarantineEntries.Clear();
        foreach (var entry in quarantine)
        {
            QuarantineEntries.Add(entry);
        }
    }

    private void RefreshVisibleApplications()
    {
        var filtered = Applications.Where(application =>
            searchText.Length == 0 || application.SearchText.Contains(searchText, StringComparison.CurrentCultureIgnoreCase));
        filtered = filterIndex switch
        {
            1 => filtered.Where(application => application.UninstallCommand.Length == 0),
            2 => filtered.Where(application => application.Publisher.Length == 0),
            _ => filtered
        };
        VisibleApplications.Clear();
        foreach (var application in filtered)
        {
            VisibleApplications.Add(application);
        }
    }

    private bool LoadSafetyAcceptance()
    {
        try
        {
            return File.Exists(settingsPath) && JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(settingsPath))?.SafetyAccepted == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static string FormatEstimate(long? kilobytes) => kilobytes is null ? "Size not reported" : $"Estimated size · {FormatBytes(kilobytes.Value * 1024)}";

    private static string FormatInstallDate(DateTimeOffset? date) => date?.ToLocalTime().ToString("d") ?? "Install date not reported";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "Size unavailable";
        }
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

    private static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "Not reported" : value;

    private sealed record UserSettings(bool SafetyAccepted);
}
