#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConfigurationPath,
    [Parameter(Mandatory)][string]$RecordDirectory,
    [Parameter(Mandatory)][string]$FilePath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WindowsSigning.ps1')
try {
    $configuration = Get-Content -LiteralPath $ConfigurationPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($configuration.schemaVersion -ne 1) { throw 'Unsupported Windows signing configuration.' }
    $context = New-WindowsSigningContext -CertificateThumbprint $configuration.certificateThumbprint `
        -ExpectedPublisher $configuration.expectedPublisher -CertificateStoreLocation $configuration.certificateStoreLocation `
        -TimestampUrl $configuration.timestampUrl
    if ($context.signToolPath -ne $configuration.signToolPath) { throw 'The installed Windows SDK SignTool changed during the build; repeat preflight.' }
    if (-not (Test-Path -LiteralPath $RecordDirectory -PathType Container)) { throw 'The build signing record directory is missing.' }
    $record = Invoke-WindowsFileSigning -FilePath $FilePath -Context $context
    Write-WindowsJsonFile -FilePath (Join-Path $RecordDirectory ($record.sha256 + '.json')) -Value $record
} catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
    exit 1
}
