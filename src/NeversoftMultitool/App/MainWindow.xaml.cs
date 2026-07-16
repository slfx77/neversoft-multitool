using Windows.Graphics;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NeversoftMultitool;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        Instance = this;
        InitializeComponent();

        // Set window size and constraints
        var appWindow = AppWindow;
        appWindow.Resize(new SizeInt32(1450, 900));

        var presenter = appWindow.Presenter as OverlappedPresenter;
        if (presenter != null)
        {
            presenter.PreferredMinimumWidth = 700;
            presenter.PreferredMinimumHeight = 450;
        }

        // Center the window
        var displayArea = DisplayArea.GetFromWindowId(
            appWindow.Id, DisplayAreaFallback.Nearest);
        if (displayArea != null)
        {
            var centeredPosition = new PointInt32(
                (displayArea.WorkArea.Width - appWindow.Size.Width) / 2,
                (displayArea.WorkArea.Height - appWindow.Size.Height) / 2);
            appWindow.Move(centeredPosition);
        }

        TrySetMicaBackdrop();
        SetupTitleBar();
    }

    public static MainWindow? Instance { get; private set; }

    private void TrySetMicaBackdrop()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            // Mica backdrop applied
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
            // Acrylic fallback
        }
        // No system backdrop supported
    }

    private void SetupTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        UpdateCaptionButtonColors();

        if (Content is FrameworkElement rootElement)
            rootElement.ActualThemeChanged += (s, e) => UpdateCaptionButtonColors();

        // Title bar configured
    }

    private void UpdateCaptionButtonColors()
    {
        var titleBar = AppWindow.TitleBar;
        if (titleBar == null) return;

        var isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark
                     || ((Content as FrameworkElement)?.ActualTheme == ElementTheme.Default
                         && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        if (isDark)
        {
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedForegroundColor = Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
        }
        else
        {
            titleBar.ButtonForegroundColor = Colors.Black;
            titleBar.ButtonHoverForegroundColor = Colors.Black;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x20, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedForegroundColor = Color.FromArgb(0xC0, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x10, 0x00, 0x00, 0x00);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0x80, 0x00, 0x00, 0x00);
        }

        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem selectedItem)
        {
            var tag = selectedItem.Tag?.ToString();

            // Hide all content
            TextureTabContent.Visibility = Visibility.Collapsed;
            RleBitmapTabContent.Visibility = Visibility.Collapsed;
            ArchiveExtractorTabContent.Visibility = Visibility.Collapsed;
            UnpackTabContent.Visibility = Visibility.Collapsed;
            AudioConverterTabContent.Visibility = Visibility.Collapsed;
            VideoConverterTabContent.Visibility = Visibility.Collapsed;
            HashReviewerTabContent.Visibility = Visibility.Collapsed;
            ScriptDecompilerTabContent.Visibility = Visibility.Collapsed;
            MeshConverterTabContent.Visibility = Visibility.Collapsed;

            // Clear status bar when switching tabs
            SetStatus("");

            // Show selected content
            switch (tag)
            {
                case "Textures":
                    TextureTabContent.Visibility = Visibility.Visible;
                    break;
                case "RleBitmaps":
                    RleBitmapTabContent.Visibility = Visibility.Visible;
                    break;
                case "Archives":
                    ArchiveExtractorTabContent.Visibility = Visibility.Visible;
                    break;
                case "Unpack":
                    UnpackTabContent.Visibility = Visibility.Visible;
                    break;
                case "AudioConverter":
                    AudioConverterTabContent.Visibility = Visibility.Visible;
                    break;
                case "VideoConverter":
                    VideoConverterTabContent.Visibility = Visibility.Visible;
                    break;
                case "HashReviewer":
                    HashReviewerTabContent.Visibility = Visibility.Visible;
                    break;
                case "ScriptDecompiler":
                    ScriptDecompilerTabContent.Visibility = Visibility.Visible;
                    break;
                case "MeshConverter":
                    MeshConverterTabContent.Visibility = Visibility.Visible;
                    break;
            }

            // Navigated to tab
        }
    }

    public void SetStatus(string message)
    {
        GlobalStatusTextBlock.Text = message;
    }
}
