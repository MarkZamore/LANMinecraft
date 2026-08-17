using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Minecraft.Tests;

/// <summary>
/// The release is published by one workflow, and every step of it talks to the
/// same API. A GitHub incident once ended a release twice over - a single 503
/// on a call nobody had wrapped - so these keep the retry rule in place and
/// prove it retries the right things. The rest keep the workflow from quietly
/// growing back the minutes that were taken out of it.
/// </summary>
public sealed class ReleaseWorkflowTests
{
    /// <summary>gh is called through the helper, never on its own.</summary>
    [Fact]
    public void ReleaseWorkflow_CallsGhOnlyThroughTheRetryHelper()
    {
        var bare = new List<string>();
        foreach (var file in ReleaseScripts())
        {
            // The helper is where gh is finally called for real.
            if (Path.GetFileName(file) == "GhRetry.ps1") continue;
            bare.AddRange(Lines(File.ReadAllText(file))
                .Select((line, index) => (Text: line, Number: index + 1))
                .Where(line => !line.Text.TrimStart().StartsWith('#'))
                .Where(line => Regex.IsMatch(line.Text, @"(?<![\w-])gh\s+(release|api|auth|run|workflow)\b"))
                .Where(line => !line.Text.Contains("Invoke-Gh", StringComparison.Ordinal))
                .Select(line => $"{Path.GetFileName(file)} line {line.Number}: {line.Text.Trim()}"));
        }

        Assert.Empty(bare);
        Assert.Contains(
            "Invoke-Gh",
            File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The .NET the image already carries is the .NET the release builds with;
    /// downloading another SDK is a minute players spend waiting, so that step
    /// is a fallback with a condition on it, not a fixture.
    /// </summary>
    [Fact]
    public void TheSdkDownload_IsOnlyAFallback()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));
        var setup = workflow.Split("      - name:", StringSplitOptions.RemoveEmptyEntries)
            .Single(step => step.Contains("actions/setup-dotnet", StringComparison.Ordinal));

