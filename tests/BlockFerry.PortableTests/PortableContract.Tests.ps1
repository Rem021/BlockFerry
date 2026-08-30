[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$failures = New-Object 'System.Collections.Generic.List[string]'

function Add-Failure {
    param([string]$Message)
    [void]$failures.Add($Message)
}

function Require-Pattern {
    param(
        [string]$Source,
        [string]$Pattern,
        [string]$Failure
    )

    if (-not [regex]::IsMatch($Source, $Pattern)) {
        Add-Failure $Failure
    }
}

function Read-AsciiScript {
    param(
        [string]$Path,
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Failure "$Label is missing."
        return [pscustomobject]@{ Source = ''; Ast = $null }
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF) {
        Add-Failure "$Label must be UTF-8 without BOM."
    }
    if (@($bytes | Where-Object { $_ -gt 0x7F }).Count -ne 0) {
        Add-Failure "$Label must contain ASCII source only."
    }

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        Add-Failure "$Label has parser errors."
    }

    return [pscustomobject]@{
        Source = [System.IO.File]::ReadAllText($Path)
        Ast = $ast
    }
}

function Get-FunctionNames {
    param([System.Management.Automation.Language.ScriptBlockAst]$Ast)

    if ($null -eq $Ast) {
        return @()
    }

    return @($Ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
    }, $true) | ForEach-Object { $_.Name })
}

function Get-CommandNames {
    param([System.Management.Automation.Language.ScriptBlockAst]$Ast)

    if ($null -eq $Ast) {
        return @()
    }

    return @($Ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst]
    }, $true) | ForEach-Object { $_.GetCommandName() } | Where-Object { $null -ne $_ })
}

$resolvedRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$fileSystemRoot = [System.IO.Path]::GetPathRoot($resolvedRoot)
if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container) -or
    [string]::Equals($resolvedRoot, $fileSystemRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Output 'FAIL: RepositoryRoot is missing or unsafe.'
    exit 1
}

$publisherPath = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedRoot -ChildPath 'scripts\Publish-Portable.ps1'))
$verifierPath = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedRoot -ChildPath 'scripts\Verify-Portable.ps1'))
$profilePath = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedRoot -ChildPath 'src\BlockFerry.App.WinUI\Properties\PublishProfiles\Portable-x64.pubxml'))
$documentationPath = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedRoot -ChildPath 'docs\PORTABLE-BETA.md'))
$rootPrefix = $resolvedRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
foreach ($path in @($publisherPath, $verifierPath, $profilePath, $documentationPath)) {
    if (-not $path.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        Add-Failure 'A portable contract path escapes RepositoryRoot.'
    }
}

if (-not (Test-Path -LiteralPath $profilePath -PathType Leaf)) {
    Add-Failure 'Portable-x64.pubxml is missing.'
}
else {
    try {
        [xml]$profile = Get-Content -LiteralPath $profilePath -Raw
        $expected = [ordered]@{
            TargetFramework = 'net10.0-windows10.0.26100.0'
            WindowsPackageType = 'None'
            RuntimeIdentifier = 'win-x64'
            SelfContained = 'true'
            WindowsAppSDKSelfContained = 'true'
            EnableMsixTooling = 'true'
            IncludeAllContentForSelfExtract = 'true'
            PublishSingleFile = 'true'
            PublishTrimmed = 'false'
            PublishReadyToRun = 'true'
            DebugType = 'None'
            DebugSymbols = 'false'
        }
        $properties = $profile.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*")
        if ($properties.Count -ne $expected.Count) {
            Add-Failure 'Portable-x64.pubxml must contain the closed single-file property set.'
        }
        foreach ($entry in $expected.GetEnumerator()) {
            $name = [string]$entry.Key
            $node = $profile.SelectSingleNode("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='$name']")
            if ($null -eq $node -or
                -not [string]::Equals($node.InnerText.Trim(), [string]$entry.Value, [System.StringComparison]::Ordinal)) {
                Add-Failure "Portable-x64.pubxml has an incorrect '$name' value."
            }
        }
    }
    catch {
        Add-Failure 'Portable-x64.pubxml is not valid XML.'
    }
}

