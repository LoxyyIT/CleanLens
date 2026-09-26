using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using CleanLens.Core.Models;
using CleanLens.Core.Localization;
using CleanLens.Core.Safety;
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
    private readonly DiskScanService diskScanService;
    private readonly CleanupPlanStore cleanupPlanStore;
    private readonly string settingsPath;
    private readonly string localDataPath;
    private readonly Stack<string> diskNavigationHistory = new();
    private bool scanned;
    private bool leftoversScanned;
    private InstalledApplication? pendingUninstallApplication;
    private string? pendingUninstallApplicationId;
    private bool uninstallRemovalVerified;
    private string searchText = string.Empty;
    private int searchScope;
    private int filterIndex;
    private int diskSelectedItemCount;
    private int duplicateSelectionCount;
    private string? applicationSortField;
    private ListSortDirection applicationSortDirection = ListSortDirection.Ascending;
    private CancellationTokenSource? diskQueryCancellation;
    private double diskTreemapWidth;
    private double diskTreemapHeight;
    private const int DiskPageSize = 1000;
    private const int SnapshotChangePageSize = 500;

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

    [ObservableProperty]
    private DiskScanRootOption? selectedDiskScanRoot;

    [ObservableProperty]
    private DiskScanSummary? diskScanSummary;

    [ObservableProperty]
    private string currentDiskDirectory = string.Empty;

    [ObservableProperty]
    private string diskStatusText = string.Empty;

    [ObservableProperty]
    private string diskSearchText = string.Empty;

    [ObservableProperty]
    private bool isDiskScanning;

    [ObservableProperty]
    private bool isDiskScanStale;

    [ObservableProperty]
    private DiskListingMode diskListingMode = DiskListingMode.CurrentFolder;

    [ObservableProperty]
    private long diskPageOffset;

    [ObservableProperty]
    private long diskEntryTotalCount;
    private long snapshotChangeTotalCount;
    private long snapshotChangeOffset;

    [ObservableProperty]
    private DiskScanEntry? selectedDiskEntry;

    [ObservableProperty]
    private int diskSidePanelMode;

    [ObservableProperty]
    private DuplicateFile? selectedDuplicate;

    [ObservableProperty]
    private DiskSnapshot? selectedSnapshotBefore;

    [ObservableProperty]
    private DiskSnapshot? selectedSnapshotAfter;

    [ObservableProperty]
    private CleanupPlan? selectedCleanupPlan;

    [ObservableProperty]
    private InstallMonitorReport? selectedInstallReport;

    [ObservableProperty]
    private string analysisStatusText = string.Empty;

    public ObservableCollection<InstalledApplication> Applications { get; } = [];
    public ObservableCollection<LeftoverCandidate> Leftovers { get; } = [];
    public ObservableCollection<HistoryEntry> HistoryEntries { get; } = [];
    public ObservableCollection<QuarantineEntry> QuarantineEntries { get; } = [];
    public ObservableCollection<ManualSearchRootSetting> ManualSearchRoots { get; } = [];
    public ObservableCollection<string> InstallMonitorResults { get; } = [];
    public ObservableCollection<InstalledApplication> SelectedApplications { get; } = [];
    public ObservableCollection<InstalledApplication> VisibleApplications { get; } = [];
    public ObservableCollection<DiskScanRootOption> DiskScanRoots { get; } = [];
    public ObservableCollection<DiskScanEntry> DiskEntries { get; } = [];
    public ObservableCollection<DiskExtensionStat> DiskExtensionStats { get; } = [];
    public ObservableCollection<DiskTreemapBlock> DiskTreemapItems { get; } = [];
    public ObservableCollection<DuplicateFile> DuplicateFiles { get; } = [];
    public ObservableCollection<DiskSnapshot> DiskSnapshots { get; } = [];
    public ObservableCollection<DiskSnapshotChange> SnapshotChanges { get; } = [];
    public ObservableCollection<CleanupPlan> CleanupPlans { get; } = [];
    public ObservableCollection<InstallMonitorReport> InstallReports { get; } = [];
    public ObservableCollection<string> SelectedInstallReportDetails { get; } = [];
    public int SystemLeftoversCount => Leftovers.Count(item => item.Category is CandidateCategory.Service or CandidateCategory.ScheduledTask or CandidateCategory.StartupEntry);
    public int FolderLeftoversCount => Leftovers.Count - SystemLeftoversCount;
    public string SelectedLeftoverExplanation => SelectedLeftover is null ? Texts["SelectDetailsHint"] :
        $"{SelectedLeftover.Path}\n\n{LocalizationCatalog.Translate(Texts.Language, SelectedLeftover.ReasonKey)}\n\n{(SelectedLeftover.Category is CandidateCategory.Service or CandidateCategory.ScheduledTask or CandidateCategory.StartupEntry ? "Read-only system artifact" : SelectedLeftover.IsUserData ? "Personal data · manual review only" : "Application data · quarantine available after confirmation")}";
    public string MonitorTargetText => SelectedApplication is null ? Texts["HistoryAllApps"] : SelectedApplication.Name;
    public bool HasVerifiedDuplicates => DuplicateFiles.Any(file => file.HashVerified);
    public bool CanDeleteDuplicates => SafetyAccepted && HasVerifiedDuplicates && duplicateSelectionCount > 0;

    public void UpdateDuplicateSelectionCount(int count)
    {
        duplicateSelectionCount = count;
        OnPropertyChanged(nameof(CanDeleteDuplicates));
    }
    public Visibility DuplicateEmptyVisibility => DuplicateFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SnapshotChangeEmptyVisibility => SnapshotChanges.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool CanPageSnapshotChangesBackward => snapshotChangeOffset > 0;
    public bool CanPageSnapshotChangesForward => snapshotChangeOffset + SnapshotChanges.Count < snapshotChangeTotalCount;
    public string SnapshotChangePageText => snapshotChangeTotalCount == 0 ? Texts["DiskNoItems"] :
        Texts.Format("DiskPageIndicator", (snapshotChangeOffset + 1).ToString("N0", CultureInfo.GetCultureInfo(Texts.Language)),
            Math.Min(snapshotChangeOffset + SnapshotChanges.Count, snapshotChangeTotalCount).ToString("N0", CultureInfo.GetCultureInfo(Texts.Language)),
            snapshotChangeTotalCount.ToString("N0", CultureInfo.GetCultureInfo(Texts.Language)));
    public Visibility PlanEmptyVisibility => CleanupPlans.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool HasSelectedCleanupPlan => SelectedCleanupPlan is not null;

    partial void OnSelectedCleanupPlanChanged(CleanupPlan? value) => OnPropertyChanged(nameof(HasSelectedCleanupPlan));
    public int SelectedApplicationCount => SelectedApplications.Count;
    public bool HasMultipleApplicationsSelected => SelectedApplicationCount > 1;
    public Visibility MultiSelectionVisibility => HasMultipleApplicationsSelected ? Visibility.Visible : Visibility.Collapsed;
    public string SelectedApplicationsSummary => HasMultipleApplicationsSelected
        ? string.Join(Environment.NewLine, SelectedApplications.Take(12).Select(application => application.Name)) + (SelectedApplications.Count > 12 ? Environment.NewLine + Texts.Format("MultipleAppsMore", SelectedApplications.Count - 12) : string.Empty)
        : string.Empty;
    public string ManualDeleteButtonText => HasMultipleApplicationsSelected ? Texts.Format("ManualDeleteMultiple", SelectedApplicationCount) : Texts["ManualDelete"];
    public bool HasReviewApplication => SelectedApplicationCount == 1 || (SelectedApplicationCount == 0 && pendingUninstallApplication is not null);
    public bool CanManualDeleteSelected => SafetyAccepted && SelectedApplicationCount > 0;
    public bool CanUninstallSelected => SafetyAccepted && SelectedApplicationCount == 1 && SelectedApplication is not null &&
        (SelectedApplication.IsAppxPackage ? !SelectedApplication.IsNonRemovablePackage : !string.IsNullOrWhiteSpace(SelectedApplication.UninstallCommand)) &&
        !SelectedApplication.Id.Equals(pendingUninstallApplicationId, StringComparison.Ordinal);
    public bool CanReviewLeftovers => SelectedApplicationCount <= 1 && uninstallRemovalVerified && pendingUninstallApplicationId is not null &&
        (SelectedApplication is null || SelectedApplication.Id == pendingUninstallApplicationId);
    public bool CanQuarantineSelected => SafetyAccepted && CanReviewLeftovers && SelectedLeftover is not null &&
        SelectedLeftover.Confidence != ConfidenceLevel.Low && !SelectedLeftover.IsUserData;
    public bool CanRestoreSelected => SafetyAccepted && SelectedQuarantine?.CanRestore == true;
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
    public string SelectedApplicationDisplayName => HasMultipleApplicationsSelected ? Texts.Format("MultipleAppsSelected", SelectedApplicationCount) : (SelectedApplication ?? pendingUninstallApplication)?.Name ?? Texts["SelectApplication"];
    public string SelectedPublisher => HasMultipleApplicationsSelected ? Texts["ManualDeleteOnly"] : string.IsNullOrWhiteSpace((SelectedApplication ?? pendingUninstallApplication)?.Publisher) ? Texts["PublisherNotListed"] : (SelectedApplication ?? pendingUninstallApplication)!.Publisher;
    public string DetailVersion => HasMultipleApplicationsSelected ? Texts["ManualDeleteOnlyNotice"] : (SelectedApplication ?? pendingUninstallApplication) is not { } application ? Texts["SelectDetailsHint"] : $"{Texts.Format("Version", Fallback(application.Version))} · {FormatInstallDate(application.InstalledAt)}";
    public string DetailLocation => HasMultipleApplicationsSelected ? Texts["MultipleSelectedPathsNotice"] : (SelectedApplication ?? pendingUninstallApplication) is { } application ? Texts.Format("InstallLocation", Fallback(application.InstallLocation)) : string.Empty;
    public string DetailSource => HasMultipleApplicationsSelected ? Texts["ManualDeleteOnly"] : (SelectedApplication ?? pendingUninstallApplication) is not { } application ? string.Empty : application.IsAppxPackage ? $"{Texts["AppxPackage"]} · {Texts["CurrentUserPackage"]}{(application.IsNonRemovablePackage ? $" · {Texts["AppxNonRemovable"]}" : string.Empty)}" : $"{Texts[application.Source.Contains("machine", StringComparison.OrdinalIgnoreCase) ? "MachineRegistry" : "UserRegistry"]} · {Texts[application.Source.Contains("Registry32", StringComparison.OrdinalIgnoreCase) ? "View32" : "View64"]} · {FormatEstimate(application.EstimatedSizeKilobytes)}";
    public string LocalDataPath => localDataPath;
    public string RemoveSearchRootText => Texts["RemoveSearchRoot"];
    public string UninstallButtonText => SelectedApplication?.IsAppxPackage == true ? Texts["RemoveStoreApp"] : Texts["RunUninstaller"];
    public bool InstallMonitorRunning => installMonitorService.IsRunning;
    public bool CanStartInstallMonitor => !InstallMonitorRunning;
    public bool CanStopInstallMonitor => InstallMonitorRunning;
    public bool CanScanDisk => SelectedDiskScanRoot is not null && !IsDiskScanning;
    public bool CanCancelDiskScan => IsDiskScanning;
    public bool CanGoBackDiskFolder => HasDiskScan && diskNavigationHistory.Count > 0 && !IsDiskScanStale && !IsDiskScanning;
    public bool CanGoUpDiskFolder => HasDiskScan && !IsDiskScanStale && !IsDiskScanning && !CurrentDiskDirectory.Equals(DiskScanSummary!.RootPath, StringComparison.OrdinalIgnoreCase);
    public bool HasDiskScan => DiskScanSummary is not null && SelectedDiskScanRoot is not null &&
        Path.GetFullPath(SelectedDiskScanRoot.Path).Equals(Path.GetFullPath(DiskScanSummary.RootPath), StringComparison.OrdinalIgnoreCase) && !IsDiskScanStale;
    public bool CanAnalyzeDisk => HasDiskScan && !IsDiskScanning;
    public Visibility DiskResultsVisibility => HasDiskScan ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DiskEmptyVisibility => HasDiskScan ? Visibility.Collapsed : Visibility.Visible;
    public bool CanDeleteDiskEntries => SafetyAccepted && HasDiskScan && !IsDiskScanning && !IsDiskScanStale && diskSelectedItemCount > 0;
    public string DiskSelectionCountText => Texts.Format("DiskSelectionCount", diskSelectedItemCount.ToString("N0", CultureInfo.GetCultureInfo(Texts.Language)));
    public bool CanOpenSelectedDiskFolder => SelectedDiskEntry is { IsDirectory: true, IsReparsePoint: false } && HasDiskScan && !IsDiskScanning && !IsDiskScanStale;
    public Visibility DiskExtensionPanelVisibility => DiskSidePanelMode == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DiskTreemapPanelVisibility => DiskSidePanelMode == 1 ? Visibility.Visible : Visibility.Collapsed;
    public bool CanPageDiskBackward => HasDiskScan && DiskPageOffset > 0 && !IsDiskScanning && !IsDiskScanStale;
    public bool CanPageDiskForward => HasDiskScan && DiskPageOffset + DiskEntries.Count < DiskEntryTotalCount && !IsDiskScanning && !IsDiskScanStale;
    public int DiskListingModeIndex => (int)DiskListingMode;
    public Visibility DiskScanProgressVisibility => IsDiskScanning ? Visibility.Visible : Visibility.Collapsed;
    public string DiskSummarySizeText => DiskScanSummary?.SizeText ?? Texts["DiskNotScanned"];
    public string DiskFileCountText => DiskScanSummary?.FileCount.ToString("N0", CultureInfo.GetCultureInfo(Texts.Language)) ?? Texts["DiskNotScanned"];
    public string DiskFolderCountText => DiskScanSummary?.FolderCount.ToString("N0", CultureInfo.GetCultureInfo(Texts.Language)) ?? Texts["DiskNotScanned"];
    public string DiskSkippedCountText => DiskScanSummary?.SkippedCount.ToString("N0", CultureInfo.GetCultureInfo(Texts.Language)) ?? Texts["DiskNotScanned"];
    public string DiskPageIndicatorText => DiskEntryTotalCount == 0
        ? Texts["DiskNoItems"]
        : Texts.Format("DiskPageIndicator", DiskPageOffset + 1, Math.Min(DiskPageOffset + DiskEntries.Count, DiskEntryTotalCount), DiskEntryTotalCount);
    public string DiskCurrentDirectoryText => string.IsNullOrWhiteSpace(CurrentDiskDirectory) ? Texts["DiskChooseRoot"] : CurrentDiskDirectory;
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

    public void UpdateSelectedApplications(IEnumerable<InstalledApplication> applications)
    {
        var selected = applications.DistinctBy(application => application.Id).ToArray();
        if (SelectedApplications.SequenceEqual(selected)) return;
        SelectedApplications.Clear();
        foreach (var application in selected) SelectedApplications.Add(application);
        if (selected.Length == 1 && SelectedApplication?.Id != selected[0].Id) SelectedApplication = selected[0];
        OnPropertyChanged(nameof(SelectedApplicationCount));
        OnPropertyChanged(nameof(HasMultipleApplicationsSelected));
        OnPropertyChanged(nameof(MultiSelectionVisibility));
        OnPropertyChanged(nameof(SelectedApplicationsSummary));
        OnPropertyChanged(nameof(SelectedApplicationDisplayName));
        OnPropertyChanged(nameof(SelectedPublisher));
        OnPropertyChanged(nameof(DetailVersion));
        OnPropertyChanged(nameof(DetailLocation));
        OnPropertyChanged(nameof(DetailSource));
        OnPropertyChanged(nameof(DetailVersion));
        OnPropertyChanged(nameof(DetailLocation));
        OnPropertyChanged(nameof(DetailSource));
        OnPropertyChanged(nameof(ManualDeleteButtonText));
        OnPropertyChanged(nameof(CanManualDeleteSelected));
        OnPropertyChanged(nameof(CanUninstallSelected));
        OnPropertyChanged(nameof(CanManualDeleteSelected));
        OnPropertyChanged(nameof(CanReviewLeftovers));
        OnPropertyChanged(nameof(CanQuarantineSelected));
        ScanLeftoversCommand.NotifyCanExecuteChanged();
    }

    public IReadOnlyList<InstalledApplication> GetSelectedApplications() => SelectedApplications.Count > 0
        ? SelectedApplications.ToArray()
        : SelectedApplication is null ? [] : [SelectedApplication];

    public async Task StartInstallMonitorAsync()
    {
        await installMonitorService.StartAsync(GetEnabledManualSearchRoots(), applicationId: SelectedApplication?.Id, applicationName: SelectedApplication?.Name);
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
        await RefreshInstallReportsAsync();
        NotifyMonitorStateChanged();
    }

    public async Task RefreshInstallReportsAsync()
    {
        var reports = await installMonitorService.GetReportsAsync();
        InstallReports.Clear();
        foreach (var report in reports) InstallReports.Add(report);
        SelectedInstallReport = InstallReports.FirstOrDefault();
    }

    partial void OnSelectedInstallReportChanged(InstallMonitorReport? value)
    {
        SelectedInstallReportDetails.Clear();
        if (value is null) return;
        SelectedInstallReportDetails.Add(value.DisplayName);
        SelectedInstallReportDetails.Add(Texts.Format("MonitorReportSummary", value.AddedApplications.Count, value.RemovedApplications.Count, value.SystemEntryChanges.Count, value.FileEvents.Count));
        foreach (var item in value.AddedApplications) SelectedInstallReportDetails.Add($"{Texts["MonitorAppAdded"]}: {item}");
        foreach (var item in value.RemovedApplications) SelectedInstallReportDetails.Add($"{Texts["MonitorAppRemoved"]}: {item}");
        foreach (var item in value.SystemEntryChanges) SelectedInstallReportDetails.Add($"{Texts["MonitorSystemEntryChanges"]}: {item}");
        foreach (var item in value.FileEvents) SelectedInstallReportDetails.Add(item);
        if (value.IsIncomplete) SelectedInstallReportDetails.Add(Texts["MonitorIncomplete"]);
    }

    public void DisposeInstallMonitor() => installMonitorService.Dispose();

    public void DisposeDiskScan()
    {
        diskQueryCancellation?.Cancel();
        diskQueryCancellation?.Dispose();
        diskScanService.Dispose();
    }

    public void RefreshDiskRoots()
    {
        var previousPath = SelectedDiskScanRoot?.Path;
        DiskScanRoots.Clear();
        foreach (var root in diskScanService.GetScanRoots()) DiskScanRoots.Add(root);
        if (!string.IsNullOrWhiteSpace(previousPath) && Directory.Exists(previousPath) && !DiskScanRoots.Any(root => root.Path.Equals(previousPath, StringComparison.OrdinalIgnoreCase)))
            DiskScanRoots.Add(new DiskScanRootOption(previousPath, previousPath));
        SelectedDiskScanRoot = DiskScanRoots.FirstOrDefault(root => root.Path.Equals(previousPath, StringComparison.OrdinalIgnoreCase)) ?? DiskScanRoots.FirstOrDefault();
        OnPropertyChanged(nameof(CanScanDisk));
    }

    public void SetDiskScanFolder(string path)
    {
        var rootPath = diskScanService.ValidateRoot(path);
        var selected = DiskScanRoots.FirstOrDefault(root => root.Path.Equals(rootPath, StringComparison.OrdinalIgnoreCase));
        var rootOption = selected ?? new DiskScanRootOption(rootPath, rootPath);
        SelectedDiskScanRoot = rootOption;
        if (selected is null) DiskScanRoots.Add(rootOption);
        OnPropertyChanged(nameof(CanScanDisk));
    }

    public async Task StartDiskScanAsync(CancellationToken cancellationToken, IProgress<DiskScanProgress>? progress = null)
    {
        if (SelectedDiskScanRoot is null) throw new InvalidOperationException(Texts["DiskChooseRoot"]);
        diskQueryCancellation?.Cancel();
        IsDiskScanning = true;
        IsDiskScanStale = false;
        DiskEntries.Clear();
        SelectedDiskEntry = null;
        DiskTreemapItems.Clear();
        UpdateDiskSelectionCount(0);
        DiskExtensionStats.Clear();
        DiskScanSummary = null;
        diskNavigationHistory.Clear();
        CurrentDiskDirectory = SelectedDiskScanRoot.Path;
        DiskListingMode = DiskListingMode.CurrentFolder;
        OnPropertyChanged(nameof(DiskListingModeIndex));
        DiskPageOffset = 0;
        DiskEntryTotalCount = 0;
        DiskSearchText = string.Empty;
        DiskStatusText = Texts["DiskScanStarted"];
        NotifyDiskStateChanged();
        try
        {
            var summary = await diskScanService.ScanAsync(SelectedDiskScanRoot.Path, progress, cancellationToken);
            DiskScanSummary = summary;
            CurrentDiskDirectory = summary.RootPath;
            DiskListingMode = DiskListingMode.CurrentFolder;
            DiskPageOffset = 0;
            await LoadDiskPageAsync();
            DiskStatusText = summary.IsIncomplete
                ? Texts.Format("DiskScanPartial", summary.SkippedCount)
                : Texts["DiskScanComplete"];
        }
        catch (OperationCanceledException)
        {
            DiskStatusText = Texts["DiskScanCancelled"];
            throw;
        }
        finally
        {
            IsDiskScanning = false;
            NotifyDiskStateChanged();
            OnPropertyChanged(nameof(DiskScanProgressVisibility));
        }
    }

    public void UpdateDiskScanProgress(DiskScanProgress progress) =>
        DiskStatusText = progress.BuildingIndex
            ? Texts["DiskIndexing"]
            : Texts.Format("DiskProgress", progress.EntriesVisited.ToString("N0", CultureInfo.GetCultureInfo(Texts.Language)), FormatBytes(progress.BytesMeasured), progress.SkippedEntries.ToString("N0", CultureInfo.GetCultureInfo(Texts.Language)));

    public async Task LoadDiskPageAsync(long? offset = null)
    {
        if (!diskScanService.HasScan || DiskScanSummary is null || string.IsNullOrWhiteSpace(CurrentDiskDirectory)) return;
        if (offset is not null) DiskPageOffset = Math.Max(0, offset.Value);
        diskQueryCancellation?.Cancel();
        diskQueryCancellation?.Dispose();
        diskQueryCancellation = new CancellationTokenSource();
        var cancellationToken = diskQueryCancellation.Token;
        try
        {
            var page = await diskScanService.GetPageAsync(DiskListingMode, CurrentDiskDirectory, DiskPageOffset, DiskPageSize, DiskSearchText, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            DiskEntries.Clear();
            SelectedDiskEntry = null;
            UpdateDiskSelectionCount(0);
            foreach (var entry in page.Entries) DiskEntries.Add(entry);
            var statisticsRoot = DiskListingMode == DiskListingMode.CurrentFolder ? CurrentDiskDirectory : DiskScanSummary.RootPath;
            var extensions = await diskScanService.GetExtensionStatsAsync(statisticsRoot, cancellationToken);
            DiskExtensionStats.Clear();
            foreach (var extension in extensions) DiskExtensionStats.Add(extension);
            DiskEntryTotalCount = page.TotalCount;
            DiskPageOffset = page.Offset;
            LayoutDiskTreemap(diskTreemapWidth, diskTreemapHeight);
            OnPropertyChanged(nameof(DiskPageIndicatorText));
            NotifyDiskStateChanged();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void LayoutDiskTreemap(double width, double height)
    {
        DiskTreemapItems.Clear();
        if (width < 4 || height < 4) return;
        var entries = DiskEntries.Where(entry => entry.SizeBytes is > 0).OrderByDescending(entry => entry.SizeBytes).ToArray();
        if (entries.Length == 0) return;
        var tiles = entries.Take(60).ToList();
        if (entries.Length > tiles.Count)
        {
            var rest = entries.Skip(tiles.Count).Aggregate(0L, (total, entry) => entry.SizeBytes.GetValueOrDefault() > long.MaxValue - total ? long.MaxValue : total + entry.SizeBytes.GetValueOrDefault());
            if (rest > 0)
            {
                tiles.Add(new DiskScanEntry(Texts.Format("DiskTreemapOthers", entries.Length - 60), string.Empty, string.Empty, false, false, rest,
                    string.Empty, 0, null, 0, 0, 0, false));
            }
        }
        var rectangles = new List<(DiskScanEntry Entry, double X, double Y, double Width, double Height)>(tiles.Count);
        LayoutTreemap(tiles, 0, tiles.Count, 0, 0, width, height, rectangles);
        foreach (var rectangle in rectangles)
        {
            var entry = rectangle.Entry;
            var size = entry.SizeText;
            var label = entry.IsDirectory ? "▣  " + entry.Name + Environment.NewLine + size : entry.Name + Environment.NewLine + size;
            var tooltip = entry.Path.Length == 0 ? entry.Name + Environment.NewLine + size : entry.Path + Environment.NewLine + size;
            var color = entry.IsIncomplete ? "#8A96A8" : entry.IsDirectory ? "#177E89" : TreemapColor(entry.Extension);
            DiskTreemapItems.Add(new DiskTreemapBlock(entry.Path, label, tooltip, color, rectangle.X, rectangle.Y,
                rectangle.Width, rectangle.Height, entry.Path.Length == 0, rectangle.Width >= 86 && rectangle.Height >= 46));
        }
    }

    public void ResizeDiskTreemap(double width, double height)
    {
        diskTreemapWidth = width;
        diskTreemapHeight = height;
        LayoutDiskTreemap(width, height);
    }

    public async Task NavigateDiskTreemapBlockAsync(DiskTreemapBlock block)
    {
        if (block.IsOther || string.IsNullOrWhiteSpace(block.Path)) return;
        var entry = DiskEntries.FirstOrDefault(item => item.Path.Equals(block.Path, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) await NavigateDiskEntryAsync(entry);
    }

    private static void LayoutTreemap(IReadOnlyList<DiskScanEntry> items, int start, int count, double x, double y, double width, double height,
        ICollection<(DiskScanEntry Entry, double X, double Y, double Width, double Height)> result)
    {
        if (count <= 0 || width <= 0 || height <= 0) return;
        if (count == 1)
        {
            result.Add((items[start], x, y, width, height));
            return;
        }
        var total = items.Skip(start).Take(count).Sum(item => (double)item.SizeBytes.GetValueOrDefault());
        if (total <= 0)
        {
            result.Add((items[start], x, y, width, height));
            return;
        }
        var splitCount = 1;
        var leftTotal = (double)items[start].SizeBytes.GetValueOrDefault();
        while (splitCount < count - 1 && leftTotal < total / 2)
        {
            leftTotal += items[start + splitCount].SizeBytes.GetValueOrDefault();
            splitCount++;
        }
        var ratio = Math.Clamp(leftTotal / total, 0.03, 0.97);
        if (width >= height)
        {
            var leftWidth = width * ratio;
            LayoutTreemap(items, start, splitCount, x, y, leftWidth, height, result);
            LayoutTreemap(items, start + splitCount, count - splitCount, x + leftWidth, y, width - leftWidth, height, result);
        }
        else
        {
            var topHeight = height * ratio;
            LayoutTreemap(items, start, splitCount, x, y, width, topHeight, result);
            LayoutTreemap(items, start + splitCount, count - splitCount, x, y + topHeight, width, height - topHeight, result);
        }
    }

    private static string TreemapColor(string extension)
    {
        string[] palette = ["#396BE8", "#13A8B8", "#17998E", "#805AD5", "#D17A24", "#B44D76", "#4B7B53", "#657A9A"];
        var hash = 17;
        foreach (var character in extension) hash = unchecked(hash * 31 + char.ToUpperInvariant(character));
        return palette[(hash & int.MaxValue) % palette.Length];
    }

    public async Task UpdateDiskSearchAsync(string value)
    {
        DiskSearchText = value.Trim();
        await LoadDiskPageAsync(0);
    }

    public async Task SetDiskListingModeAsync(DiskListingMode mode)
    {
        DiskListingMode = mode;
        OnPropertyChanged(nameof(DiskListingModeIndex));
        await LoadDiskPageAsync(0);
    }

    public void SetDiskSidePanelMode(int mode)
    {
        DiskSidePanelMode = Math.Clamp(mode, 0, 1);
        OnPropertyChanged(nameof(DiskExtensionPanelVisibility));
        OnPropertyChanged(nameof(DiskTreemapPanelVisibility));
    }

    public async Task OpenSelectedDiskFolderAsync()
    {
        if (SelectedDiskEntry is { IsDirectory: true, IsReparsePoint: false } entry) await NavigateDiskEntryAsync(entry);
    }

    public async Task NavigateDiskEntryAsync(DiskScanEntry entry)
    {
        if (!entry.IsDirectory || entry.IsReparsePoint || DiskScanSummary is null || IsDiskScanStale || IsDiskScanning) return;
        if (!DeletionPathPolicy.IsPathWithin(entry.Path, DiskScanSummary.RootPath)) return;
        if (!CurrentDiskDirectory.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)) diskNavigationHistory.Push(CurrentDiskDirectory);
        CurrentDiskDirectory = entry.Path;
        DiskListingMode = DiskListingMode.CurrentFolder;
        OnPropertyChanged(nameof(DiskListingModeIndex));
        await LoadDiskPageAsync(0);
    }

    public async Task GoBackDiskFolderAsync()
    {
        if (!CanGoBackDiskFolder || DiskScanSummary is null) return;
        var previous = diskNavigationHistory.Pop();
        if (!DeletionPathPolicy.IsPathWithin(previous, DiskScanSummary.RootPath) && !previous.Equals(DiskScanSummary.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            diskNavigationHistory.Clear();
            NotifyDiskStateChanged();
            return;
        }
        CurrentDiskDirectory = previous;
        DiskListingMode = DiskListingMode.CurrentFolder;
        OnPropertyChanged(nameof(DiskListingModeIndex));
        await LoadDiskPageAsync(0);
    }

    public async Task GoUpDiskFolderAsync()
    {
        if (DiskScanSummary is null || !CanGoUpDiskFolder) return;
        var parent = Path.GetDirectoryName(CurrentDiskDirectory);
        if (string.IsNullOrWhiteSpace(parent) || !DeletionPathPolicy.IsPathWithin(parent, DiskScanSummary.RootPath) && !parent.Equals(DiskScanSummary.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            diskNavigationHistory.Push(CurrentDiskDirectory);
            CurrentDiskDirectory = DiskScanSummary.RootPath;
        }
        else
        {
            diskNavigationHistory.Push(CurrentDiskDirectory);
            CurrentDiskDirectory = parent;
        }
        DiskListingMode = DiskListingMode.CurrentFolder;
        OnPropertyChanged(nameof(DiskListingModeIndex));
        await LoadDiskPageAsync(0);
    }

    public async Task PageDiskEntriesAsync(int direction)
    {
        var nextOffset = Math.Max(0, DiskPageOffset + direction * DiskPageSize);
        await LoadDiskPageAsync(nextOffset);
    }

    public void UpdateDiskSelectionCount(int count)
    {
        if (diskSelectedItemCount == count) return;
        diskSelectedItemCount = count;
        OnPropertyChanged(nameof(CanDeleteDiskEntries));
        OnPropertyChanged(nameof(DiskSelectionCountText));
    }

    public async Task<DiskDeletionReport> DeleteDiskEntriesAsync(IReadOnlyList<DiskScanEntry> entries, CancellationToken cancellationToken = default)
    {
        if (!SafetyAccepted) throw new InvalidOperationException(Texts["StatusSafetyRequired"]);
        var report = await diskScanService.DeleteSelectedAsync(entries, cancellationToken);
        IsDiskScanStale = report.RequiresRescan;
        if (report.RequiresRescan)
        {
            DiskScanSummary = null;
            DiskEntries.Clear();
            SelectedDiskEntry = null;
            DiskExtensionStats.Clear();
            DiskTreemapItems.Clear();
            DiskEntryTotalCount = 0;
            UpdateDiskSelectionCount(0);
            DiskStatusText = Texts["DiskRescanRequired"];
        }
        else
        {
            DiskScanSummary = report.UpdatedSummary;
            DiskStatusText = Texts.Format("DiskDeleteComplete", report.DeletedItems);
            await LoadDiskPageAsync();
        }
        NotifyDiskStateChanged();
        return report;
    }

    public async Task FindDuplicatesAsync(bool verifyHashes, IProgress<DuplicateScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!HasDiskScan || IsDiskScanning) throw new InvalidOperationException(Texts["DiskChooseRoot"]);
        DuplicateFiles.Clear();
        UpdateDuplicateSelectionCount(0);
        var result = await diskScanService.FindDuplicatesAsync(verifyHashes, progress, cancellationToken);
        foreach (var file in result) DuplicateFiles.Add(file);
        OnPropertyChanged(nameof(DuplicateEmptyVisibility));
        AnalysisStatusText = verifyHashes
            ? $"{DuplicateFiles.Count:N0} files in verified duplicate groups. Review each copy before deleting."
            : $"{DuplicateFiles.Count:N0} files share a size. Run SHA-256 verification before deleting duplicates.";
        OnPropertyChanged(nameof(HasVerifiedDuplicates));
        OnPropertyChanged(nameof(CanDeleteDuplicates));
    }

    public async Task<DiskSnapshot> SaveDiskSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!HasDiskScan || IsDiskScanning) throw new InvalidOperationException(Texts["DiskChooseRoot"]);
        var snapshot = await diskScanService.SaveSnapshotAsync(cancellationToken);
        RefreshDiskSnapshots();
        AnalysisStatusText = $"Scan saved: {snapshot.DisplayName}";
        return snapshot;
    }

    public void RefreshDiskSnapshots()
    {
        DiskSnapshots.Clear();
        foreach (var snapshot in diskScanService.GetSnapshots()) DiskSnapshots.Add(snapshot);
    }

    public async Task CompareDiskSnapshotsAsync(long? offset = null, CancellationToken cancellationToken = default)
    {
        if (SelectedSnapshotBefore is null || SelectedSnapshotAfter is null) throw new InvalidOperationException("Select two saved scans.");
        var page = await diskScanService.CompareSnapshotsAsync(SelectedSnapshotBefore, SelectedSnapshotAfter, offset ?? 0, SnapshotChangePageSize, cancellationToken);
        SnapshotChanges.Clear();
        foreach (var change in page.Entries) SnapshotChanges.Add(change);
        snapshotChangeTotalCount = page.TotalCount;
        snapshotChangeOffset = page.Offset;
        OnPropertyChanged(nameof(SnapshotChangeEmptyVisibility));
        OnPropertyChanged(nameof(SnapshotChangePageText));
        OnPropertyChanged(nameof(CanPageSnapshotChangesBackward));
        OnPropertyChanged(nameof(CanPageSnapshotChangesForward));
        AnalysisStatusText = $"{page.TotalCount:N0} paths added, removed or changed between the two scans.";
    }

    public Task PageSnapshotChangesAsync(int direction, CancellationToken cancellationToken = default)
    {
        var nextOffset = Math.Max(0, snapshotChangeOffset + direction * SnapshotChangePageSize);
        return CompareDiskSnapshotsAsync(nextOffset, cancellationToken);
    }

    public void DeleteSelectedDiskSnapshot()
    {
        if (SelectedSnapshotBefore is null) return;
        diskScanService.DeleteSnapshot(SelectedSnapshotBefore);
        SnapshotChanges.Clear();
        snapshotChangeTotalCount = 0;
        snapshotChangeOffset = 0;
        OnPropertyChanged(nameof(SnapshotChangeEmptyVisibility));
        OnPropertyChanged(nameof(SnapshotChangePageText));
        OnPropertyChanged(nameof(CanPageSnapshotChangesBackward));
        OnPropertyChanged(nameof(CanPageSnapshotChangesForward));
        RefreshDiskSnapshots();
    }

    public CleanupPlan SaveCleanupPlan(string name, IReadOnlyList<DiskScanEntry> entries)
    {
        if (!HasDiskScan || IsDiskScanning || DiskScanSummary is null) throw new InvalidOperationException("Scan the selected disk or folder first.");
        var plan = cleanupPlanStore.Save(name, DiskScanSummary, entries);
        RefreshCleanupPlans();
        AnalysisStatusText = $"Plan saved: {plan.Name}";
        return plan;
    }

    public void RefreshCleanupPlans()
    {
        CleanupPlans.Clear();
        foreach (var plan in cleanupPlanStore.Load()) CleanupPlans.Add(plan);
        OnPropertyChanged(nameof(PlanEmptyVisibility));
    }

    public async Task<IReadOnlyList<DiskScanEntry>> ResolveSelectedCleanupPlanAsync(CancellationToken cancellationToken = default)
    {
        var plan = SelectedCleanupPlan ?? throw new InvalidOperationException("Select a saved plan.");
        if (!HasDiskScan || DiskScanSummary is null || !DiskScanSummary.RootPath.Equals(plan.RootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Scan {plan.RootPath} before applying this plan. Saved paths are rechecked against the current scan.");
        var entries = await diskScanService.ResolveCurrentEntriesAsync(plan.Items.Select(item => item.Path), cancellationToken);
        if (entries.Count != plan.Items.Count) throw new InvalidOperationException("Some planned paths no longer exist in the current scan. Review and update the plan before deleting.");
        return entries;
    }

    public Task<IReadOnlyList<DiskScanEntry>> ResolveDiskPathsAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        diskScanService.ResolveCurrentEntriesAsync(paths, cancellationToken);

    public IEnumerable<DiskScanEntry> EnumerateDiskReportEntries(CancellationToken cancellationToken = default) =>
        HasDiskScan ? diskScanService.EnumerateCurrentEntries(cancellationToken) : throw new InvalidOperationException(Texts["DiskChooseRoot"]);

    public void DeleteSelectedCleanupPlan()
    {
        if (SelectedCleanupPlan is null) return;
        cleanupPlanStore.Delete(SelectedCleanupPlan);
        RefreshCleanupPlans();
    }

    public async Task<IReadOnlyList<(QuarantineEntry Entry, string? Error)>> RestoreQuarantineEntriesAsync(IReadOnlyList<QuarantineEntry> entries)
    {
        if (!SafetyAccepted) throw new InvalidOperationException(Texts["StatusSafetyRequired"]);
        var outcomes = new List<(QuarantineEntry, string?)>();
        foreach (var entry in entries)
        {
            try
            {
                if (!entry.CanRestore) throw new InvalidOperationException(entry.RestoreStatus);
                await quarantineService.RestoreAsync(entry, Texts["HistoryRestore"], Texts["HistoryRestored"]);
                outcomes.Add((entry, null));
            }
            catch (Exception ex) { outcomes.Add((entry, ex.Message)); }
        }
        await RefreshLocalRecordsAsync();
        return outcomes;
    }

    private void NotifyDiskStateChanged()
    {
        OnPropertyChanged(nameof(CanScanDisk));
        OnPropertyChanged(nameof(CanAnalyzeDisk));
        OnPropertyChanged(nameof(CanCancelDiskScan));
        OnPropertyChanged(nameof(DiskScanProgressVisibility));
        OnPropertyChanged(nameof(CanGoUpDiskFolder));
        OnPropertyChanged(nameof(CanGoBackDiskFolder));
        OnPropertyChanged(nameof(HasDiskScan));
        OnPropertyChanged(nameof(DiskResultsVisibility));
        OnPropertyChanged(nameof(DiskEmptyVisibility));
        OnPropertyChanged(nameof(CanDeleteDiskEntries));
        OnPropertyChanged(nameof(CanDeleteDuplicates));
        OnPropertyChanged(nameof(CanOpenSelectedDiskFolder));
        OnPropertyChanged(nameof(CanPageDiskBackward));
        OnPropertyChanged(nameof(CanPageDiskForward));
        OnPropertyChanged(nameof(DiskPageIndicatorText));
        OnPropertyChanged(nameof(DiskCurrentDirectoryText));
        OnPropertyChanged(nameof(DiskSummarySizeText));
        OnPropertyChanged(nameof(DiskFileCountText));
        OnPropertyChanged(nameof(DiskFolderCountText));
        OnPropertyChanged(nameof(DiskSkippedCountText));
    }

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
        diskScanService = new DiskScanService(this.localDataPath);
        cleanupPlanStore = new CleanupPlanStore(this.localDataPath);
        RefreshDiskSnapshots();
        RefreshCleanupPlans();
        RefreshDiskRoots();
        settingsPath = Path.Combine(this.localDataPath, "settings.json");
        var settings = LoadUserSettings();
        var language = LocalizationCatalog.Languages.Contains(settings.Language ?? string.Empty, StringComparer.OrdinalIgnoreCase) ? settings.Language! : "en";
        Texts = new LocalizationCatalog { Language = language };
        AnalysisStatusText = Texts["AnalysisReady"];
        DiskStatusText = Texts["DiskReadyMessage"];
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
        _ = RefreshInstallReportsAsync();
    }

    partial void OnSelectedApplicationChanged(InstalledApplication? value)
    {
        OnPropertyChanged(nameof(MonitorTargetText));
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

    partial void OnSelectedDiskScanRootChanged(DiskScanRootOption? value)
    {
        OnPropertyChanged(nameof(CanScanDisk));
        NotifyDiskStateChanged();
    }
    partial void OnSelectedDiskEntryChanged(DiskScanEntry? value) => OnPropertyChanged(nameof(CanOpenSelectedDiskFolder));

    partial void OnCurrentDiskDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(DiskCurrentDirectoryText));
        OnPropertyChanged(nameof(CanGoUpDiskFolder));
        OnPropertyChanged(nameof(CanGoBackDiskFolder));
    }

    partial void OnSelectedLeftoverChanged(LeftoverCandidate? value)
    {
        OnPropertyChanged(nameof(CanQuarantineSelected));
        OnPropertyChanged(nameof(SelectedLeftoverExplanation));
    }

    partial void OnSelectedQuarantineChanged(QuarantineEntry? value)
    {
        OnPropertyChanged(nameof(CanRestoreSelected));
    }

    partial void OnSafetyAcceptedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUninstallSelected));
        OnPropertyChanged(nameof(CanManualDeleteSelected));
        OnPropertyChanged(nameof(CanQuarantineSelected));
        OnPropertyChanged(nameof(CanRestoreSelected));
        OnPropertyChanged(nameof(CanDeleteDiskEntries));
        OnPropertyChanged(nameof(CanDeleteDuplicates));
        OnPropertyChanged(nameof(CanOpenSelectedDiskFolder));
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
        if (!HasDiskScan && !IsDiskScanning) DiskStatusText = Texts["DiskReadyMessage"];
        OnPropertyChanged(nameof(RemoveSearchRootText));
        OnPropertyChanged(nameof(UninstallButtonText));
        OnPropertyChanged(nameof(SelectedApplicationDisplayName));
        OnPropertyChanged(nameof(SelectedPublisher));
        OnPropertyChanged(nameof(DetailVersion));
        OnPropertyChanged(nameof(DetailLocation));
        OnPropertyChanged(nameof(DetailSource));
        OnPropertyChanged(nameof(SelectedApplicationsSummary));
        OnPropertyChanged(nameof(ManualDeleteButtonText));
        OnPropertyChanged(nameof(DiskPageIndicatorText));
        OnPropertyChanged(nameof(DiskSelectionCountText));
        NotifyDiskStateChanged();
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
            "Disk" => "Disk",
            "Analysis" => "Analysis",
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
            "Disk" => Texts["DiskSubtitle"],
            "Analysis" => Texts["AnalysisSubtitle"],
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
            OnPropertyChanged(nameof(SystemLeftoversCount));
            OnPropertyChanged(nameof(FolderLeftoversCount));
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
        var visibleIds = orderedApplications.Select(application => application.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = VisibleApplications.Count - 1; index >= 0; index--)
        {
            if (!visibleIds.Contains(VisibleApplications[index].Id)) VisibleApplications.RemoveAt(index);
        }
        for (var targetIndex = 0; targetIndex < orderedApplications.Length; targetIndex++)
        {
            var application = orderedApplications[targetIndex];
            var currentIndex = -1;
            for (var index = targetIndex; index < VisibleApplications.Count; index++)
            {
                if (VisibleApplications[index].Id.Equals(application.Id, StringComparison.OrdinalIgnoreCase))
                {
                    currentIndex = index;
                    break;
                }
            }
            if (currentIndex < 0)
            {
                VisibleApplications.Insert(targetIndex, application);
            }
            else
            {
                if (!VisibleApplications[currentIndex].Equals(application)) VisibleApplications[currentIndex] = application;
                if (currentIndex != targetIndex) VisibleApplications.Move(currentIndex, targetIndex);
            }
        }
        if (selectedApplication is not null && orderedApplications.FirstOrDefault(application => application.Id.Equals(selectedApplication.Id, StringComparison.OrdinalIgnoreCase)) is { } refreshedSelection)
            SelectedApplication = refreshedSelection;
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
