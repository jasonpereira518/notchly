// SPDX-License-Identifier: GPL-3.0-or-later
// One Islet per user session. A second launch forwards its request ("settings") to the first
// over a named pipe and exits, as upstream's ReopenRequestSupport does with AppKit reopen events.
using System.IO;
using System.IO.Pipes;
using Islet.Core;
using Serilog;

namespace Islet.App.Platform.Shell;

public sealed class SingleInstance : IDisposable
{
    // Stable across processes (string.GetHashCode is randomized per process): the session id.
    private static string Scope => System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString();
    private static string MutexName => $@"Local\{AppIdentity.Id}.{Scope}";
    private static string PipeName => $"{AppIdentity.Id}.{Scope}";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancel = new();

    public event Action<string>? RequestReceived;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _ = ListenAsync(_cancel.Token);
    }

    /// <summary>The instance guard, or null when Islet is already running.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        if (created) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public static void SendToRunningInstance(string request)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client);
            writer.WriteLine(request);
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            Log.Warning(e, "Could not reach the running instance");
        }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                                                                   PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(token);
                if (!string.IsNullOrWhiteSpace(line)) RequestReceived?.Invoke(line.Trim());
            }
            catch (OperationCanceledException) { return; }
            catch (IOException e) { Log.Warning(e, "Instance pipe error"); }
        }
    }

    public void Dispose()
    {
        _cancel.Cancel();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
