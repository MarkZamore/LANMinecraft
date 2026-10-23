using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Minecraft;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new App();
        app.InitializeComponent();
        var output = Path.GetFullPath("artifacts/MeasureRemoval");
        Directory.CreateDirectory(output);
        foreach (var width in new[] { 552d, 440d, 280d })
        foreach (var dpi in new[] { 1d, 1.5d, 2d })
        {
            var dialog = (Window)Activator.CreateInstance(typeof(BuildRemovalConfirmationDialog),
                BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { "LL8 Extended" }, null)!;
            var panel = (FrameworkElement)dialog.Content;
            VisualTreeHelper.SetRootDpi(dialog, new DpiScale(dpi, dpi));
            VisualTreeHelper.SetRootDpi(panel, new DpiScale(dpi, dpi));
            typeof(App).Assembly.GetType("Minecraft.DialogScale")!.GetMethod("FitToClientArea",
                BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { dialog, panel, new Size(width, 1000) });
            panel.Measure(new Size(width, double.PositiveInfinity));
            panel.Arrange(new Rect(new Point(), panel.DesiredSize));
            panel.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            foreach (var name in new[] { "ListOnlyButton", "BuildOnlyButton", "WithWorldsButton", "KeepButton" })
            {
                var button = (Button)dialog.FindName(name);
                Console.WriteLine($"{width} DIP / {dpi}x: {name}: {button.ActualWidth:F1} x {button.ActualHeight:F1}");
            }
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(dialog.Background, null, new Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
                context.DrawRectangle(new VisualBrush(panel), null, new Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth * dpi),
                (int)Math.Ceiling(panel.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, $"removal-{width}-{dpi}.png"));
            encoder.Save(stream);
            dialog.Close();
        }
    }
}
