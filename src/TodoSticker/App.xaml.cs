using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TodoSticker.Services;

namespace TodoSticker;

/// <summary>应用入口：单实例、托盘、窗口生命周期、Explorer 重启恢复、全局异常处理。</summary>
public partial class App : Application
{
    private TaskStore? _store;
    private SingleInstance? _singleInstance;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private HwndSource? _messageWindow;
    private uint _taskbarCreatedMessage;
    private bool _shuttingDown;
    private bool _errorDialogShown;

    /// <summary>当前应用实例。</summary>
    public static App Instance => (App)Current;

    /// <summary>数据存储。</summary>
    public TaskStore Store => _store ?? throw new InvalidOperationException("数据存储尚未初始化");

    /// <summary>启动参数。</summary>
    public StartupOptions Options { get; private set; } = new();

    /// <summary>是否正在退出。</summary>
    public bool IsShuttingDown => _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Options = StartupOptions.Parse(e.Args);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        try
        {
            _store = new TaskStore(Options.DataDirectory);
            _singleInstance = new SingleInstance(Options.DataDirectory);

            if (!_singleInstance.IsFirstInstance)
            {
                // 已有实例：唤醒它，然后结束本次启动。
                Shutdown();
                return;
            }

            SingleInstance.ActivationRequested += OnActivationRequested;

            CreateMessageWindow();
            CreateTray();
            CreateMainWindow();
            UpdateTrayState();
        }
        catch (Exception ex)
        {
            LogError(ex, "启动失败");
            MessageBox.Show(
                $"贴纸待办启动失败：{ex.Message}\n\n可以尝试删除数据目录后重新启动：\n{Options.DataDirectory}",
                "贴纸待办",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    // ================= 窗口 =================

    private void CreateMainWindow()
    {
        MainWindow window = new(Store, Options.NoPin);
        _window = window;
        window.UiStateChanged += (_, _) => UpdateTrayState();
        window.ExitRequested += (_, _) => RequestExit();
        window.Closed += OnMainWindowClosed;

        // 开机自启时不抢焦点；手动启动时正常激活窗口。
        window.ShowActivated = !Options.AutoStart;
        window.Show();
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Closed -= OnMainWindowClosed;
        }

        _window = null;
        UpdateTrayState();

        // 窗口关闭但应用仍在托盘运行：重新建一个（例如 Explorer 重启导致句柄失效）。
        if (!_shuttingDown && !Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_shuttingDown && _window is null)
                {
                    try
                    {
                        CreateMainWindow();
                    }
                    catch (Exception ex)
                    {
                        LogError(ex, "重建主窗口失败");
                    }
                }
            }));
        }
    }

    private void EnsureMainWindow()
    {
        if (_window is null && !_shuttingDown)
        {
            CreateMainWindow();
        }
    }

    private void ShowWindowFromUser()
    {
        try
        {
            EnsureMainWindow();
            _window?.ShowAndActivate();
            UpdateTrayState();
        }
        catch (Exception ex)
        {
            LogError(ex, "显示窗口失败");
        }
    }

    private void OnActivationRequested()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShowWindowFromUser));
    }

    // ================= 托盘 =================

    private void CreateTray()
    {
        TrayIcon tray = new("贴纸待办 · 桌面待办清单");
        tray.ShowRequested += (_, _) => ShowWindowFromUser();
        tray.ToggleVisibilityRequested += (_, _) =>
        {
            if (_window is { IsVisible: true })
            {
                _window.HideToTray();
            }
            else
            {
                ShowWindowFromUser();
            }

            UpdateTrayState();
        };
        tray.PinToggleRequested += (_, _) =>
        {
            EnsureMainWindow();
            _window?.TogglePin();
            UpdateTrayState();
        };
        tray.AutoStartToggleRequested += (_, _) =>
        {
            EnsureMainWindow();
            _window?.ToggleAutoStart();
            UpdateTrayState();
        };
        tray.ClearCompletedRequested += (_, _) => _store?.ClearCompleted();
        tray.OpenDataFolderRequested += (_, _) => OpenDataFolder();
        tray.ExitRequested += (_, _) => RequestExit();
        _tray = tray;
    }

    private void UpdateTrayState()
    {
        try
        {
            bool visible = _window?.IsVisible == true;
            bool pinned = _window?.IsPinnedToDesktop == true;
            bool autoStart = AutoStart.IsEnabled();
            _tray?.UpdateState(visible, pinned, autoStart);
        }
        catch (Exception ex)
        {
            LogError(ex, "更新托盘状态失败");
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            string directory = _store?.DataDirectory ?? Options.DataDirectory;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogError(ex, "打开数据目录失败");
        }
    }

    // ================= 系统消息（Explorer 重启等） =================

    private void CreateMessageWindow()
    {
        // 隐藏的顶层窗口（非 message-only），才能收到 TaskbarCreated / WM_DISPLAYCHANGE 广播。
        HwndSourceParameters parameters = new("StickerTodo.MessageWindow")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW
            PositionX = -32000,
            PositionY = -32000,
            Width = 1,
            Height = 1,
        };

        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(MessageWindowHook);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    }

    private IntPtr MessageWindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
        {
            RunAfter(TimeSpan.FromMilliseconds(1200), RecoverAfterExplorerRestart);
        }
        else if (msg == 0x007E) // WM_DISPLAYCHANGE
        {
            RunAfter(TimeSpan.FromMilliseconds(500), () => _window?.EnsureOnScreen());
        }

        return IntPtr.Zero;
    }

    /// <summary>在 UI 线程延迟执行（Explorer 重启 / 分辨率变化后需要等系统稳定）。</summary>
    private void RunAfter(TimeSpan delay, Action action)
    {
        _ = Task.Delay(delay).ContinueWith(
            _ =>
            {
                try
                {
                    if (Dispatcher.HasShutdownStarted)
                    {
                        return;
                    }

                    Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
                }
                catch
                {
                    // 应用退出期间忽略。
                }
            },
            TaskScheduler.Default);
    }

    /// <summary>Explorer 重启后：重新挂载托盘图标，并让窗口重新沉回桌面底部。</summary>
    private void RecoverAfterExplorerRestart()
    {
        if (_shuttingDown)
        {
            return;
        }

        try
        {
            _tray?.ReAddIcon();
            _window?.ReapplyDesktopMode();
            UpdateTrayState();
        }
        catch (Exception ex)
        {
            LogError(ex, "Explorer 重启恢复失败");
        }
    }

    // ================= 退出与异常 =================

    /// <summary>请求退出应用（保存数据、清理托盘后结束进程）。</summary>
    public void RequestExit()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;

        try
        {
            _store?.SaveNow();
        }
        catch (Exception ex)
        {
            LogError(ex, "退出时保存数据失败");
        }

        try
        {
            _tray?.Dispose();
            _tray = null;
        }
        catch (Exception ex)
        {
            LogError(ex, "清理托盘失败");
        }

        try
        {
            if (_window is not null)
            {
                MainWindow window = _window;
                _window = null;
                window.Closed -= OnMainWindowClosed;
                window.PrepareForExit();
            }
        }
        catch (Exception ex)
        {
            LogError(ex, "关闭窗口失败");
        }

        try
        {
            SingleInstance.ActivationRequested -= OnActivationRequested;
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _singleInstance?.Dispose();
            _singleInstance = null;
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _messageWindow?.Dispose();
            _messageWindow = null;
        }
        catch
        {
            // 忽略。
        }

        Shutdown();
    }

    private void Cleanup()
    {
        try
        {
            _store?.SaveNow();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _tray?.Dispose();
            _tray = null;
        }
        catch
        {
            // 忽略。
        }

        try
        {
            SingleInstance.ActivationRequested -= OnActivationRequested;
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _singleInstance?.Dispose();
            _singleInstance = null;
        }
        catch
        {
            // 忽略。
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception, "未处理的界面异常");
        e.Handled = true;

        if (!_errorDialogShown && !_shuttingDown)
        {
            _errorDialogShown = true;
            MessageBox.Show(
                $"遇到一个错误，但贴纸待办会继续运行：\n{e.Exception.Message}",
                "贴纸待办",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogError(ex, "未处理的后台异常");
        }
    }

    /// <summary>把异常写入数据目录下的 error.log（永不抛出）。</summary>
    public void LogError(Exception? exception, string context)
    {
        try
        {
            string directory = _store?.DataDirectory ?? Options.DataDirectory;
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "error.log");
            string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
            if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024)
            {
                File.Move(file, file + ".old", overwrite: true);
            }

            File.AppendAllText(file, text);
        }
        catch
        {
            // 日志失败不影响主功能。
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);
}
