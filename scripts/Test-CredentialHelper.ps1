#Requires -Version 7.0
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$version=([xml](Get-Content -LiteralPath (Join-Path $project 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$candidate=Join-Path $project "build/stage/win-x64/MathGPT.exe"
$data=Join-Path $project ('work/tests/helper-'+[guid]::NewGuid().ToString('N'))
Push-Location $project
try {
 & dotnet run --project 'tests/MathGPT.Tests/MathGPT.Tests.csproj' -- $data --seed-upgrade
 if($LASTEXITCODE){throw 'Fixture setup failed'}
 $state=Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
 foreach($valid in @($true,$false)){
  $profile=if($valid){$state.Profiles[0].Id}else{'missing-profile'}
  $start=[Diagnostics.ProcessStartInfo]::new($candidate)
  $start.UseShellExecute=$false;$start.CreateNoWindow=$true
  $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
  foreach($arg in @('--credential',$profile,'--data-dir',$data)){$start.ArgumentList.Add($arg)}
  $child=[Diagnostics.Process]::Start($start)
  $output=$child.StandardOutput.ReadToEnd();$errors=$child.StandardError.ReadToEnd();$child.WaitForExit()
  if($valid-and($output.Trim()-cne'fixture-upgrade-private-key'-or$child.ExitCode-ne0-or$errors)){throw 'GUI executable credential-helper pipe failed'}
  if(-not$valid-and($output-or$errors-or$child.ExitCode-ne1)){throw 'Invalid credential lookup did not fail silently'}
  $child.Dispose();$output=$null
 }
 [ordered]@{result='passed';winexe_pipe=$true;invalid_profile_exit=1;secret_not_in_args=$true;no_account_or_model_calls=$true;executable_sha256=(Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $project "work/credential-helper-$version-result.json") -Encoding utf8NoBOM
 'Native GUI credential helper passed'
}finally{Pop-Location}
