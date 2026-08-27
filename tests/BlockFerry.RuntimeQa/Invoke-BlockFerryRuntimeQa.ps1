#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ExecutablePath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$FixtureRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-NoReparsePoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $current = Get-Item -LiteralPath $Path -Force
    while ($null -ne $current) {
        if (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not allowed in the runtime QA path: $($current.FullName)"
        }

        if ($current -is [System.IO.FileInfo]) {
            $current = $current.Directory
        }
        else {
            $current = $current.Parent
        }
    }
}

function Get-TreeHashMap {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $rootItem = Get-Item -LiteralPath $Root -Force
    if (-not $rootItem.PSIsContainer) {
        throw "Runtime QA tree root is not a directory: $Root"
    }

    Assert-NoReparsePoint -Path $rootItem.FullName
    $prefix = $rootItem.FullName.TrimEnd([char]'\') + '\'
    $items = @(Get-ChildItem -LiteralPath $rootItem.FullName -Force -Recurse)
    foreach ($item in $items) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Runtime QA tree contains a reparse point: $($item.FullName)"
        }
    }

    $map = @()
    foreach ($file in @($items | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)) {
        $relativePath = $file.FullName.Substring($prefix.Length).Replace('\', '/')
        $map += [pscustomobject]@{
            RelativePath = $relativePath
            Length = [long]$file.Length
            Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }

    return @($map)
}

function Convert-HashMapToStableText {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Map
    )

    return (($Map | ForEach-Object {
        '{0}|{1}|{2}' -f $_.RelativePath, $_.Length, $_.Sha256
    }) -join "`n")
}

$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$resolvedFixtureRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
if ([System.IO.Path]::GetFileName($resolvedExecutable) -cne 'BlockFerry.App.WinUI.exe') {
    throw "Unexpected runtime QA executable name: $resolvedExecutable"
}

Assert-NoReparsePoint -Path $resolvedExecutable
Assert-NoReparsePoint -Path $resolvedFixtureRoot

$artifactRoot = [System.IO.Path]::GetDirectoryName($resolvedExecutable)
if ([string]::IsNullOrWhiteSpace($artifactRoot)) {
    throw "The runtime QA executable has no parent directory: $resolvedExecutable"
}
$artifactBefore = @(Get-TreeHashMap -Root $artifactRoot)
$fixtureBefore = @(Get-TreeHashMap -Root $resolvedFixtureRoot)
$artifactBeforeText = Convert-HashMapToStableText -Map $artifactBefore
$fixtureBeforeText = Convert-HashMapToStableText -Map $fixtureBefore

$process = $null
$observedTitle = $null
$observedResponding = $false
$closedGracefully = $false
try {
    $process = Start-Process -FilePath $resolvedExecutable -WorkingDirectory $artifactRoot -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            throw "BlockFerry exited before its window was ready. Exit code: $($process.ExitCode)"
        }

        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and
            $process.MainWindowTitle.StartsWith('BlockFerry', [System.StringComparison]::Ordinal) -and
            $process.Responding) {
            $observedTitle = $process.MainWindowTitle
            $observedResponding = $true
            break
        }

        Start-Sleep -Milliseconds 200
    }

    if (-not $observedResponding) {
        throw 'BlockFerry did not expose a responding owned main window within 30 seconds.'
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $closeRequested = $process.CloseMainWindow()
        if (-not $closeRequested) {
            throw 'BlockFerry did not accept CloseMainWindow().'
        }

        $closedGracefully = $process.WaitForExit(15000)
        if (-not $closedGracefully) {
            throw 'BlockFerry did not exit within 15 seconds after CloseMainWindow().'
        }
    }
    elseif ($null -ne $process) {
        $closedGracefully = $true
    }
}

$artifactAfter = @(Get-TreeHashMap -Root $artifactRoot)
$fixtureAfter = @(Get-TreeHashMap -Root $resolvedFixtureRoot)
$artifactHashUnchanged = $artifactBeforeText -ceq (Convert-HashMapToStableText -Map $artifactAfter)
$fixtureHashUnchanged = $fixtureBeforeText -ceq (Convert-HashMapToStableText -Map $fixtureAfter)
if (-not $artifactHashUnchanged) {
    throw 'The application artifact tree changed during the runtime launch audit.'
}

if (-not $fixtureHashUnchanged) {
    throw 'The fixture root changed during the runtime launch audit.'
}

$reportRoot = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ('BlockFerry-RuntimeQa-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $reportRoot
$reportPath = Join-Path -Path $reportRoot -ChildPath 'report.json'
$report = [ordered]@{
    ExecutablePath = $resolvedExecutable
    ProcessId = $process.Id
    MainWindowTitle = $observedTitle
    Responding = $observedResponding
    ClosedGracefully = $closedGracefully
    ArtifactHashUnchanged = $artifactHashUnchanged
    FixtureHashUnchanged = $fixtureHashUnchanged
    ManagedLiveSettingUnsupported = $true
    AccessibilityEvidence = 'production-XAML static gate'
    ArtifactFileCount = $artifactAfter.Count
    FixtureFileCount = $fixtureAfter.Count
}
[System.IO.File]::WriteAllText(
    $reportPath,
    ($report | ConvertTo-Json -Depth 4),
    (New-Object System.Text.UTF8Encoding($false)))

Write-Output ("Runtime QA report: $reportPath")
Write-Output 'PASS: runtime fixture launch'
