#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FeedPath,
    [Parameter(Mandatory)][string]$ReceiptPath,
    [string]$Python='C:/Users/feng/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $ReceiptPath){throw 'Use a fresh publication receipt path.'}
$taskFeed=Get-Item -LiteralPath $FeedPath
if($taskFeed.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Linked feed refused.'}
$taskVerification=@{verify_path=$taskFeed.FullName}|ConvertTo-Json -Compress|& $Python (Join-Path $PSScriptRoot 'self-update-feed.py')
if($LASTEXITCODE){throw 'Signed public feed verification failed.'}
$taskMetadata=$taskVerification|ConvertFrom-Json
if(-not$taskMetadata.verified-or$taskMetadata.sha256-notmatch'^[a-f0-9]{64}$'-or$taskMetadata.version-notmatch'^\d+\.\d+\.\d+$'){throw 'Invalid verified metadata.'}
$taskId=[Guid]::NewGuid().ToString('N')
$taskStage="/opt/mathgpt/.desktop-staging/feed-$taskId.json"
$taskRemote='D:/Codex/Projects/feng-relay/local/server/Invoke-MathGptRemote.ps1'
$taskOptions=@('-i','D:/Codex/Data/mathgpt-ops/server-128077.ed25519','-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o','UserKnownHostsFile=D:/Codex/Data/mathgpt-ops/known_hosts','-o','ConnectTimeout=15')
& 'C:/Windows/System32/OpenSSH/scp.exe' -O @taskOptions $taskFeed.FullName ('root@143.246.57.141:'+$taskStage)
if($LASTEXITCODE){throw 'Feed upload failed; public feed unchanged.'}
$taskScript=@"
set -eu
exec 9>/run/mathgpt-deploy.lock
flock -n 9
test ! -L /opt/mathgpt/downloads
test ! -L '$taskStage'
test ! -L /opt/mathgpt/downloads/mathgpt-updates.v1.json
printf '%s  %s\n' '$($taskMetadata.sha256)' '$taskStage' | sha256sum -c -
python3 - '$taskStage' '$($taskMetadata.version)' <<'PY'
import base64,json,pathlib,sys
current=pathlib.Path('/opt/mathgpt/downloads/mathgpt-updates.v1.json')
if current.exists():
    old=json.loads(base64.b64decode(json.loads(current.read_text())['payload']))
    old_version=max(tuple(map(int,r['version'].split('.'))) for r in old['releases'])
    if tuple(map(int,sys.argv[2].split('.'))) < old_version: raise SystemExit('Feed downgrade refused')
PY
chmod 0644 '$taskStage'
mv '$taskStage' /opt/mathgpt/downloads/mathgpt-updates.v1.json
"@
& $taskRemote -ScriptText $taskScript
if(-not$?){throw 'Atomic feed publication was not confirmed; inspect before retry.'}
$taskHandler=[Net.Http.HttpClientHandler]::new();$taskHandler.AllowAutoRedirect=$false
$taskHttp=[Net.Http.HttpClient]::new($taskHandler);$taskHttp.Timeout=[TimeSpan]::FromSeconds(60)
try{
 $taskResponse=$taskHttp.GetAsync('https://mathgpt.xyz/downloads/mathgpt-updates.v1.json').GetAwaiter().GetResult()
 if([int]$taskResponse.StatusCode-ne200){throw 'Public feed did not return 200.'}
 $taskBytes=$taskResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
 $taskHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskBytes)).ToLowerInvariant()
 if($taskHash-ne$taskMetadata.sha256){throw 'Public feed differs from signed local feed.'}
 [IO.Directory]::CreateDirectory((Split-Path -Parent ([IO.Path]::GetFullPath($ReceiptPath))))|Out-Null
 @{published=$true;version=$taskMetadata.version;url='https://mathgpt.xyz/downloads/mathgpt-updates.v1.json';sha256=$taskHash;bytes=$taskBytes.Length;signature_verified=$true}|ConvertTo-Json|Set-Content -LiteralPath $ReceiptPath -Encoding utf8NoBOM
 Write-Output ('SIGNED_UPDATE_FEED_PUBLISHED '+$taskMetadata.version)
}finally{$taskHttp.Dispose();$taskHandler.Dispose()}
