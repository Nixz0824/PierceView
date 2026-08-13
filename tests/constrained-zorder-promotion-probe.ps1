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

public static class ConstrainedZOrderProbeNative
{
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
    public static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

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
    public static extern bool SetForegroundWindow(IntPtr window);

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

    public static IntPtr GetVisibleWindow(IntPtr window, uint direction)
    {
        IntPtr candidate = GetWindow(window, direction);
        while (candidate != IntPtr.Zero && !IsWindowVisible(candidate))
        {
            candidate = GetWindow(candidate, direction);
        }

        return candidate;
    }

    public static string ReadTitle(IntPtr window)
    {
        StringBuilder text = new StringBuilder(512);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    public static uint ReadProcessId(IntPtr window)
    {
        uint processId;
        GetWindowThreadProcessId(window, out processId);
        return processId;
    }

    public static bool IsAbove(IntPtr expectedAbove, IntPtr expectedBelow)
    {
        for (IntPtr candidate = GetWindow(expectedBelow, 3);
             candidate != IntPtr.Zero;
             candidate = GetWindow(candidate, 3))
        {
            if (candidate == expectedAbove)
            {
                return true;
            }
        }

        return false;
    }
}
'@

[ConstrainedZOrderProbeNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

$workspace = Split-Path -Parent $PSScriptRoot
$chatGpt = [IntPtr]$ChatGptWindow
if (-not [ConstrainedZOrderProbeNative]::IsWindow($chatGpt)) {
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
$portalOutput = Join-Path $diagnosticDirectory 'constrained-zorder-promotion-probe.log'
$portalError = Join-Path $diagnosticDirectory 'constrained-zorder-promotion-probe.error.log'
$deepProcess = $null
$shallowProcess = $null
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

try {
    $deepProcess = Start-Process -FilePath $targetPath -PassThru
    $shallowProcess = Start-Process -FilePath $targetPath -PassThru
    Wait-MainWindow $deepProcess
    Wait-MainWindow $shallowProcess

    $deepWindow = [IntPtr]$deepProcess.MainWindowHandle
    $shallowWindow = [IntPtr]$shallowProcess.MainWindowHandle
    $chatRect = [ConstrainedZOrderProbeNative+Rect]::new()
    if (-not [ConstrainedZOrderProbeNative]::GetWindowRect($chatGpt, [ref]$chatRect)) {
        throw 'Could not read the ChatGPT window rectangle.'
    }

    $centerX = $chatRect.Left + [int](($chatRect.Right - $chatRect.Left) / 2)
    $centerY = $chatRect.Top + [int](($chatRect.Bottom - $chatRect.Top) / 2)
    $positionFlags = 0x0010

    if (-not [ConstrainedZOrderProbeNative]::SetWindowPos(
        $deepWindow,
        $chatGpt,
        $chatRect.Left,
        $centerY - 280,
        $chatRect.Right - $chatRect.Left,
        560,
        $positionFlags)) {
        throw 'Could not position the large -2 test window.'
    }

    if (-not [ConstrainedZOrderProbeNative]::SetWindowPos(
        $shallowWindow,
        $chatGpt,
        $centerX - 470,
        $centerY - 160,
        500,
        320,
        $positionFlags)) {
        throw 'Could not position the small -1 test window.'
    }

    [ConstrainedZOrderProbeNative]::ForceForeground($chatGpt) | Out-Null
    [ConstrainedZOrderProbeNative]::SetWindowPos(
        $deepWindow,
        $chatGpt,
        $chatRect.Left,
        $centerY - 280,
        $chatRect.Right - $chatRect.Left,
        560,
        $positionFlags) | Out-Null
    [ConstrainedZOrderProbeNative]::SetWindowPos(
        $shallowWindow,
        $chatGpt,
        $centerX - 470,
        $centerY - 160,
        500,
        320,
        $positionFlags) | Out-Null
    Start-Sleep -Milliseconds 150

    $foregroundBefore = [ConstrainedZOrderProbeNative]::GetForegroundWindow()
    $belowChatBefore = [ConstrainedZOrderProbeNative]::GetVisibleWindow($chatGpt, 2)
    $belowShallowBefore = [ConstrainedZOrderProbeNative]::GetVisibleWindow($shallowWindow, 2)
    $shallowStyleBefore = [ConstrainedZOrderProbeNative]::GetWindowLongPtr($shallowWindow, -20)
    $deepStyleBefore = [ConstrainedZOrderProbeNative]::GetWindowLongPtr($deepWindow, -20)
    if ($foregroundBefore -ne $chatGpt) {
        throw "ChatGPT could not be made foreground before the test."
    }

    if ($belowChatBefore -ne $shallowWindow -or $belowShallowBefore -ne $deepWindow) {
        throw "Initial Z-order is not ChatGPT -> small -1 -> large -2."
    }

    $portalProcess = Start-Process `
        -FilePath $portalPath `
        -ArgumentList @('--promotion-probe-hwnd', "0x$($ChatGptWindow.ToString('X'))", '--probe-duration-ms', '1800') `
        -WindowStyle Hidden `
        -PassThru `
        -RedirectStandardOutput $portalOutput `
        -RedirectStandardError $portalError

    $centerFrameReady = $false
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
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
        $errorText = if (Test-Path -LiteralPath $portalError) {
            [string](Get-Content -LiteralPath $portalError -Raw)
        }
        else {
            ''
        }
        throw "The portal did not reach its stable center frame. $errorText"
    }

    $shallowStyleDuring = [ConstrainedZOrderProbeNative]::GetWindowLongPtr($shallowWindow, -20)
    $clickX = $centerX + 120
    $clickY = $centerY
    [ConstrainedZOrderProbeNative]::SetCursorPos($clickX, $clickY) | Out-Null
    Start-Sleep -Milliseconds 35
    [ConstrainedZOrderProbeNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 35
    [ConstrainedZOrderProbeNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250

    $foregroundAfterClick = [ConstrainedZOrderProbeNative]::GetForegroundWindow()
    $belowChatAfterClick = [ConstrainedZOrderProbeNative]::GetVisibleWindow($chatGpt, 2)
    $belowDeepAfterClick = [ConstrainedZOrderProbeNative]::GetVisibleWindow($deepWindow, 2)
    $deepStyleDuring = [ConstrainedZOrderProbeNative]::GetWindowLongPtr($deepWindow, -20)
    $deepTitle = [ConstrainedZOrderProbeNative]::ReadTitle($deepWindow)
    $shallowTitle = [ConstrainedZOrderProbeNative]::ReadTitle($shallowWindow)

    # The original -1 now has an exposed area on the left. Click it to prove
    # the same session can exchange the first two background slots repeatedly.
    $returnClickX = $centerX - 240
    [ConstrainedZOrderProbeNative]::SetCursorPos($returnClickX, $clickY) | Out-Null
    Start-Sleep -Milliseconds 35
    [ConstrainedZOrderProbeNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 35
    [ConstrainedZOrderProbeNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
    $foregroundAfterReturnClick = [ConstrainedZOrderProbeNative]::GetForegroundWindow()
    $belowChatAfterReturnClick = [ConstrainedZOrderProbeNative]::GetVisibleWindow($chatGpt, 2)
    $deepTitleAfterReturn = [ConstrainedZOrderProbeNative]::ReadTitle($deepWindow)
    $shallowTitleAfterReturn = [ConstrainedZOrderProbeNative]::ReadTitle($shallowWindow)

    $portalProcess.WaitForExit()
    $portalExitCode = $portalProcess.ExitCode
    $portalLogText = [string](Get-Content -LiteralPath $portalOutput -Raw)
    $recoveryMatch = [regex]::Match($portalLogText, '回滚次数=(\d+)')
    $immediateClampMatch = [regex]::Match($portalLogText, '前台快速钳制：次数=(\d+)')
    $promotionMatch = [regex]::Match($portalLogText, '受限层级提升：次数=(\d+)')
    $foregroundRecoveryCount = if ($recoveryMatch.Success) {
        [int]$recoveryMatch.Groups[1].Value
    }
    else {
        0
    }
    $promotionCount = if ($promotionMatch.Success) {
        [int]$promotionMatch.Groups[1].Value
    }
    else {
        0
    }
    $immediateClampCount = if ($immediateClampMatch.Success) {
        [int]$immediateClampMatch.Groups[1].Value
    }
    else {
        0
    }

    $shallowStyleAfter = [ConstrainedZOrderProbeNative]::GetWindowLongPtr($shallowWindow, -20)
    $deepStyleAfter = [ConstrainedZOrderProbeNative]::GetWindowLongPtr($deepWindow, -20)
    $deepClickForwarded = $deepTitle -like '*Clicks: 1*'
    $shallowNotClicked = $shallowTitle -like '*Clicks: 0*'
    $foregroundPreserved = $foregroundAfterClick -eq $chatGpt
    $promotedDirectlyBelow = $belowChatAfterClick -eq $deepWindow
    $originalShallowShiftedBack = [ConstrainedZOrderProbeNative]::IsAbove(
        $deepWindow,
        $shallowWindow)
    $shallowNoActivateApplied = ($shallowStyleDuring.ToInt64() -band 0x08000000) -ne 0
    $deepNoActivateApplied = ($deepStyleDuring.ToInt64() -band 0x08000000) -ne 0
    $stylesRestored =
        $shallowStyleAfter -eq $shallowStyleBefore -and
        $deepStyleAfter -eq $deepStyleBefore
    $promotionGuardTriggered = $promotionCount -ge 1
    $returnedShallowDirectlyBelow = $belowChatAfterReturnClick -eq $shallowWindow
    $secondClickForwarded = $shallowTitleAfterReturn -like '*Clicks: 1*'
    $deepNotClickedTwice = $deepTitleAfterReturn -like '*Clicks: 1*'
    $foregroundPreservedAfterReturn = $foregroundAfterReturnClick -eq $chatGpt

    Write-Output "PORTAL_EXIT=$portalExitCode"
    Write-Output "CHATGPT_HWND=0x$($chatGpt.ToInt64().ToString('X'))"
    Write-Output "SHALLOW_HWND=0x$($shallowWindow.ToInt64().ToString('X'))"
    Write-Output "DEEP_HWND=0x$($deepWindow.ToInt64().ToString('X'))"
    Write-Output "FOREGROUND_AFTER_CLICK=0x$($foregroundAfterClick.ToInt64().ToString('X')) TITLE=$([ConstrainedZOrderProbeNative]::ReadTitle($foregroundAfterClick))"
    Write-Output "BELOW_CHAT_AFTER_CLICK=0x$($belowChatAfterClick.ToInt64().ToString('X')) TITLE=$([ConstrainedZOrderProbeNative]::ReadTitle($belowChatAfterClick))"
    Write-Output "BELOW_DEEP_AFTER_CLICK=0x$($belowDeepAfterClick.ToInt64().ToString('X')) PID=$([ConstrainedZOrderProbeNative]::ReadProcessId($belowDeepAfterClick)) TITLE=$([ConstrainedZOrderProbeNative]::ReadTitle($belowDeepAfterClick))"
    Write-Output "DEEP_TARGET_TITLE=$deepTitle"
    Write-Output "SHALLOW_TARGET_TITLE=$shallowTitle"
    Write-Output "FOREGROUND_PRESERVED=$foregroundPreserved"
    Write-Output "DEEP_CLICK_FORWARDED=$deepClickForwarded"
    Write-Output "SHALLOW_NOT_CLICKED=$shallowNotClicked"
    Write-Output "PROMOTED_DIRECTLY_BELOW_CHATGPT=$promotedDirectlyBelow"
    Write-Output "ORIGINAL_SHALLOW_SHIFTED_BACK=$originalShallowShiftedBack"
    Write-Output "SHALLOW_NO_ACTIVATE_APPLIED=$shallowNoActivateApplied"
    Write-Output "DEEP_NO_ACTIVATE_APPLIED=$deepNoActivateApplied"
    Write-Output "STYLES_RESTORED=$stylesRestored"
    Write-Output "FOREGROUND_RECOVERY_COUNT=$foregroundRecoveryCount"
    Write-Output "IMMEDIATE_FOREGROUND_CLAMP_COUNT=$immediateClampCount"
    Write-Output "PROMOTION_COUNT=$promotionCount"
    Write-Output "RETURNED_SHALLOW_DIRECTLY_BELOW=$returnedShallowDirectlyBelow"
    Write-Output "SECOND_CLICK_FORWARDED=$secondClickForwarded"
    Write-Output "DEEP_NOT_CLICKED_TWICE=$deepNotClickedTwice"
    Write-Output "FOREGROUND_PRESERVED_AFTER_RETURN=$foregroundPreservedAfterReturn"

    if ($portalExitCode -ne 0 -or
        -not $deepClickForwarded -or
        -not $shallowNotClicked -or
        -not $foregroundPreserved -or
        -not $promotedDirectlyBelow -or
        -not $originalShallowShiftedBack -or
        -not $shallowNoActivateApplied -or
        -not $deepNoActivateApplied -or
        -not $stylesRestored -or
        -not $promotionGuardTriggered -or
        -not $returnedShallowDirectlyBelow -or
        -not $secondClickForwarded -or
        -not $deepNotClickedTwice -or
        -not $foregroundPreservedAfterReturn -or
        $promotionCount -lt 2) {
        throw 'The constrained Z-order promotion probe failed.'
    }
}
finally {
    if ($portalProcess -and -not $portalProcess.HasExited) {
        $portalProcess.CloseMainWindow() | Out-Null
        $portalProcess.WaitForExit(2000) | Out-Null
    }

    if ($shallowProcess -and -not $shallowProcess.HasExited) {
        $shallowProcess.CloseMainWindow() | Out-Null
        $shallowProcess.WaitForExit(2000) | Out-Null
    }

    if ($deepProcess -and -not $deepProcess.HasExited) {
        $deepProcess.CloseMainWindow() | Out-Null
        $deepProcess.WaitForExit(2000) | Out-Null
    }
}
