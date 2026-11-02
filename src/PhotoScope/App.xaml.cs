using System.Windows;
using System.Windows.Threading;
using PhotoScope.Services;
using PhotoScope.ViewModels;
using PhotoScope.Views;

namespace PhotoScope;

public partial class App : Application
{
    LibraryDb? _db;
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
