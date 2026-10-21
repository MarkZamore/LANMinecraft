using System.Diagnostics;
using System.IO.Compression;

namespace Minecraft.Tests;

public sealed class NoticeOverlayAgentTests
{
    [Fact]
    public void EmbeddedAgent_UsesJava8AndKeepsAsmOffTheBootstrapPath()
    {
        using var stream = AgentResource();
        using var jar = new ZipArchive(stream);
        var manifest = Read(jar, "META-INF/MANIFEST.MF");
        Assert.Contains("Premain-Class:", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain(jar.Entries, entry => entry.FullName.StartsWith("net/minecraft/", StringComparison.Ordinal));
        Assert.Contains(jar.Entries, entry => entry.FullName == "org/objectweb/asm/ClassReader.class");
        var hooks = Assert.Single(jar.Entries, entry => entry.FullName.EndsWith("hooks.jar", StringComparison.Ordinal));
        using var nested = hooks.Open();
        using var copy = new MemoryStream();
        nested.CopyTo(copy);
        copy.Position = 0;
        using var bootstrap = new ZipArchive(copy);
        Assert.DoesNotContain(bootstrap.Entries, entry => entry.FullName.StartsWith("org/objectweb/asm/", StringComparison.Ordinal));
        foreach (var archive in new[] { jar, bootstrap })
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".class", StringComparison.Ordinal)))
        {
            using var input = entry.Open();
            var header = new byte[8];
            input.ReadExactly(header);
            Assert.Equal(new byte[] { 0xca, 0xfe, 0xba, 0xbe }, header[..4]);
            Assert.True((header[6] << 8 | header[7]) <= 52, entry.FullName + " requires Java newer than 8");
        }
    }

    [Theory]
    [InlineData("org.lwjgl.glfw.GLFW", "glfwSwapBuffers", true)]
    [InlineData("org.lwjgl.opengl.Display", "swapBuffers", false)]
    public async Task SwapHook_WorksInAnIsolatedLoader_AndCannotBreakPresentationWhenGlIsUnavailable(
        string target, string method, bool hasWindow)
    {
        var root = Path.Combine(RepositoryRoot(), "Program.Tests", "TestResults", "NoticeAgent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var agent = Path.Combine(root, "overlay-agent.jar");
            await using (var output = File.Create(agent))
            await using (var input = AgentResource())
                await input.CopyToAsync(output);
            var package = target[..target.LastIndexOf('.')];
            var name = target[(target.LastIndexOf('.') + 1)..];
            var source = Path.Combine(root, name + ".java");
            await File.WriteAllTextAsync(source, $$"""
                package {{package}};
                public class {{name}} {
                    public static int swaps;
                    public static void {{method}}({{(hasWindow ? "long window" : "")}}) { swaps++; }
                }
                """);
            var probe = Path.Combine(root, "OverlayProbe.java");
            await File.WriteAllTextAsync(probe, """
                import java.io.File;
                import java.net.URL;
                import java.net.URLClassLoader;
                public class OverlayProbe {
                    public static void main(String[] args) throws Exception {
                        URLClassLoader isolated = new URLClassLoader(new URL[]{new File(args[0]).toURI().toURL()}, null);
                        Class<?> target = Class.forName(args[1], true, isolated);
                        if (args[3].equals("true")) target.getMethod(args[2], long.class).invoke(null, 1L);
                        else target.getMethod(args[2]).invoke(null);
                        if (target.getField("swaps").getInt(null) != 1) throw new AssertionError("Presentation was lost");
                        Thread.sleep(700);
                        System.out.println("Swap succeeded in isolated loader");
                        isolated.close();
                    }
                }
                """);
            var classes = Path.Combine(root, "classes");
            Directory.CreateDirectory(classes);
            await Run(root, "javac", "--release", "8", "-d", classes, source, probe);
            var outputText = await Run(root, "java", "-Xverify:all", "-javaagent:" + agent,
                "-Dlanminecraft.notice.directory=" + root, "-cp", classes, "OverlayProbe",
                classes, target, method, hasWindow ? "true" : "false");
            Assert.Contains("Swap succeeded in isolated loader", outputText, StringComparison.Ordinal);
            Assert.Contains("[LauncherNotice] Presentation hook installed: " + (hasWindow ? "lwjgl3" : "lwjgl2"),
                outputText, StringComparison.Ordinal);
            Assert.DoesNotContain("IllegalClassFormatException", outputText, StringComparison.Ordinal);
        }
        finally { TempTree.Delete(root); }
    }

    private static Stream AgentResource() => typeof(PeerNoticeService).Assembly
        .GetManifestResourceStream("Minecraft.NoticeOverlayAgent.jar")
        ?? throw new FileNotFoundException("The launcher must embed its fullscreen notice agent.");

    private static string Read(ZipArchive jar, string path)
    {
        using var reader = new StreamReader(jar.GetEntry(path)!.Open());
        return reader.ReadToEnd();
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Program", "Minecraft.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Launcher checkout was not found.");
    }

    private static async Task<string> Run(string root, string tool, params string[] arguments)
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(home) ? tool + ".exe" : Path.Combine(home, "bin", tool + ".exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root
        };
        var temporary = Path.Combine(root, "Temp");
        var profile = Path.Combine(root, "Home");
        Directory.CreateDirectory(temporary);
        Directory.CreateDirectory(profile);
        PortableJavaEnvironment.ConfigureEnvironment(start.Environment, temporary, profile);
        var prefix = tool == "javac" ? "-J" : "";
        start.ArgumentList.Add(prefix + "-Djava.io.tmpdir=" + temporary);
        start.ArgumentList.Add(prefix + "-Duser.home=" + profile);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var text = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, text);
        return text;
    }
}
