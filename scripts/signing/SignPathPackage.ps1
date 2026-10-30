#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Preflight', 'PreparePayload', 'AcceptPayload', 'Finalize')][string]$Step,
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$Workspace = '',
    [string]$SignedInputDirectory = '',
    [string]$InnoCompilerPath = '',
    [string]$ExpectedPublisher = $env:SIGNPATH_EXPECTED_PUBLISHER,
    [string]$CertificateThumbprint = $env:SIGNPATH_CERTIFICATE_THUMBPRINT
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SignPathCommon.ps1')
if ($Step -eq 'Preflight') { Assert-SignPathPreflight; return }
if (-not $Workspace) { throw 'Workspace must be explicit.' }
$Workspace = [IO.Path]::GetFullPath($Workspace)
Assert-SignPathUnlinkedPath $Workspace
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$version = [string]([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw -Encoding UTF8)).Project.PropertyGroup.Version
if ($version -cne $script:SignPathVersion) { throw 'This reviewed workflow is restricted to MathGPT 1.6.1.' }
if ($Step -ne 'Finalize') {
    if (-not $InnoCompilerPath) { $InnoCompilerPath = (Get-Command ISCC.exe -CommandType Application -ErrorAction Stop).Source }
    if (-not (Test-Path -LiteralPath $InnoCompilerPath -PathType Leaf)) { throw 'The pinned Inno compiler is missing.' }
}
$stage = Join-Path $Workspace 'stage'
$cache = Join-Path $Workspace 'uninstaller-cache'
$packages = Join-Path $Workspace 'installer'
$unsignedPayloadDirectory = Join-Path $Workspace 'unsigned-payload'
$arguments = @('/Qp', "/DStage=$stage", "/DArchitecture=$Runtime", "/DOutputPath=$packages", "/DAppVersion=$version",
    '/DSigningMode=Production', "/DSignedUninstallerPath=$cache")
$installerName = "MathGPT-$version-Setup-$Runtime.exe"

