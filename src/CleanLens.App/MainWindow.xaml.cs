using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using CleanLens.Core.Models;
using CleanLens.Core.Safety;
using CleanLens.Windows;

namespace CleanLens.App;

public partial class MainWindow : Window
{
    private CancellationTokenSource? diskSearchDebounce;
    public MainWindow()
    {
        InitializeComponent();
        SelectAnalysisTab(0);
        DataContextChanged += MainWindow_DataContextChanged;
        Loaded += Window_Loaded;
        Closed += (_, _) =>
        {
            diskSearchDebounce?.Cancel();
            diskSearchDebounce?.Dispose();
            ViewModel.DisposeInstallMonitor();
            ViewModel.DisposeDiskScan();
        };
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
            UpdateDiskSidePanelButtons(newViewModel);
            UpdateWindowCaptionButtons(newViewModel);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedLanguage) && sender is MainViewModel viewModel)
        {
            UpdateLocalizedColumnHeaders(viewModel);
            UpdateWindowCaptionButtons(viewModel);
        }
    }

    private void UpdateWindowCaptionButtons(MainViewModel viewModel)
    {
        var isMaximized = WindowState == WindowState.Maximized;
        var minimizeText = viewModel.Texts["WindowMinimize"];
        var maximizeText = viewModel.Texts[isMaximized ? "WindowRestore" : "WindowMaximize"];
        var closeText = viewModel.Texts["WindowClose"];
        MaximizeWindowGlyph.Text = isMaximized ? "\uE923" : "\uE922";
        MinimizeWindowButton.ToolTip = minimizeText;
        MaximizeWindowButton.ToolTip = maximizeText;
        CloseWindowButton.ToolTip = closeText;
        System.Windows.Automation.AutomationProperties.SetName(MinimizeWindowButton, minimizeText);
        System.Windows.Automation.AutomationProperties.SetName(MaximizeWindowButton, maximizeText);
        System.Windows.Automation.AutomationProperties.SetName(CloseWindowButton, closeText);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel viewModel) UpdateWindowCaptionButtons(viewModel);
    }

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void WindowMaximizeRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateLocalizedColumnHeaders(MainViewModel viewModel)
    {
        ApplicationsDataGrid.Columns[2].Header = viewModel.Texts["HeaderApplication"];
        ApplicationsDataGrid.Columns[3].Header = viewModel.Texts["HeaderPublisher"];
        ApplicationsDataGrid.Columns[4].Header = viewModel.Texts["HeaderVersion"];
        ApplicationsDataGrid.Columns[5].Header = viewModel.Texts["HeaderSize"];
        LeftoversDataGrid.Columns[0].Header = viewModel.Texts["HeaderPath"];
        LeftoversDataGrid.Columns[1].Header = viewModel.Texts["HeaderSizeSimple"];
        LeftoversDataGrid.Columns[2].Header = viewModel.Texts["HeaderType"];
        LeftoversDataGrid.Columns[3].Header = viewModel.Texts["HeaderConfidence"];
        LeftoversDataGrid.Columns[4].Header = viewModel.Texts["HeaderReason"];
        DiskEntriesGrid.Columns[0].Header = viewModel.Texts["DiskItemHeader"];
        DiskEntriesGrid.Columns[1].Header = viewModel.Texts["DiskExtensionHeader"];
        DiskEntriesGrid.Columns[2].Header = viewModel.Texts["DiskSizeHeader"];
        DiskEntriesGrid.Columns[3].Header = viewModel.Texts["DiskModifiedHeader"];
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.UpdateSearch(SearchBox.Text);
        }
    }

    private void Applications_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is DataGrid grid)
            viewModel.UpdateSelectedApplications(grid.SelectedItems.OfType<InstalledApplication>());
    }

    private void ApplicationsDataGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not CheckBox)
            current = VisualTreeHelper.GetParent(current);
        if (current is not CheckBox { DataContext: InstalledApplication application }) return;

        e.Handled = true;
        if (grid.SelectedItems.Contains(application)) grid.SelectedItems.Remove(application);
        else grid.SelectedItems.Add(application);
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

    private async void Disk_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshDiskRoots();
        await ShowPageAsync("Disk");
    }

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
        DiskNav.Tag = page == "Disk" ? "Active" : null;
        AnalysisNav.Tag = page == "Analysis" ? "Active" : null;
        SettingsNav.Tag = page == "Settings" ? "Active" : null;
        ApplicationWorkspace.Visibility = page == "Applications" ? Visibility.Visible : Visibility.Collapsed;
        LeftoverWorkspace.Visibility = page == "Leftover review" ? Visibility.Visible : Visibility.Collapsed;
        HistoryWorkspace.Visibility = page == "History" ? Visibility.Visible : Visibility.Collapsed;
        QuarantineWorkspace.Visibility = page == "Quarantine" ? Visibility.Visible : Visibility.Collapsed;
        DiskWorkspace.Visibility = page == "Disk" ? Visibility.Visible : Visibility.Collapsed;
        AnalysisWorkspace.Visibility = page == "Analysis" ? Visibility.Visible : Visibility.Collapsed;
        SettingsWorkspace.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        HeaderScanButton.Visibility = page is "Disk" or "Analysis" ? Visibility.Collapsed : Visibility.Visible;
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

    private void DiskBrowse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = ViewModel.Texts["DiskChooseFolder"], Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        try { ViewModel.SetDiskScanFolder(picker.FolderName); }
        catch (Exception ex) { ShowLocalizedMessage(ViewModel.Texts["Disk"], ViewModel.Texts.Format("ActionFailed", ex.Message), CleanLensDialogTone.Warning); }
    }

    private void DiskRefreshRoots_Click(object sender, RoutedEventArgs e) => ViewModel.RefreshDiskRoots();

    private async void DiskScan_Click(object sender, RoutedEventArgs e)
    {
        using var cancellation = new CancellationTokenSource();
        var progressWindow = CleanLensDialogService.ShowProgress(this, ViewModel.Texts["DiskScan"], ViewModel.Texts["DiskScanWorking"], ViewModel.Texts["Cancel"], cancellation.Cancel);
        var progress = new Progress<DiskScanProgress>(value =>
        {
            ViewModel.UpdateDiskScanProgress(value);
            var message = value.BuildingIndex
                ? ViewModel.Texts["DiskIndexing"]
                : ViewModel.Texts.Format("DiskProgress", value.EntriesVisited.ToString("N0"), FormatBytes(value.BytesMeasured), value.SkippedEntries.ToString("N0"));
            CleanLensDialogService.SetProgressMessage(progressWindow, message);
        });
        try
        {
            await ViewModel.StartDiskScanAsync(cancellation.Token, progress);
            progressWindow.Close();
        }
        catch (OperationCanceledException)
        {
            progressWindow.Close();
        }
        catch (Exception ex)
        {
            progressWindow.Close();
            ShowLocalizedMessage(ViewModel.Texts["DiskScan"], ViewModel.Texts.Format("ActionFailed", ex.Message), CleanLensDialogTone.Warning);
        }
        finally
        {
            if (progressWindow.IsVisible) progressWindow.Close();
        }
    }

    private async void DiskListingMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox || DataContext is not MainViewModel viewModel || comboBox.SelectedIndex is < 0 or > 2) return;
        await viewModel.SetDiskListingModeAsync((DiskListingMode)comboBox.SelectedIndex);
    }

    private async void DiskSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || sender is not TextBox box) return;
        diskSearchDebounce?.Cancel();
        diskSearchDebounce?.Dispose();
        diskSearchDebounce = new CancellationTokenSource();
        var token = diskSearchDebounce.Token;
        try
        {
            await Task.Delay(180, token);
            await viewModel.UpdateDiskSearchAsync(box.Text);
        }
        catch (OperationCanceledException) { }
    }

    private async void DiskEntry_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: DiskScanEntry entry } && DataContext is MainViewModel viewModel)
            await viewModel.NavigateDiskEntryAsync(entry);
    }

    private async void DiskOpenSelectedFolder_Click(object sender, RoutedEventArgs e) => await ViewModel.OpenSelectedDiskFolderAsync();

    private void DiskTypeView_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetDiskSidePanelMode(0);
        UpdateDiskSidePanelButtons(ViewModel);
    }

    private void DiskTreemapView_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetDiskSidePanelMode(1);
        UpdateDiskSidePanelButtons(ViewModel);
    }

    private void UpdateDiskSidePanelButtons(MainViewModel viewModel)
    {
        var typesSelected = viewModel.DiskSidePanelMode == 0;
        DiskTypeViewButton.Background = typesSelected ? (Brush)FindResource("AccentSoft") : Brushes.White;
        DiskTypeViewButton.BorderBrush = typesSelected ? (Brush)FindResource("BlueBrush") : (Brush)FindResource("LineBrush");
        DiskTypeViewButton.Foreground = typesSelected ? (Brush)FindResource("BlueBrush") : (Brush)FindResource("InkBrush");
        DiskTreemapViewButton.Background = typesSelected ? Brushes.White : (Brush)FindResource("AccentSoft");
        DiskTreemapViewButton.BorderBrush = typesSelected ? (Brush)FindResource("LineBrush") : (Brush)FindResource("BlueBrush");
        DiskTreemapViewButton.Foreground = typesSelected ? (Brush)FindResource("InkBrush") : (Brush)FindResource("BlueBrush");
    }

    private void DiskTreemap_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is FrameworkElement surface)
            viewModel.ResizeDiskTreemap(surface.ActualWidth, surface.ActualHeight);
    }

    private async void DiskTreemapBlock_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is FrameworkElement { DataContext: DiskTreemapBlock block })
            await viewModel.NavigateDiskTreemapBlockAsync(block);
    }

    private void DiskEntries_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is DataGrid grid)
            viewModel.UpdateDiskSelectionCount(grid.SelectedItems.Count);
    }

    private async void DiskBack_Click(object sender, RoutedEventArgs e) => await ViewModel.GoUpDiskFolderAsync();
    private async void DiskHistoryBack_Click(object sender, RoutedEventArgs e) => await ViewModel.GoBackDiskFolderAsync();
    private async void DiskPreviousPage_Click(object sender, RoutedEventArgs e) => await ViewModel.PageDiskEntriesAsync(-1);
    private async void DiskNextPage_Click(object sender, RoutedEventArgs e) => await ViewModel.PageDiskEntriesAsync(1);

    private async void DiskDeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.SafetyAccepted)
        {
            ShowLocalizedMessage(ViewModel.Texts["SafetyNoticeTitle"], ViewModel.Texts["SafetyRequired"], CleanLensDialogTone.Warning);
            return;
        }
        var selection = DiskEntriesGrid.SelectedItems.OfType<DiskScanEntry>().ToArray();
        if (selection.Length == 0)
        {
            ShowLocalizedMessage(ViewModel.Texts["Disk"], ViewModel.Texts["DiskSelectItems"], CleanLensDialogTone.Warning);
            return;
        }
        var selected = selection.Where(item => !selection.Any(parent => !parent.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) && DeletionPathPolicy.IsPathWithin(item.Path, parent.Path))).ToArray();
        var totalBytes = selected.Where(item => item.SizeBytes is not null).Aggregate(0L, (total, item) =>
        {
            var bytes = item.SizeBytes.GetValueOrDefault();
            return bytes > long.MaxValue - total ? long.MaxValue : total + bytes;
        });
        var paths = string.Join(Environment.NewLine, selected.Select(item => item.Path));
        var partial = selected.Any(item => item.IsIncomplete || item.SizeBytes is null);
        var confirmKey = partial ? "DiskDeleteConfirmPartial" : "DiskDeleteConfirm";
        var confirm = ViewModel.Texts.Format(confirmKey, selected.Length, FormatBytes(totalBytes), paths);
        if (!CleanLensDialogService.Confirm(this, ViewModel.Texts["DiskDeleteTitle"], confirm, ViewModel.Texts["DiskDeleteSelectedText"], ViewModel.Texts["Cancel"], danger: true)) return;

        try
        {
            var report = await ViewModel.DeleteDiskEntriesAsync(selected);
            if (report.Failures.Count == 0)
            {
                ShowLocalizedMessage(ViewModel.Texts["DiskDeleteTitle"], ViewModel.Texts.Format("DiskDeleteComplete", report.DeletedItems));
            }
            else
            {
                var failureDetails = string.Join(Environment.NewLine + Environment.NewLine, report.Failures.Select(failure => $"{failure.Path}\n{failure.Error}"));
                ShowLocalizedMessage(ViewModel.Texts["DiskDeleteTitle"], ViewModel.Texts.Format("DiskDeletePartial", report.DeletedItems, report.Failures.Count, failureDetails), CleanLensDialogTone.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowLocalizedMessage(ViewModel.Texts["DiskDeleteTitle"], ViewModel.Texts.Format("ActionFailed", ex.Message), CleanLensDialogTone.Warning);
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
        var applications = ViewModel.GetSelectedApplications();
        if (applications.Count == 0)
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
        var isMultiApp = applications.Count > 1;
        using var cancellation = new CancellationTokenSource();
        var progress = CleanLensDialogService.ShowProgress(this, ViewModel.Texts["ManualDeleteTitle"], isMultiApp ? ViewModel.Texts["ManualDeleteMultiScanning"] : ViewModel.Texts["ManualDeleteScanning"], ViewModel.Texts["Cancel"], cancellation.Cancel);
        var scanProgress = new Progress<int>(count => CleanLensDialogService.SetProgressMessage(progress, ViewModel.Texts.Format("ManualDeleteScanProgress", count)));
        try
        {
            var service = new ManualDeleteService();
            var additionalRoots = ViewModel.GetEnabledManualSearchRoots();
            var defaultRoots = ViewModel.GetEnabledDefaultManualSearchRoots();
            if (isMultiApp)
            {
                candidates = await service.FindExactNameMatchesAsync(
                    applications,
                    cancellation.Token,
                    scanProgress,
                    additionalRoots,
                    defaultRoots,
                    ViewModel.IncludeRegisteredInstallLocations,
                    ViewModel.IncludeSteamLocations);
            }
            else
            {
                var application = applications[0];
                candidates = (await service.FindExactNameMatchesAsync(
                    application,
                    cancellation.Token,
                    scanProgress,
                    additionalRoots,
                    defaultRoots,
                    ViewModel.IncludeRegisteredInstallLocations,
                    ViewModel.IncludeSteamLocations)).Select(candidate => candidate with { Application = application }).ToArray();
            }
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
            isMultiApp ? ViewModel.Texts.Format("ManualDeleteMultipleIntro", applications.Count) : ViewModel.Texts["ManualDeleteIntro"],
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

        var selectedSet = selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedCandidates = candidates.Where(candidate => selectedSet.Contains(candidate.Path))
            .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();

        if (selection.Action == ManualDeleteSelectionAction.Quarantine)
        {
            var movedCount = 0;
            var failures = new List<string>();
            foreach (var candidate in selectedCandidates)
            {
                try
                {
                    await ViewModel.QuarantineManualDeleteCandidateAsync(candidate.Application ?? applications[0], candidate.Path);
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

        var confirmationPaths = selectedCandidates.Select(candidate =>
        {
            var owner = candidate.Application ?? applications[0];
            return isMultiApp ? $"{candidate.Path}  [{owner.Name}]" : candidate.Path;
        }).ToArray();
        var confirmationText = isMultiApp
            ? ViewModel.Texts.Format("ManualDeleteConfirmMultiple", confirmationPaths.Length, selectedCandidates.Select(candidate => candidate.Application?.Id ?? applications[0].Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), string.Join(Environment.NewLine, confirmationPaths))
            : ViewModel.Texts.Format("ManualDeleteConfirm", selectedPaths.Count, string.Join(Environment.NewLine, confirmationPaths));
        if (!CleanLensDialogService.Confirm(this, ViewModel.Texts["ManualDeleteTitle"], confirmationText, ViewModel.ManualDeleteButtonText, ViewModel.Texts["Cancel"], danger: true))
        {
            return;
        }
        var deletedCount = 0;
        var deletionFailures = new List<string>();
        foreach (var group in selectedCandidates.GroupBy(candidate => (candidate.Application ?? applications[0]).Id, StringComparer.OrdinalIgnoreCase))
        {
            var candidatesForApp = group.ToArray();
            var app = candidatesForApp[0].Application ?? applications[0];
            try
            {
                await new ManualDeleteService().DeleteSelectedAsync(
                    app,
                    candidatesForApp.Select(candidate => candidate.Path),
                    additionalRoots: ViewModel.GetEnabledManualSearchRoots(),
                    enabledDefaultRoots: ViewModel.GetEnabledDefaultManualSearchRoots(),
                    includeRegisteredLocations: ViewModel.IncludeRegisteredInstallLocations,
                    includeSteamLocations: ViewModel.IncludeSteamLocations);
                deletedCount += candidatesForApp.Length;
            }
            catch (Exception ex)
            {
                deletionFailures.AddRange(candidatesForApp.Select(candidate => $"{candidate.Path}\n{ex.Message}"));
            }
        }
        if (deletionFailures.Count == 0)
        {
            ViewModel.StatusText = ViewModel.Texts["ManualDeleteDone"];
            ShowLocalizedMessage(ViewModel.Texts["AppName"], ViewModel.Texts.Format("ManualDeleteDoneCount", deletedCount));
        }
        else
        {
            ShowLocalizedMessage(ViewModel.Texts["ManualDeleteTitle"], ViewModel.Texts.Format("ManualDeleteDeletePartial", deletedCount, deletionFailures.Count, string.Join(Environment.NewLine + Environment.NewLine, deletionFailures)), CleanLensDialogTone.Warning);
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
