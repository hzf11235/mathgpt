#requires -Version 7.0
# Shared, offline helpers. This file never submits data to a signing service.
$ErrorActionPreference = 'Stop'
$script:SignPathVersion = '1.6.1'
$script:SignPathPayloadNames = @('MathGPT.exe', 'MathGPT.Bridge.exe', 'MathGPT.Uninstaller.exe')

function Assert-SignPathPreflight {
    $required = @('SIGNPATH_ORGANIZATION_ID', 'SIGNPATH_PROJECT_SLUG', 'SIGNPATH_SIGNING_POLICY_SLUG',
        'SIGNPATH_PAYLOAD_CONFIG_SLUG', 'SIGNPATH_INSTALLER_CONFIG_SLUG', 'SIGNPATH_EXPECTED_PUBLISHER', 'SIGNPATH_API_TOKEN')
    foreach ($name in $required) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
            throw "Missing required signing configuration: $name. No build or signing request was started."
        }
    }
    $org = [Guid]::Empty
    if (-not [Guid]::TryParse($env:SIGNPATH_ORGANIZATION_ID, [ref]$org) -or $org -eq [Guid]::Empty) { throw 'Invalid SIGNPATH_ORGANIZATION_ID.' }
    foreach ($name in @('SIGNPATH_PROJECT_SLUG', 'SIGNPATH_SIGNING_POLICY_SLUG', 'SIGNPATH_PAYLOAD_CONFIG_SLUG', 'SIGNPATH_INSTALLER_CONFIG_SLUG')) {
        if ([Environment]::GetEnvironmentVariable($name) -notmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z') { throw "Invalid configuration: $name." }
    }
    if ($env:SIGNPATH_EXPECTED_PUBLISHER -match '[\r\n]' -or $env:SIGNPATH_EXPECTED_PUBLISHER.Length -gt 200) { throw 'Invalid SIGNPATH_EXPECTED_PUBLISHER.' }
    if ($env:SIGNPATH_CERTIFICATE_THUMBPRINT -and $env:SIGNPATH_CERTIFICATE_THUMBPRINT -notmatch '\A[0-9a-fA-F]{40}\z') { throw 'Invalid SIGNPATH_CERTIFICATE_THUMBPRINT.' }
    if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows' -or
        $env:GITHUB_EVENT_NAME -ne 'workflow_dispatch' -or $env:SIGNPATH_REPOSITORY_VISIBILITY -ne 'public') {
        throw 'This preparation workflow requires a manual run in a public repository on GitHub-hosted Windows.'
    }
    # Never return environment values: the API token exists only in the caller's environment.
    [pscustomobject]@{ preflight = 'passed'; product = 'MathGPT'; version = $script:SignPathVersion; providerTestRequired = $true }
}

