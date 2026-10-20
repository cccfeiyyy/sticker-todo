using System.Security.Cryptography;
using System.Text;

namespace TodoSticker.Services;

/// <summary>
/// 单实例管理：同一数据目录只允许一个实例；后续启动会唤醒已有实例并让它显示窗口。
/// 通过命名 Mutex + 命名事件实现（Local 会话级）。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string BaseName = "StickerTodo.SingleInstance";

    private Mutex? _mutex;
    private EventWaitHandle? _signal;
    private RegisteredWaitHandle? _wait;
    private bool _disposed;

    public SingleInstance(string identity)
    {
        string key = string.IsNullOrWhiteSpace(identity) ? "default" : identity.Trim().ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];
        string mutexName = $"Local\\{BaseName}.{hash}";
        string eventName = $"Local\\{BaseName}.Signal.{hash}";

        bool createdNew;
        try
        {
            _mutex = new Mutex(initiallyOwned: true, mutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // 另一用户/另一权限级别已创建同名对象，视为已有实例。
            createdNew = false;
        }

        IsFirstInstance = createdNew;
        if (!createdNew)
        {
            SignalExistingInstance(eventName);
            _mutex?.Dispose();
            _mutex = null;
            return;
        }

        try
        {
            _signal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, eventName, out _);
            _wait = ThreadPool.RegisterWaitForSingleObject(
                _signal,
                static (_, _) =>
                {
                    try
                    {
                        ActivationRequested?.Invoke();
                    }
                    catch
                    {
                        // 忽略唤醒回调异常。
                    }
                },
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch
        {
            // 事件创建失败时只是无法被二次启动唤醒，不影响主功能。
        }
    }

    /// <summary>是否是第一个实例（true 表示应由本实例运行）。</summary>
    public bool IsFirstInstance { get; }

    /// <summary>另一个实例启动并请求显示窗口（线程池线程，请注意调度到 UI 线程）。</summary>
    public static event Action? ActivationRequested;

    private static void SignalExistingInstance(string eventName)
    {
        // 首个实例可能刚刚启动、事件对象还没建好，短暂重试。
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(eventName, out EventWaitHandle? existing))
                {
                    using (existing)
                    {
                        existing.Set();
                    }

                    return;
                }
            }
            catch
            {
                // 继续重试。
            }

            Thread.Sleep(50);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _wait?.Unregister(null);
        }
        catch
        {
            // 忽略。
        }

        _signal?.Dispose();
        _signal = null;
        _wait = null;

        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // 忽略：未持有则释放会抛异常。
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }
}
