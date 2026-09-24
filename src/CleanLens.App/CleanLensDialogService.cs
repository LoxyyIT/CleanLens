using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CleanLens.Windows;

namespace CleanLens.App;

internal enum CleanLensDialogTone
{
    Information,
    Warning,
    Danger
}

internal static class CleanLensDialogService
{
    private static readonly Brush Ink = Brush("#14243A");
    private static readonly Brush Muted = Brush("#61758D");
    private static readonly Brush Line = Brush("#DFE8F1");
    private static readonly Brush Blue = Brush("#396BE8");
    private static readonly Brush Red = Brush("#B4233D");

    public static void ShowMessage(Window owner, string title, string message, CleanLensDialogTone tone = CleanLensDialogTone.Information, string okText = "OK")
    {
        var dialog = CreateShell(owner, title, tone, 560, 270);
        SetBody(dialog, CreateScrollableMessage(message));
        AddFooterButton(dialog, okText, isPrimary: true, isDanger: false, () => dialog.DialogResult = true);
        dialog.ShowDialog();
    }

    public static bool Confirm(Window owner, string title, string message, string acceptText, string cancelText, bool danger = false)
    {
        var dialog = CreateShell(owner, title, danger ? CleanLensDialogTone.Danger : CleanLensDialogTone.Warning, 600, 370);
        SetBody(dialog, CreateScrollableMessage(message));
        AddFooterButton(dialog, cancelText, isPrimary: false, isDanger: false, () => dialog.DialogResult = false);
        AddFooterButton(dialog, acceptText, isPrimary: true, isDanger: danger, () => dialog.DialogResult = true);
        return dialog.ShowDialog() == true;
    }

