using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using CleanLens.Core.Models;
using CleanLens.Core.Localization;
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
    private string selectedLanguage = "en";

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private string pageTitle = string.Empty;

    [ObservableProperty]
    private string pageSubtitle = string.Empty;

    [ObservableProperty]
    private Visibility disclaimerVisibility = Visibility.Visible;

    public ObservableCollection<InstalledApplication> Applications { get; } = [];
    public ObservableCollection<LeftoverCandidate> Leftovers { get; } = [];
    public ObservableCollection<HistoryEntry> HistoryEntries { get; } = [];
    public ObservableCollection<QuarantineEntry> QuarantineEntries { get; } = [];
    public ObservableCollection<InstalledApplication> VisibleApplications { get; } = [];
    public bool HasReviewApplication => SelectedApplication is not null || pendingUninstallApplication is not null;
    public LocalizationCatalog Texts { get; }
    public IReadOnlyList<string> Languages => LocalizationCatalog.Languages;

    public string AppCountText => scanned ? Applications.Count.ToString(CultureInfo.GetCultureInfo(Texts.Language)) : Texts["NotScannedYet"];
    public string TotalSizeText => !scanned || Applications.All(application => application.EstimatedSizeKilobytes is null)
        ? Texts["SizeNotReported"]
        : FormatBytes(Applications.Sum(application => application.EstimatedSizeKilobytes.GetValueOrDefault() * 1024));
    public string LeftoversText => leftoversScanned ? Leftovers.Count.ToString(CultureInfo.GetCultureInfo(Texts.Language)) : Texts["NotScannedYet"];
    public string SelectedApplicationDisplayName => (SelectedApplication ?? pendingUninstallApplication)?.Name ?? Texts["SelectApplication"];
    public string SelectedPublisher => string.IsNullOrWhiteSpace((SelectedApplication ?? pendingUninstallApplication)?.Publisher) ? Texts["PublisherNotListed"] : (SelectedApplication ?? pendingUninstallApplication)!.Publisher;
    public string DetailVersion => (SelectedApplication ?? pendingUninstallApplication) is not { } application ? Texts["SelectDetailsHint"] : $"{Texts.Format("Version", Fallback(application.Version))} · {FormatInstallDate(application.InstalledAt)}";
    public string DetailLocation => (SelectedApplication ?? pendingUninstallApplication) is { } application ? Texts.Format("InstallLocation", Fallback(application.InstallLocation)) : string.Empty;
    public string DetailSource => (SelectedApplication ?? pendingUninstallApplication) is not { } application ? string.Empty : $"{Texts[application.Source.Contains("machine", StringComparison.OrdinalIgnoreCase) ? "MachineRegistry" : "UserRegistry"]} · {Texts[application.Source.Contains("Registry32", StringComparison.OrdinalIgnoreCase) ? "View32" : "View64"]} · {FormatEstimate(application.EstimatedSizeKilobytes)}";

    public MainViewModel(IApplicationInventory inventory, LeftoverScanner leftoverScanner, CleanLensDatabase database, QuarantineService quarantineService)
    {
        this.inventory = inventory;
        this.leftoverScanner = leftoverScanner;
        this.database = database;
        this.quarantineService = quarantineService;
        var settings = LoadUserSettings();
        var language = LocalizationCatalog.Languages.Contains(settings.Language ?? string.Empty, StringComparer.OrdinalIgnoreCase) ? settings.Language! : "en";
        Texts = new LocalizationCatalog { Language = language };
        SafetyAccepted = settings.SafetyAccepted;
        SelectedLanguage = language;
        StatusText = Texts["StatusInitial"];
        PageTitle = Texts["Overview"];
        PageSubtitle = Texts["OverviewSubtitle"];
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
        OnPropertyChanged(nameof(SelectedApplicationDisplayName));
        OnPropertyChanged(nameof(SelectedPublisher));
        OnPropertyChanged(nameof(AppCountText));
        OnPropertyChanged(nameof(TotalSizeText));
        OnPropertyChanged(nameof(LeftoversText));
    }

    partial void OnSafetyAcceptedChanged(bool value)
    {
        DisclaimerVisibility = value ? Visibility.Collapsed : Visibility.Visible;
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        Texts.Language = value;
        StatusText = Texts["StatusInitial"];
        SetPage(CurrentPageKey == "LeftoverReview" ? "Leftover review" : CurrentPageKey);
        OnPropertyChanged(nameof(DetailVersion));
        OnPropertyChanged(nameof(DetailLocation));
        OnPropertyChanged(nameof(DetailSource));
        OnPropertyChanged(nameof(SelectedApplicationDisplayName));
        OnPropertyChanged(nameof(SelectedPublisher));
        SaveUserSettings();
    }

    private string CurrentPageKey { get; set; } = "Overview";

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
        CurrentPageKey = page switch
        {
            "Leftover review" => "LeftoverReview",
            "Applications" => "Applications",
            "History" => "History",
            "Quarantine" => "Quarantine",
            _ => "Overview"
        };
        PageTitle = Texts[CurrentPageKey];
        PageSubtitle = page switch
        {
            "Applications" => Texts["ApplicationsSubtitle"],
            "Leftover review" => uninstallRemovalVerified && pendingUninstallApplication is not null
                ? Texts.Format("LeftoverConfirmedSubtitle", pendingUninstallApplication.Name)
                : Texts["LeftoverSubtitle"],
            "History" => Texts["HistoryPage"],
            "Quarantine" => Texts["QuarantinePage"],
            _ => Texts["OverviewSubtitle"]
        };
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        try
        {
            StatusText = Texts["StatusScanningApps"];
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
                    ? Texts.Format("StatusUninstallRemoved", pendingUninstallApplication?.Name ?? Texts["SelectApplication"])
                    : Texts.Format("StatusUninstallStillListed", results.Count);
            }
            else
            {
                StatusText = Texts.Format("StatusScanComplete", results.Count);
            }
            await database.RecordOperationAsync(Texts["HistoryAllApps"], Texts["HistoryInventoryScan"], Texts.Format("StatusReadRegistered", results.Count));
        }
        catch (Exception ex)
        {
            StatusText = Texts.Format("StatusScanFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ScanLeftoversAsync()
    {
        var application = SelectedApplication ?? pendingUninstallApplication;
        if (application is null)
        {
            StatusText = Texts["StatusSelectApp"];
            return;
        }
        if (application.Id != pendingUninstallApplicationId || !uninstallRemovalVerified)
        {
            StatusText = Texts["StatusScanBeforeReview"];
            return;
        }

        try
        {
            StatusText = Texts["StatusCheckingData"];
            var results = await leftoverScanner.ScanAsync(application);
            Leftovers.Clear();
            foreach (var candidate in results)
            {
                Leftovers.Add(candidate);
            }
            leftoversScanned = true;
            OnPropertyChanged(nameof(LeftoversText));
            StatusText = results.Count == 0
                ? Texts["StatusNoCandidates"]
                : Texts.Format("StatusCandidates", results.Count);
        }
        catch (Exception ex)
        {
            StatusText = Texts.Format("StatusLeftoverScanFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void AcceptSafety()
    {
        if (!SafetyAccepted)
        {
            return;
        }
        SaveUserSettings();
    }

    public async Task<string> QuarantineSelectedAsync()
    {
        if (!SafetyAccepted)
        {
            throw new InvalidOperationException(Texts["StatusSafetyRequired"]);
        }
        var application = SelectedApplication ?? pendingUninstallApplication;
        if (SelectedLeftover is null || application is null || application.Id != pendingUninstallApplicationId || !uninstallRemovalVerified)
        {
            throw new InvalidOperationException(Texts["StatusSelectReviewedFolder"]);
        }
        if (SelectedLeftover.Confidence == ConfidenceLevel.Low || SelectedLeftover.IsUserData)
        {
            throw new InvalidOperationException(Texts["StatusLowConfidence"]);
        }
        var operationId = await quarantineService.MoveAsync(
            SelectedLeftover.Path,
            application.Name,
            Texts["HistoryQuarantine"],
            Texts["HistoryMovedQuarantine"]);
        Leftovers.Remove(SelectedLeftover);
        SelectedLeftover = null;
        await RefreshLocalRecordsAsync();
        OnPropertyChanged(nameof(LeftoversText));
        StatusText = Texts["StatusQuarantined"];
        return operationId;
    }

    public async Task RestoreSelectedAsync()
    {
        if (!SafetyAccepted || SelectedQuarantine is null)
        {
            throw new InvalidOperationException(Texts["StatusRestoreRequired"]);
        }
        await quarantineService.RestoreAsync(
            SelectedQuarantine,
            Texts["HistoryRestore"],
            Texts["HistoryRestored"]);
        await RefreshLocalRecordsAsync();
        StatusText = Texts["StatusRestored"];
    }

    public async Task RecordUninstallAsync(InstalledApplication application, int? processId)
    {
        pendingUninstallApplication = application;
        pendingUninstallApplicationId = application.Id;
        uninstallRemovalVerified = false;
        var result = processId is null ? Texts["HistoryLaunchedByWindows"] : Texts.Format("HistoryProcessStarted", processId.Value);
        await database.RecordOperationAsync(application.Name, Texts["HistoryOfficialUninstall"], result);
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
            StatusText = Texts.Format("StatusHistoryUnavailable", ex.Message);
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

    private UserSettings LoadUserSettings()
    {
        try
        {
            return File.Exists(settingsPath) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(settingsPath)) ?? new UserSettings(false, "en") : new UserSettings(false, "en");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UserSettings(false, "en");
        }
    }

    private void SaveUserSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new UserSettings(SafetyAccepted, SelectedLanguage)));
    }

    private string FormatEstimate(long? kilobytes) => kilobytes is null ? Texts["SizeNotReported"] : Texts.Format("EstimatedSize", FormatBytes(kilobytes.Value * 1024));

    private string FormatInstallDate(DateTimeOffset? date) => date?.ToLocalTime().ToString("d", CultureInfo.GetCultureInfo(Texts.Language)) ?? Texts["InstallDateUnknown"];

    private string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return Texts["SizeUnavailable"];
        }
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString("0.#", CultureInfo.GetCultureInfo(Texts.Language))} {units[unit]}";
    }

    private string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? Texts["NotReported"] : value;

    private sealed record UserSettings(bool SafetyAccepted, string Language);
}
