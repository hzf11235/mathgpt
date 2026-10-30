#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseManifest,
    [Parameter(Mandatory)][string]$PublicReadbackReceipt,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$SigningKeyPath='D:/Codex/Data/mathgpt-ops/mathgpt-update.key.dpapi',
    [string]$Python='C:/Users/feng/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputPath){throw 'Existing signed feeds are immutable; use a fresh output path.'}
foreach($taskFile in @($ReleaseManifest,$PublicReadbackReceipt,$SigningKeyPath)){
    $taskItem=Get-Item -LiteralPath $taskFile
    if($taskItem.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Linked signing inputs are not allowed.'}
}
$taskSecure=ConvertTo-SecureString ([IO.File]::ReadAllText($SigningKeyPath).Trim())
$taskSeed=[pscredential]::new('update-signing',$taskSecure).GetNetworkCredential().Password
try{
    # The private seed exists only in memory/stdin. It is never an argument, environment value or public artifact.
    $taskRequest=@{private_seed=$taskSeed;manifest=[IO.Path]::GetFullPath($ReleaseManifest);receipt=[IO.Path]::GetFullPath($PublicReadbackReceipt);output=[IO.Path]::GetFullPath($OutputPath)} | ConvertTo-Json -Compress
    $taskRequest | & $Python (Join-Path $PSScriptRoot 'self-update-feed.py')
    if($LASTEXITCODE){throw 'The authenticated update feed was not produced.'}
}finally{$taskSeed=$null;$taskRequest=$null;$taskSecure=$null}
