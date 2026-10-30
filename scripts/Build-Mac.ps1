#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('osx-arm64','osx-x64')][string]$Runtime='osx-arm64',
    [ValidateSet('Preview','Release')][string]$Mode='Preview',
    [string]$SigningConfig,
    [string]$DotNetPath,
    [string]$GoPath,
    [string]$PythonPath,
    [string]$OutputDirectory,
    [string]$EvidenceDirectory,
    [switch]$PreflightOnly
)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent

# Fail before publishing, touching old bundles, or attempting any Apple upload.
if($Mode -eq 'Release' -and -not $IsMacOS){
    throw 'Developer ID release requires a real macOS host, an existing Developer ID Application certificate and a notarytool Keychain profile. Use -Mode Preview for Windows cross-compilation.'
}
if($PreflightOnly -and $Mode -ne 'Release'){throw '-PreflightOnly requires -Mode Release'}

function Resolve-BuildTool([string]$Explicit,[string[]]$Names,[string]$WindowsFallback){
    if($Explicit){
        if(-not(Test-Path -LiteralPath $Explicit -PathType Leaf)){throw "Tool does not exist: $Explicit"}
        return (Get-Item -LiteralPath $Explicit).FullName
    }
    # Windows App Execution Aliases can exist without an installed interpreter.
    # Prefer the already shared project toolchain when it is present.
    if($IsWindows -and $WindowsFallback -and (Test-Path -LiteralPath $WindowsFallback -PathType Leaf)){return $WindowsFallback}
    foreach($name in $Names){
        $command=Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if($command){return $command.Source}
    }
    throw "Missing build tool: $($Names -join ', ')"
}
$python=Resolve-BuildTool $PythonPath @('python3','python') 'C:/Users/feng/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
$releaseScript=Join-Path $PSScriptRoot 'signing/MacRelease.py'
$signingArguments=@()
if($SigningConfig){$signingArguments+=@('--config',$SigningConfig)}
if($Mode -eq 'Release'){
    & $python $releaseScript preflight @signingArguments
    if($LASTEXITCODE -ne 0){throw 'macOS release preflight failed; no build or upload was performed'}
    if($PreflightOnly){return}
}
$dotnet=Resolve-BuildTool $DotNetPath @('dotnet') 'D:/Codex/Tools/DotNet9/dotnet.exe'
$go=Resolve-BuildTool $GoPath @('go') 'D:/Codex/Tools/Go/1.27.3/go/bin/go.exe'
if(-not $OutputDirectory){$OutputDirectory=Join-Path $project 'build/packages'}
if(-not $EvidenceDirectory){$EvidenceDirectory=Join-Path $project "build/verification/mac-signing/$Runtime"}

# Every build gets a clean, owned staging directory. Old packages stay untouched.
$buildRoot=Join-Path $project 'build/mac'
function Assert-NoBuildPathLinks([string]$Path){
    $check=[IO.DirectoryInfo][IO.Path]::GetFullPath($Path)
    $projectFull=[IO.Path]::GetFullPath($project)
    while($null -ne $check){
        if(Test-Path -LiteralPath $check.FullName){
            $item=Get-Item -LiteralPath $check.FullName -Force
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Build path contains a link: $($check.FullName)"}
        }
        if($check.FullName.Equals($projectFull,[StringComparison]::OrdinalIgnoreCase)){return}
        $check=$check.Parent
    }
    throw 'Build path escaped the project'
}
function Remove-OwnedBuildTree([string]$Path){
    $targetFull=[IO.Path]::GetFullPath($Path)
    $buildFull=[IO.Path]::GetFullPath($buildRoot)+[IO.Path]::DirectorySeparatorChar
    if(-not $targetFull.StartsWith($buildFull,[StringComparison]::OrdinalIgnoreCase)){throw 'Build cleanup escaped the build root'}
    Assert-NoBuildPathLinks $targetFull
    if(Test-Path -LiteralPath $targetFull){
        $item=Get-Item -LiteralPath $targetFull -Force
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Build path is a link; retained for inspection: $targetFull"}
        $links=@(Get-ChildItem -LiteralPath $targetFull -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
        if($links.Count){throw "Build path contains links; retained for inspection: $targetFull"}
        Remove-Item -LiteralPath $targetFull -Recurse -Force
    }
}
$stage=Join-Path $buildRoot "$Runtime/staging-$([Guid]::NewGuid().ToString('N'))"
$bundle=Join-Path $stage 'MathGPT.app'
$binary=Join-Path $bundle 'Contents/MacOS'
$resources=Join-Path $bundle 'Contents/Resources'
Assert-NoBuildPathLinks $stage
$null=New-Item -ItemType Directory -Force -Path $binary,$resources
try {
    & $dotnet publish (Join-Path $project 'src/MathGPT.Mac/MathGPT.Mac.csproj') -c Release -r $Runtime --self-contained true -p:RuntimeFrameworkVersion=8.0.30 -p:DebugType=None -p:DebugSymbols=false -o $binary --nologo -v quiet
    if($LASTEXITCODE -ne 0){throw 'macOS app publish failed'}
    $oldOs=$env:GOOS
    $oldArch=$env:GOARCH
    Push-Location (Join-Path $project 'bridge')
    try {
        $env:GOOS='darwin'
        $env:GOARCH=if($Runtime -eq 'osx-arm64'){'arm64'}else{'amd64'}
        & $go build -mod=vendor -trimpath '-ldflags=-s -w' -o (Join-Path $binary 'MathGPT.Bridge') .
        if($LASTEXITCODE -ne 0){throw 'macOS built-in adapter build failed'}
    } finally {
        $env:GOOS=$oldOs
        $env:GOARCH=$oldArch
        Pop-Location
    }
    foreach($name in @('LICENSE','THIRD-PARTY-NOTICES.md')){
        Copy-Item -LiteralPath (Join-Path $project $name) -Destination $resources -Force
    }
    Compress-Archive -Path (Join-Path $project 'bridge/*') -DestinationPath (Join-Path $resources 'MathGPT.Bridge-source.zip')
    $packMode=if($Mode -eq 'Release'){'prepare'}else{'preview'}
    & $python (Join-Path $PSScriptRoot 'pack-mac.py') $Runtime --mode $packMode --bundle $bundle --output-dir $OutputDirectory
    if($LASTEXITCODE -ne 0){throw 'macOS bundle preparation or preview packaging failed'}
    if($Mode -eq 'Release'){
        & $python $releaseScript release --runtime $Runtime --bundle $bundle --output-dir $OutputDirectory --evidence-dir $EvidenceDirectory @signingArguments
        if($LASTEXITCODE -ne 0){throw 'macOS signing or notarization failed; no release is ready'}
    }
    $bundleTarget=Join-Path $buildRoot "$Runtime/MathGPT.app"
    Remove-OwnedBuildTree $bundleTarget
    Move-Item -LiteralPath $bundle -Destination $bundleTarget
} finally {
    # Check the exact generated path and reject reparse points before recursion.
    Remove-OwnedBuildTree $stage
}
