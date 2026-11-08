$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    & dotnet build "DateERP.sln" -c Release --no-restore --nologo -m:1
    $buildExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($buildExitCode -ne 0) { exit $buildExitCode }

$testExe = Join-Path $repoRoot 'src\DatesErp.Desktop\bin\Release\net8.0-windows\MfgSystem.exe'
if (-not (Test-Path -LiteralPath $testExe -PathType Leaf)) {
    Write-Error "نجح الأمر دون إنتاج ملف التشغيل المتوقع: $testExe"
    exit 2
}

Write-Host 'تشغيل نسخة R2 المبنية من هذا المجلد فقط، بوضع SQL Server الاختباري المعزول.'
Write-Host 'الوجهة الوحيدة: .\SQLEXPRESS2022 / DateERP_OFFTT_TEST_20261017'
Write-Host 'لن يُقرأ config.json العام، ولن يُستخدم AutoDbResolver أو SQLite.'

$exeDirectory = Split-Path -Parent $testExe
Push-Location $exeDirectory
try {
    & $testExe '--offtt-test-mode'
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
exit $exitCode
