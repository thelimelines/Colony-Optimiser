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
$assetsPath = Join-Path (Split-Path -Parent $projectPath) "obj\project.assets.json"
$webViewRuntimeInstallerPath = Join-Path $publishDirectory "Dependencies\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
$webViewRuntimeInstallerUri = "https://go.microsoft.com/fwlink/p/?LinkId=2124701"

function Get-ResolvedPackagePath {
    param(
        [Parameter(Mandatory)]
        [object]$Assets,
        [Parameter(Mandatory)]
        [string]$PackageId
    )

    $packageRoot = ($Assets.packageFolders.PSObject.Properties | Select-Object -First 1).Name
    $library = $Assets.libraries.PSObject.Properties |
        Where-Object { $_.Name.StartsWith("$PackageId/", [System.StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ($null -eq $library) {
        throw "Package '$PackageId' was not found in $assetsPath."
    }

    $version = $library.Name.Substring($library.Name.IndexOf('/') + 1)
    return Join-Path (Join-Path $packageRoot $PackageId.ToLowerInvariant()) $version
}

function Get-RuntimePackPath {
    param(
        [Parameter(Mandatory)]
        [object]$Assets,
        [Parameter(Mandatory)]
        [string]$PackageId
    )

    $packageRoot = ($Assets.packageFolders.PSObject.Properties | Select-Object -First 1).Name
    $dependency = $Assets.project.frameworks.PSObject.Properties.Value.downloadDependencies |
        Where-Object { $_.name.Equals($PackageId, [System.StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ($null -eq $dependency) {
        throw "Runtime pack '$PackageId' was not found in $assetsPath. Restore the solution with --runtime win-x64."
    }

    $version = ([string]$dependency.version).Trim('[', ']').Split(',')[0].Trim()
    return Join-Path (Join-Path $packageRoot $PackageId.ToLowerInvariant()) $version
}

function Copy-ReleaseNotice {
    param(
        [Parameter(Mandatory)]
        [string]$Source,
        [Parameter(Mandatory)]
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required release notice was not found: $Source"
    }

    Copy-Item -LiteralPath $Source -Destination $Destination
}

function Get-WebView2RuntimeInstaller {
    param(
        [Parameter(Mandatory)]
        [string]$Destination
    )

    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    Invoke-WebRequest -Uri $webViewRuntimeInstallerUri -OutFile $Destination

    $signature = Get-AuthenticodeSignature -FilePath $Destination
    if ($signature.Status -ne "Valid" -or $signature.SignerCertificate.Subject -notmatch "Microsoft Corporation") {
        throw "The downloaded WebView2 Runtime installer did not have a valid Microsoft Corporation signature."
    }
}

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

# The standalone installer keeps the MSI, Setup EXE, and portable ZIP usable on
# Windows images that do not include the WebView2 Runtime. The Setup EXE runs it
# before the MSI; the app can run the same bundled installer for MSI/ZIP installs.
Get-WebView2RuntimeInstaller -Destination $webViewRuntimeInstallerPath

if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
    throw "NuGet assets file was not found: $assetsPath"
}

$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$licenceDirectory = Join-Path $publishDirectory "LICENSES"
New-Item -ItemType Directory -Path $licenceDirectory -Force | Out-Null

$webViewPackage = Get-ResolvedPackagePath -Assets $assets -PackageId "Microsoft.Web.WebView2"
$netCoreRuntimePack = Get-RuntimePackPath -Assets $assets -PackageId "Microsoft.NETCore.App.Runtime.win-x64"
$windowsDesktopRuntimePack = Get-RuntimePackPath -Assets $assets -PackageId "Microsoft.WindowsDesktop.App.Runtime.win-x64"

Copy-ReleaseNotice -Source (Join-Path $repositoryRoot "LICENSE") -Destination (Join-Path $publishDirectory "LICENSE.txt")
Copy-ReleaseNotice -Source (Join-Path $webViewPackage "LICENSE.txt") -Destination (Join-Path $licenceDirectory "WebView2-LICENSE.txt")
Copy-ReleaseNotice -Source (Join-Path $webViewPackage "NOTICE.txt") -Destination (Join-Path $licenceDirectory "WebView2-NOTICE.txt")
Copy-ReleaseNotice -Source (Join-Path $netCoreRuntimePack "LICENSE.TXT") -Destination (Join-Path $licenceDirectory "DotNet-Runtime-LICENSE.txt")
Copy-ReleaseNotice -Source (Join-Path $netCoreRuntimePack "THIRD-PARTY-NOTICES.TXT") -Destination (Join-Path $licenceDirectory "DotNet-Runtime-THIRD-PARTY-NOTICES.txt")
Copy-ReleaseNotice -Source (Join-Path $windowsDesktopRuntimePack "LICENSE") -Destination (Join-Path $licenceDirectory "DotNet-WindowsDesktop-LICENSE.txt")

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
    "-p:MsiPath=$msiPath" `
    "-p:WebView2RuntimeInstallerPath=$webViewRuntimeInstallerPath"

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
