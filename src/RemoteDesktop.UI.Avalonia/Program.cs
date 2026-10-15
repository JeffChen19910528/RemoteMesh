using Avalonia;

namespace RemoteDesktop.UI.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        BuildApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
