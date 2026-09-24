using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CleanLens.Core.Models;
using System.ComponentModel;
using System.Diagnostics;
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

    private async void ManualDelete_Click(object sender, RoutedEventArgs e)
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

        IReadOnlyList<string> paths;
        try
        {
            ViewModel.StatusText = ViewModel.Texts["ManualDeleteIntro"];
            paths = await new ManualDeleteService().FindExactNameMatchesAsync(application);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ViewModel.Texts.Format("ActionFailed", ex.Message), ViewModel.Texts["AppName"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (paths.Count == 0)
        {
            MessageBox.Show(this, ViewModel.Texts["ManualDeleteEmpty"], ViewModel.Texts["ManualDeleteTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var choices = new List<CheckBox>();
        var rows = new StackPanel { Margin = new Thickness(16, 0, 16, 12) };
        foreach (var path in paths)
        {
            var checkbox = new CheckBox { IsChecked = false, Margin = new Thickness(0, 9, 0, 9), FontSize = 12 };
            checkbox.Content = new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, MaxWidth = 700, ToolTip = path };
            choices.Add(checkbox);
            rows.Children.Add(checkbox);
            rows.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(222, 231, 240)) });
        }

        var dialog = new Window
        {
            Owner = this,
            Title = ViewModel.Texts["ManualDeleteTitle"],
            Width = 820,
            Height = 650,
            MinWidth = 600,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(243, 247, 251)),
            ResizeMode = ResizeMode.CanResize
        };
        var layout = new DockPanel { LastChildFill = true };
        var intro = new TextBlock
        {
            Text = ViewModel.Texts["ManualDeleteIntro"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 16, 18, 13),
            Foreground = new SolidColorBrush(Color.FromRgb(82, 101, 123)),
            FontSize = 13
        };
        DockPanel.SetDock(intro, Dock.Top);
        layout.Children.Add(intro);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
        var cancel = new Button { Content = ViewModel.Texts["Cancel"], Padding = new Thickness(18, 10, 18, 10), MinWidth = 100, Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var delete = new Button
        {
            Content = ViewModel.Texts["ManualDelete"],
            Padding = new Thickness(18, 10, 18, 10),
            MinWidth = 150,
            Background = new SolidColorBrush(Color.FromRgb(180, 35, 61)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        delete.Click += (_, _) => dialog.DialogResult = true;
        footer.Children.Add(cancel);
        footer.Children.Add(delete);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        var card = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(222, 231, 240)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(16, 0, 16, 0),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rows }
        };
        layout.Children.Add(card);
        dialog.Content = layout;
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var selectedPaths = choices.Where(choice => choice.IsChecked == true).Select(choice => ((TextBlock)choice.Content).Text).ToArray();
        if (selectedPaths.Length == 0)
        {
            return;
        }
        var pathList = string.Join(Environment.NewLine, selectedPaths);
        var confirmation = MessageBox.Show(
            this,
            ViewModel.Texts.Format("ManualDeleteConfirm", selectedPaths.Length, pathList),
            ViewModel.Texts["ManualDeleteTitle"],
            MessageBoxButton.YesNo,
            MessageBoxImage.Stop,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            await new ManualDeleteService().DeleteSelectedAsync(application, selectedPaths);
            ViewModel.StatusText = ViewModel.Texts["ManualDeleteDone"];
            MessageBox.Show(this, ViewModel.Texts["ManualDeleteDone"], ViewModel.Texts["AppName"], MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ViewModel.Texts.Format("ActionFailed", ex.Message), ViewModel.Texts["AppName"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplicationsNav.Tag = "Active";
        if (DataContext is MainViewModel viewModel && viewModel.SafetyAccepted)
        {
            _ = viewModel.ScanCommand.ExecuteAsync(null);
        }
    }
}
