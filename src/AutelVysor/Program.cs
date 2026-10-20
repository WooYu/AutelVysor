using AutelVysor.Services;

namespace AutelVysor;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
            return SelfTestService.Run();

        var app = new App();
        return app.Run();
    }
}
