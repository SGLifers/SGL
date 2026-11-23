using System.IO.Pipes;
using System.Text;
using Microsoft.Win32;
namespace SGL.Desktop;

sealed class SharedLinkActivation : IDisposable
{
    readonly CancellationTokenSource cancellation = new();
    readonly Mutex mutex;
    readonly string pipe;
    public bool IsFirst
    {
        get;
    }
    public SharedLinkActivation(string dataRoot)
    {
        string key = SGL.Core.Json.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToUpperInvariant()))[..24];
        pipe = "SGL-" + key;
        mutex = new Mutex(true, "Local\\" + pipe, out bool created);
        IsFirst = created;
    }
    public async Task Forward(string message)
    {
        using var connection = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await connection.ConnectAsync(3000);
        using var writer = new StreamWriter(connection, new UTF8Encoding(false));
        await writer.WriteLineAsync(message);
    }
    public async Task Listen(Action<string> receive)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellation.Token);
                // One small URI per connection; no credentials or arbitrary commands.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                byte[] bytes = new byte[4096];
                int count = 0;
                while (count < bytes.Length)
                {
                    int read = await server.ReadAsync(bytes.AsMemory(count), deadline.Token);
                    if (read == 0)
                        break;
                    count += read;
                    if (Array.IndexOf(bytes, (byte)'\n', 0, count) >= 0)
                        break;
                }
                if (count == bytes.Length)
                    continue;
                string message = Encoding.UTF8.GetString(bytes, 0, count).Trim();
                if (message.Length == 0 || message.StartsWith("sgl://library/install?", StringComparison.Ordinal))
                    receive(message);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }
    public static void RegisterProtocol()
    {
        string executable = Environment.ProcessPath ?? throw new IOException("Cannot locate SGL.exe.");
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\sgl");
        key.SetValue("", "URL:SGL shared library");
        key.SetValue("URL Protocol", "");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", '"' + executable + "\" --shared-link \"%1\"");
    }
    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
        if (IsFirst)
            mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
