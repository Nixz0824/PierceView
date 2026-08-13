param(
    [Parameter(Mandatory = $true)]
    [Int64]$ChatGptWindow,

    [string]$PortalExecutable
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WindowPortalProbeNative
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;

        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(IntPtr window, bool altTab);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint firstThread, uint secondThread, bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    public static bool ForceForeground(IntPtr window)
    {
        IntPtr foreground = GetForegroundWindow();
        uint ignoredProcess;
        uint foregroundThread = GetWindowThreadProcessId(foreground, out ignoredProcess);
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

    public static IntPtr GetVisibleWindowAbove(IntPtr window)
    {
        IntPtr candidate = GetWindow(window, 3);
        while (candidate != IntPtr.Zero && !IsWindowVisible(candidate))
        {
            candidate = GetWindow(candidate, 3);
        }

        return candidate;
    }

    public static IntPtr GetVisibleWindowBelow(IntPtr window)
    {
        IntPtr candidate = GetWindow(window, 2);
        while (candidate != IntPtr.Zero && !IsWindowVisible(candidate))
        {
            candidate = GetWindow(candidate, 2);
        }

        return candidate;
    }
}
'@

[WindowPortalProbeNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

$workspace = Split-Path -Parent $PSScriptRoot
$chatGpt = [IntPtr]$ChatGptWindow
if (-not [WindowPortalProbeNative]::IsWindow($chatGpt)) {
    throw "ChatGPT HWND 0x$($ChatGptWindow.ToString('X')) is not valid."
}

$targetPath = Join-Path $workspace 'tests\WindowPortal.TestTarget\bin\Release\net8.0-windows\WindowPortal.TestTarget.exe'
$portalPath = if ([string]::IsNullOrWhiteSpace($PortalExecutable)) {
    Join-Path $workspace 'src\WindowPortal\bin\Release\net8.0-windows\PierceView.exe'
}
else {
    $PortalExecutable
}
$diagnosticDirectory = Join-Path $workspace 'artifacts\diagnostics'
New-Item -ItemType Directory -Force -Path $diagnosticDirectory | Out-Null
$portalOutput = Join-Path $diagnosticDirectory 'nonactivating-click-probe.log'
$portalError = Join-Path $diagnosticDirectory 'nonactivating-click-probe.error.log'
$expectedProbeStart = "开始探测窗口 0x$($ChatGptWindow.ToString('X'))。"
$targetProcess = $null
$portalProcess = $null

try {
    $targetProcess = Start-Process -FilePath $targetPath -PassThru
    for ($attempt = 0; $attempt -lt 100 -and $targetProcess.MainWindowHandle -eq 0; $attempt++) {
        Start-Sleep -Milliseconds 50
        $targetProcess.Refresh()
    }

    if ($targetProcess.MainWindowHandle -eq 0) {
        throw 'Test target did not create a main window.'
    }

    $chatRect = [WindowPortalProbeNative+Rect]::new()
    if (-not [WindowPortalProbeNative]::GetWindowRect($chatGpt, [ref]$chatRect)) {
        throw 'Could not read the ChatGPT window rectangle.'
    }

    $centerX = $chatRect.Left + [int](($chatRect.Right - $chatRect.Left) / 2)
    $centerY = $chatRect.Top + [int](($chatRect.Bottom - $chatRect.Top) / 2)
    $targetWidth = $chatRect.Right - $chatRect.Left
    $targetHeight = 560
    $positionFlags = 0x0010
    if (-not [WindowPortalProbeNative]::SetWindowPos(
        $targetProcess.MainWindowHandle,
        $chatGpt,
        $chatRect.Left,
        $centerY - [int]($targetHeight / 2),
        $targetWidth,
        $targetHeight,
        $positionFlags)) {
        throw 'Could not position the test target directly behind ChatGPT.'
    }

    $foregroundReady = $false
    $foregroundBefore = [IntPtr]::Zero
    $windowBelowHost = [IntPtr]::Zero
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        [WindowPortalProbeNative]::SwitchToThisWindow($chatGpt, $true)
        [WindowPortalProbeNative]::ForceForeground($chatGpt) | Out-Null
        [WindowPortalProbeNative]::SetWindowPos(
            $targetProcess.MainWindowHandle,
            $chatGpt,
            $chatRect.Left,
            $centerY - [int]($targetHeight / 2),
            $targetWidth,
            $targetHeight,
            $positionFlags) | Out-Null
        Start-Sleep -Milliseconds 25
        $foregroundBefore = [WindowPortalProbeNative]::GetForegroundWindow()
        $windowBelowHost = [WindowPortalProbeNative]::GetVisibleWindowBelow($chatGpt)
        if ($foregroundBefore -eq $chatGpt -and
            $windowBelowHost -eq $targetProcess.MainWindowHandle) {
            $foregroundReady = $true
            break
        }
    }

    if (-not $foregroundReady) {
        throw "The test desktop did not settle (foreground=0x$($foregroundBefore.ToInt64().ToString('X')), host=0x$($chatGpt.ToInt64().ToString('X')), below=0x$($windowBelowHost.ToInt64().ToString('X')), target=0x$($targetProcess.MainWindowHandle.ToInt64().ToString('X')))."
    }

    $windowAboveBefore = [WindowPortalProbeNative]::GetVisibleWindowAbove($targetProcess.MainWindowHandle)
    $extendedStyleBefore = [WindowPortalProbeNative]::GetWindowLongPtr($targetProcess.MainWindowHandle, -20)

    # Redirected output files can retain the previous probe briefly while a new
    # single-file process is extracting and starting. Remove them first and bind
    # every readiness match to this run's host HWND.
    Remove-Item -LiteralPath $portalOutput, $portalError -Force -ErrorAction SilentlyContinue

    $portalProcess = Start-Process `
        -FilePath $portalPath `
        -ArgumentList @('--probe-hwnd', "0x$($ChatGptWindow.ToString('X'))", '--probe-duration-ms', '200') `
        -WindowStyle Hidden `
        -PassThru `
        -RedirectStandardOutput $portalOutput `
        -RedirectStandardError $portalError

    $visualReady = $false
    $readyBackend = ''
    $readyFirstFrame = $false
    $readySourceWindow = [IntPtr]::Zero
    $readyNoActivate = $false
    for ($attempt = 0; $attempt -lt 200; $attempt++) {
        Start-Sleep -Milliseconds 25
        if (Test-Path -LiteralPath $portalOutput) {
            $logText = [string](Get-Content -LiteralPath $portalOutput -Raw)
            if ($logText -notlike "*$expectedProbeStart*") {
                continue
            }

            $readyMatch = [regex]::Match(
                $logText,
                '视觉就绪：后端=([^，]+)，(?:多层合成已启用：可渲染层数=\d+，)?首帧已提交=(True|False)，来源HWND=0x([0-9A-Fa-f]+)，来源非激活=(True|False)')
            if ($readyMatch.Success) {
                $readyBackend = $readyMatch.Groups[1].Value
                $readyFirstFrame = [bool]::Parse($readyMatch.Groups[2].Value)
                $readySourceWindow = [IntPtr]::new(
                    [Convert]::ToInt64($readyMatch.Groups[3].Value, 16))
                $readyNoActivate = [bool]::Parse($readyMatch.Groups[4].Value)
                $visualReady = $readyBackend -eq 'GPU/WGC' -and
                    $readyFirstFrame -and
                    $readySourceWindow -eq $targetProcess.MainWindowHandle -and
                    $readyNoActivate
                break
            }
        }
    }

    if (-not $visualReady) {
        throw "The GPU portal did not become ready for the expected source (backend=$readyBackend, firstFrame=$readyFirstFrame, source=0x$($readySourceWindow.ToInt64().ToString('X')), target=0x$($targetProcess.MainWindowHandle.ToInt64().ToString('X')), noActivate=$readyNoActivate)."
    }

    $extendedStyleDuring = [WindowPortalProbeNative]::GetWindowLongPtr($targetProcess.MainWindowHandle, -20)

    [WindowPortalProbeNative]::SetCursorPos($centerX, $centerY) | Out-Null
    $hitAtClick = [WindowPortalProbeNative]::WindowFromPoint(
        [WindowPortalProbeNative+Point]::new($centerX, $centerY))
    $hitRootAtClick = if ($hitAtClick -eq [IntPtr]::Zero) {
        [IntPtr]::Zero
    }
    else {
        [WindowPortalProbeNative]::GetAncestor($hitAtClick, 2)
    }
    if ($hitRootAtClick -ne $targetProcess.MainWindowHandle) {
        throw "The portal center does not hit the expected target before input (hit=0x$($hitRootAtClick.ToInt64().ToString('X')), target=0x$($targetProcess.MainWindowHandle.ToInt64().ToString('X')))."
    }

    [WindowPortalProbeNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [WindowPortalProbeNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    $postClickReady = $false
    $hitRootBeforeWheel = [IntPtr]::Zero
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 10
        $foregroundAfterClick = [WindowPortalProbeNative]::GetForegroundWindow()
        $windowAboveAfterClick = [WindowPortalProbeNative]::GetVisibleWindowAbove(
            $targetProcess.MainWindowHandle)
        $hitBeforeWheel = [WindowPortalProbeNative]::WindowFromPoint(
            [WindowPortalProbeNative+Point]::new($centerX, $centerY))
        $hitRootBeforeWheel = if ($hitBeforeWheel -eq [IntPtr]::Zero) {
            [IntPtr]::Zero
        }
        else {
            [WindowPortalProbeNative]::GetAncestor($hitBeforeWheel, 2)
        }
        if ($foregroundAfterClick -eq $chatGpt -and
            $windowAboveAfterClick -eq $windowAboveBefore -and
            $hitRootBeforeWheel -eq $targetProcess.MainWindowHandle) {
            $postClickReady = $true
            break
        }
    }

    if (-not $postClickReady) {
        throw "The desktop did not settle after the background click (foreground=0x$($foregroundAfterClick.ToInt64().ToString('X')), expected=0x$($chatGpt.ToInt64().ToString('X')), above=0x$($windowAboveAfterClick.ToInt64().ToString('X')), expectedAbove=0x$($windowAboveBefore.ToInt64().ToString('X')), hit=0x$($hitRootBeforeWheel.ToInt64().ToString('X')), target=0x$($targetProcess.MainWindowHandle.ToInt64().ToString('X')))."
    }

    [WindowPortalProbeNative]::mouse_event(0x0800, 0, 0, 120, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 200

    $foregroundAfter = [WindowPortalProbeNative]::GetForegroundWindow()
    $windowAboveAfter = [WindowPortalProbeNative]::GetVisibleWindowAbove($targetProcess.MainWindowHandle)
    $targetTitleBuffer = [System.Text.StringBuilder]::new(512)
    [WindowPortalProbeNative]::GetWindowText(
        $targetProcess.MainWindowHandle,
        $targetTitleBuffer,
        $targetTitleBuffer.Capacity) | Out-Null
    $targetTitle = $targetTitleBuffer.ToString()
    $hostTitleBuffer = [System.Text.StringBuilder]::new(512)
    [WindowPortalProbeNative]::GetWindowText(
        $chatGpt,
        $hostTitleBuffer,
        $hostTitleBuffer.Capacity) | Out-Null
    $hostTitle = $hostTitleBuffer.ToString()

    $portalProcess.WaitForExit()
    $portalExitCode = $portalProcess.ExitCode
    $portalLogText = [string](Get-Content -LiteralPath $portalOutput -Raw)
    $recoveryMatch = [regex]::Match($portalLogText, '回滚次数=(\d+)')
    $foregroundRecoveryCount = if ($recoveryMatch.Success) {
        [int]$recoveryMatch.Groups[1].Value
    }
    else {
        0
    }
    $extendedStyleAfter = [WindowPortalProbeNative]::GetWindowLongPtr($targetProcess.MainWindowHandle, -20)
    $clickForwarded = $targetTitle -like '*Clicks: 1*'
    $wheelForwarded = $targetTitle -like '*Wheel: 120*'
    $hostWheelSuppressed = $hostTitle -like '*Wheel: 0*'
    $foregroundPreserved = $foregroundAfter -eq $foregroundBefore
    $zOrderPreserved = $windowAboveBefore -eq $windowAboveAfter
    $noActivateApplied = ($extendedStyleDuring.ToInt64() -band 0x08000000) -ne 0
    $styleRestored = $extendedStyleAfter -eq $extendedStyleBefore
    $foregroundGuardTriggered = $foregroundRecoveryCount -ge 1

    Write-Output "PORTAL_EXIT=$portalExitCode"
    Write-Output "CHATGPT_HWND=0x$($chatGpt.ToInt64().ToString('X'))"
    Write-Output "TARGET_HWND=0x$($targetProcess.MainWindowHandle.ToInt64().ToString('X'))"
    Write-Output "TARGET_TITLE=$targetTitle"
    Write-Output "HOST_TITLE=$hostTitle"
    Write-Output "READY_BACKEND=$readyBackend"
    Write-Output "READY_FIRST_FRAME=$readyFirstFrame"
    Write-Output "READY_SOURCE_HWND=0x$($readySourceWindow.ToInt64().ToString('X'))"
    Write-Output "READY_NO_ACTIVATE=$readyNoActivate"
    Write-Output "CLICK_HIT_HWND=0x$($hitRootAtClick.ToInt64().ToString('X'))"
    Write-Output "WHEEL_HIT_HWND=0x$($hitRootBeforeWheel.ToInt64().ToString('X'))"
    Write-Output "FOREGROUND_BEFORE=0x$($foregroundBefore.ToInt64().ToString('X'))"
    Write-Output "FOREGROUND_AFTER=0x$($foregroundAfter.ToInt64().ToString('X'))"
    Write-Output "CLICK_FORWARDED=$clickForwarded"
    Write-Output "WHEEL_FORWARDED=$wheelForwarded"
    Write-Output "HOST_WHEEL_SUPPRESSED=$hostWheelSuppressed"
    Write-Output "FOREGROUND_PRESERVED=$foregroundPreserved"
    Write-Output "WINDOW_ABOVE_BEFORE=0x$($windowAboveBefore.ToInt64().ToString('X'))"
    Write-Output "WINDOW_ABOVE_AFTER=0x$($windowAboveAfter.ToInt64().ToString('X'))"
    Write-Output "Z_ORDER_PRESERVED=$zOrderPreserved"
    Write-Output "NO_ACTIVATE_APPLIED=$noActivateApplied"
    Write-Output "STYLE_RESTORED=$styleRestored"
    Write-Output "FOREGROUND_RECOVERY_COUNT=$foregroundRecoveryCount"
    Write-Output "FOREGROUND_GUARD_TRIGGERED=$foregroundGuardTriggered"

    if ($portalExitCode -ne 0 -or
        -not $clickForwarded -or
        -not $wheelForwarded -or
        -not $hostWheelSuppressed -or
        -not $foregroundPreserved -or
        -not $zOrderPreserved -or
        -not $noActivateApplied -or
        -not $styleRestored) {
        throw 'The non-activating background click probe failed.'
    }
}
finally {
    if ($portalProcess -and -not $portalProcess.HasExited) {
        $portalProcess.CloseMainWindow() | Out-Null
        $portalProcess.WaitForExit(2000) | Out-Null
    }

    if ($targetProcess -and -not $targetProcess.HasExited) {
        $targetProcess.CloseMainWindow() | Out-Null
        $targetProcess.WaitForExit(2000) | Out-Null
    }
}
