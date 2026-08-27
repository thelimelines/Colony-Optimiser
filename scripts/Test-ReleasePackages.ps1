[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArtifactRoot,
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Exists {
    param([string]$Path, [string]$Description)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found: $Path"
    }
}

function Assert-Checksum {
    param([string]$AssetPath)

    $checksumPath = "$AssetPath.sha256"
    Assert-Exists -Path $checksumPath -Description 'Checksum file'
    $expectedHash = (Get-Content -LiteralPath $checksumPath -Raw).Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)[0]
    $actualHash = (Get-FileHash -LiteralPath $AssetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Checksum mismatch for $AssetPath."
    }
}

function Get-LogText {
    param([string]$LogPath)

    if (Test-Path -LiteralPath $LogPath) {
        return Get-Content -LiteralPath $LogPath -Raw
    }

    return 'No log was created.'
}

function Test-SetupUserInterface {
    param([string]$SetupPath, [string]$LogPath)

    $process = Start-Process -FilePath $SetupPath -ArgumentList "-log `"$LogPath`"" -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 4
    $process.Refresh()
    if ($process.HasExited) {
        throw "Setup exited before displaying its install UI (exit code $($process.ExitCode)).`n$(Get-LogText -LogPath $LogPath)"
    }

    if ((Get-LogText -LogPath $LogPath) -match 'Failed to initialize theme') {
        throw "Setup remained running but failed to initialize its theme.`n$(Get-LogText -LogPath $LogPath)"
    }

    $process.CloseMainWindow() | Out-Null
    Start-Sleep -Seconds 2
    $process.Refresh()
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
}

function Test-SetupLayout {
    param([string]$SetupPath, [string]$LayoutPath, [string]$LogPath)

    New-Item -ItemType Directory -Path $LayoutPath | Out-Null
    $arguments = "-layout `"$LayoutPath`" -quiet -log `"$LogPath`""
    $process = Start-Process -FilePath $SetupPath -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "Setup layout failed with exit code $($process.ExitCode).`n$(Get-LogText -LogPath $LogPath)"
    }

    $layoutBundlePath = Join-Path $LayoutPath (Split-Path -Leaf $SetupPath)
    if (-not (Test-Path -LiteralPath $layoutBundlePath -PathType Leaf)) {
        throw 'Setup layout completed without copying the bundle.'
    }

    if ((Get-LogText -LogPath $LogPath) -notmatch 'Detected package: .*\.msi') {
        throw 'Setup layout completed without detecting its embedded MSI payload.'
    }
}

function Test-MsiAdministrativeInstall {
    param([string]$MsiPath, [string]$TargetPath, [string]$LogPath)

    New-Item -ItemType Directory -Path $TargetPath | Out-Null
    $arguments = "/a `"$MsiPath`" /qn TARGETDIR=`"$TargetPath`" /l*v `"$LogPath`""
    $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "MSI administrative install failed with exit code $($process.ExitCode).`n$(Get-LogText -LogPath $LogPath)"
    }

    if (-not (Get-ChildItem -LiteralPath $TargetPath -Recurse -Filter 'ColonyOptimizer.exe' | Select-Object -First 1)) {
        throw 'MSI administrative install completed without deploying ColonyOptimizer.exe.'
    }
}

$artifactRootPath = [IO.Path]::GetFullPath($ArtifactRoot)
$artifactPrefix = "ColonyOptimizer-$Version"
$setupPath = Join-Path $artifactRootPath "$artifactPrefix-Setup.exe"
$msiPath = Join-Path $artifactRootPath "$artifactPrefix-win-x64.msi"
$zipPath = Join-Path $artifactRootPath "$artifactPrefix-win-x64.zip"

Assert-Exists -Path $setupPath -Description 'Setup EXE'
Assert-Exists -Path $msiPath -Description 'MSI'
Assert-Exists -Path $zipPath -Description 'Portable ZIP'
Assert-Checksum -AssetPath $setupPath
Assert-Checksum -AssetPath $msiPath
Assert-Checksum -AssetPath $zipPath

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "colony-optimizer-release-smoke-$([Guid]::NewGuid().ToString('N'))"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRootFullPath = [IO.Path]::GetFullPath($testRoot)
if (-not $testRootFullPath.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a release smoke directory outside the temporary directory: $testRootFullPath"
}

try {
    New-Item -ItemType Directory -Path $testRootFullPath | Out-Null
    $zipPathExtracted = Join-Path $testRootFullPath 'portable'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $zipPathExtracted
    foreach ($requiredFile in @('ColonyOptimizer.exe', 'LICENSE.txt', 'Dependencies\MicrosoftEdgeWebview2Setup.exe', 'Assets\Visualisation\Sankey.html')) {
        Assert-Exists -Path (Join-Path $zipPathExtracted $requiredFile) -Description 'Portable ZIP file'
    }

    Test-SetupUserInterface -SetupPath $setupPath -LogPath (Join-Path $testRootFullPath 'setup-ui.log')
    Test-SetupLayout -SetupPath $setupPath -LayoutPath (Join-Path $testRootFullPath 'setup-layout') -LogPath (Join-Path $testRootFullPath 'setup-layout.log')
    Test-MsiAdministrativeInstall -MsiPath $msiPath -TargetPath (Join-Path $testRootFullPath 'msi-admin') -LogPath (Join-Path $testRootFullPath 'msi-admin.log')
}
finally {
    if (Test-Path -LiteralPath $testRootFullPath) {
        Remove-Item -LiteralPath $testRootFullPath -Recurse -Force
    }
}
