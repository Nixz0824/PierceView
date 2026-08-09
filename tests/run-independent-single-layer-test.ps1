param(
    [string]$AppExecutable
)

$ErrorActionPreference = 'Stop'

$workspace = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $workspace 'src\WindowPortal\WindowPortal.csproj'
$targetProject = Join-Path $workspace 'tests\WindowPortal.TestTarget\WindowPortal.TestTarget.csproj'
$appExecutable = if ([string]::IsNullOrWhiteSpace($AppExecutable)) {
    Join-Path $workspace 'src\WindowPortal\bin\Release\net8.0-windows\PierceView.exe'
}
else {
    (Resolve-Path -LiteralPath $AppExecutable).Path
}
$targetExecutable = Join-Path $workspace 'tests\WindowPortal.TestTarget\bin\Release\net8.0-windows\WindowPortal.TestTarget.exe'

if ([string]::IsNullOrWhiteSpace($AppExecutable)) {
    dotnet build $appProject -c Release
    if ($LASTEXITCODE -ne 0) {
        throw 'PierceView Release build failed.'
    }
}

dotnet build $targetProject -c Release
if ($LASTEXITCODE -ne 0) {
    throw 'Test target Release build failed.'
}

# The host must be visible so DWM can provide a real redirected surface.
$hostProcess = Start-Process -FilePath $targetExecutable -WindowStyle Normal -PassThru
try {
    for ($attempt = 0; $attempt -lt 100 -and $hostProcess.MainWindowHandle -eq 0; $attempt++) {
        Start-Sleep -Milliseconds 50
        $hostProcess.Refresh()
    }

    if ($hostProcess.MainWindowHandle -eq 0) {
        throw 'Host test window did not start.'
    }

    & (Join-Path $PSScriptRoot 'nonactivating-click-probe.ps1') `
        -ChatGptWindow $hostProcess.MainWindowHandle.ToInt64() `
        -PortalExecutable $appExecutable

    Write-Output 'INDEPENDENT_SINGLE_LAYER_TEST=PASS'
}
finally {
    if (-not $hostProcess.HasExited) {
        $hostProcess.CloseMainWindow() | Out-Null
        if (-not $hostProcess.WaitForExit(2000)) {
            Stop-Process -Id $hostProcess.Id
        }
    }
}
