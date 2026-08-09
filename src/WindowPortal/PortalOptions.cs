using System.Globalization;

namespace WindowPortal;

internal sealed record PortalOptions(
    int Radius,
    int PollMilliseconds,
    nint? ProbeWindow,
    nint? InspectWindow,
    NativeMethods.Point? InspectPoint,
    int ProbeDurationMilliseconds,
    bool SelfTest,
    bool ListWindows,
    bool CompatibilityReport,
    bool ShowVersion,
    bool ShowHelp)
{
    public static PortalOptions Parse(string[] args)
    {
        var radius = 180;
        var pollMilliseconds = 16;
        nint? probeWindow = null;
        nint? inspectWindow = null;
        NativeMethods.Point? inspectPoint = null;
        var probeDurationMilliseconds = 1500;
        var selfTest = false;
        var listWindows = false;
        var compatibilityReport = false;
        var showVersion = false;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            switch (argument)
            {
                case "--radius":
                    radius = ParseInt(NextValue(args, ref index, argument), argument, 32, 2000);
                    break;
                case "--poll-ms":
                    pollMilliseconds = ParseInt(NextValue(args, ref index, argument), argument, 4, 1000);
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
                        60_000);
                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                case "--list-windows":
                    listWindows = true;
                    break;
                case "--compatibility-report":
                    compatibilityReport = true;
                    break;
                case "--version":
                    showVersion = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"未知参数：{argument}");
            }
        }

        var exclusiveModeCount =
            (selfTest ? 1 : 0) +
            (listWindows ? 1 : 0) +
            (compatibilityReport ? 1 : 0) +
            (showVersion ? 1 : 0) +
            (probeWindow is not null ? 1 : 0) +
            (inspectWindow is not null ? 1 : 0);
        if (exclusiveModeCount > 1)
        {
            throw new ArgumentException("--self-test、--list-windows、--compatibility-report、--version、--probe-hwnd 和 --inspect-hwnd 不能同时使用。");
        }

        if (inspectPoint is not null && inspectWindow is null)
        {
            throw new ArgumentException("--inspect-point 只能与 --inspect-hwnd 一起使用。");
        }

        return new PortalOptions(
            radius,
            pollMilliseconds,
            probeWindow,
            inspectWindow,
            inspectPoint,
            probeDurationMilliseconds,
            selfTest,
            listWindows,
            compatibilityReport,
            showVersion,
            showHelp);
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
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ||
            result < minimum ||
            result > maximum)
        {
            throw new ArgumentException($"{argument} 必须是 {minimum} 到 {maximum} 之间的整数。");
        }

        return result;
    }

    private static int ParseCoordinate(string value, string argument)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ||
            result < -100_000 ||
            result > 100_000)
        {
            throw new ArgumentException($"{argument} 坐标必须是 -100000 到 100000 之间的整数。");
        }

        return result;
    }

    internal static nint ParseWindowHandle(string value)
    {
        var isHexadecimal = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var digits = isHexadecimal ? value[2..] : value;
        var style = isHexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;

        if (!long.TryParse(digits, style, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException("--probe-hwnd 必须是正数窗口句柄，例如 0x123456。");
        }

        return checked((nint)parsed);
    }
}
