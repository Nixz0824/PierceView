param(
    [string]$Executable
)

$ErrorActionPreference = 'Stop'

$workspace = Split-Path -Parent $PSScriptRoot
$project = Join-Path $workspace 'src\WindowPortal\WindowPortal.csproj'
$targetFramework = [string](dotnet msbuild $project -nologo -getProperty:TargetFramework)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($targetFramework)) {
    throw 'Could not resolve the PierceView target framework.'
}

$executable = if ([string]::IsNullOrWhiteSpace($Executable)) {
    Join-Path $workspace "src\WindowPortal\bin\Release\$($targetFramework.Trim())\PierceView.exe"
}
else {
    (Resolve-Path -LiteralPath $Executable).Path
}
$testStateDirectory = Join-Path $workspace 'artifacts\test-state'
$testSettingsPath = Join-Path $testStateDirectory 'tray-smoke-settings.json'

if ([string]::IsNullOrWhiteSpace($Executable)) {
    dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) {
        throw 'Release build failed.'
    }
}

$existing = @(Get-Process -Name 'PierceView' -ErrorAction SilentlyContinue)
if ($existing.Count -ne 0) {
    throw 'Close the existing PierceView instance before running the tray smoke test.'
}

New-Item -ItemType Directory -Force -Path $testStateDirectory | Out-Null
$previousSettingsPath = $env:PIERCEVIEW_SETTINGS_PATH
$process = $null
try {
    $env:PIERCEVIEW_SETTINGS_PATH = $testSettingsPath
    $process = Start-Process `
        -FilePath $executable `
        -ArgumentList @('--tray-smoke-test-ms', '2000') `
        -WindowStyle Hidden `
        -PassThru

    Start-Sleep -Milliseconds 700
    $process.Refresh()
    $runningAfterStartup = -not $process.HasExited
    $mainWindowHidden = $process.MainWindowHandle -eq 0
    $exitedCleanly = $process.WaitForExit(8000)
    $exitCode = if ($exitedCleanly) { $process.ExitCode } else { -1 }
    $remainingProcesses = @(Get-Process -Name 'PierceView' -ErrorAction SilentlyContinue).Count
}
finally {
    $env:PIERCEVIEW_SETTINGS_PATH = $previousSettingsPath
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id
        $process.WaitForExit(2000) | Out-Null
    }

    foreach ($path in @($testSettingsPath, "$testSettingsPath.tmp")) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }

    if ((Test-Path -LiteralPath $testStateDirectory) -and
        @(Get-ChildItem -LiteralPath $testStateDirectory -Force).Count -eq 0) {
        Remove-Item -LiteralPath $testStateDirectory
    }
}

Write-Output "RUNNING_AFTER_STARTUP=$runningAfterStartup"
Write-Output "MAIN_WINDOW_HIDDEN=$mainWindowHidden"
Write-Output "EXITED_CLEANLY=$exitedCleanly"
Write-Output "EXIT_CODE=$exitCode"
Write-Output "REMAINING_PROCESS_COUNT=$remainingProcesses"

if (-not $runningAfterStartup -or
    -not $mainWindowHidden -or
    -not $exitedCleanly -or
    $exitCode -ne 0 -or
    $remainingProcesses -ne 0) {
    throw 'Tray smoke test failed.'
}

Write-Output 'TRAY_SMOKE_TEST=PASS'
