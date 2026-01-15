using System;
using System.IO;
using Avalonia;
using Avalonia.WebView.Desktop;

namespace SimpleMarkdownViewer;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Set up a clean WebView2 user data folder
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimpleMarkdownViewer", "WebView2Data");
        Directory.CreateDirectory(userDataFolder);

        // Set WebView2 environment variables
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
            "--disable-gpu --disable-gpu-compositing --disable-software-rasterizer --use-gl=swiftshader");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseDesktopWebView();
}
