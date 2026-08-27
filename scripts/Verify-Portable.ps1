[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PortableRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ProductVersion = '0.1.0-beta.4'
$PortableFolderName = 'BlockFerry-0.1.0-beta.4-win-x64-portable'
$PortableZipName = 'BlockFerry-0.1.0-beta.4-win-x64-portable.zip'
$RuntimeIdentifier = 'win-x64'
$ManifestFileName = 'SHA256SUMS.txt'
$ReadmeFileName = 'README-' + [char]0x5148 + [char]0x8BFB + '.txt'

function Get-NormalizedFullPath {
    param([string]$Path)

    $resolved = [System.IO.Path]::GetFullPath($Path)
    $fileSystemRoot = [System.IO.Path]::GetPathRoot($resolved)
    if (-not [string]::Equals($resolved, $fileSystemRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        $separators = [char[]]@(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar
        )
        $resolved = $resolved.TrimEnd($separators)
    }

    return $resolved
}

function Get-RootPrefix {
    param([string]$Root)

    $normalizedRoot = Get-NormalizedFullPath -Path $Root
    $fileSystemRoot = [System.IO.Path]::GetPathRoot($normalizedRoot)
    if ([string]::Equals($normalizedRoot, $fileSystemRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $normalizedRoot
    }

    return $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
}

function Test-PathHasReparsePoint {
    param([string]$Path)

    $current = [System.IO.Path]::GetFullPath($Path)
    while (-not (Test-Path -LiteralPath $current)) {
        $parent = [System.IO.Directory]::GetParent($current)
        if ($null -eq $parent) {
            break
        }

        $current = $parent.FullName
    }

    while (Test-Path -LiteralPath $current) {
        $attributes = [System.IO.File]::GetAttributes($current)
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            return $true
        }

        $fileSystemRoot = [System.IO.Path]::GetPathRoot($current)
        if ([string]::Equals(
            (Get-NormalizedFullPath -Path $current),
            (Get-NormalizedFullPath -Path $fileSystemRoot),
            [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }

        $parent = [System.IO.Directory]::GetParent($current)
        if ($null -eq $parent) {
            break
        }

        $current = $parent.FullName
    }

    return $false
}

function Resolve-PortableRoot {
    param([string]$Path)

    $resolved = Get-NormalizedFullPath -Path $Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw 'PortableRoot is not an existing directory.'
    }

    $fileSystemRoot = Get-NormalizedFullPath -Path ([System.IO.Path]::GetPathRoot($resolved))
    if ([string]::Equals($resolved, $fileSystemRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'PortableRoot must not be a filesystem root.'
    }

    $leafName = [System.IO.Path]::GetFileName($resolved)
    if (-not [string]::Equals($leafName, $PortableFolderName, [System.StringComparison]::Ordinal)) {
        throw 'PortableRoot has an unexpected folder name.'
    }

    $physicalParent = [System.IO.Directory]::GetParent($resolved)
    if ($null -eq $physicalParent) {
        throw 'PortableRoot has no physical parent directory.'
    }

    $physicalRootMatches = @(Get-ChildItem -LiteralPath $physicalParent.FullName -Force | Where-Object {
        $_.PSIsContainer -and
        [string]::Equals($_.Name, $PortableFolderName, [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($physicalRootMatches.Count -ne 1 -or
        -not [string]::Equals($physicalRootMatches[0].Name, $PortableFolderName, [System.StringComparison]::Ordinal)) {
        throw 'PortableRoot has an unexpected physical folder name.'
    }

    if (Test-PathHasReparsePoint -Path $resolved) {
        throw 'PortableRoot or one of its ancestors is a reparse point.'
    }

    return $resolved
}

function Assert-NoReparseTree {
    param([string]$Root)

    $resolvedRoot = Get-NormalizedFullPath -Path $Root
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($resolvedRoot)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        $currentItem = Get-Item -LiteralPath $current -Force
        if (($currentItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'PortableRoot contains a reparse point.'
        }

        if (-not $currentItem.PSIsContainer) {
            continue
        }

        foreach ($child in @(Get-ChildItem -LiteralPath $current -Force)) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'PortableRoot contains a reparse point.'
            }

            if ($child.PSIsContainer) {
                $pending.Push($child.FullName)
            }
        }
    }
}

function Test-SafeNormalizedPath {
    param([string]$Path)

    if ([string]::IsNullOrEmpty($Path) -or
        [System.IO.Path]::IsPathRooted($Path) -or
        $Path.IndexOf(':') -ge 0 -or
        $Path.IndexOf('\') -ge 0) {
        return $false
    }

    $segments = $Path.Split([char[]]@('/'))
    foreach ($segment in $segments) {
        if ([string]::IsNullOrEmpty($segment) -or $segment -ceq '.' -or $segment -ceq '..') {
            return $false
        }

        if ($segment.EndsWith('.', [System.StringComparison]::Ordinal) -or
            $segment.EndsWith(' ', [System.StringComparison]::Ordinal)) {
            return $false
        }

        $baseName = $segment
        $dotIndex = $baseName.IndexOf('.')
        if ($dotIndex -ge 0) {
            $baseName = $baseName.Substring(0, $dotIndex)
        }

        if ($baseName -imatch '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
            return $false
        }
    }

    return $true
}

function Get-NormalizedRelativePath {
    param(
        [string]$PortableRootValue,
        [string]$FullPath
    )

    $resolvedRoot = Get-NormalizedFullPath -Path $PortableRootValue
    $resolvedPath = [System.IO.Path]::GetFullPath($FullPath)
    $rootPrefix = Get-RootPrefix -Root $resolvedRoot
    if (-not $resolvedPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'A portable file escapes PortableRoot.'
    }

    $relativePath = $resolvedPath.Substring($rootPrefix.Length)
    if ([System.IO.Path]::IsPathRooted($relativePath)) {
        throw 'A portable relative path is rooted.'
    }

    $normalized = $relativePath.Replace('\', '/')
    if (-not (Test-SafeNormalizedPath -Path $normalized)) {
        throw 'A portable relative path is unsafe.'
    }

    return $normalized
}

function Get-Sha256Hex {
    param([string]$Path)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            $hashBytes = $sha256.ComputeHash($stream)
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $sha256.Dispose()
    }

    return ([System.BitConverter]::ToString($hashBytes)).Replace('-', '').ToLowerInvariant()
}

function Read-PortableManifest {
    param([string]$PortableRootValue)

    $resolvedRoot = Get-NormalizedFullPath -Path $PortableRootValue
    $manifestPath = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedRoot -ChildPath $ManifestFileName))
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'SHA256SUMS.txt is missing.'
    }

    $manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
    if ($manifestBytes.Length -ge 3 -and
        $manifestBytes[0] -eq 0xEF -and
        $manifestBytes[1] -eq 0xBB -and
        $manifestBytes[2] -eq 0xBF) {
        throw 'SHA256SUMS.txt must not contain a UTF-8 BOM.'
    }

    $strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
    try {
        $manifestText = $strictUtf8.GetString($manifestBytes)
    }
    catch {
        throw 'SHA256SUMS.txt is not valid UTF-8.'
    }

    if ($manifestText.Length -eq 0 -or
        -not $manifestText.EndsWith("`n", [System.StringComparison]::Ordinal) -or
        $manifestText.IndexOf("`r", [System.StringComparison]::Ordinal) -ge 0) {
        throw 'SHA256SUMS.txt must use nonempty LF-terminated lines.'
    }

    $manifestBody = $manifestText.Substring(0, $manifestText.Length - 1)
    if ($manifestBody.Length -eq 0) {
        throw 'SHA256SUMS.txt contains no entries.'
    }

    $manifestLines = @($manifestBody -split "`n")
    $entries = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::Ordinal)
    $foldedPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $manifestPaths = New-Object 'System.Collections.Generic.List[string]'
    foreach ($line in $manifestLines) {
        $match = [regex]::Match($line, '^([0-9a-f]{64})  (.+)$')
        if (-not $match.Success) {
            throw 'SHA256SUMS.txt contains a line with invalid grammar.'
        }

        $hash = $match.Groups[1].Value
        $normalizedPath = $match.Groups[2].Value
        if (-not (Test-SafeNormalizedPath -Path $normalizedPath)) {
            throw 'SHA256SUMS.txt contains an unsafe normalized path.'
        }

        if ([string]::Equals($normalizedPath, $ManifestFileName, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'SHA256SUMS.txt must not include itself.'
        }

        if ($entries.ContainsKey($normalizedPath)) {
            throw 'SHA256SUMS.txt contains a duplicate ordinal path.'
        }

        if (-not $foldedPaths.Add($normalizedPath)) {
            throw 'SHA256SUMS.txt contains a case-folded duplicate path.'
        }

        $entries.Add($normalizedPath, $hash)
        $manifestPaths.Add($normalizedPath)
    }

    $sortedManifestPaths = New-Object 'System.Collections.Generic.List[string]'
    foreach ($manifestPathEntry in $manifestPaths) {
        $sortedManifestPaths.Add($manifestPathEntry)
    }
    $sortedManifestPaths.Sort([System.StringComparer]::Ordinal)
    for ($index = 0; $index -lt $manifestPaths.Count; $index++) {
        if (-not [string]::Equals($manifestPaths[$index], $sortedManifestPaths[$index], [System.StringComparison]::Ordinal)) {
            throw 'SHA256SUMS.txt paths are not sorted ordinally.'
        }
    }

    $actualFiles = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::Ordinal)
    $actualFoldedPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @(Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Force -File)) {
        if ([string]::Equals($file.FullName, $manifestPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $normalizedPath = Get-NormalizedRelativePath -PortableRootValue $resolvedRoot -FullPath $file.FullName
        if ($actualFiles.ContainsKey($normalizedPath)) {
            throw 'PortableRoot contains a duplicate ordinal file path.'
        }

        if (-not $actualFoldedPaths.Add($normalizedPath)) {
            throw 'PortableRoot contains a case-folded duplicate file path.'
        }

        $actualFiles.Add($normalizedPath, $file.FullName)
    }

    if ($actualFiles.Count -ne $entries.Count) {
        throw 'manifest file set count mismatch.'
    }

    $actualPaths = New-Object 'System.Collections.Generic.List[string]'
    foreach ($actualPath in $actualFiles.Keys) {
        $actualPaths.Add($actualPath)
    }
    $actualPaths.Sort([System.StringComparer]::Ordinal)
    foreach ($actualPath in $actualPaths) {
        if (-not $entries.ContainsKey($actualPath)) {
            throw 'manifest file set is missing an actual file.'
        }

        $actualHash = Get-Sha256Hex -Path $actualFiles[$actualPath]
        if (-not [string]::Equals($actualHash, $entries[$actualPath], [System.StringComparison]::Ordinal)) {
            throw 'SHA256SUMS.txt contains a hash mismatch.'
        }
    }

    foreach ($manifestPathEntry in $entries.Keys) {
        if (-not $actualFiles.ContainsKey($manifestPathEntry)) {
            throw 'manifest file set contains an extra entry.'
        }
    }

    return $entries
}

function Test-SelfContainedRuntime {
    param([string]$PortableRootValue)

    $executablePath = [System.IO.Path]::GetFullPath((Join-Path -Path $PortableRootValue -ChildPath 'BlockFerry.App.WinUI.exe'))
    $item = Get-Item -LiteralPath $executablePath -Force
    if ($item.Length -lt 100MB) {
        throw 'The bundled executable is too small to contain the self-contained WinUI runtime.'
    }

    $stream = [System.IO.File]::OpenRead($executablePath)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) {
                throw 'The bundled executable has no DOS PE signature.'
            }

            $stream.Position = 0x3C
            $peOffset = $reader.ReadInt32()
            if ($peOffset -lt 0x40 -or $peOffset -gt ($item.Length - 6)) {
                throw 'The bundled executable has an invalid PE header offset.'
            }

            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
                throw 'The bundled executable is not a valid x64 PE image.'
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-RequiredRootFile {
    param(
        [string]$PortableRootValue,
        [string]$FileName
    )

    $path = [System.IO.Path]::GetFullPath((Join-Path -Path $PortableRootValue -ChildPath $FileName))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required root file is missing: $FileName"
    }

    $physicalMatches = @(Get-ChildItem -LiteralPath $PortableRootValue -Force | Where-Object {
        -not $_.PSIsContainer -and
        [string]::Equals($_.Name, $FileName, [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($physicalMatches.Count -ne 1 -or
        -not [string]::Equals($physicalMatches[0].Name, $FileName, [System.StringComparison]::Ordinal)) {
        throw 'A required root file has incorrect casing.'
    }

    $item = $physicalMatches[0]

    if ($item.Length -le 0) {
        throw "Required root file is empty: $FileName"
    }
}

function Assert-RequiredPortableFile {
    param(
        [string]$PortableRootValue,
        [string]$RelativePath
    )

    if (-not (Test-SafeNormalizedPath -Path $RelativePath)) {
        throw "Required portable path is unsafe: $RelativePath"
    }

    $resolvedRoot = Get-NormalizedFullPath -Path $PortableRootValue
    $rootPrefix = Get-RootPrefix -Root $resolvedRoot
    $currentPath = $resolvedRoot
    $segments = $RelativePath.Split([char[]]@('/'))
    for ($index = 0; $index -lt $segments.Count; $index++) {
        $segment = $segments[$index]
        $isLeaf = $index -eq ($segments.Count - 1)
        $physicalMatches = @(Get-ChildItem -LiteralPath $currentPath -Force | Where-Object {
            [string]::Equals($_.Name, $segment, [System.StringComparison]::OrdinalIgnoreCase)
        })
        if ($physicalMatches.Count -ne 1) {
            throw "Required portable file is missing: $RelativePath"
        }

        $item = $physicalMatches[0]
        if (-not [string]::Equals($item.Name, $segment, [System.StringComparison]::Ordinal)) {
            throw "A required portable path has incorrect casing: $RelativePath"
        }

        if (($isLeaf -and $item.PSIsContainer) -or (-not $isLeaf -and -not $item.PSIsContainer)) {
            throw "Required portable file is missing: $RelativePath"
        }

        $candidate = [System.IO.Path]::GetFullPath($item.FullName)
        if (-not $candidate.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Required portable path escapes PortableRoot: $RelativePath"
        }

        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "A required portable path is a reparse point: $RelativePath"
        }

        $currentPath = $item.FullName
    }

    if ($item.Length -le 0) {
        throw "Required portable file is empty: $RelativePath"
    }
}

function Assert-RequiredResourcePayload {
    param([string]$PortableRootValue)

    foreach ($relativePath in @(
        'BlockFerry.App.WinUI.pri',
        'Microsoft.UI.pri',
        'Microsoft.UI.Xaml.Controls.pri',
        'Microsoft.Windows.Workloads.pri',
        'Microsoft.WindowsAppRuntime.pri',
        'App.xbf',
        'MainPage.xbf',
        'MainWindow.xbf',
        'Controls/ConflictResolutionControl.xbf',
        'Controls/ContentAdapterCard.xbf',
        'Controls/MigrationReviewControl.xbf',
        'Controls/OptionCategoryControl.xbf',
        'Controls/OptionsSelectionControl.xbf'
    )) {
        Assert-RequiredPortableFile -PortableRootValue $PortableRootValue -RelativePath $relativePath
    }
}

function Invoke-VerifyPortable {
    param([string]$PortableRootValue)

    $resolvedRoot = Resolve-PortableRoot -Path $PortableRootValue
    Assert-NoReparseTree -Root $resolvedRoot

    $requiredRootFiles = @(
        'BlockFerry.App.WinUI.exe',
        $ReadmeFileName,
        'THIRD-PARTY-NOTICES.txt',
        'SHA256SUMS.txt'
    )
    foreach ($requiredRootFile in $requiredRootFiles) {
        Assert-RequiredRootFile -PortableRootValue $resolvedRoot -FileName $requiredRootFile
    }
    $rootItems = @(Get-ChildItem -LiteralPath $resolvedRoot -Force)
    if ($rootItems.Count -ne 4 -or $rootItems.Where({ $_.PSIsContainer }).Count -ne 0) {
        throw 'The portable folder must contain exactly four root files and no runtime directories.'
    }

    if (@(Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Force -Filter '*.pdb' -File).Count -ne 0) {
        throw 'The portable folder must not contain PDB files.'
    }

    $null = Read-PortableManifest -PortableRootValue $resolvedRoot
    Test-SelfContainedRuntime -PortableRootValue $resolvedRoot
    Write-Output 'PASS: portable artifact verification'
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        Invoke-VerifyPortable -PortableRootValue $PortableRoot
        exit 0
    }
    catch {
        Write-Output ("FAIL: {0}" -f $_.Exception.Message)
        exit 1
    }
}
