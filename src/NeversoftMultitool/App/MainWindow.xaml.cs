using Windows.Graphics;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Settings;

namespace NeversoftMultitool;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        Instance = this;
        InitializeComponent();

        // Set window size and constraints. AppWindow.Resize takes PHYSICAL
        // pixels, so scale the logical default by the monitor DPI (otherwise
        // a 150% display gets a 967-logical-px window) and keep it inside
        // the display's work area.
        var appWindow = AppWindow;
        var displayArea = DisplayArea.GetFromWindowId(
            appWindow.Id, DisplayAreaFallback.Nearest);
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var width = (int)(1450 * scale);
        var height = (int)(900 * scale);
        if (displayArea != null)
        {
            width = Math.Min(width, displayArea.WorkArea.Width);
            height = Math.Min(height, displayArea.WorkArea.Height);
        }

        appWindow.Resize(new SizeInt32(width, height));

        var presenter = appWindow.Presenter as OverlappedPresenter;
        if (presenter != null)
        {
            presenter.PreferredMinimumWidth = (int)(700 * scale);
            presenter.PreferredMinimumHeight = (int)(450 * scale);
        }

        // Center the window
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

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem { Tag: "Settings" })
            return;

        if (SettingsDrawer.Visibility == Visibility.Visible)
        {
            SettingsDrawer.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateBlenderPathStatus();
            SettingsDrawer.Visibility = Visibility.Visible;
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem selectedItem)
        {
            var tag = selectedItem.Tag?.ToString();

            // Close the settings drawer before switching tabs
            SettingsDrawer.Visibility = Visibility.Collapsed;

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

    /// <summary>
    ///     Reflects the displayed <see cref="GlobalProgress" /> operation in the
    ///     status bar; null hides the progress panel (no active operation).
    /// </summary>
    internal void ApplyGlobalProgress(GlobalProgressSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            GlobalProgressPanel.Visibility = Visibility.Collapsed;
            GlobalProgressBar.IsIndeterminate = false;
            return;
        }

        GlobalProgressPanel.Visibility = Visibility.Visible;
        GlobalProgressBar.IsIndeterminate = snapshot.Indeterminate;
        if (!snapshot.Indeterminate)
            GlobalProgressBar.Value = snapshot.Fraction * 100;
        GlobalProgressLabel.Text = snapshot.Indeterminate
            ? snapshot.Label
            : $"{snapshot.Label} — {(int)(snapshot.Fraction * 100)}%";
    }

    // ─── Settings drawer ──────────────────────────────────────────────────

    private async void LocateBlender_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickFileAsync([".exe"]);
        if (path == null) return;

        try
        {
            UserSettings.BlenderPath = path;
            SetStatus($"Blender path saved: {path}");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save Blender path: {ex.Message}");
        }

        UpdateBlenderPathStatus();
    }

    private void BlenderAutoDetect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UserSettings.BlenderPath = null;
        }
        catch (Exception ex)
        {
            SetStatus($"Could not update settings: {ex.Message}");
        }

        UpdateBlenderPathStatus();
    }

    private void UpdateBlenderPathStatus()
    {
        var saved = UserSettings.BlenderPath;
        BlenderAutoDetectButton.IsEnabled = !string.IsNullOrWhiteSpace(saved);

        if (!string.IsNullOrWhiteSpace(saved))
        {
            BlenderPathStatusText.Text = BlenderLocator.NormalizeExecutable(saved) != null
                ? $"Pinned: {saved}"
                : $"Pinned path no longer exists: {saved}";
            return;
        }

        var detected = BlenderLocator.Resolve(null);
        BlenderPathStatusText.Text = detected != null
            ? $"Auto-detected: {detected}"
            : "Blender not found — install Blender 3.2+ or click Locate Blender.";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
