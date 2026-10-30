#requires -Version 7.0
# Shared Windows Authenticode policy. No keys or passwords are read or exported.
Set-StrictMode -Version Latest

function ConvertTo-WindowsCertificateThumbprint {
    param([string]$Value, [switch]$Optional)
    $value = ($Value -replace '\s', '').ToUpperInvariant()
    if ($Optional -and -not $value) { return '' }
    if ($value -notmatch '\A[0-9A-F]{40}\z') {
        throw 'Select an explicit 40-hex certificate thumbprint with -CertificateThumbprint or MATHGPT_SIGN_CERT_THUMBPRINT.'
    }
    return $value
}

function Get-WindowsCertificatePublisher {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    return $Certificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
}

function Assert-WindowsCodeSigningEku {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    $eku = @($Certificate.Extensions | Where-Object Oid -ne $null | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
    if ($eku.Count -ne 1 -or '1.3.6.1.5.5.7.3.3' -notin @($eku[0].EnhancedKeyUsages | ForEach-Object Value)) {
        throw 'The selected certificate must explicitly contain the Code Signing EKU (1.3.6.1.5.5.7.3.3).'
    }
}

function Test-WindowsCertificateTrust {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $chain.ChainPolicy.RevocationFlag = [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
        $chain.ChainPolicy.VerificationFlags = [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(20)
        $chain.ChainPolicy.ApplicationPolicy.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
        if (-not $chain.Build($Certificate)) {
            $status = ($chain.ChainStatus | ForEach-Object { $_.Status.ToString() } | Sort-Object -Unique) -join ', '
            throw "The signing certificate does not pass system trust and online revocation checks: $status."
        }
    } finally { $chain.Dispose() }
}

function Assert-WindowsSigningCertificate {
    param(
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string]$CertificateThumbprint,
        [string]$ExpectedPublisher
    )
    if ($null -eq $Certificate) { throw 'The selected code-signing certificate was not found in the specified My certificate store.' }
    if (-not [string]::Equals($Certificate.Thumbprint, $CertificateThumbprint, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The certificate does not match the selected thumbprint.'
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedPublisher) -or
        -not [string]::Equals((Get-WindowsCertificatePublisher $Certificate), $ExpectedPublisher, [StringComparison]::Ordinal)) {
        throw 'The certificate publisher must exactly match -ExpectedPublisher (certificate SimpleName).'
    }
    $now = [DateTime]::UtcNow
    if ($Certificate.NotBefore.ToUniversalTime() -gt $now) { throw 'The selected code-signing certificate is not yet valid.' }
    if ($Certificate.NotAfter.ToUniversalTime() -le $now) { throw 'The selected code-signing certificate has expired.' }
    Assert-WindowsCodeSigningEku $Certificate
    if (-not $Certificate.HasPrivateKey) { throw 'The selected code-signing certificate has no accessible private key. Connect its HSM/token or configure its system-store provider.' }
    if ([Convert]::ToBase64String($Certificate.SubjectName.RawData) -eq [Convert]::ToBase64String($Certificate.IssuerName.RawData)) {
        throw 'Self-signed certificates are not accepted for Production signing.'
    }
    foreach ($extension in $Certificate.Extensions) {
        if ($extension.Oid.Value -eq '2.5.29.19' -and $extension.CertificateAuthority) {
            throw 'A CA certificate cannot be used as the production publisher certificate.'
        }
        if ($extension.Oid.Value -eq '2.5.29.15' -and
            -not ($extension.KeyUsages -band [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature)) {
            throw 'The code-signing certificate does not permit digital signatures.'
        }
    }
    Test-WindowsCertificateTrust $Certificate
}

function Get-WindowsStoreSigningCertificate {
    param([string]$CertificateThumbprint, [ValidateSet('CurrentUser','LocalMachine')][string]$CertificateStoreLocation)
    $path = "Cert:\$CertificateStoreLocation\My\$CertificateThumbprint"
    if (-not (Test-Path -LiteralPath $path)) { throw "No selected code-signing certificate exists in $CertificateStoreLocation/My. Production build stopped before compilation." }
    return Get-Item -LiteralPath $path
}

function Get-WindowsAuthenticodeSignature {
    param([string]$FilePath)
    return Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $FilePath
}

function Resolve-WindowsSdkSignTool {
    # Search installed Microsoft Windows SDKs only; do not download or use PATH replacements.
    $kitRoots = [Collections.Generic.List[string]]::new()
    foreach ($registryPath in @('HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots')) {
        $item = Get-ItemProperty -LiteralPath $registryPath -Name KitsRoot10 -ErrorAction SilentlyContinue
        if ($null -ne $item -and $item.KitsRoot10) { $kitRoots.Add([string]$item.KitsRoot10) }
    }
    if (${env:ProgramFiles(x86)}) { $kitRoots.Add((Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10')) }
    if ($env:ProgramFiles) { $kitRoots.Add((Join-Path $env:ProgramFiles 'Windows Kits\10')) }
    $hostArchitecture = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    $candidates = @(
        foreach ($kitRoot in ($kitRoots | Select-Object -Unique)) {
            $bin = Join-Path $kitRoot 'bin'
            if (-not (Test-Path -LiteralPath $bin -PathType Container)) { continue }
            foreach ($directory in Get-ChildItem -LiteralPath $bin -Directory) {
                $sdkVersion = $null
                if (-not [Version]::TryParse($directory.Name, [ref]$sdkVersion)) { continue }
                $path = Join-Path $directory.FullName "$hostArchitecture\signtool.exe"
                if (Test-Path -LiteralPath $path -PathType Leaf) { [pscustomobject]@{ Path = $path; Version = $sdkVersion } }
            }
        }
    )
    foreach ($candidate in ($candidates | Sort-Object Version -Descending)) {
        $signature = Get-WindowsAuthenticodeSignature $candidate.Path
        if ($null -ne $signature -and $signature.Status -eq 'Valid' -and $null -ne $signature.SignerCertificate -and
            (Get-WindowsCertificatePublisher $signature.SignerCertificate) -eq 'Microsoft Corporation') { return $candidate.Path }
    }
    throw 'Microsoft Windows SDK SignTool was not found with a valid Microsoft signature. Install the Signing Tools component from the official Windows SDK, then retry. No tool was downloaded.'
}

function New-WindowsVerificationContext {
    param([string]$ExpectedPublisher, [string]$CertificateThumbprint = '')
    if ([string]::IsNullOrWhiteSpace($ExpectedPublisher)) { throw 'An explicit -ExpectedPublisher is required for Windows signature verification.' }
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        certificateThumbprint = ConvertTo-WindowsCertificateThumbprint $CertificateThumbprint -Optional
        expectedPublisher = $ExpectedPublisher
        signToolPath = Resolve-WindowsSdkSignTool
    }
}

function New-WindowsSigningContext {
    param(
        [string]$CertificateThumbprint,
        [string]$ExpectedPublisher,
        [ValidateSet('CurrentUser','LocalMachine')][string]$CertificateStoreLocation = 'CurrentUser',
        [string]$TimestampUrl = 'https://timestamp.digicert.com'
    )
    if (-not $CertificateThumbprint -or [string]::IsNullOrWhiteSpace($ExpectedPublisher)) {
        throw 'Production signing requires -CertificateThumbprint and -ExpectedPublisher (or MATHGPT_SIGN_CERT_THUMBPRINT / MATHGPT_SIGN_PUBLISHER). No compilation was started. Use -SigningMode Preview only for an explicitly unsigned preview.'
    }
    $thumbprint = ConvertTo-WindowsCertificateThumbprint $CertificateThumbprint
    $timestampUri = $null
    if (-not [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
        $timestampUri.Scheme -notin @('http','https') -or $timestampUri.UserInfo -or $timestampUri.Fragment -or $timestampUri.Query) {
        throw 'TimestampUrl must be an explicit HTTP(S) RFC3161 endpoint without credentials, query or fragment.'
    }
    $certificate = Get-WindowsStoreSigningCertificate $thumbprint $CertificateStoreLocation
    Assert-WindowsSigningCertificate $certificate $thumbprint $ExpectedPublisher
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        certificateThumbprint = $thumbprint
        expectedPublisher = $ExpectedPublisher
        certificateStoreLocation = $CertificateStoreLocation
        certificateSubject = $certificate.Subject
        certificateNotAfterUtc = $certificate.NotAfter.ToUniversalTime().ToString('o')
        timestampUrl = $timestampUri.AbsoluteUri
        signToolPath = Resolve-WindowsSdkSignTool
    }
}

function Invoke-WindowsNativeCommand {
    param([string]$FilePath, [string[]]$Arguments, [string]$Operation)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.WorkingDirectory = (Get-Location).ProviderPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "$Operation could not start." }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if ($output.Trim()) { Write-Host $output.TrimEnd() }
        if ($process.ExitCode -ne 0) { throw "$Operation failed (exit $($process.ExitCode)); warnings are not accepted as success." }
    } finally { $process.Dispose() }
}

function Get-WindowsEmbeddedSignatureInfo {
    # Read only the PE certificate table. WinVerifyTrust/SignTool separately checks the file digest and trust.
    param([string]$FilePath)
    $stream = [IO.File]::Open($FilePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d) { throw 'Not a PE executable.' }
        $stream.Position = 60
        $peOffset = $reader.ReadUInt32()
        if ($peOffset -gt $stream.Length - 160) { throw 'Invalid PE header.' }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Invalid PE signature.' }
        $optionalHeader = [long]$peOffset + 24
        $stream.Position = $optionalHeader
        $magic = $reader.ReadUInt16()
        $directoryOffset = switch ($magic) { 0x10b { 96 }; 0x20b { 112 }; default { throw 'Unsupported PE optional header.' } }
        $stream.Position = $optionalHeader + $directoryOffset - 4
        if ($reader.ReadUInt32() -lt 5) { throw 'The PE file has no certificate directory.' }
        $stream.Position = $optionalHeader + $directoryOffset + 32
        $tableOffset = $reader.ReadUInt32()
        $tableSize = $reader.ReadUInt32()
        if ($tableOffset -eq 0 -or $tableSize -lt 8 -or ([long]$tableOffset + $tableSize) -gt $stream.Length) { throw 'No valid embedded Authenticode certificate table.' }
        $stream.Position = $tableOffset
        $certificateLength = $reader.ReadUInt32()
        $revision = $reader.ReadUInt16()
        $certificateType = $reader.ReadUInt16()
        if ($revision -ne 0x0200 -or $certificateType -ne 2 -or $certificateLength -lt 8 -or $certificateLength -gt $tableSize -or
            (([long]$certificateLength + 7) -band -8L) -ne $tableSize) {
            throw 'Require exactly one embedded PKCS#7 Authenticode signature.'
        }
        $cms = [System.Security.Cryptography.Pkcs.SignedCms]::new()
        $cms.Decode($reader.ReadBytes([int]$certificateLength - 8))
        $cms.CheckSignature($true)
        if ($cms.ContentInfo.ContentType.Value -ne '1.3.6.1.4.1.311.2.1.4' -or $cms.SignerInfos.Count -ne 1) { throw 'Require one Authenticode signer.' }
        $signer = $cms.SignerInfos[0]
        $sha256 = '2.16.840.1.101.3.4.2.1'
        $content = [System.Formats.Asn1.AsnReader]::new([ReadOnlyMemory[byte]]::new($cms.ContentInfo.Content), [System.Formats.Asn1.AsnEncodingRules]::BER)
        $sequence = $content.ReadSequence()
        $null = $sequence.ReadEncodedValue()
        $digestInfo = $sequence.ReadSequence()
        $digestAlgorithm = $digestInfo.ReadSequence().ReadObjectIdentifier()
        if ($signer.DigestAlgorithm.Value -ne $sha256 -or $digestAlgorithm -ne $sha256) { throw 'The embedded file signature must use SHA256.' }
        $timestamps = @($signer.UnsignedAttributes | Where-Object { $_.Oid.Value -in @('1.3.6.1.4.1.311.3.3.1','1.2.840.113549.1.9.16.2.14') })
        if ($timestamps.Count -ne 1 -or $timestamps[0].Values.Count -ne 1) { throw 'Require exactly one RFC3161 timestamp.' }
        $token = $null
        $consumed = 0
        $encoded = $timestamps[0].Values[0].RawData
        if (-not [System.Security.Cryptography.Pkcs.Rfc3161TimestampToken]::TryDecode([ReadOnlyMemory[byte]]::new($encoded), [ref]$token, [ref]$consumed) -or $consumed -ne $encoded.Length) { throw 'Invalid RFC3161 timestamp token.' }
        $tsaCertificate = $null
        if (-not $token.VerifySignatureForSignerInfo($signer, [ref]$tsaCertificate, $null)) { throw 'The RFC3161 timestamp is not bound to this signature.' }
        if ($token.TokenInfo.HashAlgorithmId.Value -ne $sha256 -or $token.AsSignedCms().SignerInfos[0].DigestAlgorithm.Value -ne $sha256) { throw 'The RFC3161 timestamp must use SHA256.' }
        if ($token.TokenInfo.Timestamp -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) { throw 'The RFC3161 timestamp is in the future.' }
        return [pscustomobject][ordered]@{
            fileDigestAlgorithm = 'SHA256'
            signerThumbprint = $signer.Certificate.Thumbprint
            timestampType = 'RFC3161'
            timestampDigestAlgorithm = 'SHA256'
            timestampUtc = $token.TokenInfo.Timestamp.UtcDateTime.ToString('o')
            timestampCertificateThumbprint = $tsaCertificate.Thumbprint
        }
    } finally { $reader.Dispose(); $stream.Dispose() }
}

function Assert-WindowsSignedFile {
    param([string]$FilePath, [object]$Context)
    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) { throw "Signed file is missing: $FilePath" }
    Invoke-WindowsNativeCommand $Context.signToolPath @('verify','/pa','/all','/tw','/v',$FilePath) 'Authenticode verification'
    $signature = Get-WindowsAuthenticodeSignature $FilePath
    if ($null -eq $signature -or $signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode' -or
        $null -eq $signature.SignerCertificate -or $null -eq $signature.TimeStamperCertificate) {
        throw "The file must have a valid, embedded and timestamped Authenticode signature: $FilePath"
    }
    $certificate = $signature.SignerCertificate
    if ($Context.certificateThumbprint -and -not [string]::Equals($certificate.Thumbprint, $Context.certificateThumbprint, [StringComparison]::OrdinalIgnoreCase)) { throw 'The signature certificate does not match the expected thumbprint.' }
    if (-not [string]::Equals((Get-WindowsCertificatePublisher $certificate), $Context.expectedPublisher, [StringComparison]::Ordinal)) { throw 'The signature publisher does not match the expected publisher.' }
    Assert-WindowsCodeSigningEku $certificate
    $embedded = Get-WindowsEmbeddedSignatureInfo $FilePath
    if ($embedded.signerThumbprint -ne $certificate.Thumbprint -or $embedded.timestampCertificateThumbprint -ne $signature.TimeStamperCertificate.Thumbprint) { throw 'Embedded signature and Windows trust metadata disagree.' }
    return [pscustomobject][ordered]@{
        fileName = [IO.Path]::GetFileName($FilePath)
        sha256 = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash
        signatureStatus = 'Valid'
        publisher = Get-WindowsCertificatePublisher $certificate
        certificateSubject = $certificate.Subject
        certificateThumbprint = $certificate.Thumbprint
        certificateNotAfterUtc = $certificate.NotAfter.ToUniversalTime().ToString('o')
        fileDigestAlgorithm = $embedded.fileDigestAlgorithm
        timestampType = $embedded.timestampType
        timestampDigestAlgorithm = $embedded.timestampDigestAlgorithm
        timestampUtc = $embedded.timestampUtc
        timestampCertificateThumbprint = $embedded.timestampCertificateThumbprint
        verificationPolicy = 'signtool verify /pa /all /tw; Windows Authenticode; embedded SHA256 + RFC3161 SHA256'
    }
}

