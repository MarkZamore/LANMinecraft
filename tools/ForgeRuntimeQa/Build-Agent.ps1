param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$JdkHome = $env:JAVA_HOME
)
$ErrorActionPreference = 'Stop'
$qaOutput = [IO.Path]::GetFullPath($OutputDirectory)
$qaParts = $qaOutput -split '[\\/]'
$qaLocationAllowed = $false
for ($qaIndex = 0; $qaIndex -lt $qaParts.Length - 2; $qaIndex++) {
    if ($qaParts[$qaIndex] -eq 'artifacts' -and $qaParts[$qaIndex + 1] -eq 'agent-runs') { $qaLocationAllowed = $true }
}
if (-not $qaLocationAllowed) { throw 'QA output must stay under artifacts/agent-runs/<task-id>.' }
$qaAncestor = $qaOutput
while ($qaAncestor) {
    if (Test-Path -LiteralPath $qaAncestor) {
        if ((Get-Item -LiteralPath $qaAncestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked QA paths are forbidden.' }
    }
    $qaAncestor = [IO.Path]::GetDirectoryName($qaAncestor)
}
$qaJavac = Join-Path $JdkHome 'bin/javac.exe'
$qaJar = Join-Path $JdkHome 'bin/jar.exe'
if (-not (Test-Path -LiteralPath $qaJavac) -or -not (Test-Path -LiteralPath $qaJar)) { throw 'Set JAVA_HOME or pass -JdkHome pointing to a complete JDK.' }
New-Item -ItemType Directory -Path $qaOutput -Force | Out-Null
$qaRun = Join-Path $qaOutput 'RUN.json'
if (-not (Test-Path -LiteralPath $qaRun)) {
    [IO.File]::WriteAllText($qaRun, ([ordered]@{ owner = 'ForgeRuntimeQa'; purpose = 'Native QA instrumentation build';
        createdAtUtc = [DateTime]::UtcNow.ToString('O'); expectedSizeBytes = 2MB;
        deleteAfterUtc = [DateTime]::UtcNow.AddDays(2).ToString('O'); results = @('forge-runtime-qa-agent.jar') } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}
$qaClasses = Join-Path $qaOutput 'classes'
New-Item -ItemType Directory -Path $qaClasses -Force | Out-Null
& $qaJavac --release 17 -encoding UTF-8 -d $qaClasses (Join-Path $PSScriptRoot 'QaLifecycleAgent.java')
if ($LASTEXITCODE -ne 0) { throw 'QA agent compilation failed.' }
$qaManifest = Join-Path $qaOutput 'MANIFEST.MF'
[IO.File]::WriteAllText($qaManifest, "Manifest-Version: 1.0`nPremain-Class: QaLifecycleAgent`n`n", [Text.UTF8Encoding]::new($false))
$qaAgentPath = Join-Path $qaOutput 'forge-runtime-qa-agent.jar'
& $qaJar --create --file $qaAgentPath --manifest $qaManifest -C $qaClasses .
if ($LASTEXITCODE -ne 0) { throw 'QA agent packaging failed.' }
[pscustomobject]@{ path = $qaAgentPath; sha256 = (Get-FileHash -LiteralPath $qaAgentPath -Algorithm SHA256).Hash.ToLowerInvariant(); testOnly = $true; classTransformation = $false } | ConvertTo-Json
