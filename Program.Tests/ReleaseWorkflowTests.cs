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
    [Fact]
    public void BuildAndVerificationRunIndependently_AndBothGatePublication()
    {
        var workflow = ReleaseWorkflow();
        var build = WorkflowJob(workflow, "build");
        var verify = WorkflowJob(workflow, "verify");
        var publish = WorkflowJob(workflow, "publish");
        Assert.Matches(@"(?m)^permissions:\r?\n  contents: read\r?$", workflow);
        foreach (var job in new[] { build, verify })
        {
            Assert.Matches(@"(?m)^    runs-on: windows-latest\r?$", job);
            Assert.Contains("uses: actions/checkout@", job, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"(?m)^    needs:", job);
            Assert.DoesNotMatch(@"(?m)^ +continue-on-error:", job);
            Assert.DoesNotContain("Publish-LatestRelease.ps1", job, StringComparison.Ordinal);
            Assert.DoesNotContain("Archive-PreviousBase.ps1", job, StringComparison.Ordinal);
            Assert.DoesNotContain("contents: write", job, StringComparison.Ordinal);
        }

        Assert.Contains("Program\\Publish.ps1", build, StringComparison.Ordinal);
        Assert.DoesNotContain("Test-Launcher.ps1", build, StringComparison.Ordinal);
        Assert.Contains("Test-Launcher.ps1", verify, StringComparison.Ordinal);
        Assert.Contains("Invoke-Gh release download latest", verify, StringComparison.Ordinal);
        Assert.Contains("--pattern update.json", verify, StringComparison.Ordinal);
        Assert.Contains("$baseCommit = [string]$previous.commitSha", verify, StringComparison.Ordinal);
        Assert.Contains("Get-ReleaseImpact.ps1 -BaseCommit $baseCommit -HeadCommit $env:GITHUB_SHA", verify, StringComparison.Ordinal);
        Assert.Contains("-ReleaseImpactPlan $planPath", verify, StringComparison.Ordinal);
        Assert.DoesNotContain("-ReleaseScope Pipeline", verify, StringComparison.Ordinal);
        Assert.DoesNotContain("Program\\Publish.ps1", verify, StringComparison.Ordinal);

        var needs = Regex.Match(publish, @"(?m)^    needs: \[([^\]\r\n]+)\]\r?$");
        Assert.True(needs.Success, "Publication must declare its prerequisite jobs.");
        var prerequisites = needs.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries);
        Assert.Contains("build", prerequisites);
        Assert.Contains("verify", prerequisites);
        // GitHub's default success condition rejects failed, cancelled and
        // skipped prerequisites. Do not override it to publish anyway.
        Assert.DoesNotMatch(@"(?m)^    (?:if|continue-on-error):", publish);
        Assert.Contains("contents: write", publish, StringComparison.Ordinal);
        Assert.Contains("Publish-LatestRelease.ps1", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCreatesFullUpdateBeforeStaging_AndPublishUsesThatArtifact()
    {
        var workflow = ReleaseWorkflow();
        var build = WorkflowJob(workflow, "build");
        var buildSteps = WorkflowSteps(build);
        var package = Assert.Single(buildSteps, step => step.Contains("Program\\Publish.ps1", StringComparison.Ordinal));
        var manifest = Assert.Single(buildSteps, step => step.Contains("- name: Create update manifest", StringComparison.Ordinal));
        var upload = Assert.Single(buildSteps, step => step.Contains("uses: actions/upload-artifact@", StringComparison.Ordinal));
        Assert.Equal(Array.IndexOf(buildSteps, package) + 1, Array.IndexOf(buildSteps, manifest));
        Assert.True(Array.IndexOf(buildSteps, manifest) < Array.IndexOf(buildSteps, upload));
        Assert.Contains("Get-FileHash -LiteralPath $exe -Algorithm SHA256", manifest, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 2", manifest, StringComparison.Ordinal);
        Assert.Contains("assetName = 'LANMinecraft.exe'", manifest, StringComparison.Ordinal);
        Assert.Contains("sha256 = $hash", manifest, StringComparison.Ordinal);
        Assert.Contains("sizeBytes = $size", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("deltaPatches", manifest, StringComparison.Ordinal);
        Assert.Contains("name: release-assets", upload, StringComparison.Ordinal);
        Assert.Contains("if-no-files-found: error", upload, StringComparison.Ordinal);
        Assert.Equal(new[] { "LANMinecraft.exe", "update.json" },
            Regex.Matches(upload, @"(?m)^\s+artifacts/([^\r\n]+)\r?$")
                .Select(match => match.Groups[1].Value.Trim()).ToArray());
        Assert.Contains("asset-id: ${{ steps.assets.outputs.artifact-id }}", build, StringComparison.Ordinal);
        foreach (var obsolete in new[] { ".bsdiff", "DeltaPatchTool", "Capture-PreviousRelease.ps1", "Archive-PreviousBase.ps1", "DELTA_BASES_FILE" })
            Assert.DoesNotContain(obsolete, build, StringComparison.Ordinal);

        var publishSteps = WorkflowSteps(WorkflowJob(workflow, "publish"));
        var download = Assert.Single(publishSteps, step => step.Contains("uses: actions/download-artifact@", StringComparison.Ordinal));
        var release = Assert.Single(publishSteps, step => step.Contains("Publish-LatestRelease.ps1", StringComparison.Ordinal));
        Assert.Contains("artifact-ids: ${{ needs.build.outputs.asset-id }}", download, StringComparison.Ordinal);
        Assert.Contains("merge-multiple: true", download, StringComparison.Ordinal);
        Assert.DoesNotContain("run-id:", download, StringComparison.Ordinal);
        Assert.True(Array.IndexOf(publishSteps, download) < Array.IndexOf(publishSteps, release));
    }

    [Fact]
    public void PublicationRequiresScanEvidenceForTheStagedExecutable()
    {
        var steps = WorkflowSteps(WorkflowJob(ReleaseWorkflow(), "publish"));
        var download = Assert.Single(steps, step => step.Contains("uses: actions/download-artifact@", StringComparison.Ordinal));
        var scan = Assert.Single(steps, step => step.Contains("Test-PortableHostAntivirus.ps1", StringComparison.Ordinal));
        var release = Assert.Single(steps, step => step.Contains("Publish-LatestRelease.ps1", StringComparison.Ordinal));
        Assert.True(Array.IndexOf(steps, download) < Array.IndexOf(steps, scan));
        Assert.True(Array.IndexOf(steps, scan) < Array.IndexOf(steps, release));
        Assert.Contains("-ExecutablePath", scan, StringComparison.Ordinal);
        Assert.Contains("LANMinecraft.exe", scan, StringComparison.Ordinal);
        Assert.Contains("-AntivirusEvidencePath", release, StringComparison.Ordinal);
        Assert.Contains("-Publish", release, StringComparison.Ordinal);
    }

    [Fact]
    public void GraphifyFollowsSuccessfulPublicationAndIndexesTheReleasedCommit()
    {
        var workflow = ReleaseWorkflow();
        var graphify = WorkflowJob(workflow, "graphify");
        Assert.Matches(@"(?m)^    needs: publish\r?$", graphify);
        Assert.DoesNotMatch(@"(?m)^    (?:if|continue-on-error):", graphify);
        Assert.DoesNotContain("contents: write", graphify, StringComparison.Ordinal);
        Assert.DoesNotContain("graphify", WorkflowJob(workflow, "publish"), StringComparison.OrdinalIgnoreCase);

        var steps = WorkflowSteps(graphify);
        var checkout = Assert.Single(steps, step => step.Contains("uses: actions/checkout@", StringComparison.Ordinal));
        var generate = Assert.Single(steps, step => step.Contains("Update-Graphify.ps1", StringComparison.Ordinal));
        var upload = Assert.Single(steps, step => step.Contains("uses: actions/upload-artifact@", StringComparison.Ordinal));
        Assert.Contains("ref: ${{ github.sha }}", checkout, StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", checkout, StringComparison.Ordinal);
        Assert.Contains("-ReleaseArtifact -Check", generate, StringComparison.Ordinal);
        Assert.Contains("name: graphify-${{ github.sha }}", upload, StringComparison.Ordinal);
        Assert.Contains("graphify/release/graph.json", upload, StringComparison.Ordinal);
        Assert.Contains("graphify/release/source.json", upload, StringComparison.Ordinal);
        Assert.True(Array.IndexOf(steps, checkout) < Array.IndexOf(steps, generate) &&
                    Array.IndexOf(steps, generate) < Array.IndexOf(steps, upload));
    }

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
        var workflow = ReleaseWorkflow();
        foreach (var job in new[] { "build", "verify" })
        {
            var setup = Assert.Single(WorkflowSteps(WorkflowJob(workflow, job)),
                step => step.Contains("actions/setup-dotnet", StringComparison.Ordinal));
            Assert.Contains("if:", setup, StringComparison.Ordinal);
            Assert.Contains("preinstalled", setup, StringComparison.Ordinal);
            // global.json keeps the fallback and the preinstalled SDK aligned.
            Assert.Contains("global-json-file: global.json", setup, StringComparison.Ordinal);
        }
        var globalJson = File.ReadAllText(FindRepositoryFile("global.json"));
        Assert.Contains("\"rollForward\": \"latestFeature\"", globalJson, StringComparison.Ordinal);
        Assert.Contains("10.0.", globalJson, StringComparison.Ordinal);
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

    private static string ReleaseWorkflow() => File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));

    private static string WorkflowJob(string workflow, string name)
    {
        var match = Regex.Match(workflow, $@"(?ms)^  {Regex.Escape(name)}:\r?\n(.*?)(?=^  [\w-]+:\r?$|\z)");
        Assert.True(match.Success, $"The workflow does not define the {name} job.");
        return match.Groups[1].Value;
    }

    private static string[] WorkflowSteps(string job) => Regex.Split(job, @"(?m)(?=^      - )")[1..];

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
