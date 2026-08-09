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
    Start-Sleep -Milliseconds 150
    $foregroundBefore = [WindowPortalProbeNative]::GetForegroundWindow()
    $windowAboveBefore = [WindowPortalProbeNative]::GetVisibleWindowAbove($targetProcess.MainWindowHandle)
    $extendedStyleBefore = [WindowPortalProbeNative]::GetWindowLongPtr($targetProcess.MainWindowHandle, -20)
    if ($foregroundBefore -ne $chatGpt) {
        throw "ChatGPT could not be made foreground before the test (foreground=0x$($foregroundBefore.ToInt64().ToString('X')))."
    }

    $portalProcess = Start-Process `
        -FilePath $portalPath `
        -ArgumentList @('--probe-hwnd', "0x$($ChatGptWindow.ToString('X'))", '--probe-duration-ms', '200') `
        -WindowStyle Hidden `
        -PassThru `
        -RedirectStandardOutput $portalOutput `
        -RedirectStandardError $portalError

    $centerFrameReady = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        Start-Sleep -Milliseconds 25
        if (Test-Path -LiteralPath $portalOutput) {
            $logText = [string](Get-Content -LiteralPath $portalOutput -Raw)
            if ($logText -like '*中心探测：*') {
                $centerFrameReady = $true
                break
            }
        }
    }

    if (-not $centerFrameReady) {
        throw 'The portal did not reach its stable center frame in time.'
    }

    $extendedStyleDuring = [WindowPortalProbeNative]::GetWindowLongPtr($targetProcess.MainWindowHandle, -20)

    [WindowPortalProbeNative]::SetCursorPos($centerX, $centerY) | Out-Null
    [WindowPortalProbeNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [WindowPortalProbeNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 200

    $foregroundAfter = [WindowPortalProbeNative]::GetForegroundWindow()
    $windowAboveAfter = [WindowPortalProbeNative]::GetVisibleWindowAbove($targetProcess.MainWindowHandle)
    $targetTitleBuffer = [System.Text.StringBuilder]::new(512)
    [WindowPortalProbeNative]::GetWindowText(
        $targetProcess.MainWindowHandle,
        $targetTitleBuffer,
        $targetTitleBuffer.Capacity) | Out-Null
    $targetTitle = $targetTitleBuffer.ToString()

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
    $foregroundPreserved = $foregroundAfter -eq $foregroundBefore
    $zOrderPreserved = $windowAboveBefore -eq $windowAboveAfter
    $noActivateApplied = ($extendedStyleDuring.ToInt64() -band 0x08000000) -ne 0
    $styleRestored = $extendedStyleAfter -eq $extendedStyleBefore
    $foregroundGuardTriggered = $foregroundRecoveryCount -ge 1

    Write-Output "PORTAL_EXIT=$portalExitCode"
    Write-Output "CHATGPT_HWND=0x$($chatGpt.ToInt64().ToString('X'))"
    Write-Output "TARGET_HWND=0x$($targetProcess.MainWindowHandle.ToInt64().ToString('X'))"
    Write-Output "TARGET_TITLE=$targetTitle"
    Write-Output "FOREGROUND_BEFORE=0x$($foregroundBefore.ToInt64().ToString('X'))"
    Write-Output "FOREGROUND_AFTER=0x$($foregroundAfter.ToInt64().ToString('X'))"
    Write-Output "CLICK_FORWARDED=$clickForwarded"
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
