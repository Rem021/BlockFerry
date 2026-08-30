[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedRepository = [System.IO.Path]::GetFullPath($RepositoryRoot)
$verifierPath = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedRepository -ChildPath 'scripts\Verify-Portable.ps1'))
if (-not (Test-Path -LiteralPath $resolvedRepository -PathType Container) -or
    -not (Test-Path -LiteralPath $verifierPath -PathType Leaf)) {
    Write-Output 'FAIL: RepositoryRoot or verifier is missing.'
    exit 1
}

. $verifierPath -PortableRoot $resolvedRepository

$failures = New-Object 'System.Collections.Generic.List[string]'
$testRoot = [System.IO.Path]::GetFullPath((Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ('BlockFerry-PortableVerifierTests-' + [guid]::NewGuid().ToString('N'))))
$tempPrefix = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
if (-not $testRoot.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $testRoot)) {
    Write-Output 'FAIL: Test root is not a fresh contained temp child.'
    exit 1
}

function Add-Failure {
    param([string]$Message)
    [void]$failures.Add($Message)
}

function Assert-ThrowsMessage {
    param(
        [string]$Name,
        [scriptblock]$Action,
        [string]$ExpectedMessage
    )

    try {
        $null = & $Action
        Add-Failure "$Name did not fail."
    }
    catch {
        if (-not [string]::Equals($_.Exception.Message, $ExpectedMessage, [System.StringComparison]::Ordinal)) {
            Add-Failure ("{0} failed with unexpected message: {1}" -f $Name, $_.Exception.Message)
        }
    }
}

function Assert-DoesNotThrow {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    try {
        $null = & $Action
    }
    catch {
        Add-Failure ("{0} unexpectedly failed: {1}" -f $Name, $_.Exception.Message)
    }
}

function New-TestDirectory {
    param([string]$LeafName)

    $candidate = [System.IO.Path]::GetFullPath((Join-Path -Path $testRoot -ChildPath $LeafName))
    if (-not $candidate.StartsWith(($testRoot + [System.IO.Path]::DirectorySeparatorChar), [System.StringComparison]::OrdinalIgnoreCase) -or
        (Test-Path -LiteralPath $candidate)) {
        throw 'Test fixture path is not a fresh contained child.'
    }

    $null = [System.IO.Directory]::CreateDirectory($candidate)
    return $candidate
}

function New-PeFixture {
    param(
        [string]$LeafName,
        [long]$Length = 105MB,
        [uint16]$DosSignature = 0x5A4D,
        [uint32]$PeSignature = 0x00004550,
        [uint16]$Machine = 0x8664
    )

    $fixture = New-TestDirectory -LeafName $LeafName
    $path = [System.IO.Path]::GetFullPath((Join-Path -Path $fixture -ChildPath 'BlockFerry.App.WinUI.exe'))
    $stream = [System.IO.File]::Open(
        $path,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $stream.SetLength($Length)
        $writer = New-Object System.IO.BinaryWriter($stream)
        try {
            $stream.Position = 0
            $writer.Write($DosSignature)
            $stream.Position = 0x3C
            $writer.Write([int32]0x80)
            $stream.Position = 0x80
            $writer.Write($PeSignature)
            $writer.Write($Machine)
            $writer.Flush()
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    return $fixture
}

$null = [System.IO.Directory]::CreateDirectory($testRoot)
try {
    $valid = New-PeFixture -LeafName 'valid-x64'
    Assert-DoesNotThrow `
        -Name 'valid x64 self-contained executable' `
        -Action { Test-SelfContainedRuntime -PortableRootValue $valid }

    $small = New-PeFixture -LeafName 'too-small' -Length 1MB
    Assert-ThrowsMessage `
        -Name 'small executable' `
        -Action { Test-SelfContainedRuntime -PortableRootValue $small } `
        -ExpectedMessage 'The bundled executable is too small to contain the self-contained WinUI runtime.'

    $badDos = New-PeFixture -LeafName 'bad-dos' -DosSignature 0
    Assert-ThrowsMessage `
        -Name 'bad DOS signature' `
        -Action { Test-SelfContainedRuntime -PortableRootValue $badDos } `
        -ExpectedMessage 'The bundled executable has no DOS PE signature.'

    $badPe = New-PeFixture -LeafName 'bad-pe' -PeSignature 0
    Assert-ThrowsMessage `
        -Name 'bad PE signature' `
        -Action { Test-SelfContainedRuntime -PortableRootValue $badPe } `
        -ExpectedMessage 'The bundled executable is not a valid x64 PE image.'

    $badMachine = New-PeFixture -LeafName 'bad-machine' -Machine 0x014C
    Assert-ThrowsMessage `
        -Name 'bad PE machine' `
        -Action { Test-SelfContainedRuntime -PortableRootValue $badMachine } `
        -ExpectedMessage 'The bundled executable is not a valid x64 PE image.'

    $emptyRoot = New-TestDirectory -LeafName 'empty-required-file'
    [System.IO.File]::WriteAllBytes(
        (Join-Path -Path $emptyRoot -ChildPath 'THIRD-PARTY-NOTICES.txt'),
        [byte[]]@())
    Assert-ThrowsMessage `
        -Name 'empty required root file' `
        -Action { Assert-RequiredRootFile -PortableRootValue $emptyRoot -FileName 'THIRD-PARTY-NOTICES.txt' } `
        -ExpectedMessage 'Required root file is empty: THIRD-PARTY-NOTICES.txt'

    $caseParent = New-TestDirectory -LeafName 'physical-leaf-case'
    $actualCaseRoot = [System.IO.Path]::GetFullPath((Join-Path -Path $caseParent -ChildPath 'blockferry-0.1.0-beta.5-win-x64-portable'))
    $null = [System.IO.Directory]::CreateDirectory($actualCaseRoot)
    $requestedCaseRoot = [System.IO.Path]::GetFullPath((Join-Path -Path $caseParent -ChildPath 'BlockFerry-0.1.0-beta.5-win-x64-portable'))
    Assert-ThrowsMessage `
        -Name 'incorrect physical portable leaf casing' `
        -Action { Resolve-PortableRoot -Path $requestedCaseRoot } `
        -ExpectedMessage 'PortableRoot has an unexpected physical folder name.'
}
finally {
    if ($testRoot.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $testRoot -PathType Container)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

if ($failures.Count -ne 0) {
    foreach ($failure in $failures) {
        Write-Output "FAIL: $failure"
    }
    exit 1
}

Write-Output 'PASS: portable verifier function tests'
exit 0
