using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MdBrowser;

public partial class App : Application
{
    // Both names are GUID-suffixed so they don't collide with anything else on the system.
    private const string MutexName = "Global\\MdBrowser.Singleton.B1A47A1F-7C6E-4F2A-9D7B-7A2C8E1E3F11";
    private const string PipeName  = "MdBrowser.Pipe.B1A47A1F-7C6E-4F2A-9D7B-7A2C8E1E3F11";

    private Mutex? _singletonMutex;
    private CancellationTokenSource? _pipeCts;

    /// <summary>File path passed on the command line at startup (null when none).</summary>
    public static string? StartupFilePath { get; private set; }

    /// <summary>Raised on the UI thread when a second instance forwarded a file path.</summary>
    public static event Action<string>? RemoteFileRequested;

    static App()
    {
        // Make Windows-1252 (and the other code pages) available on .NET 8.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupFilePath = ResolveFileArg(Environment.GetCommandLineArgs());

        _singletonMutex = new Mutex(initiallyOwned: true, name: MutexName, out bool createdNew);

        if (!createdNew)
        {
            // Another instance owns the mutex. Forward our file path (if any) to it and exit.
            TrySendToPrimary(StartupFilePath);
            Shutdown(0);
            return;
        }

        _pipeCts = new CancellationTokenSource();
        _ = Task.Run(() => RunPipeServerAsync(_pipeCts.Token));

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _pipeCts?.Cancel();
        }
        catch { }
        try
        {
            _singletonMutex?.ReleaseMutex();
        }
        catch { /* mutex may already be released or never acquired */ }
        _singletonMutex?.Dispose();
        base.OnExit(e);
    }

    private static string? ResolveFileArg(string[] args)
    {
        // args[0] is the .exe path itself; the file association passes the target path as args[1].
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (string.IsNullOrWhiteSpace(a)) continue;
            if (a.StartsWith('-') || a.StartsWith('/')) continue; // ignore switches
            try
            {
                if (File.Exists(a)) return Path.GetFullPath(a);
            }
            catch { }
        }
        return null;
    }

    private static void TrySendToPrimary(string? filePath)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(filePath ?? string.Empty);
        }
        catch
        {
            // Primary might be initializing - acceptable to drop. The user will see the existing window.
        }
    }

    private async Task RunPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync().ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(line))
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        ActivateMainWindow();
                        if (File.Exists(line)) RemoteFileRequested?.Invoke(line);
                    });
                }
                else
                {
                    _ = Dispatcher.BeginInvoke(ActivateMainWindow);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Brief backoff before re-listening; the loop must keep running.
                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch { return; }
            }
        }
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is null) return;
        if (MainWindow.WindowState == WindowState.Minimized)
            MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
    }
}
