namespace WindowPortal.TestTarget;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TestTargetForm(TestTargetOptions.Parse(args)));
    }
}

internal sealed record TestTargetOptions(
    string Label,
    Color? SolidColor,
    bool Passive,
    bool Animate)
{
    internal static TestTargetOptions Parse(string[] args)
    {
        var label = "WindowPortal Region Test Target";
        Color? solidColor = null;
        var passive = false;
        var animate = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--label" when index + 1 < args.Length:
                    label = args[++index];
                    break;
                case "--color" when index + 1 < args.Length:
                    solidColor = ColorTranslator.FromHtml(args[++index]);
                    break;
                case "--passive":
                    passive = true;
                    break;
                case "--animate":
                    animate = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown test target argument: {args[index]}");
            }
        }

        return new TestTargetOptions(label, solidColor, passive, animate);
    }
}
