using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TodoSticker.Services;

/// <summary>窗口在桌面上的归属模式。</summary>
public enum DesktopPinMode
{
    /// <summary>普通顶层窗口（不进任务栏），随其他窗口正常层叠。</summary>
    Normal,

    /// <summary>已嵌入 Explorer 桌面层（WorkerW），像贴纸一样粘在桌面上，不置顶。</summary>
    Desktop,

    /// <summary>嵌入失败，退化为普通窗口。</summary>
    Failed,
}

/// <summary>屏幕物理像素矩形。</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// 桌面层嵌入服务：把 WPF 窗口挂到 Explorer 的桌面窗口（WorkerW / Progman）上，
/// 使窗口成为桌面的“贴纸”——显示在壁纸之上、普通窗口之下，不置顶、不进任务栏。
///
/// 实现要点（Win11 26100 / 24H2 实测）：
///  * 新版 Win11 的壁纸宿主 WorkerW 是 Progman 的**子窗口**（Win10 时代是 Progman 的兄弟顶层窗口），
///    因此查找顺序为：Progman 的子 WorkerW → 经典顶层 WorkerW → Progman 兜底。
///  * 先 SendMessage(Progman, 0x052C) 让外壳拆分出壁纸层，再重新枚举。
///  * 嵌入时保存原始样式，改为 WS_CHILD + WS_EX_TOOLWINDOW，去掉标题栏/边框；
///    Unpin 时还原，并保证始终没有 WS_EX_TOPMOST。
///  * 所有坐标 API 均为屏幕物理像素（进程需 PerMonitorV2，WPF 主程序 app.manifest 已配置）。
///  * Explorer 重启：本服务内部维护一个隐藏顶层窗口接收 TaskbarCreated 广播，并/或定时自检，
///    在宿主失效时自动重嵌（Reattach），同时触发 <see cref="ShellRestarted"/> 供上层处理意外情况。
/// </summary>
public sealed class DesktopPin : IDisposable
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;

    private const long WS_CHILD = 0x40000000L;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_CLIPSIBLINGS = 0x04000000L;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_MINIMIZEBOX = 0x00020000L;
    private const long WS_MAXIMIZEBOX = 0x00010000L;
    private const long WS_SYSMENU = 0x00080000L;

    private const long WS_EX_TOPMOST = 0x00000008L;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;
    private const long WS_EX_LAYERED = 0x00080000L;

    private const uint LWA_ALPHA = 0x00000002;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_SHOWNA = 8;
    private const int SW_RESTORE = 9;

    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private const int GW_CHILD = 5;
    private const int GW_HWNDNEXT = 2;
    private const int GW_HWNDPREV = 3;

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;

    private const uint PROGMAN_SPAWN_WORKERW = 0x052C;

    private readonly Window _window;

    private IntPtr _hwnd;
    private IntPtr _host;
    private IntPtr _savedParent;
    private long _savedStyle;
    private long _savedExStyle;
    private bool _stylesSaved;
    private bool _disposed;
    private bool _closeRequested;
    private bool _windowHooked;

    /// <summary>最近一次已知的窗口屏幕物理像素矩形（用于重嵌后复位）。</summary>
    private PixelRect _lastBounds;

    private HwndSource? _watchSource;
    private HwndSource? _windowSource;
    private HwndSource? _focusProxy;
    private int _taskbarCreatedMessage;
    private DispatcherTimer? _shellWatchTimer;
    private bool _shellRestartNotified;

    public DesktopPin(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _window.Closed += OnWindowClosed;
    }

    /// <summary>窗口句柄（会确保窗口句柄已创建；窗口已销毁时返回 0）。</summary>
    public IntPtr Handle
    {
        get
        {
            if (_hwnd != IntPtr.Zero && Native.IsWindow(_hwnd)) return _hwnd;
            try
            {
                _hwnd = new WindowInteropHelper(_window).EnsureHandle();
            }
            catch
            {
                _hwnd = IntPtr.Zero;
            }
            return _hwnd;
        }
    }

    /// <summary>当前模式。</summary>
    public DesktopPinMode Mode { get; private set; } = DesktopPinMode.Normal;

    /// <summary>是否已嵌入桌面层。</summary>
    public bool IsPinned => Mode == DesktopPinMode.Desktop;

    /// <summary>
    /// 当前环境是否支持“可交互”的桌面层嵌入。
    ///
    /// 本机实测（Windows 11 build 26100 / 24H2，Progman 子窗口 WorkerW 结构 + Wallpaper Engine）返回
    /// <c>false</c>：WorkerW/Progman 子窗口会被全屏的 SHELLDLL_DefView 拦截鼠标（点击永远落到
    /// SysListView32），键盘焦点也无法进入本进程；且 WPF AllowsTransparency(layered) 子窗口屏幕
    /// 不会被合成（PrintWindow 有内容但屏幕上不可见）。
    ///
    /// 因此：带交互的窗口（本待办清单）请不要调用 <see cref="PinToDesktop"/>，改用
    /// “普通顶层窗口 + <see cref="PlaceAboveDesktop"/>”的退化方案；<see cref="PinToDesktop"/> 保留给
    /// 纯展示场景/可能可用的系统环境。
    /// </summary>
    public static bool InteractiveEmbeddingSupported => false;

    /// <summary>当前使用的桌面层宿主窗口句柄（未嵌入时为 0，调试/测试用）。</summary>
    public IntPtr DesktopHostHandle => _host;

    /// <summary>模式变化通知。</summary>
    public event EventHandler<DesktopPinMode>? ModeChanged;

    /// <summary>
    /// Explorer/外壳重启后触发（在 UI 线程）。触发前本服务已尝试自动重嵌，
    /// 上层可用 <see cref="Mode"/> / <see cref="Handle"/> 判断结果，必要时重建窗口。
    /// </summary>
    public event EventHandler? ShellRestarted;

    /// <summary>
    /// 尝试嵌入桌面层。成功返回 true；技术上不可用时返回 false（不抛异常）。
    /// 调用前窗口应已 Show 且句柄已创建。
    ///
    /// 注意：本机 Win11 26100 + Wallpaper Engine 环境实测为 <b>display-only</b>——
    /// 窗口会成为 WorkerW/Progman 的子窗口（父子关系/样式/位置都正确），但会被 SHELLDLL_DefView
    /// 拦截鼠标、键盘焦点无法进入，且 WPF layered 子窗口屏幕不被合成。带交互的窗口请改用
    /// “普通顶层窗口 + <see cref="PlaceAboveDesktop"/>”，并用
    /// <see cref="InteractiveEmbeddingSupported"/> 判断。
    /// </summary>
    public bool PinToDesktop()
    {
        if (_disposed) return false;

        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
        {
            SetMode(DesktopPinMode.Failed);
            return false;
        }

        // 已经嵌在有效宿主上：直接复用
        if (Mode == DesktopPinMode.Desktop && _host != IntPtr.Zero &&
            Native.IsWindow(_host) && Native.GetParent(hwnd) == _host)
        {
            EnsureWindowHook(hwnd);
            EnsureShellWatch();
            return true;
        }

        SaveWindowState(hwnd);

        IntPtr host = ResolveDesktopHostWithRetry();
        if (host == IntPtr.Zero)
        {
            SetMode(DesktopPinMode.Failed);
            return false;
        }

        if (!Attach(hwnd, host))
        {
            // 恢复普通窗口，避免停在半嵌入状态
            Detach(hwnd, hostWasValid: false);
            SetMode(DesktopPinMode.Failed);
            return false;
        }

        _host = host;
        EnsureWindowHook(hwnd);
        EnsureShellWatch();
        _shellRestartNotified = false;
        SetMode(DesktopPinMode.Desktop);
        return true;
    }

    /// <summary>解除桌面层嵌入，恢复普通顶层窗口（不置顶）。</summary>
    public void Unpin()
    {
        if (_disposed)
        {
            return;
        }

        IntPtr hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
        {
            _host = IntPtr.Zero;
            SetMode(DesktopPinMode.Normal);
            return;
        }

        if (_stylesSaved)
        {
            Detach(hwnd, hostWasValid: _host != IntPtr.Zero && Native.IsWindow(_host));
        }

        _host = IntPtr.Zero;
        SetMode(DesktopPinMode.Normal);
    }

    /// <summary>
    /// Explorer 重启后重新查找桌面层宿主并重新嵌入（会先把窗口还原为普通窗口再重新嵌入）。
    /// 返回重新嵌入是否成功；窗口已销毁或从未嵌入过时返回 false。
    /// </summary>
    public bool Reattach()
    {
        if (_disposed) return false;
        if (!_stylesSaved) return false;

        IntPtr hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
        {
            _host = IntPtr.Zero;
            return false;
        }

        // 先把窗口恢复到原始顶层状态（旧宿主可能已经销毁）
        Detach(hwnd, hostWasValid: false);
        _host = IntPtr.Zero;

        IntPtr host = ResolveDesktopHostWithRetry();
        if (host == IntPtr.Zero || !Attach(hwnd, host))
        {
            SetMode(DesktopPinMode.Failed);
            return false;
        }

        _host = host;
        EnsureWindowHook(hwnd);
        EnsureShellWatch();
        SetMode(DesktopPinMode.Desktop);
        return true;
    }

    /// <summary>设置窗口位置/大小（屏幕物理像素）。嵌入状态下也能正确定位。</summary>
    public void SetBounds(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        // 用 Handle 属性确保句柄已创建（_hwnd 只在 Handle getter 里懒初始化）
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;

        _lastBounds = new PixelRect(x, y, width, height);

        int px = x;
        int py = y;
        if (Mode == DesktopPinMode.Desktop && _host != IntPtr.Zero)
        {
            if (Native.GetClientOrigin(_host, out int ox, out int oy))
            {
                px = x - ox;
                py = y - oy;
            }
        }

        Native.SetWindowPos(hwnd, IntPtr.Zero, px, py, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>读取窗口当前屏幕物理像素位置/大小。</summary>
    public bool TryGetBounds(out PixelRect bounds)
    {
        bounds = default;
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
        if (!Native.GetWindowRect(hwnd, out Native.RECT r)) return false;
        bounds = new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        _lastBounds = bounds;
        return true;
    }

    /// <summary>显示窗口但不抢焦点。</summary>
    public void ShowWithoutActivate()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;
        Native.ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>把窗口提到最前（不使用 WS_EX_TOPMOST）。</summary>
    public void BringToFront()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;
        Native.SetWindowPos(hwnd, Native.HWND_TOP, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>把窗口压到所有普通窗口之下（不抢焦点），但仍显示在桌面之上。</summary>
    public void SendToBottom()
    {
        if (_disposed) return;
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;

        if (Mode != DesktopPinMode.Desktop && PlaceAboveDesktop())
        {
            return;
        }

        Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 把窗口放到“桌面之上、所有普通窗口之下”（插到 Progman 正上方，不抢焦点、不置顶）。
    /// 即 Rainmeter “On Desktop” 式压底：窗口保持普通顶层窗口（可点击、可输入），
    /// 只是在 Z 序上紧贴桌面壁纸层之上；其他普通窗口会自然盖住它。
    /// 未嵌入桌面层且窗口句柄有效时返回 true。
    /// </summary>
    public bool PlaceAboveDesktop()
    {
        if (_disposed) return false;
        if (Mode == DesktopPinMode.Desktop) return false;

        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;

        IntPtr progman = Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            IntPtr shell = Native.GetShellWindow();
            if (shell != IntPtr.Zero && Native.ClassNameOf(shell) == "Progman")
            {
                progman = shell;
            }
        }

        if (progman == IntPtr.Zero || !Native.IsWindow(progman))
        {
            // 找不到 Progman 时退回 HWND_BOTTOM（少数环境下可能沉到桌面之下，故仅兜底）
            Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            return true;
        }

        if (Native.GetWindow(progman, GW_HWNDPREV) == hwnd)
        {
            return true;
        }

        Native.SetWindowPos(hwnd, Native.GetWindow(progman, GW_HWNDPREV), 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        return true;
    }

    /// <summary>当前鼠标位置（屏幕物理像素）。</summary>
    public static (int X, int Y) GetCursorPosPx()
    {
        return Native.GetCursorPos(out Native.POINT p) ? (p.X, p.Y) : (0, 0);
    }

    /// <summary>
    /// 让嵌入桌面的窗口获得键盘输入焦点（嵌入为子窗口后，系统的前台窗口是其顶层祖先 Progman，
    /// 普通点击无法把键盘焦点交给本进程）。上层可在窗口收到鼠标按下时调用本方法；
    /// 非嵌入状态下等价于 Activate。
    /// </summary>
    public void ActivateForInput()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;

        if (Mode != DesktopPinMode.Desktop)
        {
            try { _window.Activate(); } catch { }
            return;
        }

        EnsureFocusProxy();
        if (_focusProxy != null)
        {
            Native.SetForegroundWindow(_focusProxy.Handle);
        }
        Native.SetFocus(hwnd);
    }

    /// <summary>窗口是否带有 WS_EX_TOPMOST 样式。</summary>
    public static bool IsTopMost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
        long ex = Native.GetWindowLongValue(hwnd, GWL_EXSTYLE);
        return (ex & WS_EX_TOPMOST) != 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            Unpin();
        }
        catch
        {
            // Dispose 不抛异常
        }

        _window.Closed -= OnWindowClosed;

        if (_windowSource != null && _windowHooked)
        {
            try { _windowSource.RemoveHook(WindowHook); } catch { }
            _windowHooked = false;
        }
        _windowSource = null;

        StopShellWatch();
        DisposeFocusProxy();
        _host = IntPtr.Zero;
        _hwnd = IntPtr.Zero;
        ModeChanged = null;
        ShellRestarted = null;
    }

    // ------------------------------------------------------------------
    // 嵌入/还原
    // ------------------------------------------------------------------

    private bool Attach(IntPtr hwnd, IntPtr host)
    {
        // 记录当前屏幕矩形，重嵌后按原位置恢复
        if (Native.GetWindowRect(hwnd, out Native.RECT rect))
        {
            _lastBounds = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        long style = Native.GetWindowLongValue(hwnd, GWL_STYLE);
        long exStyle = Native.GetWindowLongValue(hwnd, GWL_EXSTYLE);
        if (!_stylesSaved)
        {
            _savedStyle = style;
            _savedExStyle = exStyle;
            _savedParent = Native.GetParent(hwnd);
            _stylesSaved = true;
        }

        long newStyle = (style | WS_CHILD | WS_CLIPSIBLINGS)
                        & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        long newExStyle = (exStyle | WS_EX_TOOLWINDOW) & ~(WS_EX_APPWINDOW | WS_EX_TOPMOST);

        Native.SetWindowLongValue(hwnd, GWL_STYLE, newStyle);
        Native.SetWindowLongValue(hwnd, GWL_EXSTYLE, newExStyle);

        Native.SetParent(hwnd, host);
        if (Native.GetParent(hwnd) != host)
        {
            // SetParent 失败：还原样式（位置不动）
            Native.SetWindowLongValue(hwnd, GWL_STYLE, style);
            Native.SetWindowLongValue(hwnd, GWL_EXSTYLE, exStyle);
            return false;
        }

        // 变成子窗口后位置坐标变为相对父客户区：用屏幕坐标换算后重新定位到最上层兄弟
        PixelRect b = _lastBounds;
        int px = b.X;
        int py = b.Y;
        if (Native.GetClientOrigin(host, out int ox, out int oy))
        {
            px = b.X - ox;
            py = b.Y - oy;
        }

        uint flags = SWP_NOACTIVATE | SWP_FRAMECHANGED;
        if (Native.IsWindowVisible(hwnd)) flags |= SWP_SHOWWINDOW;
        Native.SetWindowPos(hwnd, Native.HWND_TOP, px, py, Math.Max(1, b.Width), Math.Max(1, b.Height), flags);

        // 校验：子窗口 GetWindowRect 仍是屏幕坐标；若与期望不符则视为失败
        if (Native.GetWindowRect(hwnd, out Native.RECT after))
        {
            int w = after.Right - after.Left;
            int h = after.Bottom - after.Top;
            if (w != b.Width || h != b.Height)
            {
                // WPF 可能按 DIP 重新布局导致尺寸偏差，再强制一次
                Native.SetWindowPos(hwnd, IntPtr.Zero, px, py, Math.Max(1, b.Width), Math.Max(1, b.Height),
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        return true;
    }

    private void Detach(IntPtr hwnd, bool hostWasValid)
    {
        // 还原屏幕矩形（SetParent 回桌面后坐标语义变化）
        PixelRect bounds = _lastBounds;
        if (Native.GetWindowRect(hwnd, out Native.RECT rect))
        {
            bounds = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        Native.SetParent(hwnd, _savedParent);

        long style = _savedStyle;
        // Normal 模式语义：普通顶层窗口（不进任务栏）→ 保留 TOOLWINDOW，永不带 TOPMOST
        long exStyle = (_savedExStyle | WS_EX_TOOLWINDOW) & ~(WS_EX_TOPMOST | WS_EX_APPWINDOW);
        Native.SetWindowLongValue(hwnd, GWL_STYLE, style);
        Native.SetWindowLongValue(hwnd, GWL_EXSTYLE, exStyle);

        Native.SetWindowPos(hwnd, Native.HWND_TOP, bounds.X, bounds.Y,
            Math.Max(1, bounds.Width), Math.Max(1, bounds.Height),
            SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

        _stylesSaved = false;
        _savedParent = IntPtr.Zero;
    }

    private void SaveWindowState(IntPtr hwnd)
    {
        if (_stylesSaved) return;
        _savedStyle = Native.GetWindowLongValue(hwnd, GWL_STYLE);
        _savedExStyle = Native.GetWindowLongValue(hwnd, GWL_EXSTYLE);
        _savedParent = Native.GetParent(hwnd);
        _stylesSaved = true;
        if (Native.GetWindowRect(hwnd, out Native.RECT r))
        {
            _lastBounds = new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
    }

    // ------------------------------------------------------------------
    // 桌面层宿主查找
    // ------------------------------------------------------------------

    /// <summary>
    /// 查找可承载贴纸的桌面层窗口：优先新版 Win11 的 Progman 子 WorkerW（壁纸宿主），
    /// 其次经典 Win10 风格的顶层 WorkerW，最后 Progman 兜底（窗口会盖住桌面图标）。
    /// </summary>
    internal static IntPtr ResolveDesktopHost()
    {
        IntPtr progman = Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            IntPtr shell = Native.GetShellWindow();
            if (shell != IntPtr.Zero && Native.ClassNameOf(shell) == "Progman")
            {
                progman = shell;
            }
        }

        if (progman != IntPtr.Zero && Native.IsWindow(progman))
        {
            // 让外壳把壁纸层拆出来（若已拆分则无害；这是 WorkerW 经典技巧的入口）
            Native.SendMessageTimeout(progman, PROGMAN_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero,
                SMTO_ABORTIFHUNG, 1000, out _);
        }

        IntPtr host = FindWorkerWChildOf(progman);
        if (host != IntPtr.Zero) return host;

        host = FindClassicWallpaperWorkerW();
        if (host != IntPtr.Zero) return host;

        // 兜底：Progman 本身（只在它确实是桌面层、含 SHELLDLL_DefView 时用）
        if (progman != IntPtr.Zero && Native.IsWindow(progman) &&
            Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
        {
            return progman;
        }

        return IntPtr.Zero;
    }

    /// <summary>新版 Win11（24H2+）：壁纸宿主 WorkerW 是 Progman 的直接子窗口且覆盖主屏。</summary>
    private static IntPtr FindWorkerWChildOf(IntPtr parent)
    {
        if (parent == IntPtr.Zero) return IntPtr.Zero;

        int screenW = Native.GetSystemMetrics(SM_CXSCREEN);
        int screenH = Native.GetSystemMetrics(SM_CYSCREEN);
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;

        IntPtr child = Native.GetWindow(parent, GW_CHILD);
        while (child != IntPtr.Zero)
        {
            if (Native.ClassNameOf(child) == "WorkerW" && Native.IsWindowVisible(child))
            {
                if (Native.GetWindowRect(child, out Native.RECT r))
                {
                    int w = r.Right - r.Left;
                    int h = r.Bottom - r.Top;
                    long area = (long)w * h;
                    if (w >= screenW / 2 && h >= screenH / 2 && area > bestArea)
                    {
                        best = child;
                        bestArea = area;
                    }
                }
            }
            child = Native.GetWindow(child, GW_HWNDNEXT);
        }
        return best;
    }

    /// <summary>经典结构（Win10 / 早期 Win11）：顶层 WorkerW 兄弟窗口作为壁纸宿主。</summary>
    private static IntPtr FindClassicWallpaperWorkerW()
    {
        var tops = new List<IntPtr>(256);
        Native.EnumWindows((h, _) => { tops.Add(h); return true; }, IntPtr.Zero);

        int screenW = Native.GetSystemMetrics(SM_CXSCREEN);
        int screenH = Native.GetSystemMetrics(SM_CYSCREEN);

        int defViewIndex = -1;
        for (int i = 0; i < tops.Count; i++)
        {
            if (Native.FindWindowEx(tops[i], IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                defViewIndex = i;
                break;
            }
        }

        bool BigEnough(IntPtr h)
        {
            if (!Native.GetWindowRect(h, out Native.RECT r)) return false;
            return (r.Right - r.Left) >= screenW / 2 && (r.Bottom - r.Top) >= screenH / 2;
        }

        // z 序中位于“桌面图标宿主”之后（更靠下）的第一个大号可见 WorkerW
        if (defViewIndex >= 0)
        {
            for (int j = defViewIndex + 1; j < tops.Count; j++)
            {
                if (Native.ClassNameOf(tops[j]) == "WorkerW" && Native.IsWindowVisible(tops[j]) && BigEnough(tops[j]))
                {
                    return tops[j];
                }
            }
        }

        // 兜底：任意大号可见顶层 WorkerW
        for (int j = 0; j < tops.Count; j++)
        {
            if (Native.ClassNameOf(tops[j]) == "WorkerW" && Native.IsWindowVisible(tops[j]) && BigEnough(tops[j]))
            {
                return tops[j];
            }
        }

        return IntPtr.Zero;
    }

    private static IntPtr ResolveDesktopHostWithRetry()
    {
        // 0x052C 之后外壳可能异步创建 WorkerW，做短暂重试；仅在失败路径上等待（最多约 0.5s）
        for (int attempt = 0; attempt < 6; attempt++)
        {
            IntPtr host = ResolveDesktopHost();
            if (host != IntPtr.Zero) return host;
            Thread.Sleep(80);
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------
    // Explorer 重启检测 / 自动重嵌
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // 键盘焦点代理（子窗口无法成为前台窗口，用一个 1x1 全透明顶层窗口承接前台/焦点）
    // ------------------------------------------------------------------

    private void EnsureFocusProxy()
    {
        if (_focusProxy != null) return;
        try
        {
            var p = new HwndSourceParameters("StickerTodo.DesktopPin.FocusProxy")
            {
                Width = 1,
                Height = 1,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = unchecked((int)(WS_POPUP | WS_VISIBLE)),
                ExtendedWindowStyle = unchecked((int)(WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT)),
            };
            _focusProxy = new HwndSource(p);
            Native.SetLayeredWindowAttributes(_focusProxy.Handle, 0, 0, LWA_ALPHA);
        }
        catch
        {
            _focusProxy = null;
        }
    }

    private void DisposeFocusProxy()
    {
        if (_focusProxy == null) return;
        try { _focusProxy.Dispose(); } catch { }
        _focusProxy = null;
    }

    private void EnsureShellWatch()
    {        if (_watchSource != null) return;

        if (_taskbarCreatedMessage == 0)
        {
            _taskbarCreatedMessage = unchecked((int)Native.RegisterWindowMessage("TaskbarCreated"));
        }

        try
        {
            var p = new HwndSourceParameters("StickerTodo.DesktopPin.ShellWatch")
            {
                Width = 0,
                Height = 0,
                PositionX = -32000,
                PositionY = -32000,
                WindowStyle = unchecked((int)WS_POPUP),
                ExtendedWindowStyle = unchecked((int)(WS_EX_TOOLWINDOW)),
            };
            _watchSource = new HwndSource(p);
            _watchSource.AddHook(ShellWatchHook);
        }
        catch
        {
            _watchSource = null;
        }

        if (_shellWatchTimer == null && _window.Dispatcher != null)
        {
            _shellWatchTimer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(2),
            };
            _shellWatchTimer.Tick += ShellWatchTimerTick;
            _shellWatchTimer.Start();
        }
    }

    private void StopShellWatch()
    {
        if (_shellWatchTimer != null)
        {
            _shellWatchTimer.Stop();
            _shellWatchTimer.Tick -= ShellWatchTimerTick;
            _shellWatchTimer = null;
        }

        if (_watchSource != null)
        {
            _watchSource.RemoveHook(ShellWatchHook);
            try { _watchSource.Dispose(); } catch { }
            _watchSource = null;
        }
    }

    private IntPtr ShellWatchHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
        {
            BeginShellRestartRecovery();
        }
        return IntPtr.Zero;
    }

    private void ShellWatchTimerTick(object? sender, EventArgs e)
    {
        if (_disposed || Mode != DesktopPinMode.Desktop) return;
        IntPtr hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
        {
            // 窗口已随旧宿主销毁：停止自检，交给 ShellRestarted 事件（若有）通知上层
            BeginShellRestartRecovery();
            return;
        }
        if (_host == IntPtr.Zero || !Native.IsWindow(_host) || Native.GetParent(hwnd) != _host)
        {
            BeginShellRestartRecovery();
        }
    }

    private void BeginShellRestartRecovery()
    {
        if (_disposed) return;
        if (_window.Dispatcher == null || _window.Dispatcher.HasShutdownStarted) return;

        if (!_window.Dispatcher.CheckAccess())
        {
            try { _window.Dispatcher.BeginInvoke(new Action(BeginShellRestartRecovery)); } catch { }
            return;
        }

        if (Mode != DesktopPinMode.Desktop) return;
        if (_shellRestartNotified) return;

        // 窗口已随旧宿主销毁：无法在本进程内恢复，立即通知上层重建窗口
        if (_hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd))
        {
            NotifyShellRestarted();
            return;
        }

        if (_host != IntPtr.Zero && Native.IsWindow(_host) && Native.GetParent(_hwnd) == _host)
        {
            // 宿主仍在，无需处理
            return;
        }

        // 异步重嵌：避免在 TaskbarCreated 广播处理中做同步等待
        var timer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        int attempts = 0;
        timer.Tick += (_, _) =>
        {
            attempts++;
            if (_disposed || Mode != DesktopPinMode.Desktop)
            {
                timer.Stop();
                return;
            }

            bool ok = Reattach();
            if (ok || attempts >= 20)
            {
                timer.Stop();
                NotifyShellRestarted();
            }
        };
        timer.Start();
    }

    private void NotifyShellRestarted()
    {
        if (_shellRestartNotified) return;
        _shellRestartNotified = true;
        try { ShellRestarted?.Invoke(this, EventArgs.Empty); } catch { }
    }

    // ------------------------------------------------------------------
    // 窗口消息钩子（WM_CLOSE / WM_DESTROY 辅助）
    // ------------------------------------------------------------------

    private void EnsureWindowHook(IntPtr hwnd)
    {
        if (_windowHooked) return;
        try
        {
            _windowSource = HwndSource.FromHwnd(hwnd);
            if (_windowSource == null) return;
            _windowSource.AddHook(WindowHook);
            _windowHooked = true;
        }
        catch
        {
            _windowSource = null;
            _windowHooked = false;
        }
    }

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLOSE)
        {
            _closeRequested = true;
        }
        else if (msg == WM_DESTROY)
        {
            // 窗口即将销毁。若是用户/程序主动 Close，则彻底清理；
            // 若是因为 Explorer/宿主销毁（未收到 WM_CLOSE），保留 ShellWatch，
            // 以便在 TaskbarCreated 到来时通过 ShellRestarted 通知上层重建窗口。
            _host = IntPtr.Zero;
            _stylesSaved = false;
            if (_closeRequested || _disposed)
            {
                StopShellWatch();
            }
        }
        return IntPtr.Zero;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _hwnd = IntPtr.Zero;
        _host = IntPtr.Zero;
        _stylesSaved = false;
        // 主动关闭/释放时停止监听；被 Explorer 波及销毁时保留，等 ShellRestarted 通知上层
        if (_closeRequested || _disposed)
        {
            StopShellWatch();
        }
    }

    // ------------------------------------------------------------------
    // 模式通知
    // ------------------------------------------------------------------

    private void SetMode(DesktopPinMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        try { ModeChanged?.Invoke(this, mode); } catch { }
    }

    // ------------------------------------------------------------------
    // Win32
    // ------------------------------------------------------------------

    private static class Native
    {
        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public static readonly IntPtr HWND_BOTTOM = new(1);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string? className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public static long GetWindowLongValue(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex).ToInt64()
                : GetWindowLong32(hWnd, nIndex);
        }

        public static void SetWindowLongValue(IntPtr hWnd, int nIndex, long value)
        {
            if (IntPtr.Size == 8)
            {
                SetWindowLongPtr64(hWnd, nIndex, new IntPtr(value));
            }
            else
            {
                SetWindowLong32(hWnd, nIndex, unchecked((int)value));
            }
        }

        public static string ClassNameOf(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return string.Empty;
            var sb = new StringBuilder(256);
            return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
        }

        public static bool GetClientOrigin(IntPtr hWnd, out int x, out int y)
        {
            var pt = new POINT { X = 0, Y = 0 };
            if (ClientToScreen(hWnd, ref pt))
            {
                x = pt.X;
                y = pt.Y;
                return true;
            }
            if (GetWindowRect(hWnd, out RECT r))
            {
                x = r.Left;
                y = r.Top;
                return true;
            }
            x = 0;
            y = 0;
            return false;
        }
    }
}
