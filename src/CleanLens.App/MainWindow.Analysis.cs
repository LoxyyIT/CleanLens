using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using CleanLens.Data;
using CleanLens.Windows;
using Microsoft.Win32;

namespace CleanLens.App;

public partial class MainWindow
{
    private int selectedAnalysisTab;

    private void SelectAnalysisTab(int index)
    {
        selectedAnalysisTab = Math.Clamp(index, 0, 2);
        DuplicateAnalysisPanel.Visibility = selectedAnalysisTab == 0 ? Visibility.Visible : Visibility.Collapsed;
        SnapshotsAnalysisPanel.Visibility = selectedAnalysisTab == 1 ? Visibility.Visible : Visibility.Collapsed;
        PlansAnalysisPanel.Visibility = selectedAnalysisTab == 2 ? Visibility.Visible : Visibility.Collapsed;
        SetAnalysisTabButton(DuplicateTabButton, selectedAnalysisTab == 0);
        SetAnalysisTabButton(SnapshotsTabButton, selectedAnalysisTab == 1);
        SetAnalysisTabButton(PlansTabButton, selectedAnalysisTab == 2);
    }

    private void SetAnalysisTabButton(Button button, bool selected)
    {
        button.Background = selected ? (System.Windows.Media.Brush)FindResource("AccentSoft") : System.Windows.Media.Brushes.White;
        button.BorderBrush = selected ? System.Windows.Media.Brushes.SteelBlue : (System.Windows.Media.Brush)FindResource("LineBrush");
        button.Foreground = selected ? (System.Windows.Media.Brush)FindResource("BlueBrush") : (System.Windows.Media.Brush)FindResource("InkBrush");
        button.FontWeight = selected ? FontWeights.Bold : FontWeights.SemiBold;
    }

    private void AnalysisDuplicatesTab_Click(object sender, RoutedEventArgs e) => SelectAnalysisTab(0);

    private void AnalysisSnapshotsTab_Click(object sender, RoutedEventArgs e) => SelectAnalysisTab(1);

    private void AnalysisPlansTab_Click(object sender, RoutedEventArgs e) => SelectAnalysisTab(2);

