function Read-ReleaseSelectors([string]$ProfileName = 'Release') {
    [xml]$settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot ($ProfileName + '.runsettings')) -Raw
    $filter = [string]$settings.RunSettings.RunConfiguration.TestCaseFilter
    if ([string]::IsNullOrWhiteSpace($filter)) { throw 'The release filter is empty.' }
    $selectors = @($filter.Split('|') | ForEach-Object {
        $part = $_.Trim()
        if ($part -notmatch '^FullyQualifiedName=(Minecraft\.Tests\.[A-Za-z0-9_]+\.[A-Za-z0-9_]+)$') {
            throw ('Unsupported release selector: ' + $part)
        }
        $matches[1]
    })
    if ($selectors.Count -eq 0 -or @($selectors | Select-Object -Unique).Count -ne $selectors.Count) {
        throw 'The release profile has no selectors or contains duplicates.'
    }
    return $selectors
}
