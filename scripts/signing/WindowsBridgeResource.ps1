#requires -Version 7.0
# Build in a tiny temporary first-party source stage; reuse vendor through a junction.
# Go's .syso archive handling does not consistently honor -overlay.
function Get-WindowsBridgeResourceTools {
    $signTool = Resolve-WindowsSdkSignTool
    $rc = Join-Path (Split-Path $signTool -Parent) 'rc.exe'
    $cvtresCandidates = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\cvtres.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\cvtres.exe')
    )
    $cvtres = $cvtresCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    foreach ($tool in @($rc, $cvtres)) {
        if (-not $tool -or -not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw 'The installed Microsoft SDK rc.exe and system .NET Framework cvtres.exe are required for Bridge product/version metadata.' }
        $signature = Get-WindowsAuthenticodeSignature $tool
        if ($null -eq $signature -or $signature.Status -ne 'Valid' -or (Get-WindowsCertificatePublisher $signature.SignerCertificate) -notin @('Microsoft Corporation','Microsoft Windows')) {
            throw 'Bridge resource tools must have valid Microsoft signatures.'
        }
    }
    return [pscustomobject]@{ rc = $rc; cvtres = $cvtres }
}

function ConvertTo-GoCompatibleResourceObject {
    param([string]$FilePath)
    # Modern cvtres emits @comp.id as IMAGE_SYM_ABSOLUTE. Go rejects it (it only
    # special-cases @feat.00). Mark that unreferenced compiler bookkeeping symbol
    # IMAGE_SYM_DEBUG instead; Go ignores it. No resource bytes or indices change.
    $bytes = [IO.File]::ReadAllBytes($FilePath)
    if ($bytes.Length -lt 20) { throw 'Invalid cvtres COFF object.' }
    $sectionCount = [BitConverter]::ToUInt16($bytes, 2)
    $table = [BitConverter]::ToUInt32($bytes, 8)
    $count = [BitConverter]::ToUInt32($bytes, 12)
    $optionalSize = [BitConverter]::ToUInt16($bytes, 16)
    if ($optionalSize -ne 0 -or $sectionCount -eq 0 -or ([long]$table + 18L * $count) -gt $bytes.Length -or
        (20L + 40L * $sectionCount) -gt $bytes.Length) { throw 'Unsupported cvtres COFF layout.' }
    $referencedSymbols = [Collections.Generic.HashSet[uint32]]::new()
    for ($section = 0; $section -lt $sectionCount; $section++) {
        $header = 20 + 40 * $section
        $relocations = [BitConverter]::ToUInt32($bytes, $header + 24)
        $relocationCount = [BitConverter]::ToUInt16($bytes, $header + 32)
        if (([long]$relocations + 10L * $relocationCount) -gt $bytes.Length) { throw 'Invalid cvtres relocation table.' }
        for ($index = 0; $index -lt $relocationCount; $index++) {
            $null = $referencedSymbols.Add([BitConverter]::ToUInt32($bytes, $relocations + 10 * $index + 4))
        }
    }
    for ($index = 0; $index -lt $count; $index++) {
        $offset = $table + 18 * $index
        $name = [Text.Encoding]::ASCII.GetString($bytes, $offset, 8).TrimEnd([char]0)
        $auxCount = $bytes[$offset + 17]
        if ($name -eq '@comp.id') {
            if ([BitConverter]::ToInt16($bytes, $offset + 12) -ne -1 -or $auxCount -ne 0 -or $referencedSymbols.Contains([uint32]$index)) {
                throw 'Unexpected or referenced cvtres @comp.id; preserve the object and stop.'
            }
            [BitConverter]::GetBytes([int16]-2).CopyTo($bytes, $offset + 12)
        }
        $index += $auxCount
    }
    [IO.File]::WriteAllBytes($FilePath, $bytes)
}

