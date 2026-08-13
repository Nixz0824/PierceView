param(
    [Parameter(Mandatory = $true)]
    [Int64]$ChatGptWindow,

    [string]$PortalExecutable,

    [ValidateRange(3000, 120000)]
    [int]$StabilityDurationMilliseconds = 30000
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing.Common
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class DynamicSourceProbeNative
{
    public delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(
        uint sourceThread,
        uint targetThread,
        [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr parameter);

    public static bool ForceForeground(IntPtr window)
    {
        IntPtr foreground = GetForegroundWindow();
        uint ignoredProcess;
        uint foregroundThread = foreground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foreground, out ignoredProcess);
        uint currentThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != currentThread &&
            AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
            return GetForegroundWindow() == window;
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    public static int CountVisibleWindows(uint processId, int width, int height)
    {
        int count = 0;
        EnumWindows(delegate(IntPtr window, IntPtr parameter)
        {
            uint candidateProcessId;
            GetWindowThreadProcessId(window, out candidateProcessId);
            Rect rect;
            if (candidateProcessId == processId &&
                IsWindowVisible(window) &&
                GetWindowRect(window, out rect) &&
                rect.Width >= width && rect.Height >= height)
            {
                count++;
            }

            return true;
        }, IntPtr.Zero);
        return count;
    }
}
'@

[DynamicSourceProbeNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

$workspace = Split-Path -Parent $PSScriptRoot
$chatGpt = [IntPtr]$ChatGptWindow
if (-not [DynamicSourceProbeNative]::IsWindow($chatGpt)) {
    throw "ChatGPT HWND 0x$($ChatGptWindow.ToString('X')) is not valid."
}

$targetPath = Join-Path $workspace 'tests\WindowPortal.TestTarget\bin\Release\net8.0-windows\WindowPortal.TestTarget.exe'
$portalPath = if ([string]::IsNullOrWhiteSpace($PortalExecutable)) {
    $project = Join-Path $workspace 'src\WindowPortal\WindowPortal.csproj'
    $targetFramework = [string](dotnet msbuild $project -nologo -getProperty:TargetFramework)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($targetFramework)) {
        throw 'Could not resolve the PierceView target framework.'
    }
    Join-Path $workspace "src\WindowPortal\bin\Release\$($targetFramework.Trim())\PierceView.exe"
}
else {
    $PortalExecutable
}
$diagnosticDirectory = Join-Path $workspace 'artifacts\diagnostics'
New-Item -ItemType Directory -Force -Path $diagnosticDirectory | Out-Null
$portalOutput = Join-Path $diagnosticDirectory 'dynamic-source-reconciliation.log'
$portalError = Join-Path $diagnosticDirectory 'dynamic-source-reconciliation.error.log'
$beforeScreenshot = Join-Path $diagnosticDirectory 'dynamic-source-before.png'
$afterScreenshot = Join-Path $diagnosticDirectory 'dynamic-source-after.png'
$processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$portalProcess = $null

function Wait-MainWindow([System.Diagnostics.Process]$Process) {
    for ($attempt = 0; $attempt -lt 100 -and $Process.MainWindowHandle -eq 0; $attempt++) {
        Start-Sleep -Milliseconds 50
        $Process.Refresh()
    }

    if ($Process.MainWindowHandle -eq 0) {
        throw "Test target process $($Process.Id) did not create a main window."
    }
}

function Test-ColorNear(
    [System.Drawing.Color]$Actual,
    [System.Drawing.Color]$Expected,
    [int]$Tolerance = 55) {
    return [Math]::Abs([int]$Actual.R - [int]$Expected.R) -le $Tolerance -and
        [Math]::Abs([int]$Actual.G - [int]$Expected.G) -le $Tolerance -and
        [Math]::Abs([int]$Actual.B - [int]$Expected.B) -le $Tolerance
}

function Get-ScreenPixel([int]$X, [int]$Y) {
    $bitmap = [System.Drawing.Bitmap]::new(1, 1)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($X, $Y, 0, 0, [System.Drawing.Size]::new(1, 1))
        return $bitmap.GetPixel(0, 0)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Save-PortalScreenshot(
    [string]$Path,
    [int]$X,
    [int]$Y,
    [int]$Width,
    [int]$Height) {
    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(
            $X,
            $Y,
            0,
            0,
            [System.Drawing.Size]::new($Width, $Height))
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Test-TransitionRegionValid(
    [int]$X,
    [int]$Y,
    [int]$Width,
    [int]$Height) {
    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(
            $X,
            $Y,
            0,
            0,
            [System.Drawing.Size]::new($Width, $Height))
        $known = 0
        $total = 0
        for ($y = 4; $y -lt $Height; $y += 8) {
            for ($x = 4; $x -lt $Width; $x += 8) {
                $total++
                $pixel = $bitmap.GetPixel($x, $y)
                if ((Test-ColorNear $pixel ([System.Drawing.Color]::FromArgb(217, 74, 74)) 70) -or
                    (Test-ColorNear $pixel ([System.Drawing.Color]::FromArgb(53, 167, 101)) 70) -or
                    (Test-ColorNear $pixel ([System.Drawing.Color]::FromArgb(53, 107, 214)) 70) -or
                    (Test-ColorNear $pixel ([System.Drawing.Color]::FromArgb(218, 166, 45)) 70) -or
                    (Test-ColorNear $pixel ([System.Drawing.Color]::FromArgb(138, 79, 208)) 70)) {
                    $known++
                }
            }
        }

        return $total -gt 0 -and ($known / [double]$total) -ge 0.2
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

try {
    $purpleProcess = Start-Process -FilePath $targetPath -ArgumentList @(
        '--passive', '--label', 'LAYER-5-PURPLE', '--color', '#8A4FD0') -PassThru
    $yellowProcess = Start-Process -FilePath $targetPath -ArgumentList @(
        '--passive', '--label', 'LAYER-4-YELLOW', '--color', '#DAA62D') -PassThru
    $blueProcess = Start-Process -FilePath $targetPath -ArgumentList @(
        '--passive', '--label', 'LAYER-3-BLUE', '--color', '#356BD6') -PassThru
    $greenProcess = Start-Process -FilePath $targetPath -ArgumentList @(
        '--passive', '--label', 'LAYER-2-GREEN', '--color', '#35A765') -PassThru
    $redProcess = Start-Process -FilePath $targetPath -ArgumentList @(
        '--passive', '--label', 'LAYER-1-RED', '--color', '#D94A4A') -PassThru
    foreach ($process in @(
        $purpleProcess,
        $yellowProcess,
        $blueProcess,
        $greenProcess,
        $redProcess)) {
        $processes.Add($process)
        Wait-MainWindow $process
    }

    $purpleWindow = [IntPtr]$purpleProcess.MainWindowHandle
    $yellowWindow = [IntPtr]$yellowProcess.MainWindowHandle
    $blueWindow = [IntPtr]$blueProcess.MainWindowHandle
    $greenWindow = [IntPtr]$greenProcess.MainWindowHandle
    $redWindow = [IntPtr]$redProcess.MainWindowHandle
    $chatRect = [DynamicSourceProbeNative+Rect]::new()
    if (-not [DynamicSourceProbeNative]::GetWindowRect($chatGpt, [ref]$chatRect)) {
        throw 'Could not read the ChatGPT window rectangle.'
    }

    $portalWidth = 420
    $portalHeight = 280
    $centerX = $chatRect.Left + [int]($chatRect.Width / 2)
    $centerY = $chatRect.Top + [int]($chatRect.Height / 2)
    $sampleX = $centerX + 100
    $sampleY = $centerY + 90
    $positionFlags = 0x0010

    function Set-TestOrder {
        # Repeated insertion directly behind the host makes the last inserted
        # test window the shallowest source. Purple therefore begins as -5.
        [DynamicSourceProbeNative]::SetWindowPos(
            $purpleWindow, $chatGpt,
            $chatRect.Left, $centerY - 210, $chatRect.Width, 420,
            $positionFlags) | Out-Null
        [DynamicSourceProbeNative]::SetWindowPos(
            $yellowWindow, $chatGpt,
            $chatRect.Left, $centerY - 210, $chatRect.Width, 420,
            $positionFlags) | Out-Null
        [DynamicSourceProbeNative]::SetWindowPos(
            $blueWindow, $chatGpt,
            $centerX - 180, $centerY + 20, 160, 100,
            $positionFlags) | Out-Null
        [DynamicSourceProbeNative]::SetWindowPos(
            $greenWindow, $chatGpt,
            $centerX + 20, $centerY - 120, 160, 100,
            $positionFlags) | Out-Null
        [DynamicSourceProbeNative]::SetWindowPos(
            $redWindow, $chatGpt,
            $centerX - 180, $centerY - 120, 160, 100,
            $positionFlags) | Out-Null
    }

    Set-TestOrder
    [DynamicSourceProbeNative]::ForceForeground($chatGpt) | Out-Null
    Set-TestOrder
    Start-Sleep -Milliseconds 150
    if ([DynamicSourceProbeNative]::GetForegroundWindow() -ne $chatGpt) {
        throw 'ChatGPT could not be made foreground.'
    }

    $portalProcess = Start-Process `
        -FilePath $portalPath `
        -ArgumentList @(
            '--multilayer-probe-hwnd',
            "0x$($ChatGptWindow.ToString('X'))",
            '--probe-duration-ms',
            ([string]($StabilityDurationMilliseconds + 2500))) `
        -WindowStyle Hidden `
        -PassThru `
        -RedirectStandardOutput $portalOutput `
        -RedirectStandardError $portalError

    $ready = $false
    for ($attempt = 0; $attempt -lt 140; $attempt++) {
        Start-Sleep -Milliseconds 25
        if (Test-Path -LiteralPath $portalOutput) {
            $logText = [string](Get-Content -LiteralPath $portalOutput -Raw)
            if ($logText -like '*中心探测：*') {
                $ready = $true
                break
            }
        }
    }
    if (-not $ready) {
        throw 'The dynamic source portal did not reach its initial frame.'
    }

    # Start-Process with redirected output can briefly hand foreground
    # ownership to a console helper. Restore the protected host after the
    # four captures are already established so the assertion below measures
    # source reconciliation rather than probe-launch behavior.
    if (-not [DynamicSourceProbeNative]::ForceForeground($chatGpt)) {
        throw 'ChatGPT could not be restored as foreground after probe startup.'
    }

    Start-Sleep -Milliseconds 100
    $beforePixel = Get-ScreenPixel $sampleX $sampleY
    Save-PortalScreenshot `
        $beforeScreenshot `
        ($centerX - [int]($portalWidth / 2)) `
        ($centerY - [int]($portalHeight / 2)) `
        $portalWidth `
        $portalHeight
    $yellowInitiallyVisible = Test-ColorNear `
        $beforePixel `
        ([System.Drawing.Color]::FromArgb(218, 166, 45))
    if (-not $yellowInitiallyVisible) {
        throw "Initial -4 was not yellow: $($beforePixel.R),$($beforePixel.G),$($beforePixel.B)."
    }

    $yellowHadNoActivate =
        ([DynamicSourceProbeNative]::GetWindowLongPtr($yellowWindow, -20).ToInt64() -band 0x08000000) -ne 0
    $purpleInitiallyUnguarded =
        ([DynamicSourceProbeNative]::GetWindowLongPtr($purpleWindow, -20).ToInt64() -band 0x08000000) -eq 0
    $foregroundBeforeClose =
        [DynamicSourceProbeNative]::GetForegroundWindow()
    $yellowProcess.CloseMainWindow() | Out-Null
    if (-not $yellowProcess.WaitForExit(1500)) {
        $yellowProcess.Kill()
        $yellowProcess.WaitForExit()
    }

    $purpleVisible = $false
    $invalidTransitionFrameCount = 0
    $visiblePortalCountMaximum = 0
    $stabilitySampleCount = 0
    $afterPixel = $beforePixel
    $stabilityDeadline = [System.Diagnostics.Stopwatch]::GetTimestamp() +
        [int64](
            ($StabilityDurationMilliseconds / 1000.0) *
            [System.Diagnostics.Stopwatch]::Frequency)
    while ([System.Diagnostics.Stopwatch]::GetTimestamp() -lt $stabilityDeadline) {
        Start-Sleep -Milliseconds 10
        $stabilitySampleCount++
        $afterPixel = Get-ScreenPixel $sampleX $sampleY
        $isYellow = Test-ColorNear `
            $afterPixel `
            ([System.Drawing.Color]::FromArgb(218, 166, 45)) 65
        $isPurple = Test-ColorNear `
            $afterPixel `
            ([System.Drawing.Color]::FromArgb(138, 79, 208)) 65
        if (-not $isYellow -and -not $isPurple) {
            $regionValid = Test-TransitionRegionValid `
                ($centerX - [int]($portalWidth / 2)) `
                ($centerY - [int]($portalHeight / 2)) `
                $portalWidth `
                $portalHeight
            if (-not $regionValid) {
                $invalidTransitionFrameCount++
            }
        }
        if ($isPurple) {
            $purpleVisible = $true
        }

        $visiblePortalCountMaximum = [Math]::Max(
            $visiblePortalCountMaximum,
            [DynamicSourceProbeNative]::CountVisibleWindows(
                [uint32]$portalProcess.Id,
                2000,
                1000))
    }

    Save-PortalScreenshot `
        $afterScreenshot `
        ($centerX - [int]($portalWidth / 2)) `
        ($centerY - [int]($portalHeight / 2)) `
        $portalWidth `
        $portalHeight
    $purpleHadNoActivate =
        ([DynamicSourceProbeNative]::GetWindowLongPtr($purpleWindow, -20).ToInt64() -band 0x08000000) -ne 0
    $foregroundAfterReplacement =
        [DynamicSourceProbeNative]::GetForegroundWindow()
    $foregroundStayedUnchanged =
        $foregroundBeforeClose -eq $chatGpt -and
        $foregroundAfterReplacement -eq $chatGpt

    $portalProcess.WaitForExit()
    $portalExitCode = $portalProcess.ExitCode
    $portalLogText = [string](Get-Content -LiteralPath $portalOutput -Raw)
    $reconciliationMatch = [regex]::Match(
        $portalLogText,
        '动态来源协调：次数=(\d+)，新建捕获=(\d+)，保帧重试=(\d+)，已隔离帧异常=(\d+)，已隔离更新异常=(\d+)，显示定位=(\d+)，最终来源=([^。]+)')
    $reconciliationCount = if ($reconciliationMatch.Success) {
        [int]$reconciliationMatch.Groups[1].Value
    }
    else { 0 }
    $replacementCount = if ($reconciliationMatch.Success) {
        [int]$reconciliationMatch.Groups[2].Value
    }
    else { 0 }
    $retryCount = if ($reconciliationMatch.Success) {
        [int]$reconciliationMatch.Groups[3].Value
    }
    else { 0 }
    $isolatedFrameFailureCount = if ($reconciliationMatch.Success) {
        [int]$reconciliationMatch.Groups[4].Value
    }
    else { 0 }
    $isolatedUpdateFailureCount = if ($reconciliationMatch.Success) {
        [int]$reconciliationMatch.Groups[5].Value
    }
    else { 0 }
    $displayPlacementCount = if ($reconciliationMatch.Success) {
        [int]$reconciliationMatch.Groups[6].Value
    }
    else { 0 }
    $finalSources = if ($reconciliationMatch.Success) {
        $reconciliationMatch.Groups[7].Value
    }
    else { '' }
    $finalSourceHandles = @($finalSources -split ',' | Where-Object { $_ })
    $purpleInFinalSources =
        $finalSources -like "*0x$($purpleWindow.ToInt64().ToString('X'))*"
    $yellowNotInFinalSources =
        $finalSources -notlike "*0x$($yellowWindow.ToInt64().ToString('X'))*"
    $purpleStyleRestored =
        ([DynamicSourceProbeNative]::GetWindowLongPtr($purpleWindow, -20).ToInt64() -band 0x08000000) -eq 0

    Write-Output "PORTAL_EXIT=$portalExitCode"
    Write-Output "RED_HWND=0x$($redWindow.ToInt64().ToString('X'))"
    Write-Output "GREEN_HWND=0x$($greenWindow.ToInt64().ToString('X'))"
    Write-Output "BLUE_HWND=0x$($blueWindow.ToInt64().ToString('X'))"
    Write-Output "YELLOW_HWND=0x$($yellowWindow.ToInt64().ToString('X'))"
    Write-Output "PURPLE_HWND=0x$($purpleWindow.ToInt64().ToString('X'))"
    Write-Output "FINAL_SOURCES=$finalSources"
    Write-Output "YELLOW_INITIALLY_VISIBLE=$yellowInitiallyVisible"
    Write-Output "PURPLE_REPLACEMENT_VISIBLE=$purpleVisible"
    Write-Output "INVALID_TRANSITION_FRAMES=$invalidTransitionFrameCount"
    Write-Output "STABILITY_DURATION_MS=$StabilityDurationMilliseconds"
    Write-Output "STABILITY_SAMPLE_COUNT=$stabilitySampleCount"
    Write-Output "VISIBLE_PORTAL_COUNT_MAX=$visiblePortalCountMaximum"
    Write-Output "YELLOW_NOACTIVATE=$yellowHadNoActivate"
    Write-Output "PURPLE_INITIALLY_UNGUARDED=$purpleInitiallyUnguarded"
    Write-Output "PURPLE_NOACTIVATE_AFTER_REPLACEMENT=$purpleHadNoActivate"
    Write-Output "PURPLE_STYLE_RESTORED=$purpleStyleRestored"
    Write-Output "FOREGROUND_BEFORE_CLOSE=0x$($foregroundBeforeClose.ToInt64().ToString('X'))"
    Write-Output "FOREGROUND_AFTER_REPLACEMENT=0x$($foregroundAfterReplacement.ToInt64().ToString('X'))"
    Write-Output "FOREGROUND_STAYED_UNCHANGED=$foregroundStayedUnchanged"
    Write-Output "RECONCILIATION_COUNT=$reconciliationCount"
    Write-Output "REPLACEMENT_COUNT=$replacementCount"
    Write-Output "FAIL_SOFT_RETRY_COUNT=$retryCount"
    Write-Output "ISOLATED_FRAME_FAILURE_COUNT=$isolatedFrameFailureCount"
    Write-Output "ISOLATED_UPDATE_FAILURE_COUNT=$isolatedUpdateFailureCount"
    Write-Output "DISPLAY_PLACEMENT_COUNT=$displayPlacementCount"
    Write-Output "FINAL_SOURCE_COUNT=$($finalSourceHandles.Count)"
    Write-Output "PURPLE_IN_FINAL_SOURCES=$purpleInFinalSources"
    Write-Output "YELLOW_NOT_IN_FINAL_SOURCES=$yellowNotInFinalSources"
    Write-Output "BEFORE_SCREENSHOT=$beforeScreenshot"
    Write-Output "AFTER_SCREENSHOT=$afterScreenshot"

    if ($portalExitCode -ne 0 -or
        -not $purpleVisible -or
        $invalidTransitionFrameCount -ne 0 -or
        $visiblePortalCountMaximum -ne 1 -or
        -not $yellowHadNoActivate -or
        -not $purpleInitiallyUnguarded -or
        -not $purpleHadNoActivate -or
        -not $purpleStyleRestored -or
        -not $foregroundStayedUnchanged -or
        $reconciliationCount -lt 1 -or
        $replacementCount -lt 1 -or
        $displayPlacementCount -ne 1 -or
        $finalSourceHandles.Count -ne 4 -or
        -not $purpleInFinalSources -or
        -not $yellowNotInFinalSources) {
        throw 'The dynamic source reconciliation probe failed.'
    }
}
finally {
    if ($portalProcess -and -not $portalProcess.HasExited) {
        $portalProcess.CloseMainWindow() | Out-Null
        if (-not $portalProcess.WaitForExit(2000)) {
            $portalProcess.Kill()
        }
    }

    foreach ($process in $processes) {
        if ($process -and -not $process.HasExited) {
            $process.CloseMainWindow() | Out-Null
            if (-not $process.WaitForExit(2000)) {
                $process.Kill()
            }
        }
    }
}
