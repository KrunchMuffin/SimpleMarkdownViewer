using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using Avalonia;
using Avalonia.WebView.Desktop;

namespace SimpleMarkdownViewer;

class Program
{
    private const string MutexName = "SimpleMarkdownViewer_SingleInstance";
    private const string PipeName = "SimpleMarkdownViewer_Pipe";

    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // Another instance is already running - send file path via named pipe
            if (args.Length > 0 && File.Exists(args[0]))
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    client.Connect(3000);
                    using var writer = new StreamWriter(client);
                    writer.WriteLine(args[0]);
                    writer.Flush();
                }
                catch { /* If pipe fails, just exit */ }
            }
            return;
        }

        // First instance - start the app
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseDesktopWebView();
}
