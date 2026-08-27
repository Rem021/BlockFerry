#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path -Path $PSScriptRoot -ChildPath 'Invoke-BlockFerryRuntimeQa.ps1'
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw "Missing runtime harness: $scriptPath"
}

$bytes = [System.IO.File]::ReadAllBytes($scriptPath)
if (@($bytes | Where-Object { $_ -gt 127 }).Count -ne 0) {
    throw 'The Windows PowerShell 5.1 runtime harness must remain ASCII-only.'
}

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath,
    [ref]$tokens,
    [ref]$parseErrors)
if (@($parseErrors).Count -ne 0) {
    throw ('Runtime harness parse errors: ' + (($parseErrors | ForEach-Object Message) -join '; '))
}

$source = [System.IO.File]::ReadAllText($scriptPath)
foreach ($fragment in @(
    '[string]$ExecutablePath',
    '[string]$FixtureRoot',
    'Start-Process',
    '-PassThru',
    'BlockFerry.App.WinUI.exe',
    'BlockFerry',
    'MainWindowTitle',
    'Responding',
    'CloseMainWindow()',
    'WaitForExit(',
    'Get-FileHash',
    'ArtifactHashUnchanged',
    'FixtureHashUnchanged',
    'ManagedLiveSettingUnsupported',
    'production-XAML static gate',
    'report.json',
    'PASS: runtime fixture launch'
)) {
    if ($source.IndexOf($fragment, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Missing runtime harness contract fragment: $fragment"
    }
}

foreach ($pattern in @(
    '(?i)\bStop-Process\b',
    '(?i)\btaskkill\b',
    '(?i)\.Kill\s*\(',
    '(?i)\bSendKeys\b',
    '(?i)\bSendInput\b',
    '(?i)\.minecraft',
    '(?i)options\.txt'
)) {
    if ([regex]::IsMatch($source, $pattern)) {
        throw "Forbidden runtime harness behavior: $pattern"
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path -Path $PSScriptRoot -ChildPath '..\..'))
$xamlPath = Join-Path -Path $repositoryRoot -ChildPath 'src\BlockFerry.App.WinUI\MainPage.xaml'
$migrationPath = Join-Path -Path $repositoryRoot -ChildPath 'src\BlockFerry.App.WinUI\MainPage.Migration.cs'
$xaml = [System.IO.File]::ReadAllText($xamlPath)
$migration = [System.IO.File]::ReadAllText($migrationPath)
if ([regex]::Matches($xaml, 'AutomationProperties\.LiveSetting="Polite"').Count -ne 1) {
    throw 'Production XAML must contain exactly one Polite live region.'
}
foreach ($fragment in @(
    'x:Name="DrawerLayer"',
    '<Border.TabFocusNavigation>Cycle</Border.TabFocusNavigation>',
    'x:Name="MigrationReviewControl"',
    'x:Name="RecoveryFolderButton"',
    'x:Name="RecoverNowButton"',
    'x:Name="ExportRecoveryDiagnosticButton"'
)) {
    if ($xaml.IndexOf($fragment, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Production XAML is missing runtime contract fragment: $fragment"
    }
}
if ($migration.IndexOf('TryPlayCommittedSound', [System.StringComparison]::Ordinal) -lt 0 -or
    $migration.IndexOf('MigrationExecutionStatus.Succeeded', [System.StringComparison]::Ordinal) -lt 0) {
    throw 'Committed completion sound binding is missing from the workflow projection.'
}

'PASS: runtime QA static contract'
