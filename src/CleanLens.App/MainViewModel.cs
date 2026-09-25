using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.ComponentModel;
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
    private const int CurrentSafetyNoticeVersion = 2;
    private readonly IApplicationInventory inventory;
    private readonly LeftoverScanner leftoverScanner;
    private readonly CleanLensDatabase database;
    private readonly QuarantineService quarantineService;
    private readonly InstallMonitorService installMonitorService;
    private readonly string settingsPath;
    private readonly string localDataPath;
    private bool scanned;
    private bool leftoversScanned;
    private InstalledApplication? pendingUninstallApplication;
    private string? pendingUninstallApplicationId;
    private bool uninstallRemovalVerified;
    private string searchText = string.Empty;
    private int searchScope;
    private int filterIndex;
    private string? applicationSortField;
    private ListSortDirection applicationSortDirection = ListSortDirection.Ascending;

    [ObservableProperty]
    private InstalledApplication? selectedApplication;

    [ObservableProperty]
    private LeftoverCandidate? selectedLeftover;

    [ObservableProperty]
    private QuarantineEntry? selectedQuarantine;

    [ObservableProperty]
    private bool safetyAccepted;

    [ObservableProperty]
    private bool safetyUninstallerAcknowledged;

    [ObservableProperty]
    private bool safetyDamageAcknowledged;

    [ObservableProperty]
    private bool safetyManualDeleteAcknowledged;

    [ObservableProperty]
    private bool safetyRestoreAcknowledged;

    [ObservableProperty]
    private bool safetyNoticeAcknowledged;

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
    public ObservableCollection<ManualSearchRootSetting> ManualSearchRoots { get; } = [];
    public ObservableCollection<string> InstallMonitorResults { get; } = [];
    public ObservableCollection<InstalledApplication> VisibleApplications { get; } = [];
    public bool HasReviewApplication => SelectedApplication is not null || pendingUninstallApplication is not null;
    public bool CanUninstallSelected => SafetyAccepted && SelectedApplication is not null &&
        (SelectedApplication.IsAppxPackage ? !SelectedApplication.IsNonRemovablePackage : !string.IsNullOrWhiteSpace(SelectedApplication.UninstallCommand)) &&
        !SelectedApplication.Id.Equals(pendingUninstallApplicationId, StringComparison.Ordinal);
    public bool CanReviewLeftovers => uninstallRemovalVerified && pendingUninstallApplicationId is not null &&
        (SelectedApplication is null || SelectedApplication.Id == pendingUninstallApplicationId);
    public bool CanQuarantineSelected => SafetyAccepted && CanReviewLeftovers && SelectedLeftover is not null &&
        SelectedLeftover.Confidence != ConfidenceLevel.Low && !SelectedLeftover.IsUserData;
    public bool CanRestoreSelected => SafetyAccepted && SelectedQuarantine is not null;
    public bool CanAcceptSafety => SafetyUninstallerAcknowledged && SafetyDamageAcknowledged && SafetyManualDeleteAcknowledged && SafetyRestoreAcknowledged;
    public LocalizationCatalog Texts { get; }
    public IReadOnlyList<string> Languages => LocalizationCatalog.Languages;
    public Visibility InventoryEmptyStateVisibility => VisibleApplications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InventoryEmptyScanVisibility => !scanned || Applications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LeftoversEmptyStateVisibility => Leftovers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HistoryEmptyStateVisibility => HistoryEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility QuarantineEmptyStateVisibility => QuarantineEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string InventoryEmptyTitle => !scanned
        ? Texts["EmptyNotScannedTitle"]
        : Applications.Count == 0 ? Texts["EmptyAppsTitle"] : Texts["EmptyFilteredTitle"];
    public string InventoryEmptyCopy => !scanned
        ? Texts["EmptyNotScannedCopy"]
        : Applications.Count == 0 ? Texts["EmptyAppsCopy"] : Texts["EmptyFilteredCopy"];
    public string LeftoversEmptyTitle => leftoversScanned ? Texts["NoResidualMatchesTitle"] : Texts["EmptyLeftoversTitle"];
    public string LeftoversEmptyCopy => leftoversScanned ? Texts["NoResidualMatchesCopy"] : Texts["EmptyLeftoversCopy"];

    public string AppCountText => scanned ? Applications.Count.ToString(CultureInfo.GetCultureInfo(Texts.Language)) : Texts["NotScannedYet"];
    public string TotalSizeText => !scanned || Applications.All(application => application.EstimatedSizeKilobytes is null)
        ? Texts["SizeNotReported"]
        : FormatBytes(Applications.Sum(application => application.EstimatedSizeKilobytes.GetValueOrDefault() * 1024));
    public string LeftoversText => leftoversScanned ? Leftovers.Count.ToString(CultureInfo.GetCultureInfo(Texts.Language)) : Texts["NotScannedYet"];
    public string SelectedApplicationDisplayName => (SelectedApplication ?? pendingUninstallApplication)?.Name ?? Texts["SelectApplication"];
    public string SelectedPublisher => string.IsNullOrWhiteSpace((SelectedApplication ?? pendingUninstallApplication)?.Publisher) ? Texts["PublisherNotListed"] : (SelectedApplication ?? pendingUninstallApplication)!.Publisher;
    public string DetailVersion => (SelectedApplication ?? pendingUninstallApplication) is not { } application ? Texts["SelectDetailsHint"] : $"{Texts.Format("Version", Fallback(application.Version))} · {FormatInstallDate(application.InstalledAt)}";
    public string DetailLocation => (SelectedApplication ?? pendingUninstallApplication) is { } application ? Texts.Format("InstallLocation", Fallback(application.InstallLocation)) : string.Empty;
    public string DetailSource => (SelectedApplication ?? pendingUninstallApplication) is not { } application ? string.Empty : application.IsAppxPackage ? $"{Texts["AppxPackage"]} · {Texts["CurrentUserPackage"]}{(application.IsNonRemovablePackage ? $" · {Texts["AppxNonRemovable"]}" : string.Empty)}" : $"{Texts[application.Source.Contains("machine", StringComparison.OrdinalIgnoreCase) ? "MachineRegistry" : "UserRegistry"]} · {Texts[application.Source.Contains("Registry32", StringComparison.OrdinalIgnoreCase) ? "View32" : "View64"]} · {FormatEstimate(application.EstimatedSizeKilobytes)}";
    public string LocalDataPath => localDataPath;
    public string RemoveSearchRootText => Texts["RemoveSearchRoot"];
    public string UninstallButtonText => SelectedApplication?.IsAppxPackage == true ? Texts["RemoveStoreApp"] : Texts["RunUninstaller"];
    public bool InstallMonitorRunning => installMonitorService.IsRunning;
    public bool CanStartInstallMonitor => !InstallMonitorRunning;
    public bool CanStopInstallMonitor => InstallMonitorRunning;
    private const string RegisteredLocationsRootKey = "@builtin:registered-locations";
    private const string SteamLocationsRootKey = "@builtin:steam-locations";

    public void AddManualSearchRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException(fullPath);
        var volumeRoot = Path.GetPathRoot(fullPath);
        if (fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(volumeRoot?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Texts["CustomSearchRootUnsafe"]);
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidOperationException(Texts["CustomSearchRootNetwork"]);
        if (ManualSearchRoots.Any(item => !string.IsNullOrWhiteSpace(item.Path) && item.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase))) return;
        var setting = new ManualSearchRootSetting(fullPath, fullPath, true);
        setting.PropertyChanged += (_, _) => SaveUserSettings();
        ManualSearchRoots.Add(setting);
        SaveUserSettings();
    }

    public void RemoveManualSearchRoot(ManualSearchRootSetting setting)
    {
        if (!setting.IsBuiltIn && ManualSearchRoots.Remove(setting)) SaveUserSettings();
    }

    public string[] GetEnabledManualSearchRoots() => ManualSearchRoots.Where(root => !root.IsBuiltIn && root.IsEnabled).Select(root => root.Path).ToArray();
    public string[] GetEnabledDefaultManualSearchRoots() => ManualSearchRoots.Where(root => root.IsBuiltIn && !string.IsNullOrWhiteSpace(root.Path) && root.IsEnabled).Select(root => root.Path).ToArray();
    public bool IncludeRegisteredInstallLocations => ManualSearchRoots.FirstOrDefault(root => root.Key == RegisteredLocationsRootKey)?.IsEnabled ?? true;
    public bool IncludeSteamLocations => ManualSearchRoots.FirstOrDefault(root => root.Key == SteamLocationsRootKey)?.IsEnabled ?? true;

    private void AddSearchRootSetting(ManualSearchRootSetting setting)
    {
        setting.PropertyChanged += (_, _) => SaveUserSettings();
        ManualSearchRoots.Add(setting);
    }

    public async Task StartInstallMonitorAsync()
    {
        await installMonitorService.StartAsync(GetEnabledManualSearchRoots());
        InstallMonitorResults.Clear();
        InstallMonitorResults.Add(Texts["MonitorStartedMessage"]);
        NotifyMonitorStateChanged();
    }

    public async Task StopInstallMonitorAsync()
    {
        var report = await installMonitorService.StopAsync();
        InstallMonitorResults.Clear();
        InstallMonitorResults.Add(Texts.Format("MonitorReportSummary", report.AddedApplications.Count, report.RemovedApplications.Count, report.SystemEntryChanges.Count, report.FileEvents.Count));
        foreach (var item in report.AddedApplications) InstallMonitorResults.Add($"{Texts["MonitorAppAdded"]}: {item}");
        foreach (var item in report.RemovedApplications) InstallMonitorResults.Add($"{Texts["MonitorAppRemoved"]}: {item}");
        foreach (var item in report.SystemEntryChanges.Take(500)) InstallMonitorResults.Add($"{Texts["MonitorSystemEntryChanges"]}: {item}");
        if (report.SystemEntryChanges.Count > 500) InstallMonitorResults.Add(Texts.Format("MonitorTruncated", report.SystemEntryChanges.Count - 500));
        foreach (var item in report.FileEvents.Take(500)) InstallMonitorResults.Add(item);
        if (report.FileEvents.Count > 500) InstallMonitorResults.Add(Texts.Format("MonitorTruncated", report.FileEvents.Count - 500));
        if (report.IsIncomplete) InstallMonitorResults.Add(Texts["MonitorIncomplete"]);
        NotifyMonitorStateChanged();
    }

    public void DisposeInstallMonitor() => installMonitorService.Dispose();

    private void NotifyMonitorStateChanged()
    {
        OnPropertyChanged(nameof(InstallMonitorRunning));
        OnPropertyChanged(nameof(CanStartInstallMonitor));
        OnPropertyChanged(nameof(CanStopInstallMonitor));
    }

    public MainViewModel(IApplicationInventory inventory, LeftoverScanner leftoverScanner, CleanLensDatabase database, QuarantineService quarantineService, string localDataPath)
    {
        this.inventory = inventory;
        this.leftoverScanner = leftoverScanner;
        this.database = database;
        this.quarantineService = quarantineService;
        installMonitorService = new InstallMonitorService(inventory, Path.GetFullPath(localDataPath));
        this.localDataPath = Path.GetFullPath(localDataPath);
        settingsPath = Path.Combine(this.localDataPath, "settings.json");
        var settings = LoadUserSettings();
        var language = LocalizationCatalog.Languages.Contains(settings.Language ?? string.Empty, StringComparer.OrdinalIgnoreCase) ? settings.Language! : "en";
        Texts = new LocalizationCatalog { Language = language };
        SafetyAccepted = settings.SafetyAccepted && settings.SafetyNoticeVersion >= CurrentSafetyNoticeVersion;
        SafetyNoticeAcknowledged = SafetyAccepted;
        SelectedLanguage = language;
        var savedRootStates = settings.ManualSearchRoots ?? [];
        var defaultRoots = ManualDeleteService.GetDefaultSearchRoots();
        foreach (var path in defaultRoots)
        {
            var saved = savedRootStates.LastOrDefault(root => root.Key.Equals(path, StringComparison.OrdinalIgnoreCase) || root.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            AddSearchRootSetting(new ManualSearchRootSetting(path, path, saved?.IsEnabled ?? true, isBuiltIn: true));
        }
        AddSearchRootSetting(new ManualSearchRootSetting(RegisteredLocationsRootKey, string.Empty,
            savedRootStates.LastOrDefault(root => root.Key == RegisteredLocationsRootKey)?.IsEnabled ?? true,
            isBuiltIn: true, displayPath: Texts["DefaultRootRegisteredLocations"]));
        AddSearchRootSetting(new ManualSearchRootSetting(SteamLocationsRootKey, string.Empty,
            savedRootStates.LastOrDefault(root => root.Key == SteamLocationsRootKey)?.IsEnabled ?? true,
            isBuiltIn: true, displayPath: Texts["DefaultRootSteamLocations"]));
        foreach (var root in settings.ManualSearchRoots ?? [])
        {
            if (root.IsBuiltIn || string.IsNullOrWhiteSpace(root.Path) || defaultRoots.Contains(root.Path, StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                var fullPath = Path.GetFullPath(root.Path);
                if (!ManualSearchRoots.Any(item => item.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
                {
                    AddSearchRootSetting(new ManualSearchRootSetting(fullPath, fullPath, root.IsEnabled));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
            }
        }
        StatusText = Texts["StatusInitial"];
        PageTitle = Texts["Applications"];
        PageSubtitle = Texts["ApplicationsSubtitle"];
        DisclaimerVisibility = SafetyNoticeAcknowledged ? Visibility.Collapsed : Visibility.Visible;
        _ = LoadLocalRecordsAsync();
    }

    partial void OnSelectedApplicationChanged(InstalledApplication? value)
    {
        Leftovers.Clear();
        leftoversScanned = false;
        OnPropertyChanged(nameof(LeftoversText));
        OnPropertyChanged(nameof(LeftoversEmptyStateVisibility));
        OnPropertyChanged(nameof(LeftoversEmptyTitle));
        OnPropertyChanged(nameof(LeftoversEmptyCopy));
        OnPropertyChanged(nameof(DetailVersion));
        OnPropertyChanged(nameof(DetailLocation));
        OnPropertyChanged(nameof(DetailSource));
        OnPropertyChanged(nameof(SelectedApplicationDisplayName));
        OnPropertyChanged(nameof(SelectedPublisher));
        OnPropertyChanged(nameof(CanUninstallSelected));
        OnPropertyChanged(nameof(UninstallButtonText));
        OnPropertyChanged(nameof(CanReviewLeftovers));
        OnPropertyChanged(nameof(CanQuarantineSelected));
        ScanLeftoversCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedLeftoverChanged(LeftoverCandidate? value)
    {
        OnPropertyChanged(nameof(CanQuarantineSelected));
    }

    partial void OnSelectedQuarantineChanged(QuarantineEntry? value)
    {
        OnPropertyChanged(nameof(CanRestoreSelected));
    }

    partial void OnSafetyAcceptedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUninstallSelected));
        OnPropertyChanged(nameof(CanQuarantineSelected));
        OnPropertyChanged(nameof(CanRestoreSelected));
        if (!value)
        {
            SafetyNoticeAcknowledged = false;
        }
    }

    partial void OnSafetyNoticeAcknowledgedChanged(bool value)
    {
        DisclaimerVisibility = value ? Visibility.Collapsed : Visibility.Visible;
    }

    partial void OnSafetyUninstallerAcknowledgedChanged(bool value) => OnPropertyChanged(nameof(CanAcceptSafety));

    partial void OnSafetyDamageAcknowledgedChanged(bool value) => OnPropertyChanged(nameof(CanAcceptSafety));

    partial void OnSafetyManualDeleteAcknowledgedChanged(bool value) => OnPropertyChanged(nameof(CanAcceptSafety));

    partial void OnSafetyRestoreAcknowledgedChanged(bool value) => OnPropertyChanged(nameof(CanAcceptSafety));

    partial void OnSelectedLanguageChanged(string value)
    {
        Texts.Language = value;
        OnPropertyChanged(nameof(RemoveSearchRootText));
        OnPropertyChanged(nameof(UninstallButtonText));
        ManualSearchRoots.FirstOrDefault(root => root.Key == RegisteredLocationsRootKey)?.UpdateDisplayPath(Texts["DefaultRootRegisteredLocations"]);
        ManualSearchRoots.FirstOrDefault(root => root.Key == SteamLocationsRootKey)?.UpdateDisplayPath(Texts["DefaultRootSteamLocations"]);
        StatusText = Texts["StatusInitial"];
        SetPage(CurrentPageKey == "LeftoverReview" ? "Leftover review" : CurrentPageKey);
        OnPropertyChanged(nameof(DetailVersion));
        OnPropertyChanged(nameof(DetailLocation));
        OnPropertyChanged(nameof(DetailSource));
        OnPropertyChanged(nameof(SelectedApplicationDisplayName));
        OnPropertyChanged(nameof(SelectedPublisher));
        OnPropertyChanged(nameof(InventoryEmptyTitle));
        OnPropertyChanged(nameof(InventoryEmptyCopy));
        OnPropertyChanged(nameof(InventoryEmptyScanVisibility));
        OnPropertyChanged(nameof(LeftoversEmptyTitle));
        OnPropertyChanged(nameof(LeftoversEmptyCopy));
        SaveUserSettings();
    }

    private string CurrentPageKey { get; set; } = "Applications";

    public void UpdateSearch(string value)
    {
        searchText = value.Trim();
        RefreshVisibleApplications();
    }

    public void UpdateSearchScope(int value)
    {
        searchScope = value;
        RefreshVisibleApplications();
    }

    public void UpdateFilter(int value)
    {
        filterIndex = value;
        RefreshVisibleApplications();
    }

    public void SortApplications(string field, ListSortDirection direction)
    {
        applicationSortField = field;
        applicationSortDirection = direction;
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
            "Settings" => "Settings",
            _ => "Applications"
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
            "Settings" => Texts["SettingsSubtitle"],
            _ => Texts["ApplicationsSubtitle"]
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
                if (!uninstallRemovalVerified)
                {
                    pendingUninstallApplication = null;
                    pendingUninstallApplicationId = null;
                }
            }
            else
            {
                StatusText = inventory.LastScanWarning is { Length: > 0 } warning
                    ? Texts.Format("StatusAppxScanWarning", warning)
                    : Texts.Format("StatusScanComplete", results.Count);
            }
            await database.RecordOperationAsync(Texts["HistoryAllApps"], Texts["HistoryInventoryScan"], Texts.Format("StatusReadRegistered", results.Count));
            OnPropertyChanged(nameof(CanUninstallSelected));
            OnPropertyChanged(nameof(CanReviewLeftovers));
            OnPropertyChanged(nameof(CanQuarantineSelected));
            ScanLeftoversCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusText = Texts.Format("StatusScanFailed", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanReviewLeftovers))]
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
            OnPropertyChanged(nameof(LeftoversEmptyStateVisibility));
            OnPropertyChanged(nameof(LeftoversEmptyTitle));
            OnPropertyChanged(nameof(LeftoversEmptyCopy));
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
        if (!CanAcceptSafety)
        {
            return;
        }
        SafetyAccepted = true;
        SaveUserSettings();
        SafetyNoticeAcknowledged = true;
    }

    [RelayCommand]
    private void ReviewSafetyNotice()
    {
        SafetyAccepted = false;
        SafetyUninstallerAcknowledged = false;
        SafetyDamageAcknowledged = false;
        SafetyManualDeleteAcknowledged = false;
        SafetyRestoreAcknowledged = false;
        SafetyNoticeAcknowledged = false;
        SaveUserSettings();
        StatusText = Texts["SafetyRequired"];
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
        OnPropertyChanged(nameof(LeftoversEmptyStateVisibility));
        StatusText = Texts["StatusQuarantined"];
        return operationId;
    }

    public async Task QuarantineManualDeleteCandidateAsync(InstalledApplication application, string candidatePath)
    {
        if (!SafetyAccepted)
        {
            throw new InvalidOperationException(Texts["StatusSafetyRequired"]);
        }
        await new ManualDeleteService().MoveCandidateToQuarantineAsync(
            application,
            candidatePath,
            quarantineService,
            Texts["HistoryQuarantine"],
            Texts["HistoryMovedQuarantine"],
            additionalRoots: GetEnabledManualSearchRoots(),
            enabledDefaultRoots: GetEnabledDefaultManualSearchRoots(),
            includeRegisteredLocations: IncludeRegisteredInstallLocations,
            includeSteamLocations: IncludeSteamLocations);
        await RefreshLocalRecordsAsync();
        StatusText = Texts["StatusQuarantined"];
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
        OnPropertyChanged(nameof(CanUninstallSelected));
        OnPropertyChanged(nameof(CanReviewLeftovers));
        OnPropertyChanged(nameof(CanQuarantineSelected));
        ScanLeftoversCommand.NotifyCanExecuteChanged();
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
        OnPropertyChanged(nameof(HistoryEmptyStateVisibility));
        OnPropertyChanged(nameof(QuarantineEmptyStateVisibility));
    }

    private void RefreshVisibleApplications()
    {
        var filtered = Applications.Where(application =>
        {
            var searchableText = searchScope switch
            {
                1 => application.Name,
                2 => application.Publisher,
                3 => application.Version,
                4 => application.InstallLocation,
                _ => application.SearchText
            };
            return searchText.Length == 0 || searchableText.Contains(searchText, StringComparison.CurrentCultureIgnoreCase);
        });
        filtered = filterIndex switch
        {
            1 => filtered.Where(application => application.UninstallCommand.Length > 0),
            2 => filtered.Where(application => application.UninstallCommand.Length == 0),
            3 => filtered.Where(application => application.Publisher.Length > 0),
            4 => filtered.Where(application => application.Publisher.Length == 0),
            5 => filtered.Where(application => application.EstimatedSizeKilobytes is not null),
            6 => filtered.Where(application => application.EstimatedSizeKilobytes is null),
            _ => filtered
        };
        filtered = ApplyApplicationSort(filtered);
        var orderedApplications = filtered.ToArray();
        var selectedApplication = SelectedApplication;
        VisibleApplications.Clear();
        foreach (var application in orderedApplications)
        {
            VisibleApplications.Add(application);
        }
        if (selectedApplication is not null && orderedApplications.Contains(selectedApplication)) SelectedApplication = selectedApplication;
        OnPropertyChanged(nameof(InventoryEmptyStateVisibility));
        OnPropertyChanged(nameof(InventoryEmptyTitle));
        OnPropertyChanged(nameof(InventoryEmptyCopy));
        OnPropertyChanged(nameof(InventoryEmptyScanVisibility));
    }

    private IEnumerable<InstalledApplication> ApplyApplicationSort(IEnumerable<InstalledApplication> applications)
    {
        var descending = applicationSortDirection == ListSortDirection.Descending;
        return applicationSortField switch
        {
            nameof(InstalledApplication.Name) => descending
                ? applications.OrderByDescending(application => application.Name, StringComparer.CurrentCultureIgnoreCase)
                : applications.OrderBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase),
            nameof(InstalledApplication.Publisher) => descending
                ? applications.OrderByDescending(application => application.Publisher, StringComparer.CurrentCultureIgnoreCase)
                : applications.OrderBy(application => application.Publisher, StringComparer.CurrentCultureIgnoreCase),
            nameof(InstalledApplication.Version) => descending
                ? applications.OrderByDescending(application => application.Version, VersionStringComparer.Instance)
                : applications.OrderBy(application => application.Version, VersionStringComparer.Instance),
            nameof(InstalledApplication.EstimatedSizeKilobytes) => descending
                ? applications.OrderBy(application => application.EstimatedSizeKilobytes is null)
                    .ThenByDescending(application => application.EstimatedSizeKilobytes).ThenBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase)
                : applications.OrderBy(application => application.EstimatedSizeKilobytes is null)
                    .ThenBy(application => application.EstimatedSizeKilobytes).ThenBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => applications.OrderBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase)
        };
    }

    private UserSettings LoadUserSettings()
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return new UserSettings(false, "en", 0);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new UserSettings(false, "en", 0);
            }

            var safetyAccepted = false;
            var language = "en";
            var safetyNoticeVersion = 0;
            var manualRoots = new List<ManualSearchRootState>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals(nameof(UserSettings.SafetyAccepted), StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    safetyAccepted = property.Value.GetBoolean();
                }
                else if (property.Name.Equals(nameof(UserSettings.Language), StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    language = property.Value.GetString() ?? "en";
                }
                else if (property.Name.Equals(nameof(UserSettings.SafetyNoticeVersion), StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var version))
                {
                    safetyNoticeVersion = version;
                }
                else if (property.Name.Equals(nameof(UserSettings.ManualSearchRoots), StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        var path = item.TryGetProperty("Path", out var pathValue) && pathValue.ValueKind == JsonValueKind.String ? pathValue.GetString() : null;
                        var key = item.TryGetProperty("Key", out var keyValue) && keyValue.ValueKind == JsonValueKind.String ? keyValue.GetString() : path;
                        var enabled = !item.TryGetProperty("IsEnabled", out var enabledValue) || enabledValue.ValueKind != JsonValueKind.False;
                        var isBuiltIn = item.TryGetProperty("IsBuiltIn", out var builtInValue) && builtInValue.ValueKind == JsonValueKind.True;
                        if (!string.IsNullOrWhiteSpace(key)) manualRoots.Add(new ManualSearchRootState(key, path ?? string.Empty, enabled, isBuiltIn));
                    }
                }
            }

            return new UserSettings(safetyAccepted, language, safetyNoticeVersion, manualRoots);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UserSettings(false, "en", 0);
        }
    }

    private void SaveUserSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new UserSettings(SafetyAccepted, SelectedLanguage, CurrentSafetyNoticeVersion, ManualSearchRoots.Select(root => new ManualSearchRootState(root.Key, root.Path, root.IsEnabled, root.IsBuiltIn)).ToList())));
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

    private sealed record ManualSearchRootState(string Key, string Path, bool IsEnabled, bool IsBuiltIn);
    private sealed record UserSettings(bool SafetyAccepted, string Language, int SafetyNoticeVersion, List<ManualSearchRootState>? ManualSearchRoots = null);

    private sealed class VersionStringComparer : IComparer<string>
    {
        public static VersionStringComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return 1;
            if (right is null) return -1;
            var leftIsVersion = Version.TryParse(left, out var leftVersion);
            var rightIsVersion = Version.TryParse(right, out var rightVersion);
            if (leftIsVersion && rightIsVersion) return leftVersion!.CompareTo(rightVersion);
            if (leftIsVersion != rightIsVersion) return leftIsVersion ? -1 : 1;
            return StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
        }
    }
}
