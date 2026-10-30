#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$OfficialSchemaPath = '',
    [string]$PreparedWorkspace = '',
    [string]$InnoCompilerPath = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'SignPathCommon.ps1')
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-SignPathDirectory $OutputDirectory
$script:caseCount = 0
function Assert-Case([string]$CaseLabel, [scriptblock]$Body) {
    & $Body
    $script:caseCount++
    Write-Host "PASS $CaseLabel"
}
function Assert-Throws([scriptblock]$Body, [string]$Pattern) {
    $caught = $null
    try { & $Body | Out-Null } catch { $caught = $_ }
    if ($null -eq $caught -or $caught.Exception.Message -notlike $Pattern) { throw "Expected failure matching: $Pattern" }
}
Assert-Case 'signing version follows the checked-out release metadata' {
    $releaseVersion = [string]([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../Directory.Build.props') -Raw -Encoding UTF8)).Project.PropertyGroup.Version
    if ($script:SignPathVersion -cne $releaseVersion -or $releaseVersion -notmatch '\A\d+\.\d+\.\d+\z') { throw 'Signing release version drifted.' }
}
function New-TestPe([string]$PathValue, [switch]$SignedShape, [switch]$Changed) {
    # Structure-only fixture, not a cryptographic signature or a trusted executable.
    $size = if ($SignedShape) { 544 } else { 521 }
    $bytes = [byte[]]::new($size)
    for ($i = 320; $i -lt 521; $i++) { $bytes[$i] = [byte]($i % 251) }
    [BitConverter]::GetBytes([uint16]0x5a4d).CopyTo($bytes, 0)
    [BitConverter]::GetBytes([uint32]128).CopyTo($bytes, 60)
    [BitConverter]::GetBytes([uint32]0x4550).CopyTo($bytes, 128)
    [BitConverter]::GetBytes([uint16]0x10b).CopyTo($bytes, 152)
    [BitConverter]::GetBytes([uint32]16).CopyTo($bytes, 244)
    if ($SignedShape) {
        [BitConverter]::GetBytes([uint32]528).CopyTo($bytes, 280)
        [BitConverter]::GetBytes([uint32]16).CopyTo($bytes, 284)
        [BitConverter]::GetBytes([uint32]1234).CopyTo($bytes, 216)
        [BitConverter]::GetBytes([uint32]16).CopyTo($bytes, 528)
    }
    if ($Changed) { $bytes[400] = 17 }
    [IO.File]::WriteAllBytes($PathValue, $bytes)
}

$environmentNames = @('SIGNPATH_ORGANIZATION_ID', 'SIGNPATH_PROJECT_SLUG', 'SIGNPATH_SIGNING_POLICY_SLUG',
    'SIGNPATH_PAYLOAD_CONFIG_SLUG', 'SIGNPATH_INSTALLER_CONFIG_SLUG', 'SIGNPATH_EXPECTED_PUBLISHER', 'SIGNPATH_API_TOKEN',
    'SIGNPATH_CERTIFICATE_THUMBPRINT', 'GITHUB_ACTIONS', 'RUNNER_ENVIRONMENT', 'RUNNER_OS', 'GITHUB_EVENT_NAME', 'SIGNPATH_REPOSITORY_VISIBILITY')
$savedEnvironment = @{}
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:SIGNPATH_ORGANIZATION_ID = [Guid]::NewGuid().ToString()
    $env:SIGNPATH_PROJECT_SLUG = 'unit-test'
    $env:SIGNPATH_SIGNING_POLICY_SLUG = 'unit-test'
    $env:SIGNPATH_PAYLOAD_CONFIG_SLUG = 'unit-test-payload'
    $env:SIGNPATH_INSTALLER_CONFIG_SLUG = 'unit-test-installer'
    $env:SIGNPATH_EXPECTED_PUBLISHER = 'Unit Test Only'
    $env:SIGNPATH_API_TOKEN = [Guid]::NewGuid().ToString()
    $env:SIGNPATH_CERTIFICATE_THUMBPRINT = ''
    $env:GITHUB_ACTIONS = 'true'
    $env:RUNNER_ENVIRONMENT = 'github-hosted'
    $env:RUNNER_OS = 'Windows'
    $env:GITHUB_EVENT_NAME = 'workflow_dispatch'
    $env:SIGNPATH_REPOSITORY_VISIBILITY = 'public'
    Assert-Case 'configuration preflight returns no secret or provider success claim' {
        $result = Assert-SignPathPreflight | ConvertTo-Json
        if ($result.Contains($env:SIGNPATH_API_TOKEN) -or $result.Contains($env:SIGNPATH_ORGANIZATION_ID) -or $result -notlike '*"providerTestRequired": true*') { throw 'Preflight leaked data or overstated readiness.' }
    }
    foreach ($name in @('SIGNPATH_ORGANIZATION_ID', 'SIGNPATH_PROJECT_SLUG', 'SIGNPATH_SIGNING_POLICY_SLUG',
        'SIGNPATH_PAYLOAD_CONFIG_SLUG', 'SIGNPATH_INSTALLER_CONFIG_SLUG', 'SIGNPATH_EXPECTED_PUBLISHER', 'SIGNPATH_API_TOKEN')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, '')
        Assert-Case "missing $name blocks preflight" { Assert-Throws { Assert-SignPathPreflight } '*Missing required signing configuration*' }
        [Environment]::SetEnvironmentVariable($name, $value)
    }
    foreach ($name in @('GITHUB_ACTIONS', 'RUNNER_ENVIRONMENT', 'RUNNER_OS', 'GITHUB_EVENT_NAME', 'SIGNPATH_REPOSITORY_VISIBILITY')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, 'disallowed')
        Assert-Case "$name rejects unsupported execution context" { Assert-Throws { Assert-SignPathPreflight } '*manual run in a public repository*' }
        [Environment]::SetEnvironmentVariable($name, $value)
    }
    $env:SIGNPATH_CERTIFICATE_THUMBPRINT = 'invalid'
    Assert-Case 'invalid public certificate pin is rejected' { Assert-Throws { Assert-SignPathPreflight } '*THUMBPRINT*' }
    $env:SIGNPATH_CERTIFICATE_THUMBPRINT = ''
} finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
}

