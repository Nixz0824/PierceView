param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$sourceDirectory = Join-Path $ProjectRoot 'src\WindowPortal'
$manifestPath = Join-Path $sourceDirectory 'app.manifest'
$sourceFiles = Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File
$sourceText = [string]::Join("`n", @($sourceFiles | ForEach-Object {
    Get-Content -LiteralPath $_.FullName -Raw
}))
$manifestText = Get-Content -LiteralPath $manifestPath -Raw

$forbiddenCapabilities = [ordered]@{
    'process-memory-read' = '\bReadProcessMemory\b'
    'process-memory-write' = '\bWriteProcessMemory\b'
    'remote-thread' = '\bCreateRemoteThread\b'
    'remote-allocation' = '\bVirtualAllocEx\b'
    'dll-injection' = '\bLoadLibrary(?:A|W)?\b'
    'keyboard-hook' = '\bWH_KEYBOARD(?:_LL)?\b'
    'low-level-mouse-hook' = '\bWH_MOUSE_LL\b|\bSetWindowsHookEx(?:W|A)?\b'
    'synthetic-input' = '\bSendInput\b|\bmouse_event\b|\bkeybd_event\b'
    'network-client' = '\bHttpClient\b|\bWebRequest\b|\bSocket\b|\bTcpClient\b'
    'registry-persistence' = '\bRegistryKey\b|CurrentVersion\\Run'
    'service-installation' = '\bCreateService\b|\bServiceInstaller\b'
    'scheduled-task' = '\bTaskScheduler\b|\bschtasks\b'
}

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($entry in $forbiddenCapabilities.GetEnumerator()) {
    if ($sourceText -match $entry.Value) {
        $failures.Add("Forbidden capability detected: $($entry.Key)")
    }
}

if ($manifestText -notmatch 'requestedExecutionLevel\s+level="asInvoker"') {
    $failures.Add('Manifest must request asInvoker.')
}

if ($manifestText -notmatch 'uiAccess="false"') {
    $failures.Add('Manifest must keep uiAccess disabled.')
}

$requiredSafetyMechanisms = [ordered]@{
    'normal-restoration' = 'WindowRegionController'
    'non-activating-style-restoration' = 'NonActivatingWindowGuard'
    'foreground-restoration' = 'ForegroundZOrderGuard'
}
foreach ($entry in $requiredSafetyMechanisms.GetEnumerator()) {
    if ($sourceText -notmatch [regex]::Escape($entry.Value)) {
        $failures.Add("Required safety mechanism missing: $($entry.Key)")
    }
}

Write-Output 'STATIC_SECURITY_AUDIT=PierceView'
Write-Output 'ELEVATION=asInvoker'
Write-Output 'UI_ACCESS=false'
Write-Output 'PROCESS_INJECTION=false'
Write-Output 'PROCESS_MEMORY_ACCESS=false'
Write-Output 'SYNTHETIC_INPUT=false'
Write-Output 'NETWORK_ACCESS=false'
Write-Output 'AUTOSTART_PERSISTENCE=false'
Write-Output 'LOCAL_SETTINGS=%LOCALAPPDATA%\PierceView\settings.json'
Write-Output 'GLOBAL_HOOK=false'
Write-Output "FAILURE_COUNT=$($failures.Count)"

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output 'RESULT=PASS'
