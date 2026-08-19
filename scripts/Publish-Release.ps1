[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$normalizedVersion = $Version.Trim()
if ($normalizedVersion.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
    $normalizedVersion = $normalizedVersion.Substring(1)
}

if ($normalizedVersion -notmatch "^\d+\.\d+\.\d+$") {
    throw "Version must be MAJOR.MINOR.PATCH, optionally prefixed with v."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "src\ColonyOptimizer.App\ColonyOptimizer.App.csproj"
$installerProjectPath = Join-Path $repositoryRoot "installer\ColonyOptimizer.Installer\ColonyOptimizer.Installer.wixproj"
$setupProjectPath = Join-Path $repositoryRoot "installer\ColonyOptimizer.Setup\ColonyOptimizer.Setup.wixproj"
$artifactRoot = Join-Path $repositoryRoot "artifacts"
$packageName = "ColonyOptimizer-$normalizedVersion-win-x64"
$publishDirectory = Join-Path $artifactRoot $packageName
$archivePath = Join-Path $artifactRoot "$packageName.zip"
$msiPath = Join-Path $artifactRoot "$packageName.msi"
$setupPath = Join-Path $artifactRoot "ColonyOptimizer-$normalizedVersion-Setup.exe"

if ((Test-Path $publishDirectory) -or (Test-Path $archivePath) -or (Test-Path $msiPath) -or (Test-Path $setupPath)) {
    throw "Release artifacts for $normalizedVersion already exist. Use a clean workspace or a new version."
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $publishDirectory `
    "-p:Version=$normalizedVersion" `
    "-p:AssemblyVersion=$normalizedVersion.0" `
    "-p:FileVersion=$normalizedVersion.0"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archivePath -CompressionLevel Optimal

dotnet build $installerProjectPath `
    --configuration Release `
    --no-restore `
    --output $artifactRoot `
    "-p:Version=$normalizedVersion" `
    "-p:PublishDirectory=$publishDirectory"

if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $msiPath)) {
    throw "MSI build completed without creating $msiPath."
}

dotnet build $setupProjectPath `
    --configuration Release `
    --no-restore `
    --output $artifactRoot `
    "-p:Version=$normalizedVersion" `
    "-p:MsiPath=$msiPath"

if ($LASTEXITCODE -ne 0) {
    throw "Setup EXE build failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $setupPath)) {
    throw "Setup EXE build completed without creating $setupPath."
}

foreach ($assetPath in @($archivePath, $msiPath, $setupPath)) {
    $checksumPath = "$assetPath.sha256"
    $hash = (Get-FileHash -Path $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path $checksumPath -Value "$hash *$(Split-Path -Leaf $assetPath)" -NoNewline -Encoding ascii
    Write-Host "Created $assetPath"
    Write-Host "Created $checksumPath"
}
