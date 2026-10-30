#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [switch]$SkipBridgeCompile
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WindowsSigning.ps1')
. (Join-Path $PSScriptRoot 'WindowsBridgeResource.ps1')
$project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$version = [string]([xml](Get-Content -LiteralPath (Join-Path $project 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
$work = Join-Path $EvidenceDirectory ('work\tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$nativeCommand = ${function:Invoke-WindowsNativeCommand}
$published = @{}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $project 'build\packages') -File -Filter 'MathGPT-1.6.0-Setup-*.exe') {
    $published[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw "Unexpected rejection: $($_.Exception.Message)" }
        return
    }
    throw "Expected a rejection matching $Pattern."
}
function Test-Case([string]$Name, [scriptblock]$Action) {
    try { & $Action; $checks.Add([ordered]@{ name = $Name; result = 'passed' }) }
    catch { $checks.Add([ordered]@{ name = $Name; result = 'failed'; error = $_.Exception.Message }) }
}
function Invoke-TestProcess([string]$File, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $File; $start.WorkingDirectory = $project; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{ exitCode = $process.ExitCode; output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}
function New-TestCertificate([bool]$CodeSigning = $true, [DateTimeOffset]$NotBefore = ([DateTimeOffset]::UtcNow.AddDays(-1)), [DateTimeOffset]$NotAfter = ([DateTimeOffset]::UtcNow.AddDays(1))) {
    $key = [System.Security.Cryptography.RSA]::Create(2048)
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=In-memory Negative Test Only', $key, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    if ($CodeSigning) {
        $oids = [System.Security.Cryptography.OidCollection]::new(); $null = $oids.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
        $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $true))
    }
    try { return $request.CreateSelfSigned($NotBefore, $NotAfter) } finally { $key.Dispose() }
}

