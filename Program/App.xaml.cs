using System.Windows;

namespace Minecraft;

public partial class App : Application
{
    private const string SkipPrestartUpdateArgument = "--skip-prestart-update=";

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
        window.Show();
    }
}