$publisher = Read-AsciiScript -Path $publisherPath -Label 'Publish-Portable.ps1'
$verifier = Read-AsciiScript -Path $verifierPath -Label 'Verify-Portable.ps1'

foreach ($entry in @(
    [pscustomobject]@{
        Parsed = $publisher
        Label = 'Publish-Portable.ps1'
        Functions = @(
            'Resolve-ExistingRoot',
            'Test-PathHasReparsePoint',
            'Assert-SafeFreshChild',
            'Assert-RawPublishPdbFree',
            'Assert-SingleFilePublish',
            'Write-PortableManifest',
            'Invoke-PortableVerifier',
            'Materialize-PortableOutputs',
            'Invoke-PublishPortable'
        )
    },
    [pscustomobject]@{
        Parsed = $verifier
        Label = 'Verify-Portable.ps1'
        Functions = @(
            'Resolve-PortableRoot',
            'Test-PathHasReparsePoint',
            'Read-PortableManifest',
            'Test-SelfContainedRuntime',
            'Invoke-VerifyPortable'
        )
    }
)) {
    $functionNames = Get-FunctionNames -Ast $entry.Parsed.Ast
    foreach ($name in $entry.Functions) {
        if ($functionNames -cnotcontains $name) {
            Add-Failure "$($entry.Label) is missing function '$name'."
        }
    }

    foreach ($forbidden in @(
        'Invoke-Expression',
        'Start-Process',
        'Remove-Item',
        'cmd.exe',
        'runas',
        'taskkill',
        'git'
    )) {
        if ((Get-CommandNames -Ast $entry.Parsed.Ast) -icontains $forbidden) {
            Add-Failure "$($entry.Label) contains forbidden command '$forbidden'."
        }
    }

    Require-Pattern -Source $entry.Parsed.Source -Pattern 'Set-StrictMode\s+-Version\s+Latest' -Failure "$($entry.Label) must enable strict mode."
    Require-Pattern -Source $entry.Parsed.Source -Pattern '\$ErrorActionPreference\s*=\s*''Stop''' -Failure "$($entry.Label) must stop on errors."
    Require-Pattern -Source $entry.Parsed.Source -Pattern '\[System\.IO\.Path\]::GetFullPath' -Failure "$($entry.Label) must normalize paths."
    Require-Pattern -Source $entry.Parsed.Source -Pattern '\[System\.IO\.FileAttributes\]::ReparsePoint' -Failure "$($entry.Label) must reject reparse points."
    Require-Pattern -Source $entry.Parsed.Source -Pattern '-LiteralPath' -Failure "$($entry.Label) must use literal paths."
}

$publisherSource = $publisher.Source
foreach ($fixed in @(
    "`$ProductVersion = '0.1.0-beta.5'",
    "`$PortableFolderName = 'BlockFerry-0.1.0-beta.5-win-x64-portable'",
    "`$PortableZipName = 'BlockFerry-0.1.0-beta.5-win-x64-portable.zip'",
    "`$PublishProfile = 'Portable-x64'",
    "`$Configuration = 'Release'",
    "`$Platform = 'x64'"
)) {
    if ($publisherSource.IndexOf($fixed, [System.StringComparison]::Ordinal) -lt 0) {
        Add-Failure "Publish-Portable.ps1 is missing fixed setting '$fixed'."
    }
}

