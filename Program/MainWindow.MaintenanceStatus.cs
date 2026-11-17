using System.Windows;
using System.Windows.Threading;

namespace Minecraft;

public partial class MainWindow
{
    private static readonly string[] MaintenanceDotFrames = [".", "..", "..."];
    private DispatcherTimer? _maintenanceDotsTimer;
    private int _maintenanceDotFrame;

    private void InitializeMaintenanceStatus()
    {
        MaintenanceProgressFooter.IsVisibleChanged += (_, _) => RefreshMaintenanceDots();
        StateChanged += (_, _) => RefreshMaintenanceDots();
    }

    private void StopMaintenanceStatus() => _maintenanceDotsTimer?.Stop();

    private void UpdateMaintenanceProgress(int percent) => PostToUi(() =>
    {
        if (_shutdownStarted) return;
        var completed = percent == 100;
        if (completed) _maintenanceDotFrame = 0;
        else MaintenancePercentageText.Text = $"{percent}%)";
        MaintenanceProgressFooter.Visibility = completed ? Visibility.Collapsed : Visibility.Visible;
    });

    private void RefreshMaintenanceDots()
    {
        if (_shutdownStarted || !MaintenanceProgressFooter.IsVisible || WindowState == WindowState.Minimized)
        {
            StopMaintenanceStatus();
            return;
        }
        if (!SystemParameters.ClientAreaAnimation)
        {
            MaintenanceDotsText.Text = MaintenanceDotFrames[^1];
            StopMaintenanceStatus();
            return;
        }

        MaintenanceDotsText.Text = MaintenanceDotFrames[_maintenanceDotFrame];
        if (_maintenanceDotsTimer is null)
        {
            _maintenanceDotsTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = ((Duration)FindResource("Motion.UpdateDots")).TimeSpan
            };
            _maintenanceDotsTimer.Tick += AdvanceMaintenanceDots;
        }
        _maintenanceDotsTimer.Start();
    }

    private void AdvanceMaintenanceDots(object? sender, EventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            RefreshMaintenanceDots();
            return;
        }
        _maintenanceDotFrame = (_maintenanceDotFrame + 1) % MaintenanceDotFrames.Length;
        MaintenanceDotsText.Text = MaintenanceDotFrames[_maintenanceDotFrame];
    }
}