function Assert-SignPathUnlinkedPath([string]$PathValue) {
    $probe = [IO.Path]::GetFullPath($PathValue)
    while ($probe) {
        if (Test-Path -LiteralPath $probe) {
            if ((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse points are not allowed: $probe" }
        }
        $probe = Split-Path -Path $probe -Parent
    }
}

function New-SignPathDirectory([string]$PathValue) {
    Assert-SignPathUnlinkedPath $PathValue
    if (Test-Path -LiteralPath $PathValue) { throw "A fresh output directory is required: $PathValue" }
    [IO.Directory]::CreateDirectory($PathValue) | Out-Null
}

function Assert-SignPathExactFiles([string]$Directory, [string[]]$Names) {
    Assert-SignPathUnlinkedPath $Directory
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { throw 'The signing artifact directory is missing.' }
    $items = @(Get-ChildItem -LiteralPath $Directory -Force)
    if ($items.Count -ne $Names.Count) { throw 'The signing artifact has missing or unexpected entries.' }
    foreach ($item in $items) {
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Name -cnotin $Names) {
            throw 'Only the explicitly named MathGPT signing targets may be returned.'
        }
    }
}

function Get-SignPathPeLayout([string]$FilePath) {
    Assert-SignPathUnlinkedPath $FilePath
    $stream = [IO.File]::OpenRead($FilePath)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 256 -or $reader.ReadUInt16() -ne 0x5a4d) { throw 'Not a PE executable.' }
        $stream.Position = 60
        $pe = [long]$reader.ReadUInt32()
        if ($pe -lt 64 -or $pe -gt $stream.Length - 160) { throw 'Invalid PE header position.' }
        $stream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Invalid PE signature.' }
        $optional = $pe + 24
        $stream.Position = $optional
        $directory = switch ($reader.ReadUInt16()) { 0x10b { 96 }; 0x20b { 112 }; default { throw 'Unsupported PE optional header.' } }
        $security = $optional + $directory + 32
        if ($security + 8 -gt $stream.Length) { throw 'Truncated PE header.' }
        $stream.Position = $optional + $directory - 4
        if ($reader.ReadUInt32() -lt 5) { throw 'Missing PE security directory.' }
        $stream.Position = $security
        $offset = [long]$reader.ReadUInt32()
        $size = [long]$reader.ReadUInt32()
        if (($offset -eq 0) -ne ($size -eq 0)) { throw 'Inconsistent PE certificate table.' }
        if ($offset -ne 0 -and ($offset -lt $security + 8 -or $offset % 8 -ne 0 -or $size -lt 8 -or $offset + $size -ne $stream.Length)) {
            throw 'The certificate table must be aligned and at the end of the PE file.'
        }
        [pscustomobject]@{ length = $stream.Length; checksum = $optional + 64; security = $security; certificateOffset = $offset; certificateSize = $size }
    } finally { $reader.Dispose(); $stream.Dispose() }
}

function Get-SignPathPePayloadDigest([string]$FilePath) {
    # Compare pre-signing bytes, excluding PE checksum, security-directory fields,
    # the certificate table and only the required zero alignment padding.
    $layout = Get-SignPathPeLayout $FilePath
    $end = if ($layout.certificateOffset) { $layout.certificateOffset } else { $layout.length }
    $segments = @(
        @{ start = 0L; end = [long]$layout.checksum },
        @{ start = [long]$layout.checksum + 4; end = [long]$layout.security },
        @{ start = [long]$layout.security + 8; end = [long]$end }
    )
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    $stream = [IO.File]::OpenRead($FilePath)
    $buffer = [byte[]]::new(65536)
    try {
        foreach ($segment in $segments) {
            $stream.Position = $segment.start
            $remaining = $segment.end - $segment.start
            while ($remaining -gt 0) {
                $read = $stream.Read($buffer, 0, [int][Math]::Min($buffer.Length, $remaining))
                if ($read -le 0) { throw 'Unexpected end of PE file.' }
                $hash.AppendData($buffer, 0, $read)
                $remaining -= $read
            }
        }
        if (-not $layout.certificateOffset -and $end % 8 -ne 0) { $hash.AppendData([byte[]]::new([int](8 - $end % 8))) }
        [Convert]::ToHexString($hash.GetHashAndReset())
    } finally { $stream.Dispose(); $hash.Dispose() }
}

function Assert-SignPathPayloadUnchanged([string]$UnsignedPath, [string]$SignedPath) {
    $before = Get-SignPathPeLayout $UnsignedPath
    $after = Get-SignPathPeLayout $SignedPath
    if ($before.certificateOffset -ne 0) { throw 'Signing inputs must be unsigned MathGPT binaries.' }
    $aligned = $before.length + (8 - $before.length % 8) % 8
    if ($after.certificateOffset -ne $aligned -or (Get-SignPathPePayloadDigest $UnsignedPath) -ne (Get-SignPathPePayloadDigest $SignedPath)) {
        throw 'Signing changed the executable payload; the response will not be packaged.'
    }
}