Test-Case 'Production missing certificate stops before build or output creation' {
    $stage = Join-Path $work 'must-not-be-created'
    $result = Invoke-TestProcess (Join-Path $PSHOME 'pwsh.exe') @('-NoProfile','-File',(Join-Path $project 'scripts\Build.ps1'),'-OutputDirectory',$stage,'-CertificateThumbprint','','-ExpectedPublisher','')
    Assert-True ($result.exitCode -ne 0 -and $result.output -match 'Production signing requires') 'Missing-certificate production build did not fail at preflight.'
    Assert-True (-not (Test-Path -LiteralPath $stage)) 'Preflight created a staging directory.'
}
Test-Case 'Missing selected certificate cannot fall back to another identity' {
    Assert-Throws { New-WindowsSigningContext -CertificateThumbprint ('0' * 40) -ExpectedPublisher 'Negative Test Only' } 'No selected code-signing certificate'
}
Test-Case 'Malformed thumbprint rejected' { Assert-Throws { ConvertTo-WindowsCertificateThumbprint 'not-a-thumbprint' } '40-hex' }
Test-Case 'Expired certificate rejected before trust or compilation' {
    $certificate = New-TestCertificate -NotBefore ([DateTimeOffset]::UtcNow.AddDays(-3)) -NotAfter ([DateTimeOffset]::UtcNow.AddDays(-1))
    try { Assert-Throws { Assert-WindowsSigningCertificate $certificate $certificate.Thumbprint 'In-memory Negative Test Only' } 'has expired' } finally { $certificate.Dispose() }
}
Test-Case 'Future certificate rejected' {
    $certificate = New-TestCertificate -NotBefore ([DateTimeOffset]::UtcNow.AddDays(1)) -NotAfter ([DateTimeOffset]::UtcNow.AddDays(3))
    try { Assert-Throws { Assert-WindowsSigningCertificate $certificate $certificate.Thumbprint 'In-memory Negative Test Only' } 'not yet valid' } finally { $certificate.Dispose() }
}
Test-Case 'Missing Code Signing EKU rejected' {
    $certificate = New-TestCertificate -CodeSigning $false
    try { Assert-Throws { Assert-WindowsSigningCertificate $certificate $certificate.Thumbprint 'In-memory Negative Test Only' } 'Code Signing EKU' } finally { $certificate.Dispose() }
}
Test-Case 'Unexpected publisher rejected' {
    $certificate = New-TestCertificate
    try { Assert-Throws { Assert-WindowsSigningCertificate $certificate $certificate.Thumbprint 'Different Publisher' } 'exactly match' } finally { $certificate.Dispose() }
}
Test-Case 'Certificate without private key rejected for local signing' {
    $certificate = New-TestCertificate
    $publicCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate.RawData)
    try { Assert-Throws { Assert-WindowsSigningCertificate $publicCertificate $publicCertificate.Thumbprint 'In-memory Negative Test Only' } 'no accessible private key' } finally { $publicCertificate.Dispose(); $certificate.Dispose() }
}
Test-Case 'Self-signed test certificate cannot be a Production identity' {
    $certificate = New-TestCertificate
    try { Assert-Throws { Assert-WindowsSigningCertificate $certificate $certificate.Thumbprint 'In-memory Negative Test Only' } 'Self-signed' } finally { $certificate.Dispose() }
}
Test-Case 'A private-key leaf from an untrusted test CA is rejected by actual system chain validation' {
    $issuerKey = [System.Security.Cryptography.RSA]::Create(2048); $leafKey = [System.Security.Cryptography.RSA]::Create(2048)
    $issuerRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=In-memory Untrusted Test CA', $issuerKey, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $issuerRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true))
    $issuerRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign,$true))
    $issuer = $issuerRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-2), [DateTimeOffset]::UtcNow.AddDays(2))
    $leafRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=In-memory Untrusted Leaf', $leafKey, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $oids = [System.Security.Cryptography.OidCollection]::new(); $null = $oids.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
    $leafRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids,$true))
    $issued = $leafRequest.Create($issuer,[DateTimeOffset]::UtcNow.AddDays(-1),[DateTimeOffset]::UtcNow.AddDays(1),[byte[]]@(1,2,3,4,5,6,7,8))
    $leaf = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($issued,$leafKey)
    try { Assert-Throws { Assert-WindowsSigningCertificate $leaf $leaf.Thumbprint 'In-memory Untrusted Leaf' } 'does not pass system trust' }
    finally { $leaf.Dispose(); $issued.Dispose(); $issuer.Dispose(); $leafKey.Dispose(); $issuerKey.Dispose() }
}
Test-Case 'Explicit Preview preflight is distinguishable and does not compile' {
    $result = & (Join-Path $project 'scripts\Build.ps1') -SigningMode Preview -PreflightOnly
    Assert-True ($result.signingMode -eq 'Preview' -and -not $result.productionSigningAvailable -and -not $result.compilationStarted) 'Preview preflight reported production signing.'
}
Test-Case 'Existing stage contents never overwritten' {
    $stage = Join-Path $work 'existing-stage'; [IO.Directory]::CreateDirectory($stage) | Out-Null
    $marker = Join-Path $stage 'preserve.txt'; [IO.File]::WriteAllText($marker, 'keep')
    Assert-Throws { & (Join-Path $project 'scripts\Build.ps1') -SigningMode Preview -OutputDirectory $stage } 'new or empty'
    Assert-True ([IO.File]::ReadAllText($marker) -eq 'keep') 'Existing stage marker changed.'
}
Test-Case 'Evidence cannot silently overwrite prior acceptance' {
    $path = Join-Path $work 'immutable.json'; Write-WindowsJsonFile $path @{ marker = 'keep' }
    Assert-Throws { Write-WindowsJsonFile $path @{ marker = 'changed' } } 'already exists'
    Assert-True ((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).marker -eq 'keep') 'Existing evidence changed.'
}

