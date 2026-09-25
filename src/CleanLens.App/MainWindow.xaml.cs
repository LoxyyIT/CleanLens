using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using CleanLens.Core.Models;
using CleanLens.Windows;

namespace CleanLens.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += MainWindow_DataContextChanged;
        Loaded += Window_Loaded;
        Closed += (_, _) => ViewModel.DisposeInstallMonitor();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldViewModel)
        {
            oldViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }
        if (e.NewValue is MainViewModel newViewModel)
        {
            newViewModel.PropertyChanged += ViewModel_PropertyChanged;
            UpdateLocalizedColumnHeaders(newViewModel);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedLanguage) && sender is MainViewModel viewModel)
        {
            UpdateLocalizedColumnHeaders(viewModel);
        }
    }

    private void UpdateLocalizedColumnHeaders(MainViewModel viewModel)
    {
        ApplicationsDataGrid.Columns[1].Header = viewModel.Texts["HeaderApplication"];
        ApplicationsDataGrid.Columns[2].Header = viewModel.Texts["HeaderPublisher"];
        ApplicationsDataGrid.Columns[3].Header = viewModel.Texts["HeaderVersion"];
        ApplicationsDataGrid.Columns[4].Header = viewModel.Texts["HeaderSize"];
        LeftoversDataGrid.Columns[0].Header = viewModel.Texts["HeaderPath"];
        LeftoversDataGrid.Columns[1].Header = viewModel.Texts["HeaderSizeSimple"];
        LeftoversDataGrid.Columns[2].Header = viewModel.Texts["HeaderType"];
        LeftoversDataGrid.Columns[3].Header = viewModel.Texts["HeaderConfidence"];
        LeftoversDataGrid.Columns[4].Header = viewModel.Texts["HeaderReason"];
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.UpdateSearch(SearchBox.Text);
        }
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && DataContext is MainViewModel viewModel)
        {
            viewModel.UpdateFilter(comboBox.SelectedIndex);
        }
    }

    private void ApplicationsDataGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || string.IsNullOrWhiteSpace(e.Column.SortMemberPath)) return;
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        foreach (var column in ApplicationsDataGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = direction;
        viewModel.SortApplications(e.Column.SortMemberPath, direction);
    }

    private void SearchScope_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && DataContext is MainViewModel viewModel)
        {
            viewModel.UpdateSearchScope(comboBox.SelectedIndex);
        }
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedValue is string language && DataContext is MainViewModel viewModel &&
            viewModel.Languages.Contains(language, StringComparer.OrdinalIgnoreCase) && !viewModel.SelectedLanguage.Equals(language, StringComparison.OrdinalIgnoreCase))
        {
            viewModel.SelectedLanguage = language;
        }
    }

    private async void Applications_Click(object sender, RoutedEventArgs e) => await ShowPageAsync("Applications");

    private void OpenGitHub_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/LoxyyIT/CleanLens") { UseShellExecute = true });

    private async void Leftovers_Click(object sender, RoutedEventArgs e)
    {
        await ShowPageAsync("Leftover review");
        if (ViewModel.HasReviewApplication)
        {
            await ViewModel.ScanLeftoversCommand.ExecuteAsync(null);
        }
    }

    private async void History_Click(object sender, RoutedEventArgs e) => await ShowPageAsync("History");

    private async void Quarantine_Click(object sender, RoutedEventArgs e) => await ShowPageAsync("Quarantine");

    private async void Settings_Click(object sender, RoutedEventArgs e) => await ShowPageAsync("Settings");

    private void AddSearchRoot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = ViewModel.Texts["SelectSearchRoot"], Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            ViewModel.AddManualSearchRoot(picker.FolderName);
        }
        catch (Exception ex)
        {
            ShowLocalizedMessage(ViewModel.Texts["CustomSearchRootsHeading"], ex.Message, CleanLensDialogTone.Warning);
        }
    }

    private void RemoveSearchRoot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ManualSearchRootSetting setting }) ViewModel.RemoveManualSearchRoot(setting);
    }

    private async void StartMonitor_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.StartInstallMonitorAsync(); }
        catch (Exception ex) { ShowLocalizedMessage(ViewModel.Texts["InstallMonitorHeading"], ViewModel.Texts.Format("ActionFailed", ex.Message), CleanLensDialogTone.Warning); }
    }

    private async void StopMonitor_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.StopInstallMonitorAsync(); }
        catch (Exception ex) { ShowLocalizedMessage(ViewModel.Texts["InstallMonitorHeading"], ViewModel.Texts.Format("ActionFailed", ex.Message), CleanLensDialogTone.Warning); }
    }

    private async void MeasureDiskUsage_Click(object sender, RoutedEventArgs e)
    {
        var application = ViewModel.SelectedApplication;
        if (application is null) return;
        using var cancellation = new CancellationTokenSource();
        var progress = CleanLensDialogService.ShowProgress(this, ViewModel.Texts["MeasureDiskUsage"], ViewModel.Texts["DiskUsageScanning"], ViewModel.Texts["Cancel"], cancellation.Cancel);
        var progressReporter = new Progress<int>(count => CleanLensDialogService.SetProgressMessage(progress, ViewModel.Texts.Format("DiskUsageProgress", count)));
        try
        {
            var result = await new ManualDeleteService().MeasureApplicationFootprintAsync(
                application,
                cancellation.Token,
                ViewModel.GetEnabledManualSearchRoots(),
                progressReporter,
                ViewModel.GetEnabledDefaultManualSearchRoots(),
                ViewModel.IncludeRegisteredInstallLocations,
                ViewModel.IncludeSteamLocations);
            var message = ViewModel.Texts.Format("MeasuredDiskUsage", FormatBytes(result.Bytes), result.Locations, result.Files, result.Skipped);
            if (result.IsIncomplete) message += "\n\n" + ViewModel.Texts["DiskUsagePartial"];
            progress.Close();
            ShowLocalizedMessage(ViewModel.Texts["MeasureDiskUsage"], message, result.IsIncomplete ? CleanLensDialogTone.Warning : CleanLensDialogTone.Information);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            progress.Close();
            ShowLocalizedMessage(ViewModel.Texts["MeasureDiskUsage"], ViewModel.Texts.Format("ActionFailed", ex.Message), CleanLensDialogTone.Warning);
        }
        finally { if (progress.IsVisible) progress.Close(); }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private async Task ShowPageAsync(string page)
    {
        var applicationsActive = page == "Applications";
        ApplicationsNav.Tag = applicationsActive ? "Active" : null;
        LeftoversNav.Tag = page == "Leftover review" ? "Active" : null;
        HistoryNav.Tag = page == "History" ? "Active" : null;
        QuarantineNav.Tag = page == "Quarantine" ? "Active" : null;
        SettingsNav.Tag = page == "Settings" ? "Active" : null;
        ApplicationWorkspace.Visibility = page == "Applications" ? Visibility.Visible : Visibility.Collapsed;
        LeftoverWorkspace.Visibility = page == "Leftover review" ? Visibility.Visible : Visibility.Collapsed;
        HistoryWorkspace.Visibility = page == "History" ? Visibility.Visible : Visibility.Collapsed;
        QuarantineWorkspace.Visibility = page == "Quarantine" ? Visibility.Visible : Visibility.Collapsed;
        SettingsWorkspace.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        SummaryMetrics.Visibility = page is "Applications" or "Leftover review" ? Visibility.Visible : Visibility.Collapsed;
        ViewModel.SetPage(page);
        if (page is "History" or "Quarantine")
        {
            await ViewModel.RefreshLocalRecordsFromUiAsync();
        }
    }

    private async void QuarantineSelected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.QuarantineSelectedAsync();
            await ShowPageAsync("Quarantine");
        }
        catch (Exception ex)
        {
            ShowLocalizedMessage(ViewModel.Texts["AppName"], ViewModel.Texts.Format("ActionFailed", ex.Message), CleanLensDialogTone.Warning);
        }
    }

    private async void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RestoreSelectedAsync();
        }
        catch (Exception ex)
        {
            ShowLocalizedMessage(ViewModel.Texts["AppName"], ViewModel.Texts.Format("ActionFailed", ex.Message), CleanLensDialogTone.Warning);
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var application = ViewModel.SelectedApplication;
        if (application is null)
        {
            ShowLocalizedMessage(ViewModel.Texts["AppName"], ViewModel.Texts["SelectFirst"]);
            return;
        }
        if (!ViewModel.SafetyAccepted)
        {
            ShowLocalizedMessage(ViewModel.Texts["SafetyNoticeTitle"], ViewModel.Texts["SafetyRequired"], CleanLensDialogTone.Warning);
            return;
        }
        if (application.IsAppxPackage)
        {
            if (application.IsNonRemovablePackage)
            {
                ShowLocalizedMessage(ViewModel.Texts["AppxPackage"], ViewModel.Texts["AppxNonRemovable"], CleanLensDialogTone.Warning);
                return;
            }
            if (!CleanLensDialogService.Confirm(this, ViewModel.Texts["AppxPackage"], ViewModel.Texts.Format("AppxRemoveConfirm", application.Name, application.PackageFullName), ViewModel.Texts["Continue"], ViewModel.Texts["Cancel"], danger: true)) return;
            try
            {
                await new AppxPackageManager().RemoveForCurrentUserAsync(application.PackageFullName);
                await ViewModel.RecordUninstallAsync(application, null);
                ViewModel.StatusText = ViewModel.Texts["StatusOfficialStarted"];
            }
            catch (Exception ex)
            {
                ShowLocalizedMessage(ViewModel.Texts["AppxPackage"], ViewModel.Texts.Format("UninstallerError", ex.Message), CleanLensDialogTone.Warning);
            }
            return;
        }
        var command = application.UninstallCommand;
        if (string.IsNullOrWhiteSpace(command) && !string.IsNullOrWhiteSpace(application.QuietUninstallCommand))
        {
            ShowLocalizedMessage(ViewModel.Texts["ReviewUnavailableTitle"], ViewModel.Texts["QuietOnly"], CleanLensDialogTone.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(command))
        {
            ShowLocalizedMessage(ViewModel.Texts["AppName"], ViewModel.Texts["NoUninstaller"]);
            return;
        }
        (string FileName, string Arguments) parsed;
        try { parsed = UninstallerLauncher.ParseExecutable(command); }
        catch (Exception ex)
        {
            ShowLocalizedMessage(ViewModel.Texts["UninstallerErrorTitle"], ViewModel.Texts.Format("UninstallerError", ex.Message), CleanLensDialogTone.Warning);
            return;
        }
        var executablePath = Path.GetFileName(parsed.FileName).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.SystemDirectory, "msiexec.exe")
            : parsed.FileName;
        var trust = new AuthenticodeVerifier().Verify(executablePath);
        var signerInfo = ViewModel.Texts.Format("UninstallerSignature", trust.IsTrusted ? ViewModel.Texts["SignatureTrusted"] : ViewModel.Texts["SignatureNotTrusted"], string.IsNullOrWhiteSpace(trust.Publisher) ? ViewModel.Texts["SignaturePublisherUnknown"] : trust.Publisher, trust.Status);
        if (Path.GetFileName(executablePath).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)) signerInfo += "\n" + ViewModel.Texts["MsiSignatureNote"];
        var answer = CleanLensDialogService.Confirm(this, ViewModel.Texts["ReviewUninstallerTitle"], ViewModel.Texts.Format("ReviewUninstallerMessage", application.Name, command) + "\n\n" + signerInfo, ViewModel.Texts["Continue"], ViewModel.Texts["Cancel"]);
        if (!answer)
        {
            return;
        }
        try
        {
            var process = new UninstallerLauncher().Start(command);
            await ViewModel.RecordUninstallAsync(application, process?.Id);
            ViewModel.StatusText = ViewModel.Texts["StatusOfficialStarted"];
        }
        catch (Exception ex)
        {
            ShowLocalizedMessage(ViewModel.Texts["UninstallerErrorTitle"], ViewModel.Texts.Format("UninstallerError", ex.Message), CleanLensDialogTone.Warning);
        }
    }

    private async void ManualDelete_Click(object sender, RoutedEventArgs e)
    {
        var application = ViewModel.SelectedApplication;
        if (application is null)
        {
            ShowLocalizedMessage(ViewModel.Texts["AppName"], ViewModel.Texts["SelectFirst"]);
            return;
        }
        if (!ViewModel.SafetyAccepted)
        {
            ShowLocalizedMessage(ViewModel.Texts["SafetyNoticeTitle"], ViewModel.Texts["SafetyRequired"], CleanLensDialogTone.Warning);
            return;
        }

        IReadOnlyList<ManualDeleteCandidate> candidates;
        using var cancellation = new CancellationTokenSource();
        var progress = CleanLensDialogService.ShowProgress(this, ViewModel.Texts["ManualDeleteTitle"], ViewModel.Texts["ManualDeleteScanning"], ViewModel.Texts["Cancel"], cancellation.Cancel);
        var scanProgress = new Progress<int>(count => CleanLensDialogService.SetProgressMessage(progress, ViewModel.Texts.Format("ManualDeleteScanProgress", count)));
        try
        {
            candidates = await new ManualDeleteService().FindExactNameMatchesAsync(
                application,
                cancellation.Token,
                scanProgress,
                ViewModel.GetEnabledManualSearchRoots(),
                ViewModel.GetEnabledDefaultManualSearchRoots(),
                ViewModel.IncludeRegisteredInstallLocations,
                ViewModel.IncludeSteamLocations);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ShowLocalizedMessage(ViewModel.Texts["AppName"], ViewModel.Texts.Format("ActionFailed", ex.Message), CleanLensDialogTone.Warning);
            return;
        }
        finally
        {
            progress.Close();
        }

        if (candidates.Count == 0)
        {
            ShowLocalizedMessage(ViewModel.Texts["ManualDeleteTitle"], ViewModel.Texts["ManualDeleteEmpty"]);
            return;
        }

        var selection = CleanLensDialogService.SelectManualDeletePaths(
            this,
            ViewModel.Texts["ManualDeleteTitle"],
            ViewModel.Texts["ManualDeleteIntro"],
            ViewModel.Texts["ManualDeleteCandidateCount"],
            ViewModel.Texts["ManualDeleteQuarantine"],
            ViewModel.Texts["ManualDeleteSelected"],
            ViewModel.Texts["Cancel"],
            candidates);
        if (selection is null)
        {
            return;
        }
        var selectedPaths = selection.Paths;
        if (selectedPaths.Count == 0)
        {
            ShowLocalizedMessage(ViewModel.Texts["ManualDeleteTitle"], ViewModel.Texts["ManualDeleteSelectOne"]);
            return;
        }

        if (selection.Action == ManualDeleteSelectionAction.Quarantine)
        {
            var selectedSet = selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedCandidates = candidates.Where(candidate => selectedSet.Contains(candidate.Path)).ToArray();
            var movedCount = 0;
            var failures = new List<string>();
            foreach (var candidate in selectedCandidates)
            {
                try
                {
                    await ViewModel.QuarantineManualDeleteCandidateAsync(application, candidate.Path);
                    movedCount++;
                }
                catch (Exception ex)
                {
                    var errorMessage = IsAccessDenied(ex)
                        ? ViewModel.Texts["QuarantineAdminRetry"]
                        : ex.Message;
                    failures.Add($"{candidate.Path}\n{errorMessage}");
                }
            }
            if (movedCount > 0)
            {
                await ShowPageAsync("Quarantine");
            }
            if (failures.Count == 0)
            {
                ShowLocalizedMessage(
                    ViewModel.Texts["QuarantinedItems"],
                    ViewModel.Texts.Format("ManualDeleteQuarantined", movedCount));
            }
            else
            {
                ShowLocalizedMessage(
                    ViewModel.Texts["QuarantinedItems"],
                    ViewModel.Texts.Format("ManualDeleteQuarantinePartial", movedCount, failures.Count, string.Join(Environment.NewLine + Environment.NewLine, failures)),
                    CleanLensDialogTone.Warning);
            }
            return;
        }

        var confirmationText = ViewModel.Texts.Format("ManualDeleteConfirm", selectedPaths.Count, string.Join(Environment.NewLine, selectedPaths));
        if (!CleanLensDialogService.Confirm(this, ViewModel.Texts["ManualDeleteTitle"], confirmationText, ViewModel.Texts["ManualDelete"], ViewModel.Texts["Cancel"], danger: true))
        {
            return;
        }
        try
        {
            await new ManualDeleteService().DeleteSelectedAsync(
                application,
                selectedPaths,
                additionalRoots: ViewModel.GetEnabledManualSearchRoots(),
                enabledDefaultRoots: ViewModel.GetEnabledDefaultManualSearchRoots(),
                includeRegisteredLocations: ViewModel.IncludeRegisteredInstallLocations,
                includeSteamLocations: ViewModel.IncludeSteamLocations);
            ViewModel.StatusText = ViewModel.Texts["ManualDeleteDone"];
            ShowLocalizedMessage(ViewModel.Texts["AppName"], ViewModel.Texts["ManualDeleteDone"]);
        }
        catch (Exception ex)
        {
            ShowLocalizedMessage(ViewModel.Texts["AppName"], ViewModel.Texts.Format("ActionFailed", ex.Message), CleanLensDialogTone.Warning);
        }
    }

    private void ShowLocalizedMessage(string title, string message, CleanLensDialogTone tone = CleanLensDialogTone.Information) =>
        CleanLensDialogService.ShowMessage(this, title, message, tone, ViewModel.Texts["DialogOk"]);

    private static bool IsAccessDenied(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException or System.Security.SecurityException ||
                (unchecked((uint)current.HResult) & 0xFFFF) == 5)
            {
                return true;
            }
        }
        return false;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplicationsNav.Tag = "Active";
        if (DataContext is MainViewModel viewModel)
        {
            LanguageSelector.SelectedValue = viewModel.SelectedLanguage;
            if (viewModel.SafetyAccepted)
            {
                _ = viewModel.ScanCommand.ExecuteAsync(null);
            }
        }
    }
}