function New-WindowsBridgeBuildDirectory {
    param(
        [string]$Version,
        [ValidateSet('win-x64','win-arm64')][string]$Runtime,
        [string]$BridgeDirectory,
        [string]$OutputDirectory,
        [object]$Tools
    )
    if ($Version -notmatch '\A\d+\.\d+\.\d+\z') { throw 'Bridge resource version must match the numeric release version.' }
    [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    $resource = Join-Path $OutputDirectory 'MathGPT.Bridge.rc'
    $compiled = Join-Path $OutputDirectory 'MathGPT.Bridge.res'
    $buildDirectory = Join-Path $OutputDirectory 'bridge-build'
    [IO.Directory]::CreateDirectory($buildDirectory) | Out-Null
    $arch = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'amd64' }
    $object = Join-Path $buildDirectory "mathgpt_version_windows_$arch.syso"
    $numericVersion = $Version.Replace('.', ',') + ',0'
    $text = @"
1 VERSIONINFO
FILEVERSION $numericVersion
PRODUCTVERSION $numericVersion
FILEFLAGSMASK 0x3fL
FILEFLAGS 0
FILEOS 0x40004L
FILETYPE 0x1L
FILESUBTYPE 0
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904b0"
    BEGIN
      VALUE "CompanyName", "MathGPT\0"
      VALUE "FileDescription", "MathGPT Protocol Bridge\0"
      VALUE "FileVersion", "$Version.0\0"
      VALUE "InternalName", "MathGPT.Bridge\0"
      VALUE "OriginalFilename", "MathGPT.Bridge.exe\0"
      VALUE "ProductName", "MathGPT\0"
      VALUE "ProductVersion", "$Version\0"
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x0409, 1200
  END
END
"@
    [IO.File]::WriteAllText($resource, $text, [Text.UTF8Encoding]::new($false))
    Invoke-WindowsNativeCommand $Tools.rc @('/nologo',('/fo' + $compiled),$resource) 'Bridge version resource compilation'
    $machine = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'X64' }
    Invoke-WindowsNativeCommand $Tools.cvtres @('/NOLOGO',('/MACHINE:' + $machine),('/OUT:' + $object),$compiled) 'Bridge version resource object conversion'
    ConvertTo-GoCompatibleResourceObject $object
    if (@(Get-ChildItem -LiteralPath $BridgeDirectory -File -Filter '*.syso').Count -ne 0) { throw 'Preserve the existing Bridge resources; do not add a second version resource.' }
    if (@(Get-ChildItem -LiteralPath $BridgeDirectory -Directory | Where-Object Name -ne 'vendor').Count -ne 0) { throw 'Bridge now has first-party subpackages; update the minimal resource staging contract before building.' }
    foreach ($file in Get-ChildItem -LiteralPath $BridgeDirectory -File) {
        if (($file.Extension -eq '.go' -and -not $file.Name.EndsWith('_test.go')) -or $file.Name -in @('go.mod','go.sum')) {
            [IO.File]::Copy($file.FullName, (Join-Path $buildDirectory $file.Name), $false)
        }
    }
    $vendorTarget = [IO.Path]::GetFullPath((Join-Path $BridgeDirectory 'vendor'))
    $vendorLink = Join-Path $buildDirectory 'vendor'
    New-Item -ItemType Junction -Path $vendorLink -Target $vendorTarget -ErrorAction Stop | Out-Null
    return [pscustomobject]@{ buildDirectory = [IO.Path]::GetFullPath($buildDirectory); temporaryRoot = [IO.Path]::GetFullPath($OutputDirectory); vendorTarget = $vendorTarget }
}

function Remove-WindowsBridgeBuildDirectory {
    param([object]$Build)
    if (-not (Test-Path -LiteralPath $Build.buildDirectory)) { return }
    $expectedDirectory = [IO.Path]::GetFullPath((Join-Path $Build.temporaryRoot 'bridge-build'))
    if (-not [string]::Equals([IO.Path]::GetFullPath($Build.buildDirectory), $expectedDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Preserve a temporary Bridge directory whose resolved path changed.'
    }
    $vendorLink = Join-Path $Build.buildDirectory 'vendor'
    if (Test-Path -LiteralPath $vendorLink) {
        $link = Get-Item -LiteralPath $vendorLink -Force
        if (-not ($link.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $link.LinkType -ne 'Junction' -or
            -not [string]::Equals([IO.Path]::GetFullPath([string]$link.Target), $Build.vendorTarget, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Preserve an unexpected temporary vendor link; its ownership or target changed.'
        }
        # Remove just the verified junction, without recursive traversal of the source vendor.
        Remove-Item -LiteralPath $vendorLink -Force
    }
    $directory = Get-Item -LiteralPath $Build.buildDirectory -Force
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        @(Get-ChildItem -LiteralPath $directory.FullName -Recurse -Force -Attributes ReparsePoint).Count -ne 0) { throw 'Preserve linked temporary Bridge files.' }
    Remove-Item -LiteralPath $directory.FullName -Recurse -Force
}
