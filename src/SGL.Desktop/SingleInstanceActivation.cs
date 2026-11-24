using System.IO.Pipes;
using System.Text;
namespace SGL.Desktop;

sealed class SingleInstanceActivation : IDisposable
{
    readonly CancellationTokenSource cancellation = new();
    readonly Mutex mutex;
    readonly string pipe;
    public bool IsFirst { get; }
    public SingleInstanceActivation(string dataRoot)
    {
        string key = SGL.Core.Json.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToUpperInvariant()))[..24];
        pipe = "SGL-" + key;
        mutex = new Mutex(true, "Local\\" + pipe, out bool created);
        IsFirst = created;
    }
    public async Task Forward()
    {
        using var connection = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await connection.ConnectAsync(3000);
        using var writer = new StreamWriter(connection, new UTF8Encoding(false));
        await writer.WriteLineAsync("");
        await writer.FlushAsync();
    }
    public async Task Listen(Action activate)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellation.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                byte[] bytes = new byte[4096];
                int count = 0;
                while (count < bytes.Length)
                {
                    int read = await server.ReadAsync(bytes.AsMemory(count), deadline.Token);
                    if (read == 0) break;
                    count += read;
                    if (Array.IndexOf(bytes, (byte)'\n', 0, count) >= 0) break;
                }
                // The pipe only raises the existing window. It accepts no links or commands.
                if (count < bytes.Length && string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(bytes, 0, count))) activate();
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }
    public void Dispose()
    {
        cancellation.Cancel();
        // Listen may still be finishing its canceled asynchronous wait.
        if (IsFirst) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