$sdkTool = Resolve-WindowsSdkSignTool
$sdkSignature = Get-WindowsAuthenticodeSignature $sdkTool
$verificationContext = New-WindowsVerificationContext -ExpectedPublisher 'Microsoft Corporation' -CertificateThumbprint $sdkSignature.SignerCertificate.Thumbprint
Test-Case 'Independent verifier accepts a real Microsoft SDK SHA256/RFC3161 fixture without publisher private key' {
    $record = Assert-WindowsSignedFile $sdkTool $verificationContext
    Assert-True ($record.publisher -eq 'Microsoft Corporation' -and $record.timestampType -eq 'RFC3161' -and $record.timestampDigestAlgorithm -eq 'SHA256') 'The genuine SDK signature was not verified.'
}
Test-Case 'Independent verifier rejects a correctly signed file from an unexpected publisher' {
    $context = New-WindowsVerificationContext -ExpectedPublisher 'MathGPT Negative Publisher Test'
    Assert-Throws { Assert-WindowsSignedFile $sdkTool $context } 'does not match the expected publisher'
}
Test-Case 'Tampered signed fixture fails Authenticode verification' {
    $tampered = Join-Path $work 'tampered-microsoft-fixture.exe'; [IO.File]::Copy($sdkTool, $tampered)
    $bytes = [IO.File]::ReadAllBytes($tampered); $bytes[512] = $bytes[512] -bxor 1; [IO.File]::WriteAllBytes($tampered, $bytes)
    Assert-Throws { Assert-WindowsSignedFile $tampered $verificationContext } 'verification failed'
}
Test-Case 'Signing order and arguments cannot accept an unsigned file after a mocked signing process' {
    $unsigned = Join-Path $work 'unsigned-boundary-test.exe'; [IO.File]::WriteAllText($unsigned, 'This is not a signed binary.')
    $script:signingCalls = [Collections.Generic.List[object]]::new()
    function script:Invoke-WindowsNativeCommand { param([string]$FilePath,[string[]]$Arguments,[string]$Operation) $script:signingCalls.Add($Arguments) }
    try {
        $context = [pscustomobject]@{ signToolPath = $sdkTool; certificateThumbprint = ('0' * 40); expectedPublisher = 'Negative Test'; timestampUrl = 'https://timestamp.digicert.com'; certificateStoreLocation = 'LocalMachine' }
        Assert-Throws { Invoke-WindowsFileSigning $unsigned $context } 'valid, embedded and timestamped'
        Assert-True ($signingCalls.Count -eq 2 -and $signingCalls[0][0] -eq 'sign' -and $signingCalls[1][0] -eq 'verify') 'Sign must precede mandatory verification.'
        $sign = $signingCalls[0]; $verify = $signingCalls[1]
        Assert-True ($sign[[Array]::IndexOf($sign,'/fd') + 1] -eq 'SHA256' -and [Array]::IndexOf($sign,'/tr') -lt [Array]::IndexOf($sign,'/td') -and $sign[[Array]::IndexOf($sign,'/td') + 1] -eq 'SHA256') 'The SHA256/RFC3161 arguments are incorrect.'
        Assert-True ('/sm' -in $sign -and '/sha1' -in $sign -and '/p' -notin $sign -and '/f' -notin $sign) 'Signing must use only the selected system-store key.'
        Assert-True ('/pa' -in $verify -and '/all' -in $verify -and '/tw' -in $verify) 'Mandatory verification policy is missing.'
    } finally { Set-Item -Path function:script:Invoke-WindowsNativeCommand -Value $nativeCommand }
}
Test-Case 'A missing timestamp is rejected even if external trust metadata claims a valid signature' {
    $unsigned = Join-Path $work 'timestamp-boundary-test.exe'; [IO.File]::WriteAllText($unsigned, 'Timestamp rejection fixture.')
    $authenticode = ${function:Get-WindowsAuthenticodeSignature}
    function script:Invoke-WindowsNativeCommand { param([string]$FilePath,[string[]]$Arguments,[string]$Operation) }
    function script:Get-WindowsAuthenticodeSignature { param([string]$FilePath) return [pscustomobject]@{ Status = 'Valid'; SignatureType = 'Authenticode'; SignerCertificate = $sdkSignature.SignerCertificate; TimeStamperCertificate = $null } }
    try { Assert-Throws { Assert-WindowsSignedFile $unsigned $verificationContext } 'valid, embedded and timestamped' }
    finally { Set-Item -Path function:script:Invoke-WindowsNativeCommand -Value $nativeCommand; Set-Item -Path function:script:Get-WindowsAuthenticodeSignature -Value $authenticode }
}
Test-Case 'External tool warnings fail closed' {
    Assert-Throws { Invoke-WindowsNativeCommand (Join-Path $PSHOME 'pwsh.exe') @('-NoProfile','-Command','exit 2') 'Warning-only boundary test' } 'warnings are not accepted as success'
}