function Assert-SignPathMetadata([string]$FilePath) {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($FilePath)
    # Inno 6.7.3 pads its fixed VERSIONINFO string slots with ASCII spaces.
    # Keep those bytes intact for Inno's second-pass comparison. The provider's
    # handling of this padding must be tested; the XML restrictions remain exact.
    if (-not $info.ProductName -or -not $info.ProductVersion -or
        $info.ProductName.TrimEnd([char]32) -cne 'MathGPT' -or $info.ProductVersion.TrimEnd([char]32) -cne $script:SignPathVersion -or
        $info.ProductMajorPart -ne 1 -or $info.ProductMinorPart -ne 6 -or $info.ProductBuildPart -ne 1 -or $info.ProductPrivatePart -ne 0) {
        throw "Require ProductName MathGPT and exact ProductVersion $script:SignPathVersion on every signing target: $FilePath"
    }
}

function Write-SignPathJson([string]$FilePath, [object]$Value) {
    $stream = [IO.File]::Open($FilePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
    try { $writer.Write(($Value | ConvertTo-Json -Depth 15)); $writer.WriteLine() }
    finally { $writer.Dispose(); $stream.Dispose() }
}

function Read-SignPathManifest([string]$Workspace) {
    $manifest = Get-Content -LiteralPath (Join-Path $Workspace 'payload-input.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.product -cne 'MathGPT' -or $manifest.version -cne $script:SignPathVersion -or
        $manifest.runtime -notin @('win-x64', 'win-arm64') -or @($manifest.mappings).Count -ne 3) { throw 'Invalid signing input manifest.' }
    $names = @($manifest.mappings | ForEach-Object signingName)
    if (@($names | Sort-Object -Unique).Count -ne 3 -or @($names | Where-Object { $_ -cnotin $script:SignPathPayloadNames }).Count) { throw 'Unexpected signing mapping.' }
    foreach ($mapping in $manifest.mappings) {
        $expected = switch -Exact ($mapping.signingName) {
            'MathGPT.exe' { 'stage/MathGPT.exe' }
            'MathGPT.Bridge.exe' { 'stage/MathGPT.Bridge.exe' }
            'MathGPT.Uninstaller.exe' {
                if ($mapping.originalName -notmatch '\Auninst-[A-Za-z0-9.-]+\.e(?:32|64)\z') { throw 'Invalid original uninstaller cache name.' }
                'uninstaller-cache/' + $mapping.originalName
            }
        }
        if ($mapping.originalPath -cne $expected -or $mapping.unsignedSha256 -notmatch '\A[0-9A-F]{64}\z') { throw 'Invalid original signing path or digest.' }
        $reference = Join-Path $Workspace ('unsigned-payload/' + $mapping.signingName)
        if ((Get-FileHash -LiteralPath $reference -Algorithm SHA256).Hash -cne $mapping.unsignedSha256) { throw 'An unsigned reference changed after preparation.' }
    }
    return $manifest
}

function Invoke-SignPathInno([string]$Compiler, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Compiler
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Inno compiler could not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        [pscustomobject]@{ exitCode = $process.ExitCode; output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}

function Assert-SignPathPrepareResult([object]$Result, [string]$CacheDirectory, [string]$PackageDirectory) {
    $cache = @(Get-ChildItem -LiteralPath $CacheDirectory -File)
    if ($Result.exitCode -ne 2 -or $cache.Count -ne 1 -or $cache[0].Name -notmatch '\Auninst-[A-Za-z0-9.-]+\.e(?:32|64)\z' -or
        $Result.output -notlike '*Signed uninstaller mode is enabled. Using an external code-signing tool,*' -or
        -not $Result.output.Contains($cache[0].FullName) -or @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.exe').Count -ne 0) {
        throw 'Inno preparation did not stop at the expected unsigned-uninstaller handoff.'
    }
    if ((Get-SignPathPeLayout $cache[0].FullName).certificateOffset -ne 0) { throw 'The prepared uninstaller must be unsigned.' }
    return $cache[0]
}
