#requires -Version 7.0
[CmdletBinding()]
param([switch]$NoShortcutChanges, [string]$InstallerPath = '', [string]$ExpectedStage = '')
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content -LiteralPath (Join-Path $project 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$scenario = Join-Path $project "work\tests\installer-$version"
$install = Join-Path $scenario '程序 中文 空格'
$data = Join-Path $scenario '用户 数据'
$setup = if ($InstallerPath) { [IO.Path]::GetFullPath($InstallerPath) } else { Join-Path $project "build\packages\MathGPT-$version-Setup-win-x64.exe" }
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8FDC56F2-AF1D-4F1A-B05F-8A2D2FD25BB5}_is1'
if (Test-Path -LiteralPath $registration) {
    $registered = Get-ItemProperty -LiteralPath $registration
    if ($registered.InstallLocation.TrimEnd('\') -ne $install.TrimEnd('\')) { throw 'Existing MathGPT installation is outside this isolated scenario.' }
}
[IO.Directory]::CreateDirectory($scenario) | Out-Null
$testTarget = Join-Path $install 'MathGPT.exe'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'MathGPT.lnk'
$shortcutPaths = @($desktopShortcut, (Join-Path ([Environment]::GetFolderPath('Programs')) 'MathGPT.lnk'))
if ($NoShortcutChanges) { $shortcutPaths = @() }
$shortcutOriginals = @{}
foreach ($shortcutPath in $shortcutPaths) {
    if (Test-Path -LiteralPath $shortcutPath) {
        if ((Get-Item -LiteralPath $shortcutPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Preserve the linked shortcut; do not run this scenario.' }
        $shortcutOriginals[$shortcutPath] = [IO.File]::ReadAllBytes($shortcutPath)
    }
}
function Shortcut-Target([string]$PathValue) {
    $shortcutShell = New-Object -ComObject WScript.Shell
    try {
        $shortcutItem = $shortcutShell.CreateShortcut($PathValue)
        try { return [IO.Path]::GetFullPath($shortcutItem.TargetPath) }
        finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcutItem) | Out-Null }
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcutShell) | Out-Null }
}
function Run-Process([string]$File,[string[]]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { throw "Process did not finish: $File" }
    if ($process.ExitCode -ne 0) { throw "Process failed with $($process.ExitCode): $File" }
    return $process.ExitCode
}
function Data-Fingerprint {
    $values = [ordered]@{}
    foreach($file in Get-ChildItem -LiteralPath $data -File -Recurse | Sort-Object FullName) {
        if($file.Name.EndsWith('.tmp')) { continue }
        $values[[IO.Path]::GetRelativePath($data,$file.FullName)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return $values | ConvertTo-Json -Compress
}
Push-Location $project
try {
    & dotnet run --project 'tests/MathGPT.Tests/MathGPT.Tests.csproj' -- $data --seed-upgrade
    if($LASTEXITCODE -ne 0) { throw 'Seed failed.' }
    $before = Data-Fingerprint
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="' + $install + '"'))
    if ($NoShortcutChanges) { $arguments += '/MATHGPT_NO_SHORTCUTS=1' }
    $installExit = Run-Process $setup $arguments
    if (-not (Test-Path -LiteralPath (Join-Path $install 'MathGPT.exe'))) { throw 'Missing installed executable.' }
    if ($ExpectedStage) {
        foreach ($payload in @('MathGPT.exe','MathGPT.Bridge.exe','MathGPT.Bridge-source.zip','README.md','LICENSE','THIRD-PARTY-NOTICES.md')) {
            if ((Get-FileHash -LiteralPath (Join-Path $install $payload) -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath (Join-Path $ExpectedStage $payload) -Algorithm SHA256).Hash) {
                throw 'Installer payload differs from the frozen verified stage.'
            }
        }
    }
    $desktopShortcutCreated = $false
    if (-not $NoShortcutChanges) {
        $desktopShortcutCreated = (Test-Path -LiteralPath $desktopShortcut) -and (Shortcut-Target $desktopShortcut) -eq $testTarget
        if (-not $desktopShortcutCreated) { throw 'Default installation did not create the desktop shortcut.' }
    }
    $ui = Join-Path $scenario 'installed-ui'
    $uiArgs = @('--data-dir',('"' + $data + '"'),'--ui-smoke',('"' + $ui + '"'))
    $launchExit = Run-Process (Join-Path $install 'MathGPT.exe') $uiArgs
    $uiChecks = Get-Content -LiteralPath (Join-Path $ui 'ui-result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if(@($uiChecks | Where-Object result -ne 'passed').Count -gt 0) { throw 'Installed UI check failed.' }
    $afterLaunch = Data-Fingerprint
    $upgradeExit = Run-Process $setup $arguments
    $afterUpgrade = Data-Fingerprint
    $uninstallExit = Run-Process (Join-Path $install 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
    $afterUninstall = Data-Fingerprint
    $programRemoved = -not (Test-Path -LiteralPath (Join-Path $install 'MathGPT.exe'))
    $dataRetained = $before -eq $afterLaunch -and $before -eq $afterUpgrade -and $before -eq $afterUninstall
    $result = [ordered]@{
        result = $(if($dataRetained -and $programRemoved){'passed'}else{'failed'})
        installExit = $installExit
        launchExit = $launchExit
        sameVersionReinstallExit = $upgradeExit
        uninstallExit = $uninstallExit
        programRemoved = $programRemoved
        settingsCredentialsAndArtifactsRetained = $dataRetained
        isAdministrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        installedPath = $install
        isolatedDataPath = $data
        sameSchemaReinstallOnly = $true
        uiChecks = $uiChecks.Count
        desktopShortcutCreated = $desktopShortcutCreated
        realDesktopPreserved = [bool]$NoShortcutChanges
        exactVerifiedPayloadCompared = [bool]$ExpectedStage
    }
    [IO.File]::WriteAllText((Join-Path $scenario 'installer-result.json'),($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    $result | ConvertTo-Json -Depth 8
    if($result.result -ne 'passed') { throw 'Installer preservation failed.' }
} finally {
    foreach ($shortcutPath in $shortcutPaths) {
        if ((Test-Path -LiteralPath $shortcutPath) -and (Shortcut-Target $shortcutPath) -ne $testTarget) {
            Write-Warning 'A shortcut changed outside this scenario; preserve it.'
            continue
        }
        if ($shortcutOriginals.ContainsKey($shortcutPath)) {
            [IO.File]::WriteAllBytes($shortcutPath, $shortcutOriginals[$shortcutPath])
        } elseif (Test-Path -LiteralPath $shortcutPath) {
            Remove-Item -LiteralPath $shortcutPath
        }
    }
    Pop-Location
}
