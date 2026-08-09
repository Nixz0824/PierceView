$ErrorActionPreference = 'Stop'

$workspace = Split-Path -Parent $PSScriptRoot
$project = Join-Path $workspace 'src\WindowPortal\WindowPortal.csproj'
$executable = Join-Path $workspace 'src\WindowPortal\bin\Release\net8.0-windows\WindowPortal.exe'

dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) {
    throw 'Release build failed.'
}

& $executable --self-test
if ($LASTEXITCODE -ne 0) {
    throw 'Self-tests failed.'
}

& (Join-Path $PSScriptRoot 'security-static-audit.ps1') -ProjectRoot $workspace
if ($LASTEXITCODE -ne 0) {
    throw 'Static security audit failed.'
}

$version = [string](& $executable --version)
$expectedVersion = "WindowPortal $([string](Get-Content -LiteralPath (Join-Path $workspace 'VERSION') -Raw).Trim())"
if ($version.Trim() -ne $expectedVersion) {
    throw "Version mismatch: expected '$expectedVersion', got '$version'."
}

Write-Output 'NON_GUI_TEST_SUITE=PASS'
