using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using CleanLens.Windows;

namespace CleanLens.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += MainWindow_DataContextChanged;
        Loaded += Window_Loaded;
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

    private async void Settings_Click(object sender, RoutedEventArgs e) => await ShowPageAsync("Settings");

    private async Task ShowPageAsync(string page)
    {
        var overviewActive = page == "Overview";
        var applicationsActive = page == "Applications";
        OverviewNav.Tag = overviewActive ? "Active" : null;
        ApplicationsNav.Tag = applicationsActive ? "Active" : null;
        LeftoversNav.Tag = page == "Leftover review" ? "Active" : null;
        HistoryNav.Tag = page == "History" ? "Active" : null;
        QuarantineNav.Tag = page == "Quarantine" ? "Active" : null;
        SettingsNav.Tag = page == "Settings" ? "Active" : null;
        ApplicationWorkspace.Visibility = page is "Overview" or "Applications" ? Visibility.Visible : Visibility.Collapsed;
        LeftoverWorkspace.Visibility = page == "Leftover review" ? Visibility.Visible : Visibility.Collapsed;
        HistoryWorkspace.Visibility = page == "History" ? Visibility.Visible : Visibility.Collapsed;
        QuarantineWorkspace.Visibility = page == "Quarantine" ? Visibility.Visible : Visibility.Collapsed;
        SettingsWorkspace.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
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
            MessageBox.Show(this, ViewModel.Texts.Format("ActionFailed", ex.Message), ViewModel.Texts["AppName"], MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(this, ViewModel.Texts.Format("ActionFailed", ex.Message), ViewModel.Texts["AppName"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var application = ViewModel.SelectedApplication;
        if (application is null)
        {
            MessageBox.Show(this, ViewModel.Texts["SelectFirst"], ViewModel.Texts["AppName"], MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ViewModel.SafetyAccepted)
        {
            MessageBox.Show(this, ViewModel.Texts["SafetyRequired"], ViewModel.Texts["SafetyNoticeTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var command = application.UninstallCommand;
        if (string.IsNullOrWhiteSpace(command) && !string.IsNullOrWhiteSpace(application.QuietUninstallCommand))
        {
            MessageBox.Show(this, ViewModel.Texts["QuietOnly"], ViewModel.Texts["ReviewUnavailableTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(command))
        {
            MessageBox.Show(this, ViewModel.Texts["NoUninstaller"], ViewModel.Texts["AppName"], MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this, ViewModel.Texts.Format("ReviewUninstallerMessage", application.Name, command), ViewModel.Texts["ReviewUninstallerTitle"], MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
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
            MessageBox.Show(this, ViewModel.Texts.Format("UninstallerError", ex.Message), ViewModel.Texts["UninstallerErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        OverviewNav.Tag = "Active";
        if (DataContext is MainViewModel viewModel && viewModel.SafetyAccepted)
        {
            _ = viewModel.ScanCommand.ExecuteAsync(null);
        }
    }
}
