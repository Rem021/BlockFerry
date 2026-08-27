[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path -Path $PSScriptRoot -ChildPath '..'))
$DotNet = (Get-Command dotnet -ErrorAction Stop).Source
$Python = if (-not [string]::IsNullOrWhiteSpace($env:BLOCKFERRY_PYTHON)) {
    [System.IO.Path]::GetFullPath($env:BLOCKFERRY_PYTHON)
}
else {
    (Get-Command python -ErrorAction Stop).Source
}

if (-not (Test-Path -LiteralPath $Python -PathType Leaf)) {
    throw 'Python executable is unavailable. Set BLOCKFERRY_PYTHON to an existing Python 3 executable.'
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

$RunProjects = @(
    'tests\BlockFerry.Core.SmokeTests\BlockFerry.Core.SmokeTests.csproj',
    'tests\BlockFerry.DiscoveryFixtureTests\BlockFerry.DiscoveryFixtureTests.csproj',
    'tests\BlockFerry.Pcl2FixtureTests\BlockFerry.Pcl2FixtureTests.csproj',
    'tests\BlockFerry.ContentFixtureTests\BlockFerry.ContentFixtureTests.csproj',
    'tests\BlockFerry.TransactionFixtureTests\BlockFerry.TransactionFixtureTests.csproj',
    'tests\BlockFerry.AppLogicTests\BlockFerry.AppLogicTests.csproj'
)

foreach ($RelativeProject in $RunProjects) {
    $Project = Join-Path -Path $RepositoryRoot -ChildPath $RelativeProject
    $Arguments = @('run', '--project', $Project, '-c', 'Release')
    if ($RelativeProject -match 'TransactionFixtureTests|AppLogicTests') {
        $Arguments += @('-r', 'win-x64')
    }
    if ($RelativeProject -match 'AppLogicTests') {
        $Arguments += '-p:WindowsAppSdkBootstrapInitialize=false'
    }

    Invoke-Checked -FilePath $DotNet -Arguments $Arguments -Label $RelativeProject
}

Invoke-Checked -FilePath $DotNet -Arguments @(
    'build',
    (Join-Path $RepositoryRoot 'src\BlockFerry.App.WinUI\BlockFerry.App.WinUI.csproj'),
    '-c', 'Release',
    '-p:Platform=x64',
    '-r', 'win-x64'
) -Label 'WinUI Release build'

$FormatProjects = @(
    'src\BlockFerry.Core\BlockFerry.Core.csproj',
    'src\BlockFerry.App.WinUI\BlockFerry.App.WinUI.csproj',
    'tests\BlockFerry.Core.SmokeTests\BlockFerry.Core.SmokeTests.csproj',
    'tests\BlockFerry.DiscoveryFixtureTests\BlockFerry.DiscoveryFixtureTests.csproj',
    'tests\BlockFerry.Pcl2FixtureTests\BlockFerry.Pcl2FixtureTests.csproj',
    'tests\BlockFerry.ContentFixtureTests\BlockFerry.ContentFixtureTests.csproj',
    'tests\BlockFerry.TransactionFixtureTests\BlockFerry.TransactionFixtureTests.csproj',
    'tests\BlockFerry.AppLogicTests\BlockFerry.AppLogicTests.csproj'
)

foreach ($RelativeProject in $FormatProjects) {
    Invoke-Checked -FilePath $DotNet -Arguments @(
        'format',
        (Join-Path $RepositoryRoot $RelativeProject),
        '--verify-no-changes',
        '--no-restore'
    ) -Label "format $RelativeProject"
}

Invoke-Checked -FilePath $Python -Arguments @(
    (Join-Path $RepositoryRoot 'tests\BlockFerry.IconTests\IconContract.Tests.py')
) -Label 'icon contract tests'

Invoke-Checked -FilePath (Get-Command pwsh -ErrorAction Stop).Source -Arguments @(
    '-NoProfile',
    '-File',
    (Join-Path $RepositoryRoot 'tests\BlockFerry.PortableTests\PortableContract.Tests.ps1'),
    '-RepositoryRoot',
    $RepositoryRoot
) -Label 'portable contract tests'

Invoke-Checked -FilePath (Get-Command pwsh -ErrorAction Stop).Source -Arguments @(
    '-NoProfile',
    '-File',
    (Join-Path $RepositoryRoot 'tests\BlockFerry.PortableTests\PortableVerifier.Tests.ps1'),
    '-RepositoryRoot',
    $RepositoryRoot
) -Label 'portable verifier tests'

Write-Output 'PASS: all BlockFerry local gates completed'