$unsigned = Join-Path $OutputDirectory 'unsigned.exe'
$signedShape = Join-Path $OutputDirectory 'structure-only.exe'
$changed = Join-Path $OutputDirectory 'changed.exe'
New-TestPe $unsigned
New-TestPe $signedShape -SignedShape
New-TestPe $changed -SignedShape -Changed
Assert-Case 'PE checksum and certificate-table fields do not change payload digest' {
    Assert-SignPathPayloadUnchanged $unsigned $signedShape
}
Assert-Case 'PE code mutation is rejected independently of trust verification' {
    Assert-Throws { Assert-SignPathPayloadUnchanged $unsigned $changed } '*changed the executable payload*'
}
Assert-Case 'unsigned return cannot be treated as signed' {
    Assert-Throws { Assert-SignPathPayloadUnchanged $unsigned $unsigned } '*changed the executable payload*'
}
Assert-Case 'already signed input is rejected' {
    Assert-Throws { Assert-SignPathPayloadUnchanged $signedShape $signedShape } '*inputs must be unsigned*'
}
Assert-Case 'malformed executable and missing metadata fail' {
    Assert-Throws { Assert-SignPathMetadata $unsigned } '*ProductVersion*'
    $bad = Join-Path $OutputDirectory 'not-pe.exe'
    [IO.File]::WriteAllBytes($bad, [byte[]]::new(500))
    Assert-Throws { Get-SignPathPeLayout $bad } '*Not a PE*'
}

