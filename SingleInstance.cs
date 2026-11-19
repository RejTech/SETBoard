using System.IO.Pipes;

namespace SETBoard;

/// <summary>
/// 单实例控制：首个实例持有互斥体并监听管道，
/// 后续实例据此唤醒已有实例或直接退出。
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = "SETBoard.SingleInstance";
    private const string PipeName = "SETBoard.Restore";
    private const string RestoreCommand = "restore";

    private static Mutex? _mutex;

    /// <summary>尝试成为首个实例；已有实例在运行时返回 false。</summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        return createdNew;
    }

    /// <summary>在首个实例中监听，收到恢复请求时在 UI 线程回调。</summary>
    public static void StartListener(Action onRestore)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();

                    using var reader = new StreamReader(server);
                    string? message = await reader.ReadLineAsync();
                    if (message == RestoreCommand)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(onRestore);
                    }
                }
                catch
                {
                    // 单次连接失败不影响后续监听
                }
            }
        });
    }

    /// <summary>通知已有实例恢复白板主界面。</summary>
    public static void NotifyRestore()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(RestoreCommand);
        }
        catch
        {
            // 已有实例未响应时静默退出
        }
    }
}
