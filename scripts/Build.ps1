#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [string]$OutputDirectory = '',
    [switch]$Installer,
    [string]$PackageDirectory = '',
    [ValidateSet('Production','Preview')][string]$SigningMode = 'Production',
    [string]$CertificateThumbprint = $env:MATHGPT_SIGN_CERT_THUMBPRINT,
    [string]$ExpectedPublisher = $env:MATHGPT_SIGN_PUBLISHER,
    [ValidateSet('CurrentUser','LocalMachine')][string]$CertificateStoreLocation = 'CurrentUser',
    [string]$TimestampUrl = 'https://timestamp.digicert.com',
    [string]$GoPath = '',
    [string]$InnoCompilerPath = '',
    [switch]$PreflightOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'signing\WindowsSigning.ps1')
. (Join-Path $PSScriptRoot 'signing\WindowsBridgeResource.ps1')
$version = [string]([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw -Encoding UTF8)).Project.PropertyGroup.Version
if ($version -notmatch '\A\d+\.\d+\.\d+\z') { throw 'Directory.Build.props must contain a numeric three-part release Version.' }

# Production fails before compilation, directory creation or package modification when signing is unavailable.
$context = $null
if ($SigningMode -eq 'Production') {
    $context = New-WindowsSigningContext -CertificateThumbprint $CertificateThumbprint -ExpectedPublisher $ExpectedPublisher `
        -CertificateStoreLocation $CertificateStoreLocation -TimestampUrl $TimestampUrl
}
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
function Resolve-BuildTool([string]$ExplicitPath, [string]$CommandName, [string]$SharedFallback) {
    if ($ExplicitPath) { return [IO.Path]::GetFullPath($ExplicitPath, $root) }
    $command = Get-Command $CommandName -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) { return $command.Source }
    return $SharedFallback
}
$go = Resolve-BuildTool $GoPath 'go.exe' 'D:\Codex\Tools\Go\1.27.3\go\bin\go.exe'
$iscc = Resolve-BuildTool $InnoCompilerPath 'ISCC.exe' 'D:\Codex\Tools\InnoSetup-6.7.3\ISCC.exe'
if (-not (Test-Path -LiteralPath $go -PathType Leaf)) { throw 'The registered shared Go toolchain is missing.' }
if ($Installer -and -not (Test-Path -LiteralPath $iscc -PathType Leaf)) { throw 'The registered Inno Setup compiler is missing.' }
$resourceTools = Get-WindowsBridgeResourceTools
if ($PreflightOnly) {
    [pscustomobject]@{ version = $version; runtime = $Runtime; signingMode = $SigningMode; compilationStarted = $false; productionSigningAvailable = ($null -ne $context); signing = $context }
    return
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "build\stage\$version\$SigningMode\$Runtime" }
if (-not $PackageDirectory) { $PackageDirectory = Join-Path $root 'build\packages' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, $root)
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory, $root)
$suffix = if ($SigningMode -eq 'Preview') { '-Preview' } else { '' }
$baseName = "MathGPT-$version$suffix-Setup-$Runtime"
$packagePath = Join-Path $PackageDirectory "$baseName.exe"
$manifestPath = Join-Path $PackageDirectory "$baseName.signing.json"

function Assert-UnlinkedOutputPath([string]$PathValue) {
    $probe = $PathValue
    while ($probe) {
        if (Test-Path -LiteralPath $probe) {
            $item = Get-Item -LiteralPath $probe -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Build output may not traverse a reparse point: $probe" }
        }
        $probe = Split-Path -Path $probe -Parent
    }
}
Assert-UnlinkedOutputPath $OutputDirectory
if (Test-Path -LiteralPath $OutputDirectory) {
    if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container) -or @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 0) {
        throw 'OutputDirectory must be new or empty. Existing candidates and released binaries are never overwritten; choose a new staging directory.'
    }
}
if ($Installer) {
    Assert-UnlinkedOutputPath $PackageDirectory
    $stagePrefix = $OutputDirectory.TrimEnd('\') + '\'
    $packagePrefix = $PackageDirectory.TrimEnd('\') + '\'
    if ($stagePrefix.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase) -or $packagePrefix.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Staging and installer output directories must be separate.'
    }
    if ((Test-Path -LiteralPath $packagePath) -or (Test-Path -LiteralPath $manifestPath)) {
        throw 'This package or signing evidence already exists. Published and prior candidate files are immutable; choose a new version or output directory.'
    }
}
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$workspace = Join-Path $root ("build\tmp\windows-signing\$version-$Runtime-" + [Guid]::NewGuid().ToString('N'))
Assert-UnlinkedOutputPath $workspace
$bridgeBuild = New-WindowsBridgeBuildDirectory -Version $version -Runtime $Runtime -BridgeDirectory (Join-Path $root 'bridge') `
    -OutputDirectory (Join-Path $workspace 'bridge-resource') -Tools $resourceTools
$payloadRecords = @()
$buildSucceeded = $false
Push-Location $root
try {
    Invoke-WindowsNativeCommand $dotnet @('publish','src/MathGPT.App/MathGPT.App.csproj','-c','Release','-r',$Runtime,'--self-contained','true','-p:RuntimeFrameworkVersion=8.0.30','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:IncludeSourceRevisionInInformationalVersion=false','-p:DebugType=None','-p:DebugSymbols=false','-o',$OutputDirectory,'--nologo') '.NET publish'
    $oldGoOs=$env:GOOS; $oldGoArch=$env:GOARCH
    Push-Location $bridgeBuild.buildDirectory
    try {
        $env:GOOS='windows'; $env:GOARCH=if($Runtime-eq'win-arm64'){'arm64'}else{'amd64'}
        Invoke-WindowsNativeCommand $go @('build','-mod=vendor','-trimpath','-ldflags=-s -w','-o',(Join-Path $OutputDirectory 'MathGPT.Bridge.exe'),'.') 'Bundled adapter build'
    } finally { $env:GOOS=$oldGoOs; $env:GOARCH=$oldGoArch; Pop-Location }
    Compress-Archive -Path (Join-Path $root 'bridge\*') -DestinationPath (Join-Path $OutputDirectory 'MathGPT.Bridge-source.zip') -Force
    foreach ($name in @('README.md','LICENSE','THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination $OutputDirectory -Force
    }
    if ($SigningMode -eq 'Production') {
        foreach ($fileName in @('MathGPT.exe','MathGPT.Bridge.exe')) {
            $payloadRecords += Invoke-WindowsFileSigning -FilePath (Join-Path $OutputDirectory $fileName) -Context $context
        }
    } else {
        Write-Warning 'Explicit Preview build: publisher signatures are absent. This output is not a production release.'
    }
    if ($Installer) {
        $candidateDirectory = Join-Path $workspace 'installer'
        [IO.Directory]::CreateDirectory($candidateDirectory) | Out-Null
        $arguments = @('/Qp',"/DStage=$OutputDirectory","/DArchitecture=$Runtime","/DOutputPath=$candidateDirectory","/DAppVersion=$version","/DSigningMode=$SigningMode")
        if ($SigningMode -eq 'Production') {
            $recordDirectory = Join-Path $workspace 'records'
            [IO.Directory]::CreateDirectory($recordDirectory) | Out-Null
            $configurationPath = Join-Path $workspace 'public-signing-configuration.json'
            Write-WindowsJsonFile -FilePath $configurationPath -Value $context
            $arguments += "/DSigningWorkspace=$workspace"
            $arguments += New-WindowsInnoSignToolArgument -PowerShellPath (Join-Path $PSHOME 'pwsh.exe') -ScriptPath (Join-Path $PSScriptRoot 'signing\WindowsSignFile.ps1') -ConfigurationPath $configurationPath -RecordDirectory $recordDirectory
        }
        $arguments += Join-Path $PSScriptRoot 'MathGPT.iss'
        Invoke-WindowsNativeCommand $iscc $arguments 'Inno installer compilation'
        $candidatePath = Join-Path $candidateDirectory "$baseName.exe"
        $manifest = $null
        if ($SigningMode -eq 'Production') {
            $installerRecord = Assert-WindowsSignedFile -FilePath $candidatePath -Context $context
            $innoRecords = @(Get-ChildItem -LiteralPath $recordDirectory -File -Filter '*.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json })
            $uninstallers = @(Get-ChildItem -LiteralPath (Join-Path $workspace 'uninstallers') -File)
            if ($uninstallers.Count -eq 0) { throw 'No signed uninstaller was retained by Inno. A releasable manifest cannot be produced.' }
            $uninstallerRecords = @(foreach ($uninstaller in $uninstallers) { Assert-WindowsSignedFile -FilePath $uninstaller.FullName -Context $context })
            foreach ($record in @($uninstallerRecords) + @($installerRecord)) {
                if ($record.sha256 -notin @($innoRecords | ForEach-Object sha256)) { throw 'A signed Inno component has no matching successful signing callback record.' }
            }
            # Recheck payload after packaging as well; a stale or changed stage cannot pass acceptance.
            $payloadRecords = @(foreach ($fileName in @('MathGPT.exe','MathGPT.Bridge.exe')) { Assert-WindowsSignedFile -FilePath (Join-Path $OutputDirectory $fileName) -Context $context })
            $manifest = [ordered]@{
                schemaVersion = 1; product = 'MathGPT'; version = $version; runtime = $Runtime
                signingMode = 'Production'; releaseReady = $true; verifiedAtUtc = [DateTime]::UtcNow.ToString('o')
                expectedPublisher = $context.expectedPublisher; certificateThumbprint = $context.certificateThumbprint
                payload = $payloadRecords; installer = $installerRecord; uninstallers = $uninstallerRecords
                innoSignedComponents = $innoRecords
                scope = 'Windows package signatures only; no SmartScreen reputation or physical-device claim'
            }
        } else {
            if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) { throw 'The preview installer was not produced.' }
            $manifest = [ordered]@{
                schemaVersion = 1; product = 'MathGPT'; version = $version; runtime = $Runtime
                signingMode = 'Preview'; releaseReady = $false; signatureStatus = 'NotSigned'
                installer = @{ fileName = [IO.Path]::GetFileName($candidatePath); sha256 = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash }
            }
        }
        [IO.Directory]::CreateDirectory($PackageDirectory) | Out-Null
        # Move without overwrite preserves every previous package, including published 1.6.0.
        [IO.File]::Move($candidatePath, $packagePath, $false)
        Write-WindowsJsonFile -FilePath $manifestPath -Value $manifest
        Write-Host "Installer: $packagePath"
        Write-Host "Signing evidence: $manifestPath"
    }
    $buildSucceeded = $true
} finally {
    Pop-Location
    try { Remove-WindowsBridgeBuildDirectory $bridgeBuild }
    catch { Write-Warning "Temporary Bridge files were preserved: $($_.Exception.Message)" }
    if ($SigningMode -eq 'Preview' -and $buildSucceeded) {
        try {
            $temporaryPrefix = [IO.Path]::GetFullPath((Join-Path $root 'build\tmp\windows-signing')).TrimEnd('\') + '\'
            $resolvedWorkspace = [IO.Path]::GetFullPath($workspace)
            if (-not $resolvedWorkspace.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                (Get-Item -LiteralPath $resolvedWorkspace -Force).Attributes -band [IO.FileAttributes]::ReparsePoint -or
                @(Get-ChildItem -LiteralPath $resolvedWorkspace -Recurse -Force -Attributes ReparsePoint).Count -ne 0) { throw 'The temporary output path or links changed.' }
            Remove-Item -LiteralPath $resolvedWorkspace -Recurse -Force
        } catch { Write-Warning "Temporary preview build files were preserved: $($_.Exception.Message)" }
    }
}