    private async void Analysis_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshDiskSnapshots();
        ViewModel.RefreshCleanupPlans();
        await ShowPageAsync("Analysis");
    }

    private async void DuplicateSize_Click(object sender, RoutedEventArgs e) => await FindDuplicatesAsync(false);

    private async void DuplicateHash_Click(object sender, RoutedEventArgs e) => await FindDuplicatesAsync(true);

    private void DuplicatesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.UpdateDuplicateSelectionCount(DuplicatesGrid.SelectedItems.Count);
    }

    private async Task FindDuplicatesAsync(bool verifyHashes)
    {
        using var cancellation = new CancellationTokenSource();
        var progress = CleanLensDialogService.ShowProgress(this, ViewModel.Texts["DuplicatesTab"], ViewModel.Texts["DuplicateNotice"], ViewModel.Texts["Cancel"], cancellation.Cancel);
        var reporter = new Progress<DuplicateScanProgress>(value => CleanLensDialogService.SetProgressMessage(progress,
            $"{value.Candidates:N0} candidates · {value.FilesHashed:N0} hashed · {DiskSizeFormatter.Format(value.BytesHashed)} read"));
        try { await ViewModel.FindDuplicatesAsync(verifyHashes, reporter, cancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowLocalizedMessage(ViewModel.Texts["DuplicatesTab"], ex.Message, CleanLensDialogTone.Warning); }
        finally { progress.Close(); }
    }

    private async void DuplicateDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = DuplicatesGrid.SelectedItems.OfType<DuplicateFile>().ToArray();
        if (selected.Length == 0) { ShowLocalizedMessage(ViewModel.Texts["DuplicatesTab"], ViewModel.Texts["DiskSelectItems"]); return; }
        if (selected.Any(file => !file.HashVerified)) { ShowLocalizedMessage(ViewModel.Texts["DuplicatesTab"], ViewModel.Texts["DuplicateNotice"], CleanLensDialogTone.Warning); return; }
        if (selected.GroupBy(file => file.GroupKey).Any(group => ViewModel.DuplicateFiles.Count(file => file.GroupKey == group.Key) <= group.Count()))
        {
            ShowLocalizedMessage(ViewModel.Texts["DuplicatesTab"], "Keep at least one file from every duplicate group.", CleanLensDialogTone.Warning);
            return;
        }
        try
        {
            foreach (var file in selected)
            {
                var info = new FileInfo(file.Path);
                if (!info.Exists || info.Length != file.SizeBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"File changed: {file.Path}");
                using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.GroupKey, StringComparison.Ordinal))
                    throw new IOException($"Content changed: {file.Path}");
            }
            var entries = await ViewModel.ResolveDiskPathsAsync(selected.Select(file => file.Path));
            if (entries.Count != selected.Length) throw new InvalidOperationException("Scan again: one or more selected files are no longer indexed.");
            var paths = string.Join(Environment.NewLine, entries.Select(item => item.Path));
            if (!CleanLensDialogService.Confirm(this, ViewModel.Texts["DiskDeleteTitle"],
                $"{ViewModel.Texts["DuplicateNotice"]}\n\n{paths}\n\n{ViewModel.Texts["DiskDeleteSelectedText"]}?",
                ViewModel.Texts["DiskDeleteSelectedText"], ViewModel.Texts["Cancel"], danger: true)) return;
            var report = await ViewModel.DeleteDiskEntriesAsync(entries);
            ViewModel.DuplicateFiles.Clear();
            ShowLocalizedMessage(ViewModel.Texts["DuplicatesTab"], report.Failures.Count == 0
                ? ViewModel.Texts.Format("DiskDeleteComplete", report.DeletedItems)
                : ViewModel.Texts.Format("DiskDeletePartial", report.DeletedItems, report.Failures.Count, string.Join("\n", report.Failures.Select(failure => failure.Path + ": " + failure.Error))),
                report.Failures.Count == 0 ? CleanLensDialogTone.Information : CleanLensDialogTone.Warning);
        }
        catch (Exception ex) { ShowLocalizedMessage(ViewModel.Texts["DuplicatesTab"], ex.Message, CleanLensDialogTone.Warning); }
    }

    private async void SaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.SaveDiskSnapshotAsync(); }
        catch (Exception ex) { ShowLocalizedMessage(ViewModel.Texts["SnapshotsTab"], ex.Message, CleanLensDialogTone.Warning); }
    }

    private async void CompareSnapshots_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.CompareDiskSnapshotsAsync(); }
        catch (Exception ex) { ShowLocalizedMessage(ViewModel.Texts["SnapshotsTab"], ex.Message, CleanLensDialogTone.Warning); }
    }

    private async void PreviousSnapshotPage_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.PageSnapshotChangesAsync(-1); }
        catch (Exception ex) { ShowLocalizedMessage(ViewModel.Texts["SnapshotsTab"], ex.Message, CleanLensDialogTone.Warning); }
    }

    private async void NextSnapshotPage_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.PageSnapshotChangesAsync(1); }
        catch (Exception ex) { ShowLocalizedMessage(ViewModel.Texts["SnapshotsTab"], ex.Message, CleanLensDialogTone.Warning); }
    }

    private void DeleteSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedSnapshotBefore is null) return;
        if (!CleanLensDialogService.Confirm(this, ViewModel.Texts["DeleteSnapshot"], ViewModel.SelectedSnapshotBefore.DisplayName,
            ViewModel.Texts["DeleteSnapshot"], ViewModel.Texts["Cancel"])) return;
        try { ViewModel.DeleteSelectedDiskSnapshot(); }
        catch (Exception ex) { ShowLocalizedMessage(ViewModel.Texts["SnapshotsTab"], ex.Message, CleanLensDialogTone.Warning); }
    }

    private void SavePlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entries = DiskEntriesGrid.SelectedItems.OfType<DiskScanEntry>().ToArray();
            var plan = ViewModel.SaveCleanupPlan($"{ViewModel.Texts["PlansTab"]} · {DateTime.Now:g}", entries);
            ViewModel.SelectedCleanupPlan = ViewModel.CleanupPlans.FirstOrDefault(item => item.Id == plan.Id);
            _ = ShowPageAsync("Analysis");
            SelectAnalysisTab(2);
        }
        catch (Exception ex) { ShowLocalizedMessage(ViewModel.Texts["PlansTab"], ex.Message, CleanLensDialogTone.Warning); }
    }

    private async void ApplyPlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entries = await ViewModel.ResolveSelectedCleanupPlanAsync();
            var details = string.Join(Environment.NewLine, entries.Select(entry => $"{entry.Path} · {entry.SizeText}"));
            if (!CleanLensDialogService.Confirm(this, ViewModel.Texts["ApplyPlan"],
                $"{ViewModel.Texts["PlanNotice"]}\n\n{details}\n\n{ViewModel.Texts["DiskDeleteSelectedText"]}?",
                ViewModel.Texts["DiskDeleteSelectedText"], ViewModel.Texts["Cancel"], danger: true)) return;
            var report = await ViewModel.DeleteDiskEntriesAsync(entries);
            if (report.Failures.Count == 0 && ViewModel.SelectedCleanupPlan is not null) ViewModel.DeleteSelectedCleanupPlan();
            ShowLocalizedMessage(ViewModel.Texts["PlansTab"], report.Failures.Count == 0
                ? ViewModel.Texts.Format("DiskDeleteComplete", report.DeletedItems)
                : ViewModel.Texts.Format("DiskDeletePartial", report.DeletedItems, report.Failures.Count, string.Join("\n", report.Failures.Select(failure => failure.Path + ": " + failure.Error))),
                report.Failures.Count == 0 ? CleanLensDialogTone.Information : CleanLensDialogTone.Warning);
        }
        catch (Exception ex) { ShowLocalizedMessage(ViewModel.Texts["PlansTab"], ex.Message, CleanLensDialogTone.Warning); }
    }

    private void DeletePlan_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCleanupPlan is null) return;
        if (!CleanLensDialogService.Confirm(this, ViewModel.Texts["DeletePlan"], ViewModel.SelectedCleanupPlan.Name,
            ViewModel.Texts["DeletePlan"], ViewModel.Texts["Cancel"])) return;
        try { ViewModel.DeleteSelectedCleanupPlan(); }
        catch (Exception ex) { ShowLocalizedMessage(ViewModel.Texts["PlansTab"], ex.Message, CleanLensDialogTone.Warning); }
    }

    private async void RestoreMultiple_Click(object sender, RoutedEventArgs e)
    {
        var entries = QuarantineList.SelectedItems.OfType<QuarantineEntry>().ToArray();
        if (entries.Length == 0) return;
        var paths = string.Join(Environment.NewLine, entries.Select(entry => $"{entry.OriginalPath} · {entry.RestoreStatus}"));
        if (!CleanLensDialogService.Confirm(this, ViewModel.Texts["RestoreMultiple"], paths,
            ViewModel.Texts["RestoreMultiple"], ViewModel.Texts["Cancel"])) return;
        var outcomes = await ViewModel.RestoreQuarantineEntriesAsync(entries);
        var failures = outcomes.Where(outcome => outcome.Error is not null).ToArray();
        ShowLocalizedMessage(ViewModel.Texts["RestoreMultiple"], failures.Length == 0
            ? $"{entries.Length} items restored."
            : $"{entries.Length - failures.Length} restored · {failures.Length} failed\n\n" + string.Join("\n", failures.Select(failure => failure.Entry.OriginalPath + ": " + failure.Error)),
            failures.Length == 0 ? CleanLensDialogTone.Information : CleanLensDialogTone.Warning);
    }

    private async void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        var title = ViewModel.PageTitle;
        IReadOnlyList<string> columns;
        IEnumerable<IReadOnlyList<string>> rows;
        if (ApplicationWorkspace.IsVisible)
        {
            columns = ["Application", "Publisher", "Version", "Estimated KB", "Install location", "Source"];
            rows = ViewModel.VisibleApplications.Select(app => (IReadOnlyList<string>)[app.Name, app.Publisher, app.Version, app.EstimatedSizeKilobytes?.ToString() ?? "", app.InstallLocation, app.Source]);
        }
        else if (LeftoverWorkspace.IsVisible)
        {
            columns = ["Path", "Category", "Confidence", "Size", "Reason", "Read only"];
            rows = ViewModel.Leftovers.Select(item => (IReadOnlyList<string>)[item.Path, item.Category.ToString(), item.Confidence.ToString(), item.SizeText, item.Reason, item.IsUserData.ToString()]);
        }
        else if (DiskWorkspace.IsVisible)
        {
            columns = ["Path", "Type", "Size bytes", "Modified", "Incomplete"];
            rows = ViewModel.EnumerateDiskReportEntries().Select(item => (IReadOnlyList<string>)[item.Path, item.IsDirectory ? "Folder" : item.Extension, item.SizeBytes?.ToString() ?? "", item.LastWriteText, item.IsIncomplete.ToString()]);
        }
        else if (AnalysisWorkspace.IsVisible && selectedAnalysisTab == 0)
        {
            columns = ["Path", "Size bytes", "Group", "SHA-256 verified"];
            rows = ViewModel.DuplicateFiles.Select(item => (IReadOnlyList<string>)[item.Path, item.SizeBytes.ToString(), item.GroupKey, item.HashVerified.ToString()]);
        }
        else if (AnalysisWorkspace.IsVisible && selectedAnalysisTab == 1)
        {
            columns = ["Path", "Change", "Before bytes", "After bytes"];
            rows = ViewModel.SnapshotChanges.Select(item => (IReadOnlyList<string>)[item.Path, item.Kind, item.PreviousBytes?.ToString() ?? "", item.CurrentBytes?.ToString() ?? ""]);
        }
        else if (AnalysisWorkspace.IsVisible && selectedAnalysisTab == 2)
        {
            columns = ["Path", "Size bytes", "Folder", "Risk"];
            rows = ViewModel.SelectedCleanupPlan?.Items.Select(item => (IReadOnlyList<string>)[item.Path, item.SizeBytes?.ToString() ?? "", item.IsDirectory.ToString(), item.Risk]) ?? [];
        }
        else if (QuarantineWorkspace.IsVisible)
        {
            columns = ["Application", "Original path", "Quarantine path", "Created", "Restore status"];
            rows = ViewModel.QuarantineEntries.Select(item => (IReadOnlyList<string>)[item.ApplicationName, item.OriginalPath, item.QuarantinePath, item.CreatedAt.ToString("O"), item.RestoreStatus]);
        }
        else if (HistoryWorkspace.IsVisible)
        {
            columns = ["Application", "Operation", "Result", "Created"];
            rows = ViewModel.HistoryEntries.Select(item => (IReadOnlyList<string>)[item.ApplicationName, item.Operation, item.Result, item.CreatedAt.ToString("O")]);
        }
        else if (SettingsWorkspace.IsVisible && ViewModel.SelectedInstallReport is { } monitorReport)
        {
            columns = ["Category", "Entry", "Session", "Incomplete"];
            var summary = monitorReport.DisplayName;
            rows = monitorReport.AddedApplications.Select(item => (IReadOnlyList<string>)["App added", item, summary, monitorReport.IsIncomplete.ToString()])
                .Concat(monitorReport.RemovedApplications.Select(item => (IReadOnlyList<string>)["App removed", item, summary, monitorReport.IsIncomplete.ToString()]))
                .Concat(monitorReport.SystemEntryChanges.Select(item => (IReadOnlyList<string>)["Service/startup", item, summary, monitorReport.IsIncomplete.ToString()]))
                .Concat(monitorReport.FileEvents.Select(item => (IReadOnlyList<string>)["File event", item, summary, monitorReport.IsIncomplete.ToString()]));
        }
        else
        {
            columns = ["Entry"];
            rows = ViewModel.InstallMonitorResults.Select(item => (IReadOnlyList<string>)[item]);
        }
        var picker = new SaveFileDialog { Title = ViewModel.Texts["ExportReport"], Filter = "HTML report (*.html)|*.html|CSV (*.csv)|*.csv", DefaultExt = ".html", FileName = "CleanLens-report" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (DiskWorkspace.IsVisible) await Task.Run(() => LocalReportExporter.Export(picker.FileName, title, columns, rows));
            else LocalReportExporter.Export(picker.FileName, title, columns, rows);
            ShowLocalizedMessage(ViewModel.Texts["ExportReport"], picker.FileName);
        }
        catch (Exception ex) { ShowLocalizedMessage(ViewModel.Texts["ExportReport"], ex.Message, CleanLensDialogTone.Warning); }
    }
}
