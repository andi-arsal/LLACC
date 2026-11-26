# Membangun llacc.dll (Release) lalu menjalankan uji pada proses 64-bit dan 32-bit.
#   powershell -ExecutionPolicy Bypass -File test.ps1 [-Images folder_bmp]
param([string]$Images)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" "$here\llacc.csproj" /t:Rebuild /p:Configuration=Release /nologo /v:q
if ($LASTEXITCODE -ne 0) { throw "Build gagal." }
$failed = $false
foreach ($platform in 'x64', 'x86') {
  $out = "$here\bin\test-$platform"
  New-Item -ItemType Directory -Force $out | Out-Null
  Copy-Item "$here\lib\*.dll", "$here\bin\Release\llacc.dll" $out -Force
  Copy-Item "$here\app.config.example" "$out\LlaccTests.exe.config" -Force
  & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /platform:$platform /out:"$out\LlaccTests.exe" /r:"$out\llacc.dll" "$here\tests\LlaccTests.cs"
  if ($LASTEXITCODE -ne 0) { throw "Kompilasi uji gagal." }
  if ($Images -and $platform -eq 'x64') { & "$out\LlaccTests.exe" (Resolve-Path $Images).Path } else { & "$out\LlaccTests.exe" }
  if ($LASTEXITCODE -ne 0) { $failed = $true }
}
if ($failed) { throw "Ada uji yang gagal." }
