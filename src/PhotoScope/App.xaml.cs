using System.Windows;
using System.Windows.Threading;
using PhotoScope.Services;
using PhotoScope.ViewModels;
using PhotoScope.Views;

namespace PhotoScope;

public partial class App : Application
{
    LibraryDb? _db;

    /// <summary>
    /// Debug builds only: PHOTOSCOPE_OFFSCREEN=1 keeps every window off-screen and unfocused so
    /// automated UI checks never cover or steal input from the desktop.
    /// </summary>
#if DEBUG
    public static bool Offscreen { get; } = Environment.GetEnvironmentVariable("PHOTOSCOPE_OFFSCREEN") == "1";
#else
    public static bool Offscreen => false;
#endif

    /// <summary>Test mode: pushes a window to the bottom of the z-order without activating it.</summary>
    public static void SendToBack(Window w)
    {
        if (!Offscreen) return;
        w.ShowActivated = false;
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            SetWindowPos(hwnd, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        };
    }

    /// <summary>
    /// Test mode: adds an invisible "DebugSnapshot" button that UI Automation can invoke to render
    /// the window's visual tree to PHOTOSCOPE_SNAPSHOT (a PNG path). Independent of screen state.
    /// </summary>
    public static void AddSnapshotHook(Window w, System.Windows.Controls.Panel host)
    {
        if (!Offscreen) return;
        var b = new System.Windows.Controls.Button { Width = 1, Height = 1, Opacity = 0, Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        System.Windows.Automation.AutomationProperties.SetName(b, "DebugSnapshot");
        b.Click += (_, _) =>
        {
            var path = Environment.GetEnvironmentVariable("PHOTOSCOPE_SNAPSHOT");
            if (string.IsNullOrEmpty(path) || w.Content is not FrameworkElement root) return;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
            var bg = new System.Windows.Shapes.Rectangle { Width = root.ActualWidth, Height = root.ActualHeight, Fill = w.Background };
            bg.Measure(new Size(root.ActualWidth, root.ActualHeight)); bg.Arrange(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            rtb.Render(bg);
            rtb.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        };
        host.Children.Add(b);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    int _errorDialogOpen;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Fatal unhandled exception", args.ExceptionObject as Exception);

        try
        {
            AppPaths.EnsureCreated();
        }
        catch (Exception ex)
        {
            MessageBox.Show("PhotoScope couldn't create its data folder:\n" + AppPaths.Root + "\n\n" + Friendly.Describe(ex),
                "PhotoScope", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        Log.Info($"PhotoScope {typeof(App).Assembly.GetName().Version} starting; data folder {AppPaths.Root}");
        Log.PruneOld();

        var settings = SettingsService.Load();
        ThemeService.Initialize();

        try
        {
            _db = new LibraryDb(AppPaths.Database);
        }
        catch (Exception ex)
        {
            Log.Error("Library database could not be opened", ex);
            Dialogs.Error("Can't open your library",
                "PhotoScope couldn't open its library database. Nothing has been changed. " +
                "If another copy of PhotoScope is running, close it and try again.\n\nDetails were written to the log in " + AppPaths.Logs);
            Shutdown(1);
            return;
        }

        var thumbs = new ThumbnailCache(AppPaths.Thumbs);
        ThumbnailLoader.Initialize(thumbs, Dispatcher);

        try
        {
            var vm = new MainViewModel(_db, thumbs, settings);
            var window = new MainWindow(vm);
            MainWindow = window;
            if (Offscreen)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.WindowState = WindowState.Normal;
                window.Left = 0;
                window.Top = 0;
            }
            window.Show();
            _ = StartAsync(vm, e.Args);
        }
        catch (Exception ex)
        {
            Log.Error("Main window failed to start", ex);
            Dialogs.Error("PhotoScope couldn't start", "Something went wrong while opening the main window. Details were written to the log in " + AppPaths.Logs);
            Shutdown(1);
        }
    }

    static async Task StartAsync(MainViewModel vm, string[] args)
    {
        await vm.InitializeAsync();
#if DEBUG
        // Developer aid for automated UI checks: --debug-import <folder> [copy]
        var i = Array.IndexOf(args, "--debug-import");
        if (i >= 0 && i + 1 < args.Length)
        {
            var copy = args.Contains("copy");
            await vm.ImportAsync(new ImportOptions
            {
                Sources = [args[i + 1]],
                Recursive = true,
                Mode = copy ? ImportMode.CopyToLibrary : ImportMode.ReferenceInPlace,
                LibraryFolder = vm.Settings.LibraryFolder,
                SkipDuplicates = true,
            });
        }
#endif
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SettingsService.Save();
        ThemeService.Shutdown();
        _db?.Dispose();
        Log.Info("PhotoScope exited");
        base.OnExit(e);
    }

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;

        // Never stack error dialogs if something keeps failing.
        if (Interlocked.Exchange(ref _errorDialogOpen, 1) == 1) return;
        try
        {
            var message = e.Exception is UserFacingException ufe ? ufe.Message : Friendly.Describe(e.Exception);
            Dialogs.Error("Something went wrong", message + "\n\nPhotoScope is still running. Details were written to the log.");
        }
        catch
        {
            // Showing the error failed too; it's already logged.
        }
        finally
        {
            Interlocked.Exchange(ref _errorDialogOpen, 0);
        }
    }
}