function Invoke-WindowsFileSigning {
    param([string]$FilePath, [object]$Context)
    $arguments = @('sign','/s','My','/sha1',$Context.certificateThumbprint,'/fd','SHA256','/tr',$Context.timestampUrl,'/td','SHA256','/d','MathGPT','/du','https://mathgpt.xyz')
    if ($Context.certificateStoreLocation -eq 'LocalMachine') { $arguments += '/sm' }
    $arguments += $FilePath
    Invoke-WindowsNativeCommand $Context.signToolPath $arguments 'Authenticode signing and RFC3161 timestamping'
    return Assert-WindowsSignedFile $FilePath $Context
}

function New-WindowsInnoSignToolArgument {
    param([string]$PowerShellPath, [string]$ScriptPath, [string]$ConfigurationPath, [string]$RecordDirectory)
    $values = @($PowerShellPath, $ScriptPath, $ConfigurationPath, $RecordDirectory)
    foreach ($value in $values) { if ($value -match '["\r\n]') { throw 'Inno signing paths may not contain quotes or newlines.' } }
    # Inno substitutes $f as an already quoted filename and $q as a quote. Escape literal $ in paths.
    $quoted = @($values | ForEach-Object { '$q' + $_.Replace('$','$$') + '$q' })
    return '/SMathGPTWindows=' + $quoted[0] + ' -NoLogo -NoProfile -NonInteractive -File ' + $quoted[1] +
        ' -ConfigurationPath ' + $quoted[2] + ' -RecordDirectory ' + $quoted[3] + ' -FilePath $f'
}

function Write-WindowsJsonFile {
    param([string]$FilePath, [object]$Value)
    # CreateNew prevents a new build or a failed retry from silently replacing prior evidence.
    $stream = [IO.File]::Open($FilePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
    try { $writer.Write(($Value | ConvertTo-Json -Depth 12)); $writer.WriteLine() }
    finally { $writer.Dispose(); $stream.Dispose() }
}
