using System.Globalization;

namespace WindowPortal;

internal sealed record PortalOptions(
    int Radius,
    int PollMilliseconds,
    bool RadiusWasSpecified,
    bool PollWasSpecified,
    nint? ProbeWindow,
    nint? InspectWindow,
    NativeMethods.Point? InspectPoint,
    int ProbeDurationMilliseconds,
    bool SelfTest,
    bool ListWindows,
    bool ShowVersion,
    bool ShowHelp,
    int? TraySmokeTestMilliseconds)
{
    internal static PortalOptions Parse(string[] args)
    {
        var radius = 180;
        var pollMilliseconds = 16;
        var radiusWasSpecified = false;
        var pollWasSpecified = false;
        nint? probeWindow = null;
        nint? inspectWindow = null;
        NativeMethods.Point? inspectPoint = null;
        var probeDurationMilliseconds = 1500;
        var selfTest = false;
        var listWindows = false;
        var showVersion = false;
        var showHelp = false;
        int? traySmokeTestMilliseconds = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--radius":
                    radius = ParseInt(NextValue(args, ref index, argument), argument, 64, 400);
                    radiusWasSpecified = true;
                    break;
                case "--poll-ms":
                    pollMilliseconds = ParseInt(NextValue(args, ref index, argument), argument, 8, 100);
                    pollWasSpecified = true;
                    break;
                case "--probe-hwnd":
                    probeWindow = ParseWindowHandle(NextValue(args, ref index, argument));
                    break;
                case "--inspect-hwnd":
                    inspectWindow = ParseWindowHandle(NextValue(args, ref index, argument));
                    break;
                case "--inspect-point":
                    var x = ParseCoordinate(NextValue(args, ref index, argument), argument);
                    var y = ParseCoordinate(NextValue(args, ref index, argument), argument);
                    inspectPoint = new NativeMethods.Point(x, y);
                    break;
                case "--probe-duration-ms":
                    probeDurationMilliseconds = ParseInt(
                        NextValue(args, ref index, argument),
                        argument,
                        100,
                        60000);
                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                case "--list-windows":
                    listWindows = true;
                    break;
                case "--version":
                    showVersion = true;
                    break;
                case "--tray-smoke-test-ms":
                    traySmokeTestMilliseconds = ParseInt(
                        NextValue(args, ref index, argument),
                        argument,
                        250,
                        10000);
                    break;
                case "-h":
                case "/?":
                case "--help":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"未知参数：{argument}");
            }
        }

        var exclusiveModeCount =
            (selfTest ? 1 : 0) +
            (listWindows ? 1 : 0) +
            (showVersion ? 1 : 0) +
            (probeWindow.HasValue ? 1 : 0) +
            (inspectWindow.HasValue ? 1 : 0);
        if (exclusiveModeCount > 1)
        {
            throw new ArgumentException(
                "--self-test、--list-windows、--version、--probe-hwnd 和 --inspect-hwnd 不能同时使用。");
        }

        if (inspectPoint.HasValue && !inspectWindow.HasValue)
        {
            throw new ArgumentException("--inspect-point 只能与 --inspect-hwnd 一起使用。");
        }

        return new PortalOptions(
            radius,
            pollMilliseconds,
            radiusWasSpecified,
            pollWasSpecified,
            probeWindow,
            inspectWindow,
            inspectPoint,
            probeDurationMilliseconds,
            selfTest,
            listWindows,
            showVersion,
            showHelp,
            traySmokeTestMilliseconds);
    }

    internal static nint ParseWindowHandle(string value)
    {
        var hexadecimal = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var digits = hexadecimal ? value[2..] : value;
        var style = hexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;
        if (!long.TryParse(digits, style, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException(
                "窗口句柄必须是正数，例如 0x123456。");
        }

        return checked((nint)parsed);
    }

    private static string NextValue(string[] args, ref int index, string argument)
    {
        index++;
        if (index >= args.Length)
        {
            throw new ArgumentException($"{argument} 缺少值。");
        }

        return args[index];
    }

    private static int ParseInt(string value, string argument, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < minimum ||
            parsed > maximum)
        {
            throw new ArgumentException(
                $"{argument} 必须是 {minimum} 到 {maximum} 之间的整数。");
        }

        return parsed;
    }

    private static int ParseCoordinate(string value, string argument)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed is < -100000 or > 100000)
        {
            throw new ArgumentException(
                $"{argument} 坐标必须是 -100000 到 100000 之间的整数。");
        }

        return parsed;
    }
}
