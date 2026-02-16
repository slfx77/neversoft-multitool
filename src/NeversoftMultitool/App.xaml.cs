using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace NeversoftMultitool;

public partial class App : Application
{
    private const int ATTACH_PARENT_PROCESS = -1;

    public App()
    {
        AttachConsole(ATTACH_PARENT_PROCESS);
        Console.WriteLine("[NeversoftMultitool] Application starting...");

        UnhandledException += App_UnhandledException;

        try
        {
            InitializeComponent();
            Console.WriteLine("[NeversoftMultitool] App initialized");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRASH] App.InitializeComponent failed: {ex}");
            throw;
        }
    }

    public new static App Current => (App)Application.Current;

    public Window? MainWindow { get; private set; }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void App_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Console.WriteLine($"[CRASH] Unhandled exception: {e.Exception}");
        Console.WriteLine($"[CRASH] Message: {e.Message}");
        e.Handled = false;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Console.WriteLine("[NeversoftMultitool] Creating main window...");
            MainWindow = new MainWindow();
            Console.WriteLine("[NeversoftMultitool] Activating main window...");
            MainWindow.Activate();
            Console.WriteLine("[NeversoftMultitool] Main window activated");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRASH] OnLaunched failed: {ex}");
            throw;
        }
    }
}
