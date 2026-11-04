using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Minecraft;

internal static class Program
{
    private sealed record NoticeMessage(string Name, string Heading, string Detail);

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected messages.json and output directory.");
        var messages = JsonSerializer.Deserialize<NoticeMessage[]>(File.ReadAllText(args[0]))
            ?? throw new InvalidDataException("No messages supplied.");
        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent(); // No Run: no launcher startup, Steam, network, or game.
        var results = new List<object>();
        foreach (var message in messages)
        {
            if (!Regex.IsMatch(message.Name, "^[a-z0-9-]+$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Invalid output label.");
            foreach (var width in new[] { 568d, 440d, 280d })
            foreach (var dpi in new[] { 1d, 1.5d, 2d })
            {
                var dialog = (Window)Activator.CreateInstance(typeof(NoticeDialog), BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new object[] { message.Heading, message.Detail }, null)!;
                var panel = (FrameworkElement)dialog.Content;
                VisualTreeHelper.SetRootDpi(dialog, new DpiScale(dpi, dpi));
                VisualTreeHelper.SetRootDpi(panel, new DpiScale(dpi, dpi));
                typeof(App).Assembly.GetType("Minecraft.DialogScale")!.GetMethod("FitToClientArea", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { dialog, panel, new Size(width, 900) });
                panel.Measure(new Size(width, double.PositiveInfinity));
                panel.Arrange(new Rect(new Point(), panel.DesiredSize));
                panel.UpdateLayout();
                var heading = (TextBlock)dialog.FindName("HeadingText");
                var detail = (TextBlock)dialog.FindName("DetailText");
                if (heading.Text != message.Heading || detail.Text != message.Detail ||
                    panel.DesiredSize.Width > width + 0.5 || !panel.LayoutTransform.Value.IsIdentity ||
                    heading.FontSize != (double)dialog.FindResource("Font.SizeHeading") ||
                    detail.FontSize != (double)dialog.FindResource("Font.SizeCaption") ||
                    heading.ActualHeight <= 0 || detail.ActualHeight <= 0)
                    throw new InvalidOperationException(JsonSerializer.Serialize(new {
                        message.Name, width, dpi, panel.DesiredSize, panel.ActualWidth, panel.ActualHeight,
                        Transform = panel.LayoutTransform.Value.ToString(), HeadingFont = heading.FontSize,
                        DetailFont = detail.FontSize, HeadingHeight = heading.ActualHeight, DetailHeight = detail.ActualHeight,
                        Heading = heading.Text, Detail = detail.Text }));

                var height = panel.DesiredSize.Height;
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                {
                    context.DrawRectangle(dialog.Background, null, new Rect(0, 0, width, height));
                    context.DrawRectangle(new VisualBrush(panel), null,
                        new Rect(panel.Margin.Left, panel.Margin.Top, panel.ActualWidth, panel.ActualHeight));
                }
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi), (int)Math.Ceiling(height * dpi),
                    96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var name = FormattableString.Invariant($"{message.Name}-{width}-{dpi}.png");
                using (var stream = File.Create(Path.Combine(output, name))) encoder.Save(stream);
                results.Add(new { message.Name, Width = width, Dpi = dpi, Height = height,
                    HeadingHeight = heading.ActualHeight, DetailHeight = detail.ActualHeight, File = name });
                dialog.Close();
            }
        }
        File.WriteAllText(Path.Combine(output, "layout.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Measured {results.Count} notices with actual WPF controls.");
        app.Shutdown();
    }
}
