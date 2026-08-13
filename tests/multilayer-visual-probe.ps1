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

    public struct PortalWindowState
    {
        public Rect Bounds;
        public int RegionType;
        public long ExtendedStyle;
        public uint LayeredFlags;
        public byte LayeredAlpha;
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

    [DllImport("user32.dll")]
    public static extern int GetWindowRgn(IntPtr window, IntPtr region);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLayeredWindowAttributes(
        IntPtr window,
        out uint colorKey,
        out byte alpha,
        out uint flags);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr value);

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

    public static PortalWindowState[] GetVisiblePortalStates(
        uint processId,
        int minimumWidth,
        int minimumHeight)
    {
        List<PortalWindowState> result = new List<PortalWindowState>();
        EnumWindows(delegate(IntPtr window, IntPtr parameter)
        {
            uint windowProcessId;
            GetWindowThreadProcessId(window, out windowProcessId);
            Rect rect;
            if (windowProcessId == processId &&
                IsWindowVisible(window) &&
                GetWindowRect(window, out rect) &&
                rect.Width >= minimumWidth &&
                rect.Height >= minimumHeight)
            {
                IntPtr region = CreateRectRgn(0, 0, 0, 0);
                int regionType = region == IntPtr.Zero ? 0 : GetWindowRgn(window, region);
                if (region != IntPtr.Zero)
                {
                    DeleteObject(region);
                }

                uint colorKey;
                byte alpha;
                uint layeredFlags;
                if (!GetLayeredWindowAttributes(
                        window,
                        out colorKey,
                        out alpha,
                        out layeredFlags))
                {
                    alpha = 0;
                    layeredFlags = 0;
                }

                result.Add(new PortalWindowState
                {
                    Bounds = rect,
                    RegionType = regionType,
                    ExtendedStyle = GetWindowLongPtr(window, -20).ToInt64(),
                    LayeredFlags = layeredFlags,
                    LayeredAlpha = alpha
                });
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
    Join-Path $workspace 'src\WindowPortal\bin\Release\net8.0-windows\PierceView.exe'
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
$deepestProcess = $null
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

function Get-KnownLayerCoverage(
    [System.Drawing.Bitmap]$Bitmap,
    [int]$HalfWidth,
    [int]$HalfHeight) {
    $knownPixelCount = 0
    $sampledPixelCount = 0
    for ($y = 4; $y -lt $Bitmap.Height; $y += 8) {
        for ($x = 4; $x -lt $Bitmap.Width; $x += 8) {
            $offsetX = $x - $HalfWidth
            $offsetY = $y - $HalfHeight
            if ([Math]::Abs($offsetX) -gt $HalfWidth -or
                [Math]::Abs($offsetY) -gt $HalfHeight) {
                continue
            }

            $sampledPixelCount++
            $pixel = $Bitmap.GetPixel($x, $y)
            if ((Test-ColorNear $pixel ([System.Drawing.Color]::FromArgb(217, 74, 74)) 65) -or
                (Test-ColorNear $pixel ([System.Drawing.Color]::FromArgb(53, 107, 214)) 65) -or
                (Test-ColorNear $pixel ([System.Drawing.Color]::FromArgb(53, 167, 101)) 65) -or
                (Test-ColorNear $pixel ([System.Drawing.Color]::FromArgb(218, 166, 45)) 65)) {
                $knownPixelCount++
            }
        }
    }

    if ($sampledPixelCount -eq 0) {
        return 0.0
    }

    return $knownPixelCount / [double]$sampledPixelCount
}

try {
    $deepestProcess = Start-Process -FilePath $targetPath -ArgumentList @(
        '--passive', '--label', 'LAYER-4-YELLOW', '--color', '#DAA62D') -PassThru
    $deepProcess = Start-Process -FilePath $targetPath -ArgumentList @(
        '--passive', '--label', 'LAYER-3-BLUE', '--color', '#356BD6') -PassThru
    $middleProcess = Start-Process -FilePath $targetPath -ArgumentList @(
        '--passive', '--label', 'LAYER-2-GREEN', '--color', '#35A765') -PassThru
    $shallowProcess = Start-Process -FilePath $targetPath -ArgumentList @(
        '--passive', '--label', 'LAYER-1-RED', '--color', '#D94A4A') -PassThru
    Wait-MainWindow $deepestProcess
    Wait-MainWindow $deepProcess
    Wait-MainWindow $middleProcess
    Wait-MainWindow $shallowProcess

    $deepestWindow = [IntPtr]$deepestProcess.MainWindowHandle
    $deepWindow = [IntPtr]$deepProcess.MainWindowHandle
    $middleWindow = [IntPtr]$middleProcess.MainWindowHandle
    $shallowWindow = [IntPtr]$shallowProcess.MainWindowHandle
    $chatRect = [MultiLayerVisualProbeNative+Rect]::new()
    if (-not [MultiLayerVisualProbeNative]::GetWindowRect($chatGpt, [ref]$chatRect)) {
        throw 'Could not read the ChatGPT window rectangle.'
    }

    $portalWidth = 420
    $portalHeight = 280
    $portalHalfWidth = [int]($portalWidth / 2)
    $portalHalfHeight = [int]($portalHeight / 2)
    $centerX = $chatRect.Left + [int](($chatRect.Right - $chatRect.Left) / 2)
    $centerY = $chatRect.Top + [int](($chatRect.Bottom - $chatRect.Top) / 2)
    $positionFlags = 0x0010

    # Four quadrants reconstruct -1/-2/-3 over the full -4 background. Each
    # foreground source is intentionally smaller than the source behind it.
    [MultiLayerVisualProbeNative]::SetWindowPos(
        $deepestWindow,
        $chatGpt,
        $chatRect.Left,
        $centerY - 210,
        $chatRect.Width,
        420,
        $positionFlags) | Out-Null
    [MultiLayerVisualProbeNative]::SetWindowPos(
        $deepWindow,
        $chatGpt,
        $centerX - 180,
        $centerY + 20,
        160,
        100,
        $positionFlags) | Out-Null
    [MultiLayerVisualProbeNative]::SetWindowPos(
        $middleWindow, $chatGpt, $centerX + 20, $centerY - 120, 160, 100, $positionFlags) | Out-Null
    [MultiLayerVisualProbeNative]::SetWindowPos(
        $shallowWindow, $chatGpt, $centerX - 180, $centerY - 120, 160, 100, $positionFlags) | Out-Null
    [MultiLayerVisualProbeNative]::ForceForeground($chatGpt) | Out-Null

    # Re-apply the deterministic application order after bringing ChatGPT forward.
    [MultiLayerVisualProbeNative]::SetWindowPos(
        $deepestWindow,
        $chatGpt,
        $chatRect.Left,
        $centerY - 210,
        $chatRect.Width,
        420,
        $positionFlags) | Out-Null
    [MultiLayerVisualProbeNative]::SetWindowPos(
        $deepWindow,
        $chatGpt,
        $centerX - 180,
        $centerY + 20,
        160,
        100,
        $positionFlags) | Out-Null
    [MultiLayerVisualProbeNative]::SetWindowPos(
        $middleWindow, $chatGpt, $centerX + 20, $centerY - 120, 160, 100, $positionFlags) | Out-Null
    [MultiLayerVisualProbeNative]::SetWindowPos(
        $shallowWindow, $chatGpt, $centerX - 180, $centerY - 120, 160, 100, $positionFlags) | Out-Null
    Start-Sleep -Milliseconds 150

    if ([MultiLayerVisualProbeNative]::GetForegroundWindow() -ne $chatGpt) {
        throw 'ChatGPT could not be made foreground. Close or leave exclusive-fullscreen applications before GUI tests.'
    }

    # Keep the portal alive beyond the complete sampling interval. Otherwise
    # the normal HidePortal/exit transition can be sampled as a false black
    # frame while the process is still completing its finally block.
    $portalProcess = Start-Process `
        -FilePath $portalPath `
        -ArgumentList @('--multilayer-probe-hwnd', "0x$($ChatGptWindow.ToString('X'))", '--probe-duration-ms', '2500') `
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
    $bitmap = [System.Drawing.Bitmap]::new($portalWidth, $portalHeight)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(
            $centerX - $portalHalfWidth,
            $centerY - $portalHalfHeight,
            0,
            0,
            [System.Drawing.Size]::new($portalWidth, $portalHeight))
        $bitmap.Save($screenshotPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $leftPixel = $bitmap.GetPixel($portalHalfWidth - 100, $portalHalfHeight - 50)
        $centerPixel = $bitmap.GetPixel($portalHalfWidth + 100, $portalHalfHeight - 50)
        $rightPixel = $bitmap.GetPixel($portalHalfWidth - 100, $portalHalfHeight + 90)
        $deepestPixel = $bitmap.GetPixel($portalHalfWidth + 100, $portalHalfHeight + 90)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }

    # The DirectComposition display HWND stays fixed at the virtual-screen
    # origin. This probe keeps the shader crop fixed as well for deterministic
    # four-layer stability sampling.

    $maxDistinctPortalPositions = 0
    $missingPortalRegionFrameCount = 0
    $layeredPortalWindowCount = 0
    $colorKeyPortalWindowCount = 0
    $invalidAlphaLayeredFrameCount = 0
    $prewarmAlphaFrameCount = 0
    $missingPortalWindowFrameCount = 0
    $invalidCompositeFrameCount = 0
    $confirmedValidNonLayerPointSampleCount = 0
    $invalidCompositeSamples = [System.Collections.Generic.List[string]]::new()
    $sampleBitmap = [System.Drawing.Bitmap]::new(1, 1)
    $sampleGraphics = [System.Drawing.Graphics]::FromImage($sampleBitmap)
    try {
        for ($sample = 0; $sample -lt 100 -and -not $portalProcess.HasExited; $sample++) {
            $states = [MultiLayerVisualProbeNative]::GetVisiblePortalStates(
                [uint32]$portalProcess.Id,
                $portalWidth,
                $portalHeight)
            $distinctPositions = @($states | ForEach-Object {
                "$($_.Bounds.Left),$($_.Bounds.Top),$($_.Bounds.Right),$($_.Bounds.Bottom)"
            } | Sort-Object -Unique).Count
            $maxDistinctPortalPositions = [Math]::Max(
                $maxDistinctPortalPositions,
                $distinctPositions)
            if ($states.Count -eq 0) {
                $missingPortalWindowFrameCount++
            }
            else {
                $sampleBounds = $states[0].Bounds
                $sampleGraphics.CopyFromScreen(
                    $centerX,
                    $centerY + 80,
                    0,
                    0,
                    [System.Drawing.Size]::new(1, 1))
                $samplePixel = $sampleBitmap.GetPixel(0, 0)
                $knownLayerColor =
                    (Test-ColorNear $samplePixel ([System.Drawing.Color]::FromArgb(217, 74, 74)) 65) -or
                    (Test-ColorNear $samplePixel ([System.Drawing.Color]::FromArgb(53, 107, 214)) 65) -or
                    (Test-ColorNear $samplePixel ([System.Drawing.Color]::FromArgb(53, 167, 101)) 65) -or
                    (Test-ColorNear $samplePixel ([System.Drawing.Color]::FromArgb(218, 166, 45)) 65)
                if (-not $knownLayerColor) {
                    $stateSummary = @($states | ForEach-Object {
                        "r=$($_.RegionType),a=$($_.LayeredAlpha),f=0x$($_.LayeredFlags.ToString('X'))"
                    }) -join '|'
                    $invalidFrameBitmap = [System.Drawing.Bitmap]::new($portalWidth, $portalHeight)
                    $invalidFrameGraphics = [System.Drawing.Graphics]::FromImage($invalidFrameBitmap)
                    try {
                        $invalidFrameGraphics.CopyFromScreen(
                            $centerX - $portalHalfWidth,
                            $centerY - $portalHalfHeight,
                            0,
                            0,
                            [System.Drawing.Size]::new($portalWidth, $portalHeight))
                        $confirmationPixel = $invalidFrameBitmap.GetPixel(
                            $portalHalfWidth,
                            $portalHalfHeight + 80)
                        $knownLayerCoverage = Get-KnownLayerCoverage `
                            $invalidFrameBitmap `
                            $portalHalfWidth `
                            $portalHalfHeight
                        if ($knownLayerCoverage -ge 0.2) {
                            $confirmedValidNonLayerPointSampleCount++
                        }
                        else {
                            $invalidCompositeFrameCount++
                            $invalidCompositeSamples.Add(
                                "$sample`:$($samplePixel.R),$($samplePixel.G),$($samplePixel.B)" +
                                "->$($confirmationPixel.R),$($confirmationPixel.G),$($confirmationPixel.B)" +
                                "@$($sampleBounds.Left),$($sampleBounds.Top);coverage=$($knownLayerCoverage.ToString('F3'))" +
                                ";n=$($states.Count);$stateSummary")
                            $invalidFrameBitmap.Save(
                                (Join-Path $diagnosticDirectory "multilayer-invalid-$sample.png"),
                                [System.Drawing.Imaging.ImageFormat]::Png)
                        }
                    }
                    finally {
                        $invalidFrameGraphics.Dispose()
                        $invalidFrameBitmap.Dispose()
                    }
                }
            }
            if (@($states | Where-Object RegionType -eq 0).Count -gt 0) {
                $missingPortalRegionFrameCount++
            }
            $layeredPortalWindowCount = [Math]::Max(
                $layeredPortalWindowCount,
                @($states | Where-Object { ($_.ExtendedStyle -band 0x00080000) -ne 0 }).Count)
            $colorKeyPortalWindowCount = [Math]::Max(
                $colorKeyPortalWindowCount,
                @($states | Where-Object { ($_.LayeredFlags -band 0x00000001) -ne 0 }).Count)
            $prewarmStates = @($states | Where-Object {
                ($_.ExtendedStyle -band 0x00080000) -ne 0 -and
                ($_.LayeredFlags -band 0x00000002) -ne 0 -and
                $_.LayeredAlpha -eq 1
            })
            if ($prewarmStates.Count -gt 0) {
                $prewarmAlphaFrameCount++
            }
            $fullAlphaStateCount = @($states | Where-Object {
                ($_.ExtendedStyle -band 0x00080000) -ne 0 -and
                ($_.LayeredFlags -band 0x00000002) -ne 0 -and
                $_.LayeredAlpha -eq 255
            }).Count
            if (@($states | Where-Object {
                ($_.ExtendedStyle -band 0x00080000) -ne 0 -and
                $_.LayeredFlags -ne 0 -and
                (($_.LayeredFlags -band 0x00000002) -eq 0 -or
                 ($_.LayeredAlpha -ne 1 -and $_.LayeredAlpha -ne 255))
            }).Count -gt 0 -or
                ($prewarmStates.Count -gt 0 -and $fullAlphaStateCount -eq 0)) {
                $invalidAlphaLayeredFrameCount++
            }
            Start-Sleep -Milliseconds 10
        }
    }
    finally {
        $sampleGraphics.Dispose()
        $sampleBitmap.Dispose()
    }

    $portalProcess.WaitForExit()
    $portalExitCode = $portalProcess.ExitCode
    $portalLogText = [string](Get-Content -LiteralPath $portalOutput -Raw)
    $layerMatch = [regex]::Match($portalLogText, '多层合成(?:已启用)?：可渲染层数=(\d+)')
    $timingMatch = [regex]::Match(
        $portalLogText,
        '固定多层换帧：\d+ 帧，平均=([\d.]+)ms，最慢=([\d.]+)ms|连续换帧：\d+ 帧，平均=([\d.]+)ms，最慢=([\d.]+)ms')
    $renderedLayerCount = if ($layerMatch.Success) {
        [int]$layerMatch.Groups[1].Value
    }
    else {
        0
    }
    $averageFrameMilliseconds = if ($timingMatch.Success) {
        $value = if ($timingMatch.Groups[1].Success) {
            $timingMatch.Groups[1].Value
        }
        else {
            $timingMatch.Groups[3].Value
        }
        [double]$value
    }
    else {
        [double]::PositiveInfinity
    }
    $slowestFrameMilliseconds = if ($timingMatch.Success) {
        $value = if ($timingMatch.Groups[2].Success) {
            $timingMatch.Groups[2].Value
        }
        else {
            $timingMatch.Groups[4].Value
        }
        [double]$value
    }
    else {
        [double]::PositiveInfinity
    }

    $leftLayerVisible = Test-ColorNear $leftPixel ([System.Drawing.Color]::FromArgb(217, 74, 74))
    $centerLayerVisible = Test-ColorNear $centerPixel ([System.Drawing.Color]::FromArgb(53, 167, 101))
    $rightLayerVisible = Test-ColorNear $rightPixel ([System.Drawing.Color]::FromArgb(53, 107, 214))
    $deepestLayerVisible = Test-ColorNear $deepestPixel ([System.Drawing.Color]::FromArgb(218, 166, 45))
    $fourLayersVisible = $leftLayerVisible -and $centerLayerVisible -and
        $rightLayerVisible -and $deepestLayerVisible
    $layerPositionsSynchronized = $maxDistinctPortalPositions -le 1
    $performanceWithinBudget = $averageFrameMilliseconds -lt 25 -and $slowestFrameMilliseconds -lt 150

    Write-Output "PORTAL_EXIT=$portalExitCode"
    Write-Output "RENDERED_LAYER_COUNT=$renderedLayerCount"
    Write-Output "LEFT_PIXEL=$($leftPixel.R),$($leftPixel.G),$($leftPixel.B)"
    Write-Output "CENTER_PIXEL=$($centerPixel.R),$($centerPixel.G),$($centerPixel.B)"
    Write-Output "RIGHT_PIXEL=$($rightPixel.R),$($rightPixel.G),$($rightPixel.B)"
    Write-Output "DEEPEST_PIXEL=$($deepestPixel.R),$($deepestPixel.G),$($deepestPixel.B)"
    Write-Output "FOUR_LAYERS_VISIBLE=$fourLayersVisible"
    Write-Output "MAX_DISTINCT_PORTAL_POSITIONS=$maxDistinctPortalPositions"
    Write-Output "LAYER_POSITIONS_SYNCHRONIZED=$layerPositionsSynchronized"
    Write-Output "MISSING_PORTAL_REGION_FRAMES=$missingPortalRegionFrameCount"
    Write-Output "LAYERED_PORTAL_WINDOW_COUNT=$layeredPortalWindowCount"
    Write-Output "COLOR_KEY_PORTAL_WINDOW_COUNT=$colorKeyPortalWindowCount"
    Write-Output "INVALID_ALPHA_LAYERED_FRAMES=$invalidAlphaLayeredFrameCount"
    Write-Output "PREWARM_ALPHA_FRAMES=$prewarmAlphaFrameCount"
    Write-Output "MISSING_PORTAL_WINDOW_FRAMES=$missingPortalWindowFrameCount"
    Write-Output "INVALID_COMPOSITE_FRAMES=$invalidCompositeFrameCount"
    Write-Output "INVALID_COMPOSITE_SAMPLES=$($invalidCompositeSamples -join ';')"
    Write-Output "CONFIRMED_VALID_NON_LAYER_POINT_SAMPLES=$confirmedValidNonLayerPointSampleCount"
    Write-Output "AVERAGE_FRAME_MS=$averageFrameMilliseconds"
    Write-Output "SLOWEST_FRAME_MS=$slowestFrameMilliseconds"
    Write-Output "PERFORMANCE_WITHIN_BUDGET=$performanceWithinBudget"
    Write-Output "SCREENSHOT=$screenshotPath"

    if ($portalExitCode -ne 0 -or
        $renderedLayerCount -ne 4 -or
        -not $fourLayersVisible -or
        -not $layerPositionsSynchronized -or
        $colorKeyPortalWindowCount -ne 0 -or
        $missingPortalWindowFrameCount -ne 0 -or
        $invalidCompositeFrameCount -ne 0 -or
        -not $performanceWithinBudget) {
        throw 'The multi-layer visual and motion probe failed.'
    }
}
finally {
    if ($portalProcess -and -not $portalProcess.HasExited) {
        $portalProcess.CloseMainWindow() | Out-Null
        $portalProcess.WaitForExit(2000) | Out-Null
    }

    foreach ($process in @($shallowProcess, $middleProcess, $deepProcess, $deepestProcess)) {
        if ($process -and -not $process.HasExited) {
            $process.CloseMainWindow() | Out-Null
            $process.WaitForExit(2000) | Out-Null
        }
    }
}
