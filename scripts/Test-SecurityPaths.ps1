#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $project 'src\MathGPT.Core\bin\Release\net8.0-windows\MathGPT.Core.dll')
$scenario = Join-Path $project 'work\tests\path-safety'
$target = Join-Path $scenario 'target'
$link = Join-Path $scenario 'linked'
[IO.Directory]::CreateDirectory($target) | Out-Null
$marker = Join-Path $target 'preserve.txt'
[IO.File]::WriteAllText($marker,'fixture-preserve',[Text.UTF8Encoding]::new($false))
if(Test-Path -LiteralPath $link) { throw 'Existing link scenario requires inspection.' }
$item = New-Item -ItemType Junction -Path $link -Target $target
$rejected = $false
try {
    try { [MathGPT.Core.SafeFiles]::Child($scenario,'linked/out.txt') | Out-Null }
    catch {
        if($_.Exception.GetBaseException() -is [MathGPT.Core.UserException]) { $rejected = $true }
        else { throw }
    }
    $preserved = [IO.File]::ReadAllText($marker) -eq 'fixture-preserve'
    $result = @{ result=$(if($rejected -and $preserved){'passed'}else{'failed'}); reparseRejected=$rejected; targetPreserved=$preserved }
    [IO.File]::WriteAllText((Join-Path $scenario 'path-result.json'),($result|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $result | ConvertTo-Json
    if(-not $rejected -or -not $preserved) { throw 'Path boundary check failed.' }
} finally {
    $actual = Get-Item -LiteralPath $link -Force
    if($actual.FullName -eq [IO.Path]::GetFullPath($link) -and ($actual.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $link -Force
    }
}
