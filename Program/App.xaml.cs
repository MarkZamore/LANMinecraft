using System.Windows;

namespace Minecraft;

public partial class App : Application
{
    private const string SkipPrestartUpdateArgument = "--skip-prestart-update=";

    private SingleInstanceGuard? _instanceGuard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
                try
                {
                    var paths = new AppPaths(AppPaths.ResolveApplicationRoot());
                    new Logger(paths.LogFile).Warn($"Pre-start update failed: {ex.Message}");
                }
                catch
                {
                }
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

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceGuard?.Dispose();
        _instanceGuard = null;
        base.OnExit(e);
    }
}
