using System.Windows;
using System.Windows.Controls;
using CleanLens.Windows;

namespace CleanLens.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += Window_Loaded;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

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

    private async void Applications_Click(object sender, RoutedEventArgs e) => await ShowPageAsync("Applications");

    private async void Overview_Click(object sender, RoutedEventArgs e) => await ShowPageAsync("Overview");

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

    private async Task ShowPageAsync(string page)
    {
        ApplicationWorkspace.Visibility = page is "Overview" or "Applications" ? Visibility.Visible : Visibility.Collapsed;
        LeftoverWorkspace.Visibility = page == "Leftover review" ? Visibility.Visible : Visibility.Collapsed;
        HistoryWorkspace.Visibility = page == "History" ? Visibility.Visible : Visibility.Collapsed;
        QuarantineWorkspace.Visibility = page == "Quarantine" ? Visibility.Visible : Visibility.Collapsed;
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
            MessageBox.Show(this, ex.Message, "CleanLens", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(this, ex.Message, "CleanLens", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var application = ViewModel.SelectedApplication;
        if (application is null)
        {
            MessageBox.Show(this, "Select an application first.", "CleanLens", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ViewModel.SafetyAccepted)
        {
            MessageBox.Show(this, "Accept the safety notice before starting an uninstaller.", "Safety notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var command = application.UninstallCommand;
        if (string.IsNullOrWhiteSpace(command) && !string.IsNullOrWhiteSpace(application.QuietUninstallCommand))
        {
            MessageBox.Show(this, "Windows lists only a quiet uninstall command for this application. CleanLens will not start a silent uninstall.", "Review unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(command))
        {
            MessageBox.Show(this, "Windows has no registered uninstaller command for this application.", "CleanLens", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this, $"CleanLens will start the registered uninstaller for {application.Name}.\n\nCommand:\n{command}\n\nThis command comes from local application metadata and may remove data outside CleanLens. Review its executable and arguments before continuing.", "Review official uninstaller", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }
        try
        {
            var process = new UninstallerLauncher().Start(command);
            await ViewModel.RecordUninstallAsync(application, process is null ? "The registered uninstaller was launched by Windows." : $"Started process {process.Id}.");
            ViewModel.StatusText = "Official uninstaller launched. When it finishes, scan applications and then review associated leftovers.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not start the registered uninstaller.\n\n{ex.Message}", "CleanLens", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && viewModel.SafetyAccepted)
        {
            _ = viewModel.ScanCommand.ExecuteAsync(null);
        }
    }
}
