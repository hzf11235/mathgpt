#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$FilePath,
    [Parameter(Mandatory)][string]$ExpectedPublisher,
    [string]$CertificateThumbprint = '',
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WindowsSigning.ps1')
# This read-only entry point also accepts artifacts signed by an external service.
# It requires no local publisher private key and does not declare an installer release ready.
$context = New-WindowsVerificationContext -ExpectedPublisher $ExpectedPublisher -CertificateThumbprint $CertificateThumbprint
$records = @(foreach ($path in $FilePath) { Assert-WindowsSignedFile -FilePath ([IO.Path]::GetFullPath($path)) -Context $context })
$result = [ordered]@{ schemaVersion = 1; verification = 'passed'; releaseReady = $false; scope = 'only the supplied files'; files = $records }
if ($OutputPath) { Write-WindowsJsonFile -FilePath $OutputPath -Value $result }
$result | ConvertTo-Json -Depth 12
