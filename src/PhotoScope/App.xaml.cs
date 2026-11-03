using System.Windows;
using System.Windows.Threading;
using PhotoScope.Services;
using PhotoScope.ViewModels;
using PhotoScope.Views;

namespace PhotoScope;

public partial class App : Application
{
    LibraryDb? _db;
    SingleInstance? _instance;
    MainViewModel? _vm;
    MainWindow? _mainWindow;
    ViewerWindow? _fileViewer;
    bool _started;   // this copy got as far as loading settings (secondaries never do)
    bool _exiting;
    bool _ready;     // view model exists and a window is up: hand-offs can be handled
    readonly List<InstanceMessage> _pending = new();

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
#if !DEBUG
        return;
#else
        if (!Offscreen) return;
        w.ShowActivated = false;
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            SetWindowPos(hwnd, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        };
#endif
    }

    /// <summary>
    /// Test mode: adds an invisible "DebugSnapshot" button that UI Automation can invoke to render
    /// the window's visual tree to PHOTOSCOPE_SNAPSHOT (a PNG path). Independent of screen state.
    /// </summary>
    public static void AddSnapshotHook(Window w, System.Windows.Controls.Panel host)
    {
#if DEBUG
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
#endif
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

        // Stay alive through start-up dialogs; switched to "close with the last window" once one is shown.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var fileToOpen = CommandLine.FindFileArgument(e.Args);

        // One copy per user: a second launch hands its file to the running copy and exits.
        var instanceId = SingleInstance.IdFor(AppPaths.Root);
        _instance = SingleInstance.Acquire(instanceId);
        if (!_instance.IsPrimary)
        {
            var message = fileToOpen != null
                ? new InstanceMessage(InstanceMessage.Open, fileToOpen)
                : new InstanceMessage(InstanceMessage.Activate);
            var delivered = SingleInstance.TrySend(instanceId, message, TimeSpan.FromSeconds(8));
            if (!delivered)
            {
                // The other copy may just have exited: if so, carry on as the running copy.
                _instance.Dispose();
                _instance = SingleInstance.Acquire(instanceId);
            }
            if (delivered || !_instance.IsPrimary)
            {
                if (!delivered)
                    MessageBox.Show("PhotoScope is already running but isn't responding. Close it (or end it in Task Manager) and try again.",
                        "PhotoScope", MessageBoxButton.OK, MessageBoxImage.Warning);
                _instance.Dispose();
                _instance = null;
                Shutdown(0);
                return;
            }
        }
        // Listen straight away so launches during start-up are queued rather than refused.
        _instance.StartServer(AcceptInstanceMessage);
        _started = true;

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
            _vm = new MainViewModel(_db, thumbs, settings);

            // Opened with a file: show just the viewer, straight away. The library loads behind it.
            if (fileToOpen == null || !OpenFile(fileToOpen)) ShowMainWindow();
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            _ready = true;
            foreach (var queued in _pending.ToList()) HandleInstanceMessage(queued);
            _pending.Clear();
            _ = StartAsync(_vm, e.Args);
        }
        catch (Exception ex)
        {
            Log.Error("Main window failed to start", ex);
            Dialogs.Error("PhotoScope couldn't start", "Something went wrong while opening the main window. Details were written to the log in " + AppPaths.Logs);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Pipe thread: accept a hand-off only if the UI thread answers (it isn't hung) and we aren't
    /// shutting down. Accepted messages are queued; the sender is then told they were delivered.
    /// </summary>
    Task<bool> AcceptInstanceMessage(InstanceMessage message) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (_exiting || Dispatcher.HasShutdownStarted) return false;
            if (!_ready) _pending.Add(message);
            else Dispatcher.BeginInvoke(() => HandleInstanceMessage(message));
            return true;
        }).Task;

    /// <summary>A later launch passed us a file to open, or asked us to come to the front.</summary>
    void HandleInstanceMessage(InstanceMessage message)
    {
        if (_exiting) return;
        Log.Info($"Message from another launch: {message.Command} {message.Path}");
        if (message.Command == InstanceMessage.Open && message.Path != null)
        {
            if (!OpenFile(message.Path) && !Windows.OfType<Window>().Any(w => w.IsVisible)) ShowMainWindow();
        }
        else
        {
            ShowMainWindow();
        }
    }

    /// <summary>Opens a file in the file viewer (reusing it if it's open). False if the file can't be opened.</summary>
    bool OpenFile(string path)
    {
        if (_vm == null) return false;
        var check = CommandLine.Check(path); // network paths are probed with a short timeout
        if (!check.Ok)
        {
            Log.Info($"Can't open {path}: {check.Problem}");
            Dialogs.Error("Can't open this file", check.Message!);
            return false;
        }
        try
        {
            // Reuse the viewer even if it hasn't finished loading (several files opened at once).
            if (_fileViewer is { } existing)
            {
                existing.OpenFile(check.Path!);
                return true;
            }
            var viewer = ViewerWindow.ForFile(_vm, check.Path!);
            viewer.Closed += (_, _) => { if (_fileViewer == viewer) _fileViewer = null; };
            _fileViewer = viewer;
            if (MainWindow == null || !MainWindow.IsVisible) MainWindow = viewer;
            viewer.Show();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't open {path} in the viewer", ex);
            Dialogs.Error("Can't open this file", Friendly.DescribeImageError(ex, path));
            return false;
        }
    }

    /// <summary>Shows the library window, creating it the first time (it isn't created when opening a file).</summary>
    void ShowMainWindow()
    {
        if (_vm == null) return;
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow(_vm);
            _mainWindow.Closed += (_, _) =>
            {
                _mainWindow = null;
                // Don't leave Application.MainWindow pointing at a closed window.
                if (_fileViewer != null) MainWindow = _fileViewer;
            };
            if (Offscreen)
            {
                _mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.Left = 0;
                _mainWindow.Top = 0;
            }
        }
        MainWindow = _mainWindow;
        if (!_mainWindow.IsVisible) _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        if (!Offscreen) _mainWindow.Activate();
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
        _exiting = true;
        _instance?.Dispose();
        if (!_started)
        {
            // A second launch that handed its file over: it never loaded settings, so must not save them.
            base.OnExit(e);
            return;
        }
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