$artifact = Join-Path $OutputDirectory 'artifact'
New-SignPathDirectory $artifact
foreach ($name in $script:SignPathPayloadNames) { Copy-Item -LiteralPath $unsigned -Destination (Join-Path $artifact $name) }
Assert-Case 'exact three-target return is accepted structurally' { Assert-SignPathExactFiles $artifact $script:SignPathPayloadNames }
Copy-Item -LiteralPath $unsigned -Destination (Join-Path $artifact 'ThirdParty.dll')
Assert-Case 'extra service-returned file is rejected' { Assert-Throws { Assert-SignPathExactFiles $artifact $script:SignPathPayloadNames } '*unexpected entries*' }
Assert-Case 'existing output directory cannot be reused' { Assert-Throws { New-SignPathDirectory $artifact } '*fresh output directory*' }

$mappingWorkspace = Join-Path $OutputDirectory 'mapping'
$mappingInput = Join-Path $mappingWorkspace 'unsigned-payload'
[IO.Directory]::CreateDirectory($mappingInput) | Out-Null
$mappings = @(foreach ($name in $script:SignPathPayloadNames) {
    Copy-Item -LiteralPath $unsigned -Destination (Join-Path $mappingInput $name)
    $originalName = if ($name -eq 'MathGPT.Uninstaller.exe') { 'uninst-6.7.3-1234567890.e32' } else { $name }
    $originalPath = if ($name -eq 'MathGPT.Uninstaller.exe') { 'uninstaller-cache/' + $originalName } else { 'stage/' + $name }
    [ordered]@{ signingName = $name; originalName = $originalName; originalPath = $originalPath; unsignedSha256 = (Get-FileHash -LiteralPath $unsigned).Hash }
})
$mappingManifest = [ordered]@{ schemaVersion = 1; product = 'MathGPT'; version = $script:SignPathVersion; runtime = 'win-x64'; mappings = $mappings }
Write-SignPathJson (Join-Path $mappingWorkspace 'payload-input.json') $mappingManifest
Assert-Case 'mapped cache preserves the original name and raw digest' { $null = Read-SignPathManifest $mappingWorkspace }
$mappingManifest.mappings[2].originalPath = '../../outside.exe'
[IO.File]::WriteAllText((Join-Path $mappingWorkspace 'payload-input.json'), ($mappingManifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Assert-Case 'manifest path traversal is rejected' { Assert-Throws { Read-SignPathManifest $mappingWorkspace } '*original signing path*' }

$cacheDirectory = Join-Path $OutputDirectory 'cache'
$packageDirectory = Join-Path $OutputDirectory 'packages'
New-SignPathDirectory $cacheDirectory
New-SignPathDirectory $packageDirectory
$cacheFile = Join-Path $cacheDirectory 'uninst-6.7.3-1234567890.e32'
Copy-Item -LiteralPath $unsigned -Destination $cacheFile
$expectedOutput = 'Signed uninstaller mode is enabled. Using an external code-signing tool, please attach your digital signature to the following executable file: ' + $cacheFile
Assert-Case 'only the exact Inno preparation handoff is accepted' {
    $null = Assert-SignPathPrepareResult ([pscustomobject]@{ exitCode = 2; output = $expectedOutput }) $cacheDirectory $packageDirectory
    Assert-Throws { Assert-SignPathPrepareResult ([pscustomobject]@{ exitCode = 1; output = $expectedOutput }) $cacheDirectory $packageDirectory } '*expected unsigned-uninstaller*'
    Assert-Throws { Assert-SignPathPrepareResult ([pscustomobject]@{ exitCode = 2; output = 'some unrelated compiler failure' }) $cacheDirectory $packageDirectory } '*expected unsigned-uninstaller*'
}

foreach ($name in @('signpath-payload.xml', 'signpath-installer.xml')) {
    Assert-Case "$name has the documented namespace and fixed product identity" {
        $xml = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot $name) -Raw -Encoding UTF8)
        if ($xml.DocumentElement.NamespaceURI -cne 'http://signpath.io/artifact-configuration/v1') { throw 'Wrong namespace.' }
        foreach ($node in $xml.SelectNodes('//*[local-name()="pe-file"]')) {
            if ($node.GetAttribute('product-name') -cne 'MathGPT' -or $node.GetAttribute('product-version') -cne '${version}') { throw 'Missing identity restriction.' }
        }
        $versionParameter = $xml.SelectSingleNode('//*[local-name()="parameter"][@name="version"]')
        if ($null -eq $versionParameter -or $versionParameter.GetAttribute('required') -cne 'true') { throw 'Version must be a required signing parameter.' }
        if ($OfficialSchemaPath) {
            $settings = [Xml.XmlReaderSettings]::new()
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $settings.ValidationType = [Xml.ValidationType]::Schema
            $null = $settings.Schemas.Add('http://signpath.io/artifact-configuration/v1', [IO.Path]::GetFullPath($OfficialSchemaPath))
            $reader = [Xml.XmlReader]::Create((Join-Path $PSScriptRoot $name), $settings)
            try { while ($reader.Read()) { } } finally { $reader.Dispose() }
        }
    }
}
Assert-Case 'manual workflow supplies a source version and minimal artifact read permission' {
    $workflow = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../.github/workflows/windows-signpath.yml') -Raw -Encoding UTF8
    if ($workflow -notmatch '(?m)^  actions: read\s*$' -or $workflow -notmatch 'id: signing_preflight' -or
        ([regex]::Matches($workflow, 'version: "\$\{\{ steps\.signing_preflight\.outputs\.version \}\}"')).Count -ne 2) {
        throw 'Both signing requests require the checked-out version and artifact read permission.'
    }
}
if ($PreparedWorkspace) {
    Assert-Case 'real prepared artifacts preserve the raw uninstaller mapping' {
        $prepared = Read-SignPathManifest $PreparedWorkspace
        if ($prepared.status -cne 'provider-test-required' -or $prepared.releaseReady) { throw 'Prepared evidence overstates readiness.' }
        foreach ($mapping in $prepared.mappings) {
            $original = Join-Path $PreparedWorkspace $mapping.originalPath
            Assert-SignPathMetadata $original
            if ((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -cne $mapping.unsignedSha256) { throw 'Mapping changed real prepared bytes.' }
        }
    }
    Assert-Case 'real unsigned artifacts cannot cross the signing handoff' {
        $packageScript = Join-Path $PSScriptRoot 'SignPathPackage.ps1'
        $arguments = @{ Step = 'AcceptPayload'; Runtime = 'win-x64'; Workspace = $PreparedWorkspace
            SignedInputDirectory = (Join-Path $PreparedWorkspace 'unsigned-payload')
            ExpectedPublisher = 'SignPath Foundation'; InnoCompilerPath = $InnoCompilerPath }
        Assert-Throws { & $packageScript @arguments } '*changed the executable payload*'
    }
    Assert-Case 'failed handoff produced no verified manifest or final installer' {
        if ((Test-Path -LiteralPath (Join-Path $PreparedWorkspace 'verified-package')) -or
            @(Get-ChildItem -LiteralPath (Join-Path $PreparedWorkspace 'installer') -Filter '*.exe').Count) { throw 'Failed handoff produced a releasable output.' }
    }
}
$summary = [ordered]@{ casesPassed = $script:caseCount; officialSchemaValidated = [bool]$OfficialSchemaPath
    realPreparedArtifactsChecked = [bool]$PreparedWorkspace
    realSigningPerformed = $false; realCiRunPerformed = $false; providerTestRequired = $true
    fixtureScope = 'Structure, content-preservation and fail-closed checks only; no cryptographic trust claim.' }
Write-SignPathJson (Join-Path $OutputDirectory 'result.json') $summary
$summary | ConvertTo-Json