$iscc = 'D:\Codex\Tools\InnoSetup-6.7.3\ISCC.exe'
$iss = Join-Path $project 'scripts\MathGPT.iss'
Test-Case 'Actual Inno preview uses supplied version and explicit Preview package name' {
    $stage = Join-Path $work 'inno-preview-stage'; $out = Join-Path $work 'inno-preview-output'
    [IO.Directory]::CreateDirectory($stage) | Out-Null; [IO.Directory]::CreateDirectory($out) | Out-Null
    [IO.File]::WriteAllText((Join-Path $stage 'fixture.txt'), 'Packaging fixture; never executed.')
    $result = Invoke-TestProcess $iscc @('/Qp',"/DStage=$stage",'/DArchitecture=win-x64',"/DOutputPath=$out","/DAppVersion=$version",'/DSigningMode=Preview',$iss)
    [IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'inno-preview.log'), $result.output)
    $setup = Join-Path $out "MathGPT-$version-Preview-Setup-win-x64.exe"
    Assert-True ($result.exitCode -eq 0 -and (Test-Path -LiteralPath $setup)) 'Preview compiler did not produce the versioned preview fixture.'
    Assert-True ((Get-WindowsAuthenticodeSignature $setup).Status -eq 'NotSigned') 'The preview fixture unexpectedly claimed a trusted publisher.'
}
Test-Case 'Actual Inno external Prepare produces only an unsigned uninstaller and fails specifically for missing signature' {
    $stage = Join-Path $work 'external-stage'; $out = Join-Path $work 'external-output'; $cache = Join-Path $work 'external-uninstallers'
    foreach ($directory in @($stage,$out,$cache)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
    [IO.File]::WriteAllText((Join-Path $stage 'fixture.txt'), 'Uninstaller preparation fixture; never executed.')
    $result = Invoke-TestProcess $iscc @('/Qp',"/DStage=$stage",'/DArchitecture=win-x64',"/DOutputPath=$out","/DAppVersion=$version",'/DSigningMode=Production','/DExternalSigning=Prepare',"/DSignedUninstallerPath=$cache",$iss)
    [IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'inno-external-prepare.log'), $result.output)
    $uninstallers = @(Get-ChildItem -LiteralPath $cache -File)
    Assert-True ($result.exitCode -ne 0 -and $result.output -match 'uninstaller' -and $result.output -match 'sign') 'Prepare failed for an unrelated reason or unexpectedly produced an installer.'
    Assert-True ($uninstallers.Count -eq 1 -and $uninstallers[0].Name -match '^uninst-.*\.e(32|64)$' -and (Get-WindowsAuthenticodeSignature $uninstallers[0].FullName).Status -eq 'NotSigned') 'The expected unsigned Inno uninstaller was not produced.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $out "MathGPT-$version-Setup-win-x64.exe"))) 'Prepare produced a production installer.'
}
Test-Case 'Actual Inno external Package rejects an unsigned cached uninstaller even when source signcheck passes' {
    $stage = Join-Path $work 'external-stage'; $out = Join-Path $work 'external-output'; $cache = Join-Path $work 'external-uninstallers'
    [IO.File]::Copy($sdkTool, (Join-Path $stage 'MathGPT.exe')); [IO.File]::Copy($sdkTool, (Join-Path $stage 'MathGPT.Bridge.exe'))
    $result = Invoke-TestProcess $iscc @('/Qp',"/DStage=$stage",'/DArchitecture=win-x64',"/DOutputPath=$out","/DAppVersion=$version",'/DSigningMode=Production','/DExternalSigning=Package',"/DSignedUninstallerPath=$cache",$iss)
    [IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'inno-external-package-rejected.log'), $result.output)
    Assert-True ($result.exitCode -ne 0 -and $result.output -match 'uninstaller' -and $result.output -match 'sign') 'External Package did not reject the unsigned uninstaller.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $out "MathGPT-$version-Setup-win-x64.exe"))) 'An unsigned cached uninstaller produced a production installer.'
}
Test-Case 'Actual Inno local callback quotes paths and refuses a signing-tool failure before producing a package' {
    $testRoot = Join-Path $work 'Inno 中文 $ paths'; $stage = Join-Path $testRoot 'stage'; $out = Join-Path $testRoot 'output'; $records = Join-Path $testRoot 'records'; $signWorkspace = Join-Path $testRoot 'workspace'
    foreach ($directory in @($stage,$out,$records,(Join-Path $signWorkspace 'uninstallers'))) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
    [IO.File]::Copy($sdkTool, (Join-Path $stage 'MathGPT.exe')); [IO.File]::Copy($sdkTool, (Join-Path $stage 'MathGPT.Bridge.exe'))
    $configuration = Join-Path $testRoot 'public configuration.json'; [IO.File]::WriteAllText($configuration, '{}')
    $callback = Join-Path $testRoot 'Reject signing.ps1'
    $callbackSource = @'
param([string]$ConfigurationPath,[string]$RecordDirectory,[string]$FilePath)
$result = @{ configurationPath = $ConfigurationPath; recordDirectory = $RecordDirectory; filePath = $FilePath }
[IO.File]::WriteAllText((Join-Path $RecordDirectory 'observed.json'), ($result | ConvertTo-Json))
exit 37
'@
    [IO.File]::WriteAllText($callback, $callbackSource, [Text.UTF8Encoding]::new($false))
    $signArgument = New-WindowsInnoSignToolArgument (Join-Path $PSHOME 'pwsh.exe') $callback $configuration $records
    $result = Invoke-TestProcess $iscc @('/Qp',"/DStage=$stage",'/DArchitecture=win-x64',"/DOutputPath=$out","/DAppVersion=$version",'/DSigningMode=Production',"/DSigningWorkspace=$signWorkspace",$signArgument,$iss)
    [IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'inno-rejected-signing.log'), $result.output)
    $observedPath = Join-Path $records 'observed.json'
    Assert-True ($result.exitCode -ne 0 -and (Test-Path -LiteralPath $observedPath)) 'The external signing callback was not reached.'
    $observed = Get-Content -LiteralPath $observedPath -Raw | ConvertFrom-Json
    Assert-True ($observed.configurationPath -eq $configuration -and $observed.recordDirectory -eq $records -and $observed.filePath.StartsWith(($signWorkspace + '\uninstallers\'), [StringComparison]::OrdinalIgnoreCase)) 'Inno changed a quoted Unicode, dollar or space-containing path.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $out "MathGPT-$version-Setup-win-x64.exe"))) 'A signing-tool failure produced a releasable package.'
}