if ($Step -eq 'PreparePayload') {
    foreach ($name in @('MathGPT.exe', 'MathGPT.Bridge.exe')) {
        $path = Join-Path $stage $name
        Assert-SignPathMetadata $path
        if ((Get-SignPathPeLayout $path).certificateOffset -ne 0) { throw 'Payload already has a signature.' }
    }
    New-SignPathDirectory $cache
    New-SignPathDirectory $packages
    New-SignPathDirectory $unsignedPayloadDirectory
    $result = Invoke-SignPathInno $InnoCompilerPath ($arguments + '/DExternalSigning=Prepare' + (Join-Path $root 'scripts/MathGPT.iss'))
    $uninstaller = Assert-SignPathPrepareResult $result $cache $packages
    Assert-SignPathMetadata $uninstaller.FullName
    $mappings = @(foreach ($name in $script:SignPathPayloadNames) {
        $originalPath = if ($name -eq 'MathGPT.Uninstaller.exe') { 'uninstaller-cache/' + $uninstaller.Name } else { 'stage/' + $name }
        $original = Join-Path $Workspace $originalPath
        Copy-Item -LiteralPath $original -Destination (Join-Path $unsignedPayloadDirectory $name)
        [ordered]@{ signingName = $name; originalName = [IO.Path]::GetFileName($original); originalPath = $originalPath
            unsignedSha256 = (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash
            payloadSha256 = Get-SignPathPePayloadDigest $original }
    })
    Write-SignPathJson (Join-Path $Workspace 'payload-input.json') ([ordered]@{
        schemaVersion = 1; product = 'MathGPT'; version = $version; runtime = $Runtime
        status = 'provider-test-required'; releaseReady = $false
        metadataProviderCheck = 'Exact XML identity restrictions retained; Inno VERSIONINFO padding handling requires a provider test.'
        mappingPurpose = 'Inno uninstaller PE is copied unchanged to an explicit .exe signing name, then returned to its original cache name.'
        mappings = $mappings
    })
    Write-Host 'Prepared exactly three unsigned MathGPT PE targets. No signing request was submitted by this script.'
    return
}

$manifest = Read-SignPathManifest $Workspace
if ($manifest.runtime -cne $Runtime) { throw 'Runtime differs from the prepared signing manifest.' }
if ([string]::IsNullOrWhiteSpace($ExpectedPublisher)) { throw 'ExpectedPublisher is required for public-trust verification.' }
if (-not $SignedInputDirectory) { throw 'SignedInputDirectory is required.' }

if ($Step -eq 'AcceptPayload') {
    Assert-SignPathExactFiles $SignedInputDirectory $script:SignPathPayloadNames
    $signedPaths = @(foreach ($mapping in $manifest.mappings) {
        $reference = Join-Path $unsignedPayloadDirectory $mapping.signingName
        $returned = Join-Path $SignedInputDirectory $mapping.signingName
        $original = Join-Path $Workspace $mapping.originalPath
        if ((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -cne $mapping.unsignedSha256) { throw 'An original file changed before signed payload restoration.' }
        Assert-SignPathMetadata $returned
        Assert-SignPathPayloadUnchanged $reference $returned
        $returned
    })
    # No private key is required. Trust, exact publisher and SHA256 timestamps are mandatory.
    $verifyArguments = @{ FilePath = $signedPaths; ExpectedPublisher = $ExpectedPublisher; CertificateThumbprint = $CertificateThumbprint
        OutputPath = (Join-Path $Workspace 'payload-verification.json') }
    & (Join-Path $PSScriptRoot 'WindowsVerify.ps1') @verifyArguments
    foreach ($mapping in $manifest.mappings) {
        Copy-Item -LiteralPath (Join-Path $SignedInputDirectory $mapping.signingName) -Destination (Join-Path $Workspace $mapping.originalPath) -Force
    }
    $packagedPayload = @(foreach ($mapping in $manifest.mappings) {
        [ordered]@{ originalPath = $mapping.originalPath; sha256 = (Get-FileHash -LiteralPath (Join-Path $Workspace $mapping.originalPath) -Algorithm SHA256).Hash }
    })
    $result = Invoke-SignPathInno $InnoCompilerPath ($arguments + '/DExternalSigning=Package' + (Join-Path $root 'scripts/MathGPT.iss'))
    if ($result.exitCode -ne 0) { throw "Inno packaging rejected the signed handoff (exit $($result.exitCode)). $($result.output)" }
    foreach ($record in $packagedPayload) {
        if ((Get-FileHash -LiteralPath (Join-Path $Workspace $record.originalPath) -Algorithm SHA256).Hash -cne $record.sha256) { throw 'A signed component changed while Inno packaged it.' }
    }
    $candidate = Join-Path $packages $installerName
    Assert-SignPathExactFiles $packages @($installerName)
    Assert-SignPathMetadata $candidate
    if ((Get-SignPathPeLayout $candidate).certificateOffset -ne 0) { throw 'The installer signing input must be unsigned.' }
    $installerInput = Join-Path $Workspace 'unsigned-installer'
    New-SignPathDirectory $installerInput
    Copy-Item -LiteralPath $candidate -Destination (Join-Path $installerInput $installerName)
    Write-SignPathJson (Join-Path $Workspace 'installer-input.json') ([ordered]@{
        schemaVersion = 1; fileName = $installerName; unsignedSha256 = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
        payloadSha256 = Get-SignPathPePayloadDigest $candidate; status = 'provider-test-required'; releaseReady = $false
        packagedPayload = $packagedPayload
    })
    Write-Host 'Verified and embedded all three signed targets. Final installer still requires the second signing request.'
    return
}

Assert-SignPathExactFiles $SignedInputDirectory @($installerName)
$installerReference = Join-Path $Workspace "unsigned-installer/$installerName"
$installerRecord = Get-Content -LiteralPath (Join-Path $Workspace 'installer-input.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($installerRecord.schemaVersion -ne 1 -or $installerRecord.fileName -cne $installerName -or
    (Get-FileHash -LiteralPath $installerReference -Algorithm SHA256).Hash -cne $installerRecord.unsignedSha256 -or
    @($installerRecord.packagedPayload).Count -ne 3) { throw 'The unsigned installer reference or packaged payload evidence changed.' }
$signedInstaller = Join-Path $SignedInputDirectory $installerName
Assert-SignPathPayloadUnchanged $installerReference $signedInstaller
Assert-SignPathMetadata $signedInstaller
$allPaths = @($signedInstaller)
foreach ($mapping in $manifest.mappings) {
    $original = Join-Path $Workspace $mapping.originalPath
    $packaged = @($installerRecord.packagedPayload | Where-Object { $_.originalPath -ceq $mapping.originalPath })
    if ($packaged.Count -ne 1 -or (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -cne $packaged[0].sha256) { throw 'A component differs from the signed bytes embedded by Inno.' }
    Assert-SignPathPayloadUnchanged (Join-Path $unsignedPayloadDirectory $mapping.signingName) $original
    Assert-SignPathMetadata $original
    $allPaths += $original
}
if ($allPaths.Count -ne 4) { throw 'All four signed components are required.' }
$verifyArguments = @{ FilePath = $allPaths; ExpectedPublisher = $ExpectedPublisher; CertificateThumbprint = $CertificateThumbprint
    OutputPath = (Join-Path $Workspace 'final-verification.json') }
& (Join-Path $PSScriptRoot 'WindowsVerify.ps1') @verifyArguments
$verification = Get-Content -LiteralPath (Join-Path $Workspace 'final-verification.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($verification.verification -ne 'passed' -or @($verification.files).Count -ne 4) { throw 'Four-component verification failed.' }
foreach ($name in @('SIGNPATH_PAYLOAD_REQUEST_ID', 'SIGNPATH_INSTALLER_REQUEST_ID')) {
    $requestId = [Guid]::Empty
    if (-not [Guid]::TryParse([Environment]::GetEnvironmentVariable($name), [ref]$requestId) -or $requestId -eq [Guid]::Empty) { throw "A real signing request ID is required: $name" }
}
$release = Join-Path $Workspace 'verified-package'
New-SignPathDirectory $release
Copy-Item -LiteralPath $signedInstaller -Destination (Join-Path $release $installerName)
Write-SignPathJson (Join-Path $release "$installerName.signing.json") ([ordered]@{
    schemaVersion = 1; product = 'MathGPT'; version = $version; runtime = $Runtime; signingMode = 'SignPath'
    verification = 'passed'; releaseReady = $false; providerTestRequired = $false
    scope = 'Four component signatures verified; publication, installation acceptance and SmartScreen reputation are separate.'
    verifiedAtUtc = [DateTime]::UtcNow.ToString('o'); expectedPublisher = $ExpectedPublisher
    sourceCommit = $env:GITHUB_SHA; workflowRunId = $env:GITHUB_RUN_ID
    payloadRequestId = $env:SIGNPATH_PAYLOAD_REQUEST_ID; installerRequestId = $env:SIGNPATH_INSTALLER_REQUEST_ID
    mappings = $manifest.mappings; unsignedInstaller = $installerRecord; files = $verification.files
})
Write-Host 'Four MathGPT signatures verified. Candidate retained as a workflow artifact; no release was published.'
