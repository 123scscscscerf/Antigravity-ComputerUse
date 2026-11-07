using System.Text;
using AntigravityBridge.Core.Ipc;
using AntigravityBridge.Core.Native;

namespace AntigravityBridge.Core;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Enable Per-Monitor v2 DPI awareness so screen coordinates and captures match physical pixels 1:1
        try
        {
            Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch
        {
            // Ignore if already set by manifest/host
        }

        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        string pipeName = "AntigravityBridge";
        string? singleJson = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pipe-name" && i + 1 < args.Length)
            {
                pipeName = args[++i];
            }
            else if (args[i] == "--exec" && i + 1 < args.Length)
            {
                singleJson = args[++i];
            }
        }

        var server = new NamedPipeRpcServer();

        if (!string.IsNullOrWhiteSpace(singleJson))
        {
            string res = server.ProcessRawJsonRequest(singleJson);
            Console.WriteLine(res);
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await server.RunPipeServerAsync(pipeName, cts.Token);
        return 0;
    }
}
