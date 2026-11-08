#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\DatesErp.Desktop\DatesErp.Desktop.csproj'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\OFFTT_LATEST_20261022'
$dataDir = Join-Path $env:LOCALAPPDATA 'OFFTT_LATEST_20261022'

if ((Test-Path $installDir) -or (Test-Path $dataDir)) {
    throw "المجلد موجود مسبقاً ولن يُستبدل: $installDir"
}

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:OFFTTStandalone=true',
    '-o', $installDir
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "فشل نشر الحزمة المنفصلة (dotnet publish)."
}

$exePath = Join-Path $installDir 'MfgSystem.exe'
$launcherBat = Join-Path $installDir 'تشغيل_OFFTT.bat'
Set-Content -Path $launcherBat -Encoding UTF8 -Value "@echo off`r`nstart `"`" `"%~dp0MfgSystem.exe`" --offtt-standalone`r`n"

Start-Process -FilePath $exePath -ArgumentList '--offtt-standalone'
