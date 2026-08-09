param(
    [Parameter(Mandatory = $true)]
    [Int64]$ChatGptWindow,

    [string]$PortalExecutable
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing.Common
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class MultiLayerVisualProbeNative
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
    public static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

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

    public static Rect[] GetVisiblePortalRects(uint processId, int diameter)
    {
        List<Rect> result = new List<Rect>();
        EnumWindows(delegate(IntPtr window, IntPtr parameter)
        {
            uint windowProcessId;
            GetWindowThreadProcessId(window, out windowProcessId);
            Rect rect;
            if (windowProcessId == processId &&
                IsWindowVisible(window) &&
                GetWindowRect(window, out rect) &&
                rect.Width == diameter &&
                rect.Height == diameter)
            {
                result.Add(rect);
            }

            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
}
'@

[MultiLayerVisualProbeNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

$workspace = Split-Path -Parent $PSScriptRoot
$chatGpt = [IntPtr]$ChatGptWindow
if (-not [MultiLayerVisualProbeNative]::IsWindow($chatGpt)) {
    throw "ChatGPT HWND 0x$($ChatGptWindow.ToString('X')) is not valid."
}

$targetPath = Join-Path $workspace 'tests\WindowPortal.TestTarget\bin\Release\net8.0-windows\WindowPortal.TestTarget.exe'
$portalPath = if ([string]::IsNullOrWhiteSpace($PortalExecutable)) {
    Join-Path $workspace 'src\WindowPortal\bin\Release\net8.0-windows\WindowPortal.exe'
}
else {
    $PortalExecutable
}
$diagnosticDirectory = Join-Path $workspace 'artifacts\diagnostics'
New-Item -ItemType Directory -Force -Path $diagnosticDirectory | Out-Null
$portalOutput = Join-Path $diagnosticDirectory 'multilayer-visual-probe.log'
$portalError = Join-Path $diagnosticDirectory 'multilayer-visual-probe.error.log'
$screenshotPath = Join-Path $diagnosticDirectory 'multilayer-visual-probe.png'
$deepProcess = $null
$middleProcess = $null
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

function Test-ColorNear(
    [System.Drawing.Color]$Actual,
    [System.Drawing.Color]$Expected,
    [int]$Tolerance = 55) {
    return [Math]::Abs([int]$Actual.R - [int]$Expected.R) -le $Tolerance -and
        [Math]::Abs([int]$Actual.G - [int]$Expected.G) -le $Tolerance -and
        [Math]::Abs([int]$Actual.B - [int]$Expected.B) -le $Tolerance
}

try {
    $deepProcess = Start-Process -FilePath $targetPath -ArgumentList @(
        '--passive', '--label', 'LAYER-3-BLUE', '--color', '#356BD6') -PassThru
    $middleProcess = Start-Process -FilePath $targetPath -ArgumentList @(
        '--passive', '--label', 'LAYER-2-GREEN', '--color', '#35A765') -PassThru
    $shallowProcess = Start-Process -FilePath $targetPath -ArgumentList @(
        '--passive', '--label', 'LAYER-1-RED', '--color', '#D94A4A') -PassThru
    Wait-MainWindow $deepProcess
    Wait-MainWindow $middleProcess
    Wait-MainWindow $shallowProcess

    $deepWindow = [IntPtr]$deepProcess.MainWindowHandle
    $middleWindow = [IntPtr]$middleProcess.MainWindowHandle
    $shallowWindow = [IntPtr]$shallowProcess.MainWindowHandle
    $chatRect = [MultiLayerVisualProbeNative+Rect]::new()
    if (-not [MultiLayerVisualProbeNative]::GetWindowRect($chatGpt, [ref]$chatRect)) {
        throw 'Could not read the ChatGPT window rectangle.'
    }

    $radius = 180
    $diameter = 361
    $centerX = $chatRect.Left + [int](($chatRect.Right - $chatRect.Left) / 2)
    $centerY = $chatRect.Top + [int](($chatRect.Bottom - $chatRect.Top) / 2)
    $positionFlags = 0x0010

    # Keep the deepest source under the complete horizontal sweep used by
    # WindowPortal's probe mode. The shallower red/green windows still expose
    # three distinct layers at the center, while blue provides a valid source
    # after the probe leaves those narrow windows.
    [MultiLayerVisualProbeNative]::SetWindowPos(
        $deepWindow,
        $chatGpt,
        $chatRect.Left,
        $centerY - 210,
        $chatRect.Width,
        420,
        $positionFlags) | Out-Null
    [MultiLayerVisualProbeNative]::SetWindowPos(
        $middleWindow, $chatGpt, $centerX + 40, $centerY - 180, 140, 360, $positionFlags) | Out-Null
    [MultiLayerVisualProbeNative]::SetWindowPos(
        $shallowWindow, $chatGpt, $centerX - 180, $centerY - 180, 140, 360, $positionFlags) | Out-Null
    [MultiLayerVisualProbeNative]::ForceForeground($chatGpt) | Out-Null

    # Re-apply the deterministic application order after bringing ChatGPT forward.
    [MultiLayerVisualProbeNative]::SetWindowPos(
        $deepWindow,
        $chatGpt,
        $chatRect.Left,
        $centerY - 210,
        $chatRect.Width,
        420,
        $positionFlags) | Out-Null
    [MultiLayerVisualProbeNative]::SetWindowPos(
        $middleWindow, $chatGpt, $centerX + 40, $centerY - 180, 140, 360, $positionFlags) | Out-Null
    [MultiLayerVisualProbeNative]::SetWindowPos(
        $shallowWindow, $chatGpt, $centerX - 180, $centerY - 180, 140, 360, $positionFlags) | Out-Null
    Start-Sleep -Milliseconds 150

    if ([MultiLayerVisualProbeNative]::GetForegroundWindow() -ne $chatGpt) {
        throw 'ChatGPT could not be made foreground. Close or leave exclusive-fullscreen applications before GUI tests.'
    }

    $portalProcess = Start-Process `
        -FilePath $portalPath `
        -ArgumentList @('--probe-hwnd', "0x$($ChatGptWindow.ToString('X'))", '--probe-duration-ms', '500', '--radius', "$radius") `
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
        throw "The multi-layer portal did not reach its center frame. $errorText"
    }

    Start-Sleep -Milliseconds 80
    $bitmap = [System.Drawing.Bitmap]::new($diameter, $diameter)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(
            $centerX - $radius,
            $centerY - $radius,
            0,
            0,
            [System.Drawing.Size]::new($diameter, $diameter))
        $bitmap.Save($screenshotPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $leftPixel = $bitmap.GetPixel($radius - 100, $radius + 80)
        $centerPixel = $bitmap.GetPixel($radius, $radius + 80)
        $rightPixel = $bitmap.GetPixel($radius + 100, $radius + 80)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }

    $maxDistinctPortalPositions = 0
    for ($sample = 0; $sample -lt 100 -and -not $portalProcess.HasExited; $sample++) {
        $rects = [MultiLayerVisualProbeNative]::GetVisiblePortalRects(
            [uint32]$portalProcess.Id,
            $diameter)
        $distinctPositions = @($rects | ForEach-Object {
            "$($_.Left),$($_.Top),$($_.Right),$($_.Bottom)"
        } | Sort-Object -Unique).Count
        $maxDistinctPortalPositions = [Math]::Max(
            $maxDistinctPortalPositions,
            $distinctPositions)
        Start-Sleep -Milliseconds 10
    }

    $portalProcess.WaitForExit()
    $portalExitCode = $portalProcess.ExitCode
    $portalLogText = [string](Get-Content -LiteralPath $portalOutput -Raw)
    $layerMatch = [regex]::Match($portalLogText, '多层合成(?:已启用)?：可渲染层数=(\d+)')
    $timingMatch = [regex]::Match(
        $portalLogText,
        '连续换帧：\d+ 帧，平均=([\d.]+)ms，最慢=([\d.]+)ms')
    $renderedLayerCount = if ($layerMatch.Success) {
        [int]$layerMatch.Groups[1].Value
    }
    else {
        0
    }
    $averageFrameMilliseconds = if ($timingMatch.Success) {
        [double]$timingMatch.Groups[1].Value
    }
    else {
        [double]::PositiveInfinity
    }
    $slowestFrameMilliseconds = if ($timingMatch.Success) {
        [double]$timingMatch.Groups[2].Value
    }
    else {
        [double]::PositiveInfinity
    }

    $leftLayerVisible = Test-ColorNear $leftPixel ([System.Drawing.Color]::FromArgb(217, 74, 74))
    $centerLayerVisible = Test-ColorNear $centerPixel ([System.Drawing.Color]::FromArgb(53, 107, 214))
    $rightLayerVisible = Test-ColorNear $rightPixel ([System.Drawing.Color]::FromArgb(53, 167, 101))
    $threeLayersVisible = $leftLayerVisible -and $centerLayerVisible -and $rightLayerVisible
    $layerPositionsSynchronized = $maxDistinctPortalPositions -le 1
    $performanceWithinBudget = $averageFrameMilliseconds -lt 25 -and $slowestFrameMilliseconds -lt 150

    Write-Output "PORTAL_EXIT=$portalExitCode"
    Write-Output "RENDERED_LAYER_COUNT=$renderedLayerCount"
    Write-Output "LEFT_PIXEL=$($leftPixel.R),$($leftPixel.G),$($leftPixel.B)"
    Write-Output "CENTER_PIXEL=$($centerPixel.R),$($centerPixel.G),$($centerPixel.B)"
    Write-Output "RIGHT_PIXEL=$($rightPixel.R),$($rightPixel.G),$($rightPixel.B)"
    Write-Output "THREE_LAYERS_VISIBLE=$threeLayersVisible"
    Write-Output "MAX_DISTINCT_PORTAL_POSITIONS=$maxDistinctPortalPositions"
    Write-Output "LAYER_POSITIONS_SYNCHRONIZED=$layerPositionsSynchronized"
    Write-Output "AVERAGE_FRAME_MS=$averageFrameMilliseconds"
    Write-Output "SLOWEST_FRAME_MS=$slowestFrameMilliseconds"
    Write-Output "PERFORMANCE_WITHIN_BUDGET=$performanceWithinBudget"
    Write-Output "SCREENSHOT=$screenshotPath"

    if ($portalExitCode -ne 0 -or
        $renderedLayerCount -ne 3 -or
        -not $threeLayersVisible -or
        -not $layerPositionsSynchronized -or
        -not $performanceWithinBudget) {
        throw 'The multi-layer visual and motion probe failed.'
    }
}
finally {
    if ($portalProcess -and -not $portalProcess.HasExited) {
        $portalProcess.CloseMainWindow() | Out-Null
        $portalProcess.WaitForExit(2000) | Out-Null
    }

    foreach ($process in @($shallowProcess, $middleProcess, $deepProcess)) {
        if ($process -and -not $process.HasExited) {
            $process.CloseMainWindow() | Out-Null
            $process.WaitForExit(2000) | Out-Null
        }
    }
}
