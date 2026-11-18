using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Minecraft;

internal static class Program
{
    private const BindingFlags InternalInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags InternalStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [STAThread]
    private static void Main(string[] args)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var repository = FindRepository();
        var artifactRoot = Path.Combine(repository, "artifacts");
        var requestedOutput = args.SingleOrDefault(arg => arg.StartsWith("--output=", StringComparison.Ordinal));
        var output = requestedOutput is null
            ? Path.Combine(artifactRoot, "MeasureRemoval")
            : Path.GetFullPath(requestedOutput["--output=".Length..], repository);
        if (!output.StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Measurements must remain inside repository artifacts.");
        Directory.CreateDirectory(output);
        typeof(App).Assembly.GetType("Minecraft.BootstrapLaunchContext", throwOnError: true)!
            .GetMethod("InitializeTestComponentsFromEnvironment", InternalStatic)!.Invoke(null, null);
        // Use an ordinary WPF Application so the product App's deferred Startup cannot run.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = ReadResources(repository) };
        var results = new List<object>();
        foreach (var variant in new[] { "builds", "personal", "worlds" })
        foreach (var width in new[] { 552d, 440d, 280d })
        foreach (var dpi in new[] { 1d, 1.25d, 1.5d, 2d })
        {
            var dialog = CreateDialog(variant);
            var panel = (FrameworkElement)dialog.Content;
            VisualTreeHelper.SetRootDpi(dialog, new DpiScale(dpi, dpi));
            VisualTreeHelper.SetRootDpi(panel, new DpiScale(dpi, dpi));
            typeof(App).Assembly.GetType("Minecraft.DialogScale")!.GetMethod("FitToClientArea", InternalStatic)!
                .Invoke(null, new object[] { dialog, panel, new Size(width, 1000) });
            panel.Measure(new Size(width, double.PositiveInfinity));
            panel.Arrange(new Rect(new Point(), panel.DesiredSize));
            panel.UpdateLayout();

            var choices = ((StackPanel)panel).Children.OfType<Grid>().Single().Children.OfType<Button>()
                .Where(button => button.Visibility == Visibility.Visible).ToArray();
            var expectedChoices = variant switch { "builds" => 5, "personal" => 3, _ => 2 };
            if (choices.Length != expectedChoices ||
                choices.Any(button => button.ActualWidth < 1 || button.DesiredSize.Width > button.ActualWidth + .5 ||
                                      button.ActualHeight < 1 || button.DesiredSize.Height > button.ActualHeight + .5))
                throw new InvalidOperationException($"Clipped choices in {variant} at {width} DIP and {dpi}x.");
            if (variant != "worlds")
            {
                var allWithWorlds = (Button)dialog.FindName("AllWithWorldsButton");
                if (Grid.GetColumnSpan(allWithWorlds) != 3 || allWithWorlds.ActualWidth <= choices[0].ActualWidth)
                    throw new InvalidOperationException("The bottom action must span both columns.");
            }

            var height = panel.DesiredSize.Height;
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(dialog.Background, null, new Rect(0, 0, width, height));
                context.DrawRectangle(new VisualBrush(panel), null,
                    new Rect(panel.Margin.Left, panel.Margin.Top, panel.ActualWidth, panel.ActualHeight));
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi),
                (int)Math.Ceiling(height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var name = FormattableString.Invariant($"{variant}-{width}-{dpi}.png");
            using (var stream = File.Create(Path.Combine(output, name))) encoder.Save(stream);
            results.Add(new { Variant = variant, Width = width, Dpi = dpi, Height = height,
                Choices = choices.Select(button => new
                {
                    button.Name, button.Content, button.ActualWidth, button.ActualHeight,
                    Row = Grid.GetRow(button), Column = Grid.GetColumn(button), Span = Grid.GetColumnSpan(button)
                }).ToArray(), File = name });
            dialog.Close();
        }
        File.WriteAllText(Path.Combine(output, "layout.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Measured {results.Count} removal dialogs with actual WPF controls.");
        app.Shutdown();
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Program", "App.xaml"))) return directory.FullName;
        throw new DirectoryNotFoundException("Launcher repository was not found.");
    }

    private static ResourceDictionary ReadResources(string repository)
    {
        var source = XDocument.Load(Path.Combine(repository, "Program", "App.xaml")).Root!;
        var dictionary = new XElement(source.Name.Namespace + "ResourceDictionary",
            source.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
            source.Elements().Single().Elements());
        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString().Replace(
            "clr-namespace:Minecraft\"", "clr-namespace:Minecraft;assembly=LANMinecraft\"", StringComparison.Ordinal));
    }

    private static Window CreateDialog(string variant)
    {
        const string name = "Большая личная сборка";
        if (variant == "worlds")
        {
            var worldDialog = (Window)typeof(WorldRemovalConfirmationDialog).GetMethod("Prepared", InternalStatic)!.Invoke(null, null)!;
            ((TextBlock)worldDialog.FindName("QuestionText")).Text = $"Вы точно хотите удалить мир «Мой дом · {name}»?";
            return worldDialog;
        }
        var dialog = (Window)Activator.CreateInstance(typeof(BuildRemovalConfirmationDialog),
            InternalInstance, null, new object[] { name }, null)!;
        typeof(BuildRemovalConfirmationDialog).GetMethod("Configure", InternalInstance)!
            .Invoke(dialog, new object[] { name, true, variant == "personal", true, true });
        return dialog;
    }
}
