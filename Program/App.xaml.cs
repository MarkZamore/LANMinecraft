using System.Windows;
using System.Windows.Threading;

namespace Minecraft;

public partial class App : Application
{
    private const string SkipPrestartUpdateArgument = "--skip-prestart-update=";

    private SingleInstanceGuard? _instanceGuard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before anything that can fail. An error the launcher does not catch
        // is still the launcher's to show: without these, Windows draws it, and
        // a light box quoting a .NET path over a dark launcher is the one thing
        // the design rules say never to put in front of a player.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // These two cannot be shown - one fires after the player has done
        // nothing and forgotten it, the other while the process is already
        // going down - but a report is worth nothing without them in the log.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            TryLog($"Unobserved task failure: {args.Exception.GetBaseException()}");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            TryLog($"Unhandled failure: {args.ExceptionObject}");

        // Transport spike harness (temporary, see SteamSpikeRunner): runs the
        // Steam checks in a console instead of opening the launcher window.
        if (SteamSpikeRunner.TryRun(e.Args))
        {
            Shutdown();
            return;
        }

        // Before anything that touches the settings file or the instance
        // folders: a second copy must not get as far as writing to them.
        _instanceGuard = SingleInstanceGuard.TryAcquire();
        if (_instanceGuard is null)
        {
            SingleInstanceGuard.AskRunningInstanceToShowItself();
            Shutdown();
            return;
        }

        if (!e.Args.Any(argument => argument.StartsWith(SkipPrestartUpdateArgument, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var paths = new AppPaths(AppPaths.ResolveApplicationRoot());
                paths.Ensure();
                var logger = new Logger(paths.LogFile);
                var updateService = new UpdateService(paths, logger);
                if (updateService.RequestRestartForActiveInstallation())
                {
                    Shutdown();
                    return;
                }
                var prepared = updateService.TryGetPreparedUpdate();
                if (prepared is not null)
                {
                    updateService.StartInstall(prepared, UpdateInstallMode.InstallAndRestart);
                    Shutdown();
                    return;
                }
            }
            catch (Exception ex)
            {
                TryLog($"Pre-start update failed: {ex.Message}");
            }
        }

        var window = new MainWindow();
        MainWindow = window;
        // A second press of the icon is a request for this window, not for
        // another one, and the guard is what carries the request across.
        _instanceGuard.AnotherInstanceStarted += () => window.Dispatcher.BeginInvoke(() =>
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
        });
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLog($"Unhandled UI failure: {e.Exception}");
        e.Handled = true;
        var owner = MainWindow;
        NoticeDialog.Show(owner, "Непредвиденная ошибка", e.Exception.Message);
        // Nothing is on screen yet, so there is nothing to go back to. A
        // launcher left alive without its window is a process the player can
        // only end from the task manager.
        if (owner is null) Shutdown();
    }

    /// <summary>Writes to the log, or gives up quietly if even that is broken.</summary>
    private static void TryLog(string message)
    {
        try
        {
            var paths = new AppPaths(AppPaths.ResolveApplicationRoot());
            new Logger(paths.LogFile).Warn(message);
        }
        catch
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceGuard?.Dispose();
        _instanceGuard = null;
        base.OnExit(e);
    }
}