Require-Pattern -Source $publisherSource -Pattern '(?s)function\s+Assert-SingleFilePublish.+\$items\.Count\s*-ne\s+1.+BlockFerry\.App\.WinUI\.exe.+100MB' -Failure 'Publisher must require exactly one large self-contained executable.'
Require-Pattern -Source $publisherSource -Pattern 'Assert-RawPublishPdbFree\s+-PublishRoot\s+\$stagingPublish(?s).+Assert-SingleFilePublish\s+-PublishRoot\s+\$stagingPublish' -Failure 'Publisher must reject PDBs before accepting the single file.'
Require-Pattern -Source $publisherSource -Pattern '''-p:PublishProfile=Portable-x64''' -Failure 'Publisher must use the fixed profile for restore and publish.'
Require-Pattern -Source $publisherSource -Pattern '''-p:DebugSymbols=false''' -Failure 'Publisher must suppress debug symbols.'
Require-Pattern -Source $publisherSource -Pattern '\[System\.Environment\]::SetEnvironmentVariable' -Failure 'Publisher must isolate and restore build environment variables.'
Require-Pattern -Source $publisherSource -Pattern '\[System\.IO\.FileMode\]::CreateNew' -Failure 'Publisher must materialize files without overwriting.'
Require-Pattern -Source $publisherSource -Pattern '\[System\.IO\.File\]::Move' -Failure 'Publisher must atomically materialize the final zip.'
Require-Pattern -Source $publisherSource -Pattern '\[System\.IO\.Directory\]::Move' -Failure 'Publisher must avoid merging final folders.'
Require-Pattern -Source $publisherSource -Pattern 'SHA256SUMS\.txt' -Failure 'Publisher must create a SHA-256 manifest.'
Require-Pattern -Source $publisherSource -Pattern 'Compress-Archive\s+-LiteralPath' -Failure 'Publisher must create a literal-path zip.'
Require-Pattern -Source $publisherSource -Pattern 'Expand-Archive\s+-LiteralPath' -Failure 'Publisher must verify a literal-path zip round trip.'
Require-Pattern -Source $publisherSource -Pattern 'PASS: portable folder, manifest, and zip round-trip verified' -Failure 'Publisher has an incorrect success marker.'

$verifierSource = $verifier.Source
Require-Pattern -Source $verifierSource -Pattern '0x5A4D' -Failure 'Verifier must check the DOS PE signature.'
Require-Pattern -Source $verifierSource -Pattern '0x00004550' -Failure 'Verifier must check the PE signature.'
Require-Pattern -Source $verifierSource -Pattern '0x8664' -Failure 'Verifier must check the x64 machine value.'
Require-Pattern -Source $verifierSource -Pattern '100MB' -Failure 'Verifier must reject implausibly small self-contained executables.'
Require-Pattern -Source $verifierSource -Pattern '\$rootItems\.Count\s*-ne\s+4' -Failure 'Verifier must require a clean four-file root.'
Require-Pattern -Source $verifierSource -Pattern '\*\.pdb' -Failure 'Verifier must reject PDB files.'
Require-Pattern -Source $verifierSource -Pattern 'manifest file set' -Failure 'Verifier must enforce manifest file-set equality.'
Require-Pattern -Source $verifierSource -Pattern 'PASS: portable artifact verification' -Failure 'Verifier has an incorrect success marker.'

if (-not (Test-Path -LiteralPath $documentationPath -PathType Leaf)) {
    Add-Failure 'PORTABLE-BETA.md is missing.'
}
else {
    $documentationBytes = [System.IO.File]::ReadAllBytes($documentationPath)
    try {
        $strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
        $documentation = $strictUtf8.GetString($documentationBytes)
    }
    catch {
        Add-Failure 'PORTABLE-BETA.md must be valid UTF-8.'
        $documentation = ''
    }

    foreach ($anchor in @(
        'BlockFerry.App.WinUI.exe',
        'Windows 10 1809',
        'self-contained single-file',
        'Windows App SDK',
        'SHA256SUMS.txt',
        'THIRD-PARTY-NOTICES.txt',
        'JEI',
        'latest.log',
        '20',
        '90'
    )) {
        if ($documentation.IndexOf($anchor, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            Add-Failure "PORTABLE-BETA.md is missing anchor '$anchor'."
        }
    }
}

if ($failures.Count -ne 0) {
    foreach ($failure in $failures) {
        Write-Output "FAIL: $failure"
    }
    exit 1
}

Write-Output 'PASS: portable script contract'
exit 0