    public static IReadOnlyList<string>? SelectManualDeletePaths(Window owner, string title, string intro, string candidateCount, string deleteText, string cancelText, IReadOnlyList<ManualDeleteCandidate> candidates)
    {
        var dialog = CreateShell(owner, title, CleanLensDialogTone.Danger, 800, 700);
        var body = new DockPanel();
        var introBlock = new TextBlock
        {
            Text = intro,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Muted,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 14)
        };
        DockPanel.SetDock(introBlock, Dock.Top);
        body.Children.Add(introBlock);
        var summary = new TextBlock
        {
            Text = string.Format(candidateCount, candidates.Count),
            Foreground = Ink,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(summary, Dock.Top);
        body.Children.Add(summary);
        var list = new StackPanel();
        var checkboxes = new List<CheckBox>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var path = new TextBlock
            {
                Text = candidate.Path,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Ink,
                FontSize = 12,
                ToolTip = candidate.Path
            };
            var source = new TextBlock
            {
                Text = candidate.Source,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Muted,
                FontSize = 10,
                Margin = new Thickness(0, 4, 0, 0)
            };
            var description = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
            description.Children.Add(path);
            description.Children.Add(source);
            var checkBox = new CheckBox
            {
                IsChecked = false,
                Content = description,
                Tag = candidate.Path,
                VerticalContentAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 0, 8),
                ToolTip = candidate.Path
            };
            checkboxes.Add(checkBox);
            var row = new Border
            {
                Background = Brushes.White,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 7),
                Child = checkBox
            };
            list.Children.Add(row);
        }
        body.Children.Add(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = true
        });
        SetBody(dialog, body);
        AddFooterButton(dialog, cancelText, isPrimary: false, isDanger: false, () => dialog.DialogResult = false);
        AddFooterButton(dialog, deleteText, isPrimary: true, isDanger: true, () => dialog.DialogResult = true);
        if (dialog.ShowDialog() != true)
        {
            return null;
        }
        return checkboxes.Where(item => item.IsChecked == true).Select(item => (string)item.Tag).ToArray();
    }

    public static Window ShowProgress(Window owner, string title, string message, string cancelText, Action cancel)
    {
        var dialog = CreateShell(owner, title, CleanLensDialogTone.Information, 500, 240);
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var status = CreateMessageBody(message);
        body.Children.Add(status);
        var progress = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 7,
            Margin = new Thickness(0, 20, 0, 0),
            Background = Brush("#E9EFF6"),
            Foreground = Blue,
            BorderThickness = new Thickness(0)
        };
        body.Children.Add(progress);
        SetBody(dialog, body);
        var parts = (DialogParts)dialog.Tag;
        dialog.Tag = parts with { Status = status };
        AddFooterButton(dialog, cancelText, isPrimary: false, isDanger: false, cancel);
        dialog.Closed += (_, _) => owner.IsEnabled = true;
        dialog.Closing += (_, _) => cancel();
        owner.IsEnabled = false;
        dialog.Show();
        dialog.Activate();
        return dialog;
    }

    public static void SetProgressMessage(Window dialog, string message)
    {
        if (dialog.Tag is DialogParts { Status: not null } parts)
        {
            parts.Status.Text = message;
        }
    }

    private static Window CreateShell(Window owner, string title, CleanLensDialogTone tone, double width, double height)
    {
        var dialog = new Window
        {
            Owner = owner,
            Title = title,
            Width = width,
            Height = height,
            MinWidth = width,
            MinHeight = height,
            MaxWidth = width,
            MaxHeight = Math.Max(height, owner.ActualHeight - 48),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = Ink
        };
        var surface = new Border
        {
            Background = Brushes.White,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.17, Color = Color.FromRgb(14, 31, 52) }
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Background = Brush("#FBFCFE"), Margin = new Thickness(1) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        var accent = tone == CleanLensDialogTone.Danger ? Red : tone == CleanLensDialogTone.Warning ? Brush("#D08B18") : Blue;
        var glyph = tone == CleanLensDialogTone.Danger ? "!" : tone == CleanLensDialogTone.Warning ? "!" : "i";
        var icon = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(11),
            Background = Brush(tone == CleanLensDialogTone.Danger ? "#FFF0F2" : tone == CleanLensDialogTone.Warning ? "#FFF7E8" : "#EEF4FF"),
            Child = new TextBlock { Text = glyph, Foreground = accent, FontSize = 17, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        icon.Margin = new Thickness(18, 0, 0, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(icon);
        var titleBlock = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        titleBlock.Margin = new Thickness(14, 0, 8, 0);
        Grid.SetColumn(titleBlock, 1);
        header.Children.Add(titleBlock);
        var close = new Button
        {
            Width = 36,
            Height = 36,
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(0),
            Background = Brush("#B9364B"),
            BorderBrush = Brush("#D46C7C"),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            FocusVisualStyle = null,
            ToolTip = "Close",
            Template = ButtonTemplate(),
            Content = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 4,4 L 12,12 M 12,4 L 4,12"),
                Stroke = Brushes.White,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        close.Padding = new Thickness(0);
        AutomationProperties.SetName(close, "Close");
        close.Click += (_, _) => dialog.Close();
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) dialog.DragMove(); };
        Grid.SetRow(header, 0);
        layout.Children.Add(header);
        var separator = new Border { Height = 1, Background = Line, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetRow(separator, 0);
        layout.Children.Add(separator);
        var bodyHost = new Border { Padding = new Thickness(22, 18, 22, 18), ClipToBounds = true };
        Grid.SetRow(bodyHost, 1);
        layout.Children.Add(bodyHost);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 10, 20, 16) };
        var footerHost = new Border { Background = Brush("#FBFCFE"), BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
        Grid.SetRow(footerHost, 2);
        layout.Children.Add(footerHost);
        surface.Child = layout;
        dialog.Content = surface;
        dialog.Tag = new DialogParts(bodyHost, footer);
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { dialog.Close(); e.Handled = true; } };
        return dialog;
    }

    private static void SetBody(Window dialog, UIElement body)
    {
        var parts = (DialogParts)dialog.Tag;
        parts.Body.Child = body;
    }

    private static UIElement CreateScrollableMessage(string message) => new ScrollViewer
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = CreateMessageBody(message)
    };

    private static TextBlock CreateMessageBody(string message) => new()
    {
        Text = message,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Muted,
        FontSize = 13,
        LineHeight = 20,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static void AddFooterButton(Window dialog, string text, bool isPrimary, bool isDanger, Action action)
    {
        var parts = (DialogParts)dialog.Tag;
        var button = CreateButton(text, isPrimary, isDanger);
        button.Click += (_, _) => action();
        parts.Footer.Children.Add(button);
    }

    private static Button CreateButton(string text, bool isPrimary, bool isDanger)
    {
        var background = isDanger ? Red : isPrimary ? Blue : Brushes.White;
        var foreground = isPrimary ? Brushes.White : Ink;
        var border = isPrimary ? background : Line;
        var button = new Button
        {
            Content = text,
            MinWidth = 108,
            Height = 40,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(16, 8, 16, 8),
            Background = background,
            Foreground = foreground,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            FocusVisualStyle = null,
            Template = ButtonTemplate()
        };
        return button;
    }

    private static ControlTemplate ButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ButtonSurface";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(content);
        template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.88, "ButtonSurface"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72, "ButtonSurface"));
        template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "ButtonSurface"));
        template.Triggers.Add(disabled);
        return template;
    }

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));

    private sealed record DialogParts(Border Body, StackPanel Footer, TextBlock? Status = null);
}