        Assert.Contains("if:", setup, StringComparison.Ordinal);
        Assert.Contains("preinstalled", setup, StringComparison.Ordinal);
        // global.json decides which of the installed SDKs is used, so the
        // fallback download and the preinstalled one agree on the version.
        Assert.Contains("global-json-file: global.json", setup, StringComparison.Ordinal);
        var globalJson = File.ReadAllText(FindRepositoryFile("global.json"));
        Assert.Contains("\"rollForward\": \"latestFeature\"", globalJson, StringComparison.Ordinal);
        Assert.Contains("10.0.", globalJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// The delta tool is built once, in Prepare, and then run from its own
    /// assembly: dotnet run would rebuild it for each of the four invocations.
    /// </summary>
    [Fact]
    public void TheDeltaTool_IsBuiltOnceAndRunFromItsAssembly()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));

        var rebuilt = Lines(workflow)
            .Where(line => !line.TrimStart().StartsWith('#'))
            .Where(line => line.Contains("dotnet run", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(rebuilt);
        Assert.Contains("DeltaPatchTool.dll", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet build Program\\Patch\\DeltaPatchTool.csproj -c Release --no-restore",
            workflow,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The dependency audit lives in restore itself, for every project at once.
    /// It replaced a listing step that printed findings and passed anyway, so
    /// losing these properties would quietly lose the audit.
    /// </summary>
    [Fact]
    public void TheDependencyAudit_IsPartOfEveryRestore()
    {
        var properties = File.ReadAllText(FindRepositoryFile("Directory.Build.props"));

        Assert.Contains("<NuGetAudit>true</NuGetAudit>", properties, StringComparison.Ordinal);
        Assert.Contains("<NuGetAuditMode>all</NuGetAuditMode>", properties, StringComparison.Ordinal);
        foreach (var code in new[] { "NU1902", "NU1903", "NU1904" })
        {
            Assert.Contains(code, properties, StringComparison.Ordinal);
        }

        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));
        Assert.DoesNotContain("--vulnerable", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// Work that does not need other work runs beside it. A background job in
    /// PowerShell reports failure only in its state, so every one of them is
    /// waited for and then asked how it went - a job whose error is never
    /// received is a release that ships without the thing the job was doing.
    /// </summary>
    [Fact]
    public void EveryBackgroundJob_IsWaitedForAndChecked()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));
        var steps = workflow
            .Split("      - name:", StringSplitOptions.RemoveEmptyEntries)
            .Where(step => step.Contains("Start-Job", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(steps);
        foreach (var step in steps)
        {
            var title = Lines(step).First().Trim();
            Assert.True(step.Contains("Wait-Job", StringComparison.Ordinal), $"{title} never waits");
            Assert.True(step.Contains("Receive-Job", StringComparison.Ordinal), $"{title} never receives");
            Assert.True(
                step.Contains("State -eq 'Failed'", StringComparison.Ordinal),
                $"{title} never asks whether the job failed");
        }
    }

    /// <summary>
    /// A script started as a background job is invoked as a file, not handed
    /// over as text: Start-Job -FilePath leaves $PSScriptRoot empty, and the
    /// scripts find the retry helper next to themselves.
    /// </summary>
    [Fact]
    public void BackgroundScripts_KeepTheirOwnDirectory()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));

        Assert.DoesNotContain("Start-Job -Name 'capture' -FilePath", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-FilePath (Join-Path $env:GITHUB_WORKSPACE", workflow, StringComparison.Ordinal);
        foreach (var script in new[] { "Capture-PreviousRelease.ps1", "Archive-PreviousBase.ps1" })
        {
            var text = File.ReadAllText(FindRepositoryFile(".github", "scripts", script));
            Assert.Contains("$PSScriptRoot/GhRetry.ps1", text, StringComparison.Ordinal);
            Assert.Contains("$env:GH_REPO", text, StringComparison.Ordinal);
        }
    }

    /// <summary>Every step that calls gh dot-sources the helper first.</summary>
    [Fact]
    public void EveryStepThatCallsGh_LoadsTheRetryHelper()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));
        var steps = workflow.Split("      - name:", StringSplitOptions.RemoveEmptyEntries);

        var missing = steps
            .Where(step => step.Contains("Invoke-Gh ", StringComparison.Ordinal))
            .Where(step => !step.Contains("GhRetry.ps1", StringComparison.Ordinal))
            .Select(step => step.Split('\n')[0].Trim())
            .ToArray();

        Assert.Empty(missing);
    }

    /// <summary>
    /// GitHub answering 503 is weather, not an answer: the call is made again.
    /// </summary>
    [Theory]
    [InlineData("non-200 OK status code: 503 Service Unavailable", 2, 0, 3)]
    [InlineData("HTTP 503: No server is currently available", 1, 0, 2)]
    [InlineData("HTTP 502: Bad Gateway", 99, 1, 6)]
    [InlineData("error connecting to api.github.com: connection reset by peer", 1, 0, 2)]
    public void ATransientFailure_IsTriedAgain(string message, int failures, int expectedExit, int expectedCalls)
    {
        var result = RunHelper(message, failures);

        Assert.Equal(expectedExit, result.ExitCode);
        Assert.Equal(expectedCalls, result.Calls);
        if (expectedExit == 0) Assert.Contains("ok", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A 404 is an answer several steps are built on - "is there a release with
    /// this tag?" - so it comes back at once, unretried.
    /// </summary>
    [Theory]
    [InlineData("release not found (HTTP 404)")]
    [InlineData("gh: Not Found (HTTP 404)")]
    [InlineData("unknown command \"nonesuch\" for \"gh release\"")]
    public void AnAnswerFromGitHub_IsNotRetried(string message)
    {
        var result = RunHelper(message, failures: 99);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, result.Calls);
    }

    /// <summary>
    /// Runs the helper with a stub gh first on PATH, and reports what happened:
    /// the exit code the caller would read, how many times gh was run, and what
    /// came back on standard output.
    /// </summary>
    private static (int ExitCode, int Calls, string Output) RunHelper(string stderrMessage, int failures)
    {
        var helper = FindRepositoryFile(".github", "scripts", "GhRetry.ps1");
        var shell = FindShell();
        var sandbox = Directory.CreateTempSubdirectory("gh-retry-test");
        try
        {
            var log = Path.Combine(sandbox.FullName, "calls.log");
            File.WriteAllText(log, string.Empty);
            // A stub that fails the first few times and then succeeds, counting
            // its own calls: one line per call, so the count is the line count.
            // Everything it runs is named by full path - a Git Bash on PATH
            // offers a `find` that walks the whole disk instead of counting lines.
            File.WriteAllText(Path.Combine(sandbox.FullName, "gh.cmd"), """
                @echo off
                >>"%GH_STUB_LOG%" echo call
                for /f %%A in ('%SystemRoot%\System32\find.exe /c /v "" ^< "%GH_STUB_LOG%"') do set COUNT=%%A
                if %COUNT% LEQ %GH_STUB_FAILURES% (
                  echo %GH_STUB_MESSAGE% 1>&2
                  exit /b 1
                )
                echo ok
                exit /b 0
                """);

            var script =
                $". '{helper}'{Environment.NewLine}" +
                $"$out = Invoke-Gh release view latest --json tagName{Environment.NewLine}" +
                $"Write-Output \"EXIT:$LASTEXITCODE\"{Environment.NewLine}" +
                $"Write-Output \"OUT:$out\"";
            var startInfo = new ProcessStartInfo(shell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(script);
            startInfo.Environment["PATH"] = sandbox.FullName + Path.PathSeparator + startInfo.Environment["PATH"];
            startInfo.Environment["GH_RETRY_DELAYS"] = "0,0,0,0,0";
            startInfo.Environment["GH_STUB_LOG"] = log;
            startInfo.Environment["GH_STUB_FAILURES"] = failures.ToString();
            startInfo.Environment["GH_STUB_MESSAGE"] = stderrMessage;

            using var process = Process.Start(startInfo)!;
            // Both pipes are drained while the shell runs. PowerShell 5.1 is
            // loud about a native command that writes to stderr, and a full
            // stderr buffer would stop it before it ever reached its last line.
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(120_000), "the helper did not finish in two minutes");
            var output = standardOutput.GetAwaiter().GetResult();
            standardError.GetAwaiter().GetResult();

            var exit = Regex.Match(output, @"EXIT:(-?\d+)");
            Assert.True(exit.Success, $"no exit code in the helper output: {output}");
            var calls = File.ReadAllLines(log).Count(line => line.Trim().Length > 0);
            return (int.Parse(exit.Groups[1].Value), calls, output);
        }
        finally
        {
            try { sandbox.Delete(recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>PowerShell 7 if the machine has it - the workflow runs pwsh.</summary>
    private static string FindShell()
    {
        foreach (var candidate in new[] { "pwsh.exe", "pwsh", "powershell.exe" })
        {
            var found = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator)
                .Where(directory => directory.Length > 0)
                .Select(directory => Path.Combine(directory, candidate))
                .FirstOrDefault(File.Exists);
            if (found is not null) return found;
        }

        throw new InvalidOperationException("No PowerShell was found to run the release helper with.");
    }

    /// <summary>The text as lines, whichever way the file ends them.</summary>
    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(line => line.TrimEnd('\r'));

    /// <summary>Every PowerShell the release runs: the steps live in the YAML.</summary>
    private static IEnumerable<string> ReleaseScripts()
    {
        yield return FindRepositoryFile(".github", "workflows", "release.yml");
        var scripts = Path.GetDirectoryName(FindRepositoryFile(".github", "scripts", "GhRetry.ps1"))!;
        foreach (var script in Directory.EnumerateFiles(scripts, "*.ps1").OrderBy(path => path))
        {
            yield return script;
        }
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(relativeParts)}");
    }
}