if (-not $SkipBridgeCompile) {
    foreach ($runtime in @('win-x64','win-arm64')) {
        Test-Case "Actual $runtime Bridge build includes matching product/version resources without source writes or vendor copies" {
            $resourceDirectory = Join-Path $work $runtime
            $tools = Get-WindowsBridgeResourceTools
            $bridgeBuild = New-WindowsBridgeBuildDirectory -Version $version -Runtime $runtime -BridgeDirectory (Join-Path $project 'bridge') -OutputDirectory $resourceDirectory -Tools $tools
            $output = Join-Path $resourceDirectory 'MathGPT.Bridge.exe'
            $oldGoOs = $env:GOOS; $oldGoArch = $env:GOARCH
            Push-Location $bridgeBuild.buildDirectory
            try {
                $env:GOOS = 'windows'; $env:GOARCH = if ($runtime -eq 'win-arm64') { 'arm64' } else { 'amd64' }
                Invoke-WindowsNativeCommand 'D:\Codex\Tools\Go\1.27.3\go\bin\go.exe' @('build','-mod=vendor','-trimpath','-ldflags=-s -w','-o',$output,'.') 'Bridge metadata fixture build'
            } finally { Pop-Location; $env:GOOS = $oldGoOs; $env:GOARCH = $oldGoArch; Remove-WindowsBridgeBuildDirectory $bridgeBuild }
            $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($output)
            Assert-True ($metadata.ProductName -eq 'MathGPT' -and $metadata.ProductVersion -eq $version -and $metadata.FileDescription -eq 'MathGPT Protocol Bridge') 'Bridge product/version metadata does not match the application.'
            $bytes = [IO.File]::ReadAllBytes($output); $peOffset = [BitConverter]::ToInt32($bytes,60); $machine = [BitConverter]::ToUInt16($bytes,$peOffset + 4)
            Assert-True ($machine -eq $(if ($runtime -eq 'win-arm64') { 0xaa64 } else { 0x8664 })) 'The resource build changed the Bridge target architecture.'
            $metadataRecord = @{ productName = $metadata.ProductName; productVersion = $metadata.ProductVersion; fileDescription = $metadata.FileDescription; machine = ('0x{0:x4}' -f $machine); sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash; signatureStatus = (Get-WindowsAuthenticodeSignature $output).Status.ToString(); executed = $false }
            [IO.File]::WriteAllText((Join-Path $EvidenceDirectory "$runtime-bridge-metadata.json"), ($metadataRecord | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        }
    }
}
Test-Case 'Previously published Windows 1.6.0 packages remain byte-for-byte unchanged' {
    foreach ($path in $published.Keys) { Assert-True ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $published[$path]) 'A published package changed.' }
}
$result = [ordered]@{
    schemaVersion = 1; version = $version; platform = 'Windows'; checkedAtUtc = [DateTime]::UtcNow.ToString('o')
    scope = 'configuration, negative gates, real SDK verification, Inno contract and unexecuted Bridge metadata builds'
    mathgptTrustedSigningPerformed = $false; mathgptReleaseReady = $false; certificateStoreModified = $false
    passed = @($checks | Where-Object { $_.result -eq 'passed' }).Count
    failed = @($checks | Where-Object { $_.result -eq 'failed' }).Count
    publishedPackages = $published; checks = @($checks)
}
$summaryPath = Join-Path $EvidenceDirectory 'windows-signing-tests.json'
[IO.File]::WriteAllText($summaryPath, ($result | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
$result | ConvertTo-Json -Depth 10
if ($result.failed -eq 0) {
    $resolvedWork = [IO.Path]::GetFullPath($work)
    $allowedPrefix = $EvidenceDirectory.TrimEnd('\') + '\work\'
    if (-not $resolvedWork.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $resolvedWork -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refuse unsafe fixture cleanup.' }
    Remove-Item -LiteralPath $resolvedWork -Recurse -Force
} else { throw "$($result.failed) Windows signing tests failed. See $summaryPath" }
