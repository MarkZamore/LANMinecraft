using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Minecraft;

namespace Minecraft.Tests;

public sealed class MemoryRecommendationTests
{
    [Fact]
    public void ClickingRecommendation_RestoresAutomaticMemory_OnlyWhileMemoryIsEditableAndReady()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Program", "Minecraft.csproj")))
                repository = repository.Parent;
            var root = Path.Combine(repository!.FullName, "artifacts", "MemoryRecommendationTests", Guid.NewGuid().ToString("N"));
            try
            {
                var paths = new AppPaths(root);
                paths.Ensure();
                var service = new SettingsService(paths);
                var settings = new AppSettings { ClientRelativePath = "RecommendationTest", MaxHeapGb = 4 };
                service.RememberMemoryForPack(settings, 4);
                settings.MemoryByPack["OtherPack"] = 3;
                // Exercise the actual handler with real controls and persistence,
                // without starting the window's Steam/network/game services.
                var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
                var memory = new TextBox { Text = "4" };
                void Set(string name, object? value) => typeof(MainWindow).GetField(name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(window, value);
                void Click() => typeof(MainWindow).GetMethod("MemoryEstimateButton_Click",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new Button(), new RoutedEventArgs()]);
                Set("_settings", settings);
                Set("_settingsService", service);
                Set("MemoryTextBox", memory);
                Set("MemoryEstimateText", new TextBlock());
                Set("MemoryEstimateButton", new Button());
                Click(); // No finished recommendation yet.
                Assert.Equal("4", memory.Text);
                Set("_recommendedMemoryGb", 2);
                memory.IsEnabled = false;
                Click();
                Assert.Equal("4", memory.Text);
                memory.IsEnabled = true;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                Set("_memoryEstimateWait", timer);
                timer.Start();
                try { Click(); Assert.Equal("4", memory.Text); }
                finally { timer.Stop(); }
                Assert.Equal(4, settings.MemoryByPack["RecommendationTest"]);
                Click();
                Assert.Equal("2", memory.Text);
                Assert.Equal(2, settings.MaxHeapGb);
                var reloaded = service.Load();
                Assert.Null(service.RememberedMemoryGb(reloaded));
                Assert.DoesNotContain("RecommendationTest", reloaded.MemoryByPack.Keys);
                Assert.Equal(3, reloaded.MemoryByPack["OtherPack"]);
                memory.TextChanged += (_, e) => typeof(MainWindow).GetMethod("MemoryTextBox_TextChanged",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [memory, e]);
                memory.Text = "3";
                Assert.Equal(3, service.Load().MemoryByPack["RecommendationTest"]);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            return true;
        });
    }
}
