[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$RepositoryRoot,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$SdkRoot,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$WorkRoot,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputRoot,

    [switch]$ValidateLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ProductVersion = '0.1.0-beta.5'
$PortableFolderName = 'BlockFerry-0.1.0-beta.5-win-x64-portable'
$PortableZipName = 'BlockFerry-0.1.0-beta.5-win-x64-portable.zip'
$RuntimeIdentifier = 'win-x64'
$PublishProfile = 'Portable-x64'
$Configuration = 'Release'
$Platform = 'x64'
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

function Resolve-ExistingRoot {
    param(
        [string]$Path,
        [string]$Role,
        [switch]$Writable
    )

    $resolved = Get-NormalizedFullPath -Path $Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "$Role is not an existing directory."
    }

    $fileSystemRoot = Get-NormalizedFullPath -Path ([System.IO.Path]::GetPathRoot($resolved))
    if ([string]::Equals($resolved, $fileSystemRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Role must not be a filesystem root."
    }

    if ($Writable.IsPresent) {
        $userProfileValue = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)
        if (-not [string]::IsNullOrEmpty($userProfileValue)) {
            $userProfile = Get-NormalizedFullPath -Path $userProfileValue
            if ([string]::Equals($resolved, $userProfile, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "$Role must not be the user-profile root."
            }
        }
    }

    if (Test-PathHasReparsePoint -Path $resolved) {
        throw "$Role or one of its ancestors is a reparse point."
    }

    return $resolved
}

function Test-PathIsSameOrAncestor {
    param(
        [string]$PossibleAncestor,
        [string]$Path
    )

    $ancestor = Get-NormalizedFullPath -Path $PossibleAncestor
    $candidate = Get-NormalizedFullPath -Path $Path
    if ([string]::Equals($ancestor, $candidate, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    return $candidate.StartsWith((Get-RootPrefix -Root $ancestor), [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-RoleRootIsolation {
    param(
        [string]$Repository,
        [string]$Sdk,
        [string]$Work,
        [string]$Output
    )

    $roles = [ordered]@{
        RepositoryRoot = $Repository
        SdkRoot = $Sdk
        WorkRoot = $Work
        OutputRoot = $Output
    }
    $roleNames = @($roles.Keys)
    for ($leftIndex = 0; $leftIndex -lt $roleNames.Count; $leftIndex++) {
        for ($rightIndex = $leftIndex + 1; $rightIndex -lt $roleNames.Count; $rightIndex++) {
            $leftName = $roleNames[$leftIndex]
            $rightName = $roleNames[$rightIndex]
            if ([string]::Equals($roles[$leftName], $roles[$rightName], [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "$leftName and $rightName must not be equal."
            }
        }
    }

    foreach ($mutableRoot in @($Work, $Output)) {
        foreach ($protectedRoot in @($Repository, $Sdk)) {
            if ((Test-PathIsSameOrAncestor -PossibleAncestor $mutableRoot -Path $protectedRoot) -or
                (Test-PathIsSameOrAncestor -PossibleAncestor $protectedRoot -Path $mutableRoot)) {
                throw 'Mutable roots must not overlap repository or SDK roots.'
            }
        }
    }

    if ((Test-PathIsSameOrAncestor -PossibleAncestor $Work -Path $Output) -or
        (Test-PathIsSameOrAncestor -PossibleAncestor $Output -Path $Work)) {
        throw 'WorkRoot and OutputRoot must not overlap.'
    }
}

function Resolve-RequiredChildFile {
    param(
        [string]$Root,
        [string]$RelativePath,
        [string]$Label
    )

    $resolvedRoot = Get-NormalizedFullPath -Path $Root
    $candidate = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedRoot -ChildPath $RelativePath))
    if ([string]::Equals($candidate, $resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $candidate.StartsWith((Get-RootPrefix -Root $resolvedRoot), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label escapes its intended root."
    }

    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "$Label is missing."
    }

    if (Test-PathHasReparsePoint -Path $candidate) {
        throw "$Label or one of its ancestors is a reparse point."
    }

    return $candidate
}

function Resolve-RequiredChildDirectory {
    param(
        [string]$Root,
        [string]$RelativePath,
        [string]$Label
    )

    $resolvedRoot = Get-NormalizedFullPath -Path $Root
    $candidate = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedRoot -ChildPath $RelativePath))
    if ([string]::Equals($candidate, $resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $candidate.StartsWith((Get-RootPrefix -Root $resolvedRoot), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label escapes its intended root."
    }

    if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
        throw "$Label is missing."
    }

    if (Test-PathHasReparsePoint -Path $candidate) {
        throw "$Label or one of its ancestors is a reparse point."
    }

    return $candidate
}

function Get-SafeFreshCandidate {
    param(
        [string]$IntendedRoot,
        [string]$ChildPath,
        [string[]]$ForbiddenRoots
    )

    $resolvedRoot = Get-NormalizedFullPath -Path $IntendedRoot
    $resolvedChild = [System.IO.Path]::GetFullPath($ChildPath)
    $fileSystemRoot = Get-NormalizedFullPath -Path ([System.IO.Path]::GetPathRoot($resolvedChild))
    if ([string]::Equals($resolvedChild, $resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'A mutable child must not equal its intended root.'
    }

    if (-not $resolvedChild.StartsWith((Get-RootPrefix -Root $resolvedRoot), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'A mutable child escapes its intended root.'
    }

    if ([string]::Equals($resolvedChild, $fileSystemRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'A mutable child must not be a filesystem root.'
    }

    $userProfileValue = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)
    if (-not [string]::IsNullOrEmpty($userProfileValue)) {
        $userProfile = Get-NormalizedFullPath -Path $userProfileValue
        if ([string]::Equals($resolvedChild, $userProfile, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'A mutable child must not equal the user-profile root.'
        }
    }

    foreach ($forbiddenRoot in $ForbiddenRoots) {
        if ([string]::Equals($resolvedChild, $forbiddenRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'A mutable child equals a protected root.'
        }
    }

    if (Test-Path -LiteralPath $resolvedChild) {
        throw "Fresh child already exists: $resolvedChild"
    }

    if (Test-PathHasReparsePoint -Path $resolvedChild) {
        throw 'A mutable child has a reparse-point ancestor.'
    }

    return $resolvedChild
}

function Assert-SafeFreshChild {
    param(
        [string]$IntendedRoot,
        [string]$ChildPath,
        [string[]]$ForbiddenRoots
    )

    $resolvedChild = Get-SafeFreshCandidate -IntendedRoot $IntendedRoot -ChildPath $ChildPath -ForbiddenRoots $ForbiddenRoots
    $null = [System.IO.Directory]::CreateDirectory($resolvedChild)
    if (-not (Test-Path -LiteralPath $resolvedChild -PathType Container)) {
        throw 'Fresh directory creation did not produce a directory.'
    }

    $resolvedRoot = Get-NormalizedFullPath -Path $IntendedRoot
    if ([string]::Equals($resolvedChild, $resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedChild.StartsWith((Get-RootPrefix -Root $resolvedRoot), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fresh directory failed post-creation containment verification.'
    }

    if (Test-PathHasReparsePoint -Path $resolvedChild) {
        throw 'Fresh directory failed post-creation reparse verification.'
    }

    return $resolvedChild
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
            throw 'A shipped path is a reparse point.'
        }

        if (-not $currentItem.PSIsContainer) {
            continue
        }

        foreach ($child in @(Get-ChildItem -LiteralPath $current -Force)) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'A shipped path is a reparse point.'
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
        [string]$PortableRoot,
        [string]$FullPath
    )

    $resolvedRoot = Get-NormalizedFullPath -Path $PortableRoot
    $resolvedPath = [System.IO.Path]::GetFullPath($FullPath)
    $rootPrefix = Get-RootPrefix -Root $resolvedRoot
    if (-not $resolvedPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'A shipped path escapes the portable root.'
    }

    $relativePath = $resolvedPath.Substring($rootPrefix.Length)
    if ([System.IO.Path]::IsPathRooted($relativePath)) {
        throw 'A shipped relative path is rooted.'
    }

    $normalized = $relativePath.Replace('\', '/')
    if (-not (Test-SafeNormalizedPath -Path $normalized)) {
        throw 'A shipped relative path is unsafe.'
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

function Assert-AppIconPayloadContract {
    param(
        [string]$SourceIcon,
        [string]$RuntimeRoot,
        [string]$Label
    )

    $resolvedSourceIcon = [System.IO.Path]::GetFullPath($SourceIcon)
    if (-not (Test-Path -LiteralPath $resolvedSourceIcon -PathType Leaf)) {
        throw "$Label source AppIcon.ico is missing."
    }

    $resolvedRuntimeRoot = Resolve-ExistingRoot -Path $RuntimeRoot -Role "$Label runtime root"
    $runtimeIcon = Resolve-RequiredChildFile -Root $resolvedRuntimeRoot -RelativePath 'Assets\AppIcon.ico' -Label "$Label runtime AppIcon.ico"
    $forbiddenRuntimePng = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedRuntimeRoot -ChildPath 'Assets\AppIcon-1024.png'))
    if (Test-Path -LiteralPath $forbiddenRuntimePng) {
        throw "$Label runtime payload must not contain Assets\\AppIcon-1024.png."
    }

    $sourceHash = (Get-Sha256Hex -Path $resolvedSourceIcon).ToUpperInvariant()
    $runtimeHash = (Get-Sha256Hex -Path $runtimeIcon).ToUpperInvariant()
    if (-not [string]::Equals($sourceHash, $runtimeHash, [System.StringComparison]::Ordinal)) {
        throw "$Label runtime AppIcon.ico hash differs from the repository source."
    }
}

function Copy-FileCreateNew {
    param(
        [string]$SourcePath,
        [string]$DestinationPath,
        [string]$Label
    )

    $resolvedSource = [System.IO.Path]::GetFullPath($SourcePath)
    $resolvedDestination = [System.IO.Path]::GetFullPath($DestinationPath)
    if (-not (Test-Path -LiteralPath $resolvedSource -PathType Leaf)) {
        throw "$Label source is missing."
    }

    $sourceItem = Get-Item -LiteralPath $resolvedSource -Force
    if ($sourceItem.Length -le 0) {
        throw "$Label source is empty."
    }

    if (Test-PathHasReparsePoint -Path $resolvedSource) {
        throw "$Label source or one of its ancestors is a reparse point."
    }

    if (Test-Path -LiteralPath $resolvedDestination) {
        throw "$Label destination already exists."
    }

    $destinationParent = [System.IO.Directory]::GetParent($resolvedDestination)
    if ($null -eq $destinationParent -or
        -not (Test-Path -LiteralPath $destinationParent.FullName -PathType Container) -or
        (Test-PathHasReparsePoint -Path $destinationParent.FullName)) {
        throw "$Label destination parent is unavailable or unsafe."
    }

    $destinationCreated = $false
    try {
        $sourceStream = [System.IO.File]::Open(
            $resolvedSource,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        try {
            $destinationStream = [System.IO.File]::Open(
                $resolvedDestination,
                [System.IO.FileMode]::CreateNew,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::None)
            $destinationCreated = $true
            try {
                $sourceStream.CopyTo($destinationStream)
                $destinationStream.Flush()
            }
            finally {
                $destinationStream.Dispose()
            }
        }
        finally {
            $sourceStream.Dispose()
        }

        if (-not [string]::Equals(
            (Get-Sha256Hex -Path $resolvedSource),
            (Get-Sha256Hex -Path $resolvedDestination),
            [System.StringComparison]::Ordinal)) {
            throw "$Label copy hash mismatch."
        }
    }
    catch {
        if ($destinationCreated -and (Test-Path -LiteralPath $resolvedDestination -PathType Leaf)) {
            if (-not (Test-PathHasReparsePoint -Path $resolvedDestination)) {
                [System.IO.File]::Delete($resolvedDestination)
            }
        }

        throw
    }

    return $resolvedDestination
}

function Assert-RawPublishPdbFree {
    param([string]$PublishRoot)

    $resolvedPublishRoot = Resolve-ExistingRoot -Path $PublishRoot -Role 'Raw publish root'
    Assert-NoReparseTree -Root $resolvedPublishRoot
    $pdbFiles = @(Get-ChildItem -LiteralPath $resolvedPublishRoot -Recurse -Force -Filter '*.pdb' -File)
    if ($pdbFiles.Count -ne 0) {
        throw "Raw publish output contains $($pdbFiles.Count) PDB file(s)."
    }
}

function Assert-SingleFilePublish {
    param([string]$PublishRoot)

    $resolvedPublishRoot = Resolve-ExistingRoot -Path $PublishRoot -Role 'Raw single-file publish root'
    Assert-NoReparseTree -Root $resolvedPublishRoot
    $items = @(Get-ChildItem -LiteralPath $resolvedPublishRoot -Force)
    if ($items.Count -ne 1 -or
        $items[0].PSIsContainer -or
        -not [string]::Equals(
            $items[0].Name,
            'BlockFerry.App.WinUI.exe',
            [System.StringComparison]::Ordinal) -or
        $items[0].Length -lt 100MB) {
        throw 'Raw publish must contain exactly one non-empty self-contained BlockFerry executable.'
    }
}

function Copy-RequiredWinUiPublishArtifacts {
    param(
        [string]$TargetDir,
        [string]$PublishRoot
    )

    $resolvedTargetDir = Resolve-ExistingRoot -Path $TargetDir -Role 'WinUI TargetDir'
    $resolvedPublishRoot = Resolve-ExistingRoot -Path $PublishRoot -Role 'Raw publish root'
    $targetExe = Resolve-RequiredChildFile -Root $resolvedTargetDir -RelativePath 'BlockFerry.App.WinUI.exe' -Label 'TargetDir executable'
    $publishedExe = Resolve-RequiredChildFile -Root $resolvedPublishRoot -RelativePath 'BlockFerry.App.WinUI.exe' -Label 'Raw publish executable'
    if ((Get-Item -LiteralPath $targetExe -Force).Length -le 0 -or
        (Get-Item -LiteralPath $publishedExe -Force).Length -le 0) {
        throw 'TargetDir and raw-publish executables must be non-empty.'
    }

    if (-not [string]::Equals(
        (Get-Sha256Hex -Path $targetExe),
        (Get-Sha256Hex -Path $publishedExe),
        [System.StringComparison]::Ordinal)) {
        throw 'TargetDir executable does not match the raw-publish executable.'
    }

    $requiredArtifacts = @(
        'BlockFerry.App.WinUI.pri',
        'App.xbf',
        'MainPage.xbf',
        'MainWindow.xbf',
        'Controls\ConflictResolutionControl.xbf',
        'Controls\ContentAdapterCard.xbf',
        'Controls\MigrationReviewControl.xbf',
        'Controls\OptionCategoryControl.xbf',
        'Controls\OptionsSelectionControl.xbf'
    )
    if ($requiredArtifacts.Count -ne 9) {
        throw 'The WinUI artifact allowlist must contain exactly six items.'
    }

    $copyPlan = New-Object 'System.Collections.Generic.List[object]'
    foreach ($relativePath in $requiredArtifacts) {
        $sourcePath = Resolve-RequiredChildFile -Root $resolvedTargetDir -RelativePath $relativePath -Label "Required WinUI artifact '$relativePath'"
        if ((Get-Item -LiteralPath $sourcePath -Force).Length -le 0) {
            throw "Required WinUI artifact '$relativePath' is empty."
        }

        $destinationPath = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedPublishRoot -ChildPath $relativePath))
        if ([string]::Equals($destinationPath, $resolvedPublishRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not $destinationPath.StartsWith((Get-RootPrefix -Root $resolvedPublishRoot), [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Required WinUI publish destination '$relativePath' escapes the raw publish root."
        }

        if (Test-Path -LiteralPath $destinationPath) {
            throw "Required WinUI publish destination already exists: $relativePath"
        }

        if (Test-PathHasReparsePoint -Path $destinationPath) {
            throw "Required WinUI publish destination '$relativePath' has a reparse-point ancestor."
        }

        $copyPlan.Add([pscustomobject]@{
            RelativePath = $relativePath
            SourcePath = $sourcePath
            DestinationPath = $destinationPath
        })
    }

    $controlsDirectory = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedPublishRoot -ChildPath 'Controls'))
    if (Test-Path -LiteralPath $controlsDirectory) {
        if (-not (Test-Path -LiteralPath $controlsDirectory -PathType Container) -or
            (Test-PathHasReparsePoint -Path $controlsDirectory)) {
            throw 'Raw publish Controls path is not a safe directory.'
        }
    }
    else {
        $null = Assert-SafeFreshChild -IntendedRoot $resolvedPublishRoot -ChildPath $controlsDirectory -ForbiddenRoots @($resolvedTargetDir)
    }

    foreach ($copyItem in $copyPlan) {
        $null = Copy-FileCreateNew `
            -SourcePath $copyItem.SourcePath `
            -DestinationPath $copyItem.DestinationPath `
            -Label "Required WinUI artifact '$($copyItem.RelativePath)'"
    }

    Assert-NoReparseTree -Root $resolvedPublishRoot
}

function Write-PortableManifest {
    param([string]$PortableRoot)

    $resolvedRoot = Get-NormalizedFullPath -Path $PortableRoot
    Assert-NoReparseTree -Root $resolvedRoot
    $manifestPath = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedRoot -ChildPath $ManifestFileName))
    if (Test-Path -LiteralPath $manifestPath) {
        throw 'SHA256SUMS.txt already exists before manifest creation.'
    }

    $ordinalPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    $foldedPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $fileByPath = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::Ordinal)
    $normalizedPaths = New-Object 'System.Collections.Generic.List[string]'
    foreach ($file in @(Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Force -File)) {
        if ([string]::Equals($file.FullName, $manifestPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A manifest input file is a reparse point.'
        }

        $normalizedPath = Get-NormalizedRelativePath -PortableRoot $resolvedRoot -FullPath $file.FullName
        if (-not $ordinalPaths.Add($normalizedPath)) {
            throw 'A duplicate ordinal manifest path was found.'
        }

        if (-not $foldedPaths.Add($normalizedPath)) {
            throw 'A case-folded duplicate manifest path was found.'
        }

        $fileByPath.Add($normalizedPath, $file.FullName)
        $normalizedPaths.Add($normalizedPath)
    }

    if ($normalizedPaths.Count -eq 0) {
        throw 'The portable folder contains no files to manifest.'
    }

    $normalizedPaths.Sort([System.StringComparer]::Ordinal)
    $manifestLines = New-Object 'System.Collections.Generic.List[string]'
    foreach ($normalizedPath in $normalizedPaths) {
        $hash = Get-Sha256Hex -Path $fileByPath[$normalizedPath]
        $manifestLines.Add("$hash  $normalizedPath")
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $manifestText = ($manifestLines -join "`n") + "`n"
    [System.IO.File]::WriteAllText($manifestPath, $manifestText, $utf8NoBom)
    Assert-NoReparseTree -Root $resolvedRoot
}

function Invoke-DotNetChecked {
    param(
        [string]$DotNetExe,
        [string[]]$Arguments,
        [string]$Operation
    )

    & $DotNetExe @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Operation failed with exit code $exitCode."
    }
}

function Invoke-PortableVerifier {
    param(
        [string]$VerifierPath,
        [string]$PortableRoot
    )

    $hostExecutableName = if ([string]::Equals(
        $PSVersionTable.PSEdition,
        'Core',
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        'pwsh.exe'
    }
    else {
        'powershell.exe'
    }
    $powerShellExe = [System.IO.Path]::GetFullPath((Join-Path -Path $PSHOME -ChildPath $hostExecutableName))
    if (-not (Test-Path -LiteralPath $powerShellExe -PathType Leaf)) {
        throw "PowerShell host executable is missing: $powerShellExe"
    }

    $verifierArguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $VerifierPath,
        '-PortableRoot', $PortableRoot
    )
    & $powerShellExe @verifierArguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Portable verifier failed with exit code $exitCode."
    }
}

function Materialize-PortableOutputs {
    param(
        [string]$VerifiedPortableRoot,
        [string]$VerifiedZipPath,
        [string]$OutputRoot,
        [string]$FinalPortableFolder,
        [string]$FinalPortableZip,
        [string]$VerifierPath,
        [string[]]$ForbiddenRoots
    )

    $resolvedPortableRoot = Resolve-ExistingRoot -Path $VerifiedPortableRoot -Role 'Verified work portable root'
    $resolvedZipPath = [System.IO.Path]::GetFullPath($VerifiedZipPath)
    if (-not (Test-Path -LiteralPath $resolvedZipPath -PathType Leaf) -or
        (Get-Item -LiteralPath $resolvedZipPath -Force).Length -le 0 -or
        (Test-PathHasReparsePoint -Path $resolvedZipPath)) {
        throw 'Verified work zip is missing, empty, or unsafe.'
    }

    $resolvedOutput = Resolve-ExistingRoot -Path $OutputRoot -Role 'OutputRoot' -Writable
    $finalFolder = Get-SafeFreshCandidate -IntendedRoot $resolvedOutput -ChildPath $FinalPortableFolder -ForbiddenRoots $ForbiddenRoots
    $finalZip = Get-SafeFreshCandidate -IntendedRoot $resolvedOutput -ChildPath $FinalPortableZip -ForbiddenRoots $ForbiddenRoots
    $temporaryRootCandidate = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedOutput -ChildPath '.blockferry-portable-materializing'))
    $temporaryFolderCandidate = [System.IO.Path]::GetFullPath((Join-Path -Path $temporaryRootCandidate -ChildPath $PortableFolderName))
    $temporaryZipCandidate = [System.IO.Path]::GetFullPath((Join-Path -Path $temporaryRootCandidate -ChildPath $PortableZipName))
    $null = Get-SafeFreshCandidate -IntendedRoot $resolvedOutput -ChildPath $temporaryRootCandidate -ForbiddenRoots $ForbiddenRoots
    $null = Get-SafeFreshCandidate -IntendedRoot $temporaryRootCandidate -ChildPath $temporaryFolderCandidate -ForbiddenRoots $ForbiddenRoots
    $null = Get-SafeFreshCandidate -IntendedRoot $temporaryRootCandidate -ChildPath $temporaryZipCandidate -ForbiddenRoots $ForbiddenRoots

    $temporaryRootCreated = $false
    $temporaryZipCreated = $false
    $finalZipCreated = $false
    $finalFolderCreated = $false
    try {
        $temporaryRoot = Assert-SafeFreshChild -IntendedRoot $resolvedOutput -ChildPath $temporaryRootCandidate -ForbiddenRoots $ForbiddenRoots
        $temporaryRootCreated = $true
        $temporaryFolder = Assert-SafeFreshChild -IntendedRoot $temporaryRoot -ChildPath $temporaryFolderCandidate -ForbiddenRoots $ForbiddenRoots
        $portableItems = @(Get-ChildItem -LiteralPath $resolvedPortableRoot -Force)
        if ($portableItems.Count -eq 0) {
            throw 'Verified work portable root is empty.'
        }

        foreach ($portableItem in $portableItems) {
            Copy-Item -LiteralPath $portableItem.FullName -Destination $temporaryFolder -Recurse
        }
        Assert-NoReparseTree -Root $temporaryFolder
        Invoke-PortableVerifier -VerifierPath $VerifierPath -PortableRoot $temporaryFolder

        $temporaryZip = Copy-FileCreateNew -SourcePath $resolvedZipPath -DestinationPath $temporaryZipCandidate -Label 'Portable zip materialization'
        $temporaryZipCreated = $true

        [System.IO.File]::Move($temporaryZip, $finalZip)
        $finalZipCreated = $true
        [System.IO.Directory]::Move($temporaryFolder, $finalFolder)
        $finalFolderCreated = $true
        [System.IO.Directory]::Delete($temporaryRoot, $false)
        $temporaryRootCreated = $false
        Invoke-PortableVerifier -VerifierPath $VerifierPath -PortableRoot $finalFolder
        return
    }
    catch {
        $originalMessage = $_.Exception.Message
        $cleanupFailures = New-Object 'System.Collections.Generic.List[string]'

        if ($finalFolderCreated -and (Test-Path -LiteralPath $finalFolder -PathType Container)) {
            try {
                Assert-NoReparseTree -Root $finalFolder
                [System.IO.Directory]::Delete($finalFolder, $true)
            }
            catch {
                $cleanupFailures.Add($_.Exception.Message)
            }
        }

        if ($finalZipCreated -and (Test-Path -LiteralPath $finalZip -PathType Leaf)) {
            try {
                if (Test-PathHasReparsePoint -Path $finalZip) {
                    throw 'Final zip cleanup refused a reparse point.'
                }

                if (-not [string]::Equals(
                    (Get-Sha256Hex -Path $resolvedZipPath),
                    (Get-Sha256Hex -Path $finalZip),
                    [System.StringComparison]::Ordinal)) {
                    throw 'Final zip cleanup refused a hash mismatch.'
                }

                [System.IO.File]::Delete($finalZip)
            }
            catch {
                $cleanupFailures.Add($_.Exception.Message)
            }
        }

        if ($temporaryZipCreated -and (Test-Path -LiteralPath $temporaryZipCandidate -PathType Leaf)) {
            try {
                if (Test-PathHasReparsePoint -Path $temporaryZipCandidate) {
                    throw 'Temporary zip cleanup refused a reparse point.'
                }

                [System.IO.File]::Delete($temporaryZipCandidate)
            }
            catch {
                $cleanupFailures.Add($_.Exception.Message)
            }
        }

        if ($temporaryRootCreated -and (Test-Path -LiteralPath $temporaryRootCandidate -PathType Container)) {
            try {
                Assert-NoReparseTree -Root $temporaryRootCandidate
                [System.IO.Directory]::Delete($temporaryRootCandidate, $true)
            }
            catch {
                $cleanupFailures.Add($_.Exception.Message)
            }
        }

        if ($cleanupFailures.Count -ne 0) {
            throw "Output materialization failed: $originalMessage Cleanup also failed: $($cleanupFailures -join ' | ')"
        }

        throw "Output materialization failed: $originalMessage"
    }
}

function Invoke-PublishPortable {
    param(
        [string]$RepositoryRootValue,
        [string]$SdkRootValue,
        [string]$WorkRootValue,
        [string]$OutputRootValue,
        [switch]$ValidateLaunchValue
    )

    if ($ValidateLaunchValue.IsPresent) {
        throw 'ValidateLaunch is not implemented in Task 2.'
    }

    $resolvedRepository = Resolve-ExistingRoot -Path $RepositoryRootValue -Role 'RepositoryRoot'
    $resolvedSdk = Resolve-ExistingRoot -Path $SdkRootValue -Role 'SdkRoot'
    $resolvedWork = Resolve-ExistingRoot -Path $WorkRootValue -Role 'WorkRoot' -Writable
    $resolvedOutput = Resolve-ExistingRoot -Path $OutputRootValue -Role 'OutputRoot' -Writable
    Assert-RoleRootIsolation -Repository $resolvedRepository -Sdk $resolvedSdk -Work $resolvedWork -Output $resolvedOutput
    $protectedRoots = @($resolvedRepository, $resolvedSdk, $resolvedWork, $resolvedOutput)

    $dotNetExe = Resolve-RequiredChildFile -Root $resolvedSdk -RelativePath 'dotnet.exe' -Label 'dotnet.exe'
    $winUiProject = Resolve-RequiredChildFile -Root $resolvedRepository -RelativePath 'src\BlockFerry.App.WinUI\BlockFerry.App.WinUI.csproj' -Label 'WinUI project'
    $null = Resolve-RequiredChildFile -Root $resolvedRepository -RelativePath 'src\BlockFerry.App.WinUI\Properties\PublishProfiles\Portable-x64.pubxml' -Label 'Portable publish profile'
    $documentationSource = Resolve-RequiredChildFile -Root $resolvedRepository -RelativePath 'docs\PORTABLE-BETA.md' -Label 'Portable documentation'
    $verifierPath = Resolve-RequiredChildFile -Root $resolvedRepository -RelativePath 'scripts\Verify-Portable.ps1' -Label 'Portable verifier'
    $noticesSource = Resolve-RequiredChildFile -Root $resolvedRepository -RelativePath 'THIRD-PARTY-NOTICES.txt' -Label 'Third-party notices'
    $null = Resolve-RequiredChildFile -Root $resolvedRepository -RelativePath 'src\BlockFerry.App.WinUI\Assets\AppIcon.ico' -Label 'Repository AppIcon.ico'

    $stagingCandidate = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedWork -ChildPath 'BlockFerry-0.1.0-beta.5-win-x64-portable-staging'))
    $roundTripCandidate = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedWork -ChildPath 'BlockFerry-0.1.0-beta.5-win-x64-portable-roundtrip'))
    $dotnetHomeCandidate = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedWork -ChildPath 'd'))
    $nugetPackagesCandidate = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedWork -ChildPath 'n'))
    if ($nugetPackagesCandidate.Length -gt 120) {
        throw 'WorkRoot is too long for the isolated NuGet cache used by legacy WinUI XAML compilation.'
    }
    $portableFolderCandidate = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedOutput -ChildPath $PortableFolderName))
    $portableZipCandidate = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedOutput -ChildPath $PortableZipName))

    foreach ($workCandidate in @($stagingCandidate, $roundTripCandidate, $dotnetHomeCandidate, $nugetPackagesCandidate)) {
        $null = Get-SafeFreshCandidate -IntendedRoot $resolvedWork -ChildPath $workCandidate -ForbiddenRoots $protectedRoots
    }
    $null = Get-SafeFreshCandidate -IntendedRoot $resolvedOutput -ChildPath $portableFolderCandidate -ForbiddenRoots $protectedRoots
    $null = Get-SafeFreshCandidate -IntendedRoot $resolvedOutput -ChildPath $portableZipCandidate -ForbiddenRoots $protectedRoots

    $stagingRoot = Assert-SafeFreshChild -IntendedRoot $resolvedWork -ChildPath $stagingCandidate -ForbiddenRoots $protectedRoots
    $stagingPublish = Assert-SafeFreshChild -IntendedRoot $stagingRoot -ChildPath (Join-Path -Path $stagingRoot -ChildPath 'publish') -ForbiddenRoots $protectedRoots
    $portableFolderWorkCandidate = [System.IO.Path]::GetFullPath((Join-Path -Path $stagingRoot -ChildPath $PortableFolderName))
    $portableZip = [System.IO.Path]::GetFullPath((Join-Path -Path $stagingRoot -ChildPath $PortableZipName))
    $null = Get-SafeFreshCandidate -IntendedRoot $stagingRoot -ChildPath $portableFolderWorkCandidate -ForbiddenRoots $protectedRoots
    $null = Get-SafeFreshCandidate -IntendedRoot $stagingRoot -ChildPath $portableZip -ForbiddenRoots $protectedRoots
    $dotnetHome = Assert-SafeFreshChild -IntendedRoot $resolvedWork -ChildPath $dotnetHomeCandidate -ForbiddenRoots $protectedRoots
    $nugetPackages = Assert-SafeFreshChild -IntendedRoot $resolvedWork -ChildPath $nugetPackagesCandidate -ForbiddenRoots $protectedRoots

    $environmentNames = @(
        'DOTNET_ROOT',
        'DOTNET_ROOT_X64',
        'DOTNET_CLI_HOME',
        'NUGET_PACKAGES',
        'DOTNET_MULTILEVEL_LOOKUP'
    )
    $environmentSnapshot = @{}
    $processEnvironment = [System.Environment]::GetEnvironmentVariables([System.EnvironmentVariableTarget]::Process)
    foreach ($environmentName in $environmentNames) {
        $environmentSnapshot[$environmentName] = [pscustomobject]@{
            Existed = $processEnvironment.Contains($environmentName)
            Value = [System.Environment]::GetEnvironmentVariable($environmentName, [System.EnvironmentVariableTarget]::Process)
        }
    }

    try {
        [System.Environment]::SetEnvironmentVariable('DOTNET_ROOT', $resolvedSdk, [System.EnvironmentVariableTarget]::Process)
        [System.Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $resolvedSdk, [System.EnvironmentVariableTarget]::Process)
        [System.Environment]::SetEnvironmentVariable('DOTNET_CLI_HOME', $dotnetHome, [System.EnvironmentVariableTarget]::Process)
        [System.Environment]::SetEnvironmentVariable('NUGET_PACKAGES', $nugetPackages, [System.EnvironmentVariableTarget]::Process)
        [System.Environment]::SetEnvironmentVariable('DOTNET_MULTILEVEL_LOOKUP', '0', [System.EnvironmentVariableTarget]::Process)

        $restoreArguments = @(
            'restore', $winUiProject,
            '-r', 'win-x64',
            '-p:NuGetAudit=false',
            '-p:PublishProfile=Portable-x64',
            '-p:DebugType=None',
            '-p:DebugSymbols=false'
        )
        Invoke-DotNetChecked -DotNetExe $dotNetExe -Arguments $restoreArguments -Operation 'dotnet restore'

        $publishArguments = @(
            'publish', $winUiProject,
            '-c', 'Release',
            '-p:Platform=x64',
            '-r', 'win-x64',
            '--no-restore',
            '-p:PublishProfile=Portable-x64',
            '-p:DebugType=None',
            '-p:DebugSymbols=false',
            "-p:PublishDir=$stagingPublish"
        )
        Invoke-DotNetChecked -DotNetExe $dotNetExe -Arguments $publishArguments -Operation 'dotnet publish'
    }
    finally {
        foreach ($environmentName in $environmentNames) {
            $snapshot = $environmentSnapshot[$environmentName]
            if ($snapshot.Existed) {
                [System.Environment]::SetEnvironmentVariable($environmentName, $snapshot.Value, [System.EnvironmentVariableTarget]::Process)
            }
            else {
                [System.Environment]::SetEnvironmentVariable($environmentName, $null, [System.EnvironmentVariableTarget]::Process)
            }
        }
    }

    Assert-NoReparseTree -Root $stagingPublish
    $publishItems = @(Get-ChildItem -LiteralPath $stagingPublish -Force)
    if ($publishItems.Count -eq 0) {
        throw 'Publish output is empty.'
    }

    Assert-RawPublishPdbFree -PublishRoot $stagingPublish
    Assert-SingleFilePublish -PublishRoot $stagingPublish

    $portableFolder = Assert-SafeFreshChild -IntendedRoot $stagingRoot -ChildPath $portableFolderWorkCandidate -ForbiddenRoots $protectedRoots
    $publishItems = @(Get-ChildItem -LiteralPath $stagingPublish -Force)
    foreach ($publishItem in $publishItems) {
        Copy-Item -LiteralPath $publishItem.FullName -Destination $portableFolder -Recurse
    }
    Assert-NoReparseTree -Root $portableFolder

    $utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $documentationText = [System.IO.File]::ReadAllText($documentationSource, $utf8Strict)
    $readmeDestination = [System.IO.Path]::GetFullPath((Join-Path -Path $portableFolder -ChildPath $ReadmeFileName))
    if (Test-Path -LiteralPath $readmeDestination) {
        throw 'The shipped readme path already exists in publish output.'
    }
    [System.IO.File]::WriteAllText($readmeDestination, $documentationText, $utf8NoBom)

    $noticesDestination = [System.IO.Path]::GetFullPath((Join-Path -Path $portableFolder -ChildPath 'THIRD-PARTY-NOTICES.txt'))
    if (Test-Path -LiteralPath $noticesDestination -PathType Leaf) {
        if (-not [string]::Equals(
            (Get-Sha256Hex -Path $noticesSource),
            (Get-Sha256Hex -Path $noticesDestination),
            [System.StringComparison]::Ordinal)) {
            throw 'Published third-party notices differ from the repository source.'
        }
    }
    else {
        Copy-Item -LiteralPath $noticesSource -Destination $noticesDestination
    }

    Write-PortableManifest -PortableRoot $portableFolder
    Invoke-PortableVerifier -VerifierPath $verifierPath -PortableRoot $portableFolder

    Compress-Archive -LiteralPath $portableFolder -DestinationPath $portableZip
    if (-not (Test-Path -LiteralPath $portableZip -PathType Leaf)) {
        throw 'Portable zip creation did not produce a file.'
    }
    if (Test-PathHasReparsePoint -Path $portableZip) {
        throw 'Portable zip or one of its ancestors is a reparse point.'
    }

    $roundTripRoot = Assert-SafeFreshChild -IntendedRoot $resolvedWork -ChildPath $roundTripCandidate -ForbiddenRoots $protectedRoots
    Expand-Archive -LiteralPath $portableZip -DestinationPath $roundTripRoot
    Assert-NoReparseTree -Root $roundTripRoot

    $roundTripItems = @(Get-ChildItem -LiteralPath $roundTripRoot -Force)
    if ($roundTripItems.Count -ne 1 -or
        -not $roundTripItems[0].PSIsContainer -or
        -not [string]::Equals($roundTripItems[0].Name, $PortableFolderName, [System.StringComparison]::Ordinal)) {
        throw 'Zip round-trip must contain exactly one correctly named top-level folder.'
    }

    $roundTripPortableRoot = [System.IO.Path]::GetFullPath($roundTripItems[0].FullName)
    if (-not $roundTripPortableRoot.StartsWith((Get-RootPrefix -Root $roundTripRoot), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Expanded portable folder escapes the round-trip root.'
    }
    Assert-NoReparseTree -Root $roundTripPortableRoot
    Invoke-PortableVerifier -VerifierPath $verifierPath -PortableRoot $roundTripPortableRoot

    $null = Materialize-PortableOutputs -VerifiedPortableRoot $portableFolder -VerifiedZipPath $portableZip `
        -OutputRoot $resolvedOutput `
        -FinalPortableFolder $portableFolderCandidate `
        -FinalPortableZip $portableZipCandidate `
        -VerifierPath $verifierPath `
        -ForbiddenRoots $protectedRoots

    Write-Output 'PASS: portable folder, manifest, and zip round-trip verified'
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        if ($ValidateLaunch.IsPresent) {
            throw 'ValidateLaunch is not implemented in Task 2.'
        }

        Invoke-PublishPortable `
            -RepositoryRootValue $RepositoryRoot `
            -SdkRootValue $SdkRoot `
            -WorkRootValue $WorkRoot `
            -OutputRootValue $OutputRoot `
            -ValidateLaunchValue:$ValidateLaunch
        exit 0
    }
    catch {
        Write-Output ("FAIL: {0}" -f $_.Exception.Message)
        exit 1
    }
}
