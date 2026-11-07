using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Reflection.Desktop;

internal static class InstanceActivation
{
    internal static string PipeName(string dataRoot)
    {
        using var process = Process.GetCurrentProcess();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToUpperInvariant();
        return $"Reflection-{process.SessionId}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))}";
    }

    internal static async Task<bool> TryActivateAsync(string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.In,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            var reply = new byte[1];
            return await pipe.ReadAsync(reply, timeout.Token) == 1 && reply[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Trace.TraceWarning("Could not activate Reflection: {0}", ex.Message);
            return false;
        }
    }

    internal static async Task ListenAsync(string name, Func<Task<bool>> activate, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                var restored = await activate().ConfigureAwait(false);
                try { await pipe.WriteAsync(new byte[] { restored ? (byte)1 : (byte)0 }, cancellation).ConfigureAwait(false); }
                catch (IOException ex) { Trace.TraceWarning("Reflection activation client disconnected: {0}", ex.Message); }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not listen for Reflection activation: {0}", ex.Message);
        }
    }
}
