using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using TodoSticker.Models;
using TodoSticker.Services;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace TodoSticker;

/// <summary>贴纸待办主窗口：极简贴纸卡片 + 待办/已完成两页 + 拖动/折叠/托盘。</summary>
public partial class MainWindow : Window
{
    private const string VersionText = "1.1.1";

    private readonly TaskStore _store;
    private readonly bool _noPin;
    private readonly ContextMenu _moreMenu;

    private MenuItem _pinMenuItem = null!;
    private MenuItem _autoStartMenuItem = null!;
    private MenuItem _collapseMenuItem = null!;

    private DesktopPin? _pin;
    private double _dpiScale = 1.0;
    private bool _loaded;
    private bool _allowClose;
    private bool _collapsed;
    private bool _pinned;
    private bool _completedPage;
    private bool _dragging;
    private bool _resizing;
    private PixelRect _lastBounds;
    private PixelRect _dragStartRect;
    private (int X, int Y) _dragStartCursor;
    private (int X, int Y) _resizeStartCursor;
    private int _resizeStartWidth;
    private int _resizeStartHeight;
    private int _expandedHeight;
    private string _pendingSignature = string.Empty;
    private string _completedSignature = string.Empty;
    private string _categoryChipsSignature = string.Empty;
    private DispatcherTimer? _desktopKeeperTimer;

    // 待办任务拖拽排序
    private TodoItem? _dragItem;
    private FrameworkElement? _dragRow;
    private Point _dragStartPoint;
    private bool _dragActive;
    private List<TodoItem> _visiblePending = new();

    public MainWindow(TaskStore store, bool noPin)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _noPin = noPin;

        InitializeComponent();

        _moreMenu = BuildMoreMenu();
        MoreButton.ContextMenu = _moreMenu;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Deactivated += OnDeactivated;
        DpiChanged += (_, e) => _dpiScale = e.NewDpi.DpiScaleX;
        _store.Changed += OnStoreChanged;

        UpdateHeaderSummary();
    }

    /// <summary>窗口状态（可见/贴桌面/折叠）变化，供托盘刷新。</summary>
    public event EventHandler? UiStateChanged;

    /// <summary>用户请求退出应用。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>是否处于桌面贴纸模式（不置顶、自动压底、不进任务栏）。</summary>
    public bool IsPinnedToDesktop => _pinned;

    // ================== 初始化 ==================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _dpiScale = GetDpiScale();
        ApplyToolWindowStyle();
        _pin = new DesktopPin(this);
        _pin.ModeChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshPinUi));
        DebugLog($"SourceInitialized hwnd=0x{new WindowInteropHelper(this).Handle.ToInt64():X} dpi={_dpiScale:F2}");
    }

    /// <summary>加上 WS_EX_TOOLWINDOW：不进任务栏、不参与 Alt+Tab，更接近“桌面贴纸”。</summary>
    private void ApplyToolWindowStyle()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            long extendedStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            extendedStyle |= WsExToolWindow;
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(extendedStyle));
        }
        catch (Exception ex)
        {
            App.Instance.LogError(ex, "设置工具窗样式失败");
        }
    }

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;

        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    private static string GetWindowClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        System.Text.StringBuilder buffer = new(256);
        GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _dpiScale = GetDpiScale();

        AppSettings settings = _store.Settings;
        bool firstRun = settings.WindowX is null || settings.WindowY is null;

        int width;
        int height;
        int x;
        int y;
        if (firstRun)
        {
            width = (int)Math.Round(380 * _dpiScale);
            height = (int)Math.Round(540 * _dpiScale);
            Drawing.Rectangle workArea = WinForms.Screen.PrimaryScreen?.WorkingArea
                                         ?? new Drawing.Rectangle(0, 0, 1280, 800);
            x = workArea.Right - width - (int)Math.Round(48 * _dpiScale);
            y = workArea.Top + (int)Math.Round(96 * _dpiScale);
        }
        else
        {
            width = settings.WindowWidth;
            height = settings.WindowHeight;
            x = settings.WindowX!.Value;
            y = settings.WindowY!.Value;
        }

        _expandedHeight = height;
        _collapsed = settings.Collapsed;
        ApplyCollapsedVisibility();

        if (!_noPin && settings.PinToDesktop)
        {
            EnableDesktopStickerMode();
        }

        PixelRect bounds;
        if (_collapsed)
        {
            double collapsedDip = MeasureCollapsedHeight();
            int collapsedHeight = (int)Math.Ceiling(collapsedDip * _dpiScale);
            bounds = ClampFullyVisible(x, y, width, collapsedHeight);
        }
        else
        {
            bounds = ClampFullyVisible(x, y, width, height);
        }

        ApplyBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);

        if (firstRun)
        {
            _store.UpdateSettings(s =>
            {
                s.WindowX = bounds.X;
                s.WindowY = bounds.Y;
                s.WindowWidth = width;
                s.WindowHeight = height;
            });
        }

        RefreshLists();
        SwitchTab(settings.LastTab == "completed", persist: false);
        RefreshPinUi();
        RefreshMoreMenu();
        RefreshCategoryChips();
        UpdateNewTaskCategoryUi();
        UpdateCollapseUi();
        PlayShowAnimation();

        if (ShowActivated)
        {
            TaskInput.Focus();
        }

        if (_pinned)
        {
            StartDesktopKeeperTimer();
            EnsureVisibleOnDesktop();
        }

        DebugLog($"OnLoaded: settings=({settings.WindowX},{settings.WindowY},{settings.WindowWidth}x{settings.WindowHeight}) bounds={bounds} dpi={_dpiScale:F2} actual=({Left:F1},{Top:F1},{ActualWidth:F1}x{ActualHeight:F1}) active={IsActive} pinned={_pinned}");
        UiStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PlayShowAnimation()
    {
        Opacity = 1;
        DoubleAnimation animation = new(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, animation);
    }

    private double GetDpiScale()
    {
        try
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            return scale > 0 ? scale : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    // ================== 定位 / 拖动 / 缩放 ==================

    private void ApplyBounds(int x, int y, int width, int height)
    {
        double scale = CurrentDpiScale();
        width = Math.Max((int)Math.Round(280 * scale), width);
        height = Math.Max((int)Math.Round(44 * scale), height);
        _lastBounds = new PixelRect(x, y, width, height);

        try
        {
            Left = x / scale;
            Top = y / scale;
            Width = width / scale;
            Height = height / scale;
            DebugLog($"ApplyBounds -> ({Left:F1},{Top:F1}) {Width:F1}x{Height:F1} DIP (physical {x},{y} {width}x{height}, dpi {scale:F2})");
        }
        catch (Exception ex)
        {
            App.Instance.LogError(ex, "设置窗口位置失败");
        }
    }

    private PixelRect GetCurrentBounds()
    {
        double scale = CurrentDpiScale();
        try
        {
            double left = double.IsNaN(Left) ? 0 : Left;
            double top = double.IsNaN(Top) ? 0 : Top;
            double width = ActualWidth > 0 ? ActualWidth : (double.IsNaN(Width) ? 380 : Width);
            double height = ActualHeight > 0 ? ActualHeight : (double.IsNaN(Height) ? 540 : Height);
            PixelRect bounds = new(
                (int)Math.Round(left * scale),
                (int)Math.Round(top * scale),
                (int)Math.Round(width * scale),
                (int)Math.Round(height * scale));
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                _lastBounds = bounds;
                return bounds;
            }
        }
        catch (Exception ex)
        {
            App.Instance.LogError(ex, "读取窗口位置失败");
        }

        if (_lastBounds.Width > 0 && _lastBounds.Height > 0)
        {
            return _lastBounds;
        }

        return new PixelRect(0, 0, _store.Settings.WindowWidth, _store.Settings.WindowHeight);
    }

    /// <summary>当前窗口所在屏幕的 DPI 缩放（1.0 = 96 DPI）。</summary>
    private double CurrentDpiScale()
    {
        try
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (scale > 0)
            {
                _dpiScale = scale;
                return scale;
            }
        }
        catch
        {
            // 忽略：窗口尚未显示时用已知值。
        }

        return _dpiScale > 0 ? _dpiScale : 1.0;
    }

    private static (int X, int Y) GetCursorPosition()
    {
        try
        {
            return DesktopPin.GetCursorPosPx();
        }
        catch
        {
            Drawing.Point position = WinForms.Cursor.Position;
            return (position.X, position.Y);
        }
    }

    private PixelRect ClampFullyVisible(int x, int y, int width, int height)
    {
        Drawing.Rectangle virtualScreen = WinForms.SystemInformation.VirtualScreen;
        int clampedWidth = Math.Min(width, Math.Max(120, virtualScreen.Width - 8));
        int clampedHeight = Math.Min(height, Math.Max(120, virtualScreen.Height - 8));
        int clampedX = Math.Clamp(x, virtualScreen.Left, virtualScreen.Right - clampedWidth);
        int clampedY = Math.Clamp(y, virtualScreen.Top, virtualScreen.Bottom - clampedHeight);
        return new PixelRect(clampedX, clampedY, clampedWidth, clampedHeight);
    }

    private PixelRect ClampMove(int x, int y, int width, int height)
    {
        Drawing.Rectangle virtualScreen = WinForms.SystemInformation.VirtualScreen;
        int visibleX = (int)(48 * _dpiScale);
        int visibleY = (int)(36 * _dpiScale);
        int clampedX = Math.Clamp(x, virtualScreen.Left - width + visibleX, virtualScreen.Right - visibleX);
        int clampedY = Math.Clamp(y, virtualScreen.Top, virtualScreen.Bottom - visibleY);
        return new PixelRect(clampedX, clampedY, width, height);
    }

    private void HeaderDragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _dragStartRect = GetCurrentBounds();
        _dragStartCursor = GetCursorPosition();
        _dragging = true;
        HeaderDragArea.CaptureMouse();
        e.Handled = true;
    }

    private void HeaderDragArea_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag(save: true);
            return;
        }

        (int cursorX, int cursorY) = GetCursorPosition();
        int dx = cursorX - _dragStartCursor.X;
        int dy = cursorY - _dragStartCursor.Y;
        PixelRect target = ClampMove(
            _dragStartRect.X + dx,
            _dragStartRect.Y + dy,
            _dragStartRect.Width,
            _dragStartRect.Height);
        ApplyBounds(target.X, target.Y, target.Width, target.Height);
    }

    private void HeaderDragArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag(save: true);
        e.Handled = true;
    }

    private void HeaderDragArea_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            EndDrag(save: true);
        }
    }

    private void EndDrag(bool save)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (Mouse.Captured == HeaderDragArea)
        {
            Mouse.Capture(null);
        }

        if (save)
        {
            PixelRect bounds = GetCurrentBounds();
            _store.UpdateSettings(s =>
            {
                s.WindowX = bounds.X;
                s.WindowY = bounds.Y;
            });
        }
    }

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _collapsed)
        {
            return;
        }

        PixelRect bounds = GetCurrentBounds();
        _resizeStartWidth = bounds.Width;
        _resizeStartHeight = bounds.Height;
        _resizeStartCursor = GetCursorPosition();
        _resizing = true;
        ResizeGrip.CaptureMouse();
        e.Handled = true;
    }

    private void ResizeGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_resizing)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndResize(save: true);
            return;
        }

        (int cursorX, int cursorY) = GetCursorPosition();
        int dx = cursorX - _resizeStartCursor.X;
        int dy = cursorY - _resizeStartCursor.Y;
        PixelRect current = GetCurrentBounds();
        int minWidth = (int)Math.Round(300 * _dpiScale);
        int minHeight = (int)Math.Round(260 * _dpiScale);
        Drawing.Rectangle virtualScreen = WinForms.SystemInformation.VirtualScreen;
        int maxWidth = Math.Max(minWidth, virtualScreen.Right - current.X);
        int maxHeight = Math.Max(minHeight, virtualScreen.Bottom - current.Y);

        int width = Math.Clamp(_resizeStartWidth + dx, minWidth, maxWidth);
        int height = Math.Clamp(_resizeStartHeight + dy, minHeight, maxHeight);
        ApplyBounds(current.X, current.Y, width, height);
    }

    private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndResize(save: true);
        e.Handled = true;
    }

    private void ResizeGrip_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_resizing)
        {
            EndResize(save: true);
        }
    }

    private void EndResize(bool save)
    {
        if (!_resizing)
        {
            return;
        }

        _resizing = false;
        if (Mouse.Captured == ResizeGrip)
        {
            Mouse.Capture(null);
        }

        if (save)
        {
            PixelRect bounds = GetCurrentBounds();
            _expandedHeight = bounds.Height;
            _store.UpdateSettings(s =>
            {
                s.WindowX = bounds.X;
                s.WindowY = bounds.Y;
                s.WindowWidth = bounds.Width;
                s.WindowHeight = bounds.Height;
            });
        }
    }

    /// <summary>屏幕分辨率/多屏变化后确保窗口仍在可见范围内。</summary>
    public void EnsureOnScreen()
    {
        try
        {
            PixelRect bounds = GetCurrentBounds();
            PixelRect clamped = ClampFullyVisible(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            if (clamped != bounds)
            {
                ApplyBounds(clamped.X, clamped.Y, clamped.Width, clamped.Height);
                _store.UpdateSettings(s =>
                {
                    s.WindowX = clamped.X;
                    s.WindowY = clamped.Y;
                });
            }
        }
        catch (Exception ex)
        {
            App.Instance.LogError(ex, "校正窗口位置失败");
        }
    }

    // ================== 折叠 ==================

    private void ToggleCollapsed() => SetCollapsed(!_collapsed);

    private void SetCollapsed(bool collapsed, bool persist = true)
    {
        if (_collapsed == collapsed)
        {
            return;
        }

        _collapsed = collapsed;
        ApplyCollapsedVisibility();

        PixelRect current = GetCurrentBounds();
        if (collapsed)
        {
            double collapsedDip = MeasureCollapsedHeight();
            int collapsedHeight = (int)Math.Ceiling(collapsedDip * _dpiScale);
            ApplyBounds(current.X, current.Y, current.Width, collapsedHeight);
        }
        else
        {
            int width = _store.Settings.WindowWidth > 0 ? _store.Settings.WindowWidth : current.Width;
            int height = _expandedHeight > 0 ? _expandedHeight : 540;
            ApplyBounds(current.X, current.Y, width, height);
            PendingScroll.ScrollToTop();
        }

        if (persist)
        {
            _store.UpdateSettings(s => s.Collapsed = collapsed);
        }

        UpdateHeaderSummary();
        RefreshMoreMenu();
        UpdateCollapseUi();
        UiStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyCollapsedVisibility()
    {
        Visibility contentVisibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        AddArea.Visibility = contentVisibility;
        TabsArea.Visibility = contentVisibility;
        CategoryArea.Visibility = contentVisibility;
        ListArea.Visibility = contentVisibility;
        FooterArea.Visibility = contentVisibility;
    }

    private double MeasureCollapsedHeight()
    {
        UpdateLayout();
        double availableWidth = Math.Max(120, ActualWidth - 28);
        Card.Measure(new Size(availableWidth, double.PositiveInfinity));
        double height = Card.DesiredSize.Height + 28;
        return Math.Max(height, 84);
    }

    // ================== 列表与页签 ==================

    private void RefreshLists()
    {
        List<TodoItem> pending = _store.Pending.Where(MatchesCategoryFilter).ToList();
        List<TodoItem> completed = _store.Completed.Where(MatchesCategoryFilter).ToList();
        _visiblePending = pending;

        string pendingSignature = string.Join('\u0001', pending.Select(i => i.Id + ':' + i.Text + ':' + i.SortOrder + ':' + i.CategoryId));
        if (pendingSignature != _pendingSignature)
        {
            _pendingSignature = pendingSignature;
            PendingList.ItemsSource = pending;
        }

        string completedSignature = string.Join('\u0001', completed.Select(i => i.Id + ':' + i.CompletedAt?.UtcTicks + ':' + i.CategoryId));
        if (completedSignature != _completedSignature)
        {
            _completedSignature = completedSignature;
            CompletedList.ItemsSource = completed;
        }

        PendingCountText.Text = pending.Count.ToString(CultureInfo.InvariantCulture);
        CompletedCountText.Text = completed.Count.ToString(CultureInfo.InvariantCulture);

        bool pendingEmpty = pending.Count == 0;
        PendingEmpty.Visibility = pendingEmpty ? Visibility.Visible : Visibility.Collapsed;
        PendingScroll.Visibility = pendingEmpty ? Visibility.Collapsed : Visibility.Visible;

        bool completedEmpty = completed.Count == 0;
        CompletedEmpty.Visibility = completedEmpty ? Visibility.Visible : Visibility.Collapsed;
        CompletedScroll.Visibility = completedEmpty ? Visibility.Collapsed : Visibility.Visible;
        CompletedSummary.Text = completedEmpty ? "完成的任务会留在这里" : $"共 {completed.Count} 项 · 最近完成的在前";
        ClearCompletedButton.Visibility = completedEmpty ? Visibility.Collapsed : Visibility.Visible;

        bool filtering = !string.IsNullOrEmpty(ActiveCategoryFilter);
        string filterName = FilterDisplayName();
        PendingEmptyTitle.Text = filtering ? $"「{filterName}」下还没有待办" : "还没有待办";
        PendingEmptyHint.Text = filtering ? "点击上方分类切换，或选择「全部」查看" : "在上方输入，按 Enter 就添加啦";
        CompletedEmptyTitle.Text = filtering ? $"「{filterName}」下还没有已完成任务" : "还没有完成的任务";

        RefreshCategoryChips();
        UpdateNewTaskCategoryUi();
        UpdateHeaderSummary();
    }

    private void SwitchTab(bool completedPage, bool persist)
    {
        _completedPage = completedPage;
        PendingPanel.Visibility = completedPage ? Visibility.Collapsed : Visibility.Visible;
        CompletedPanel.Visibility = completedPage ? Visibility.Visible : Visibility.Collapsed;
        PendingTab.Style = (Style)FindResource(completedPage ? "TabButtonStyle" : "TabButtonActiveStyle");
        CompletedTab.Style = (Style)FindResource(completedPage ? "TabButtonActiveStyle" : "TabButtonStyle");

        if (!completedPage)
        {
            PendingScroll.ScrollToTop();
        }

        if (persist)
        {
            _store.UpdateSettings(s => s.LastTab = completedPage ? "completed" : "pending");
        }
    }

    private void UpdateHeaderSummary()
    {
        int pendingCount = _store.Pending.Count;
        int completedCount = _store.Completed.Count;
        string filterSuffix = string.IsNullOrEmpty(ActiveCategoryFilter) ? string.Empty : $" · {FilterDisplayName()}";
        if (_collapsed)
        {
            HeaderSubtitle.Text = $"{pendingCount} 项待办 · 已完成 {completedCount}{filterSuffix}";
            return;
        }

        string date = DateTime.Now.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
        HeaderSubtitle.Text = pendingCount == 0
            ? $"{date} · 今天暂无待办{filterSuffix}"
            : $"{date} · {pendingCount} 项待办{filterSuffix}";
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        if (!_loaded || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_loaded && !Dispatcher.HasShutdownStarted)
            {
                RefreshLists();
            }
        }));
    }

    // ================== 任务操作 ==================

    private void TaskInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        TaskPlaceholder.Visibility = string.IsNullOrEmpty(TaskInput.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TaskInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddTaskFromInput();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            TaskInput.Clear();
            e.Handled = true;
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e) => AddTaskFromInput();

    private void AddTaskFromInput()
    {
        string? categoryId = _store.Settings.NewTaskCategoryId;
        TodoItem? item = _store.Add(TaskInput.Text, categoryId);
        if (item is null)
        {
            TaskInput.Focus();
            return;
        }

        TaskInput.Clear();
        TaskInput.Focus();

        if (_completedPage)
        {
            SwitchTab(false, persist: true);
        }

        // 新任务不在当前分类筛选范围内时，自动切回“全部”，避免用户看不到刚添加的任务。
        if (!string.IsNullOrEmpty(ActiveCategoryFilter) && !MatchesCategoryFilter(item))
        {
            SelectCategoryFilter(null);
        }

        RefreshLists();
        PendingScroll.ScrollToTop();
    }

    private void PendingCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoItem item })
        {
            _store.Complete(item.Id);
        }
    }

    private void PendingDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoItem item })
        {
            _store.Delete(item.Id);
        }
    }

    private void CompletedRestore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoItem item })
        {
            _store.Restore(item.Id);
        }
    }

    private void CompletedDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoItem item })
        {
            _store.Delete(item.Id);
        }
    }

    private void PendingTab_Click(object sender, RoutedEventArgs e) => SwitchTab(false, persist: true);

    private void CompletedTab_Click(object sender, RoutedEventArgs e) => SwitchTab(true, persist: true);

    private void ClearCompletedButton_Click(object sender, RoutedEventArgs e)
    {
        int count = _store.Completed.Count;
        if (count == 0)
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            $"确定清空 {count} 项已完成任务吗？\n此操作不可恢复。",
            "贴纸待办",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (result == MessageBoxResult.Yes)
        {
            _store.ClearCompleted();
        }
    }

    // ================== 折叠按钮 / 分类 / 拖拽排序 ==================

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => ToggleCollapsed();

    private void UpdateCollapseUi()
    {
        CollapseGlyph.Text = _collapsed ? "\uE70E" : "\uE70D";
        CollapseButton.ToolTip = _collapsed ? "展开贴纸" : "折叠成小贴纸";
    }

    private string? ActiveCategoryFilter => _store.Settings.CategoryFilter;

    private Category? FindCategory(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _store.Categories.FirstOrDefault(c => c.Id == id);
    }

    private string FilterDisplayName()
    {
        string? filter = ActiveCategoryFilter;
        if (string.IsNullOrEmpty(filter))
        {
            return "全部";
        }

        if (filter == TaskStore.UncategorizedFilter)
        {
            return "未分类";
        }

        return FindCategory(filter)?.Name ?? "全部";
    }

    private bool MatchesCategoryFilter(TodoItem item)
    {
        string? filter = ActiveCategoryFilter;
        if (string.IsNullOrEmpty(filter))
        {
            return true;
        }

        if (filter == TaskStore.UncategorizedFilter)
        {
            return item.CategoryId is null;
        }

        return string.Equals(item.CategoryId, filter, StringComparison.Ordinal);
    }

    private Brush ParseCategoryBrush(string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return Brushes.Gray;
        }

        try
        {
            return new BrushConverter().ConvertFromString(colorHex) as Brush ?? Brushes.Gray;
        }
        catch
        {
            return Brushes.Gray;
        }
    }

    /// <summary>刷新顶部分类筛选胶囊（全部 / 未分类 / 各分类）。</summary>
    private void RefreshCategoryChips()
    {
        string? filter = ActiveCategoryFilter;
        IReadOnlyList<Category> categories = _store.Categories;
        string signature = (filter ?? "all")
                           + '\u0002'
                           + string.Join('\u0001', categories.Select(c => c.Id + ':' + c.Name + ':' + c.ColorHex));
        if (signature == _categoryChipsSignature)
        {
            return;
        }

        _categoryChipsSignature = signature;
        CategoryChipsPanel.Children.Clear();
        CategoryChipsPanel.Children.Add(CreateCategoryChip("全部", null, filter is null, null));
        CategoryChipsPanel.Children.Add(CreateCategoryChip("未分类", TaskStore.UncategorizedFilter, filter == TaskStore.UncategorizedFilter, "#CBD5E1"));
        foreach (Category category in categories)
        {
            CategoryChipsPanel.Children.Add(CreateCategoryChip(category.Name, category.Id, filter == category.Id, category.ColorHex));
        }
    }

    private Button CreateCategoryChip(string text, string? filterKey, bool active, string? colorHex)
    {
        StackPanel content = new() { Orientation = Orientation.Horizontal };
        if (!string.IsNullOrWhiteSpace(colorHex))
        {
            content.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = ParseCategoryBrush(colorHex),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)FindResource("UiFont"),
        });

        Button chip = new()
        {
            Content = content,
            Style = (Style)FindResource(active ? "CategoryChipActiveStyle" : "CategoryChipStyle"),
            Tag = filterKey,
        };

        if (filterKey is not null && filterKey != TaskStore.UncategorizedFilter)
        {
            chip.ContextMenu = BuildCategoryChipMenu(filterKey);
        }

        string automationId = filterKey switch
        {
            null => "CategoryChip_all",
            TaskStore.UncategorizedFilter => "CategoryChip_none",
            _ => "CategoryChip_" + filterKey,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(chip, automationId);
        System.Windows.Automation.AutomationProperties.SetName(chip, "分类：" + text);
        chip.Click += (_, _) => SelectCategoryFilter(filterKey);
        return chip;
    }

    private ContextMenu? BuildCategoryChipMenu(string categoryId)
    {
        Category? category = FindCategory(categoryId);
        if (category is null)
        {
            return null;
        }

        ContextMenu menu = new();
        MenuItem renameItem = new() { Header = "重命名分类…" };
        renameItem.Click += (_, _) => RenameCategoryFlow(category);
        MenuItem deleteItem = new() { Header = "删除分类…" };
        deleteItem.Click += (_, _) => DeleteCategoryFlow(category);
        menu.Items.Add(renameItem);
        menu.Items.Add(deleteItem);
        return menu;
    }

    private void SelectCategoryFilter(string? filterKey)
    {
        string? normalized = string.IsNullOrWhiteSpace(filterKey) ? null : filterKey;
        if (string.Equals(_store.Settings.CategoryFilter, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _store.UpdateSettings(s => s.CategoryFilter = normalized);
        RefreshCategoryChips();
        RefreshLists();
    }

    /// <summary>分类胶囊超出宽度时，用鼠标滚轮横向滚动。</summary>
    private void CategoryArea_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CategoryArea.ScrollableWidth <= 0)
        {
            return;
        }

        CategoryArea.ScrollToHorizontalOffset(CategoryArea.HorizontalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    private void NewTaskCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        ContextMenu menu = new();

        MenuItem noneItem = new()
        {
            Header = "未分类",
            IsCheckable = true,
            IsChecked = _store.Settings.NewTaskCategoryId is null,
        };
        noneItem.Click += (_, _) =>
        {
            _store.UpdateSettings(s => s.NewTaskCategoryId = null);
            UpdateNewTaskCategoryUi();
        };
        menu.Items.Add(noneItem);

        foreach (Category category in _store.Categories)
        {
            string categoryId = category.Id;
            MenuItem item = new()
            {
                Header = category.Name,
                IsCheckable = true,
                IsChecked = _store.Settings.NewTaskCategoryId == categoryId,
            };
            item.Click += (_, _) =>
            {
                _store.UpdateSettings(s => s.NewTaskCategoryId = categoryId);
                UpdateNewTaskCategoryUi();
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        MenuItem createItem = new() { Header = "新建分类…" };
        createItem.Click += (_, _) => AddCategoryFlow();
        menu.Items.Add(createItem);

        menu.PlacementTarget = NewTaskCategoryButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void UpdateNewTaskCategoryUi()
    {
        Category? category = FindCategory(_store.Settings.NewTaskCategoryId);
        if (category is null)
        {
            NewTaskCategoryText.Text = "未分类";
            NewTaskCategoryDot.Fill = ParseCategoryBrush("#CBD5E1");
            NewTaskCategoryButton.ToolTip = "选择新任务的分类（当前：未分类）";
            return;
        }

        NewTaskCategoryText.Text = category.Name;
        NewTaskCategoryDot.Fill = ParseCategoryBrush(category.ColorHex);
        NewTaskCategoryButton.ToolTip = $"选择新任务的分类（当前：{category.Name}）";
    }

    private void CategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not TodoItem item)
        {
            return;
        }

        ContextMenu menu = BuildTaskCategoryMenu(item);
        menu.PlacementTarget = element;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildTaskCategoryMenu(TodoItem item)
    {
        ContextMenu menu = new();
        MenuItem noneItem = new()
        {
            Header = "未分类",
            IsCheckable = true,
            IsChecked = item.CategoryId is null,
        };
        noneItem.Click += (_, _) => _store.SetCategory(item.Id, null);
        menu.Items.Add(noneItem);

        foreach (Category category in _store.Categories)
        {
            string categoryId = category.Id;
            MenuItem itemMenu = new()
            {
                Header = category.Name,
                IsCheckable = true,
                IsChecked = string.Equals(item.CategoryId, categoryId, StringComparison.Ordinal),
            };
            itemMenu.Click += (_, _) => _store.SetCategory(item.Id, categoryId);
            menu.Items.Add(itemMenu);
        }

        menu.Items.Add(new Separator());
        MenuItem createItem = new() { Header = "新建分类…" };
        createItem.Click += (_, _) => AddCategoryFlow();
        menu.Items.Add(createItem);
        return menu;
    }

    private void AddCategoryFlow()
    {
        string? name = TextPromptDialog.Show(this, "新建分类", "给分类起个名字（最多 8 个字符）", string.Empty, TaskStore.MaxCategoryNameLength);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Category? category = _store.AddCategory(name);
        if (category is null)
        {
            MessageBox.Show(this, "分类名重复或无效，请换一个名字。", "贴纸待办", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _store.UpdateSettings(s => s.NewTaskCategoryId = category.Id);
        _categoryChipsSignature = string.Empty;
        RefreshCategoryChips();
        UpdateNewTaskCategoryUi();
    }

    private void RenameCategoryFlow(Category category)
    {
        string? name = TextPromptDialog.Show(this, "重命名分类", "输入新的分类名（最多 8 个字符）", category.Name, TaskStore.MaxCategoryNameLength);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == category.Name)
        {
            return;
        }

        if (!_store.RenameCategory(category.Id, name))
        {
            MessageBox.Show(this, "重命名失败：分类名重复或无效。", "贴纸待办", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void DeleteCategoryFlow(Category category)
    {
        int inUse = _store.Items.Count(i => string.Equals(i.CategoryId, category.Id, StringComparison.Ordinal));
        string message = inUse > 0
            ? $"删除分类「{category.Name}」？\n该分类下的 {inUse} 个任务会变成“未分类”。"
            : $"删除分类「{category.Name}」？";
        MessageBoxResult result = MessageBox.Show(
            this,
            message,
            "删除分类",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _store.DeleteCategory(category.Id);
        _categoryChipsSignature = string.Empty;
        _pendingSignature = string.Empty;
        RefreshCategoryChips();
        RefreshLists();
    }

    // ---------- 待办任务拖拽排序 ----------

    private void PendingRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement row
            || row.DataContext is not TodoItem item)
        {
            return;
        }

        if (FindVisualAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        _dragItem = item;
        _dragRow = row;
        _dragStartPoint = e.GetPosition(PendingList);
        _dragActive = false;
        row.CaptureMouse();
        e.Handled = true;
    }

    private void PendingRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow is null || _dragItem is null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPendingDrag(releaseCapture: true);
            return;
        }

        Point current = e.GetPosition(PendingList);
        if (!_dragActive)
        {
            if (Math.Abs(current.Y - _dragStartPoint.Y) < 6 && Math.Abs(current.X - _dragStartPoint.X) < 6)
            {
                return;
            }

            _dragActive = true;
            _dragRow.Opacity = 0.45;
        }

        e.Handled = true;
    }

    private void PendingRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        TodoItem? dragged = _dragItem;
        bool wasActive = _dragActive;
        Point dropPoint = e.GetPosition(PendingList);
        EndPendingDrag(releaseCapture: true);

        if (!wasActive || dragged is null)
        {
            return;
        }

        int sourceIndex = _visiblePending.FindIndex(i => i.Id == dragged.Id);
        if (sourceIndex < 0)
        {
            return;
        }

        int targetIndex = FindPendingDropIndex(dropPoint.Y);
        if (targetIndex > sourceIndex)
        {
            targetIndex--;
        }

        targetIndex = Math.Clamp(targetIndex, 0, _visiblePending.Count - 1);
        if (targetIndex == sourceIndex || _visiblePending.Count < 2)
        {
            return;
        }

        List<string> newOrder = _visiblePending.Select(i => i.Id).ToList();
        newOrder.RemoveAt(sourceIndex);
        newOrder.Insert(targetIndex, dragged.Id);
        bool applied = _store.ApplyPendingOrder(newOrder);
        DebugLog($"drag reorder: applied={applied} from={sourceIndex} to={targetIndex} order={string.Join('>', newOrder)}");
        e.Handled = true;
    }

    private void PendingRow_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragRow is not null)
        {
            EndPendingDrag(releaseCapture: false);
        }
    }

    private int FindPendingDropIndex(double y)
    {
        for (int index = 0; index < _visiblePending.Count; index++)
        {
            if (PendingList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
            {
                continue;
            }

            try
            {
                Point topLeft = container.TransformToAncestor(PendingList).Transform(new Point(0, 0));
                if (y < topLeft.Y + container.ActualHeight / 2)
                {
                    return index;
                }
            }
            catch
            {
                // 容器尚未完成布局时跳过。
            }
        }

        return _visiblePending.Count;
    }

    private void EndPendingDrag(bool releaseCapture)
    {
        FrameworkElement? row = _dragRow;
        _dragRow = null;
        _dragItem = null;
        _dragActive = false;
        if (row is null)
        {
            return;
        }

        row.Opacity = 1;
        if (releaseCapture && Mouse.Captured == row)
        {
            Mouse.Capture(null);
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T typed)
            {
                return typed;
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    // ================== 贴桌面 / 托盘 / 菜单 ==================

    private void PinButton_Click(object sender, RoutedEventArgs e) => TogglePin();

    private void HideButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshMoreMenu();
        _moreMenu.PlacementTarget = MoreButton;
        _moreMenu.Placement = PlacementMode.Bottom;
        _moreMenu.IsOpen = true;
    }

    /// <summary>切换“贴到桌面”（桌面贴纸模式：窗口始终压在其他窗口下方，不置顶、不进任务栏）。</summary>
    public void TogglePin()
    {
        if (_noPin)
        {
            MessageBox.Show(this, "本次以 --no-pin 模式启动，窗口不会进入桌面贴纸模式。", "贴纸待办", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PixelRect bounds = GetCurrentBounds();
        if (_pinned)
        {
            _pinned = false;
            _store.UpdateSettings(s => s.PinToDesktop = false);
            try
            {
                _pin?.BringToFront();
                Activate();
            }
            catch (Exception ex)
            {
                App.Instance.LogError(ex, "取消贴桌面失败");
            }
        }
        else
        {
            EnableDesktopStickerMode();
            _store.UpdateSettings(s => s.PinToDesktop = true);
        }

        ApplyBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        RefreshPinUi();
        UiStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 进入桌面贴纸模式：普通无边框窗口 + WS_EX_TOOLWINDOW（不进任务栏/Alt+Tab）+ 不置顶，
    /// 并把窗口 z 序固定在“桌面之上、所有普通窗口之下”，达到“粘在桌面上、不挡路”的效果。
    /// （真正的 WorkerW 桌面层嵌入在 Win11 26H1/WPE 环境下不可交互，故不采用。）
    /// </summary>
    private void EnableDesktopStickerMode()
    {
        if (_noPin || _pin is null)
        {
            return;
        }

        _pinned = true;
    }


    /// <summary>
    /// 桌面可见性守护：若贴纸窗口被系统“显示桌面”层（Win+D）或桌面窗口盖住、而屏幕上并没有普通窗口盖住它，
    /// 则用“瞬时 TOPMOST → 立即 NOTOPMOST”把它重新提回桌面之上（不留下置顶属性、不抢焦点），
    /// 保证“贴在桌面上”的视觉与可点性。普通窗口在前台时不做任何事。
    /// </summary>
    private void EnsureVisibleOnDesktop()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !IsVisible)
            {
                return;
            }

            PixelRect bounds = GetCurrentBounds();
            NativePoint point = new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            IntPtr hit = WindowFromPoint(point);
            if (hit == hwnd)
            {
                return;
            }

            string hitClass = GetWindowClassName(hit);
            bool coveredByDesktop = hitClass is "SysListView32" or "SHELLDLL_DefView" or "Progman" or "WorkerW"
                                    or "WorkerW" or "DesktopWindowXamlSource";
            if (!coveredByDesktop)
            {
                return;
            }

            const uint flags = 0x0001 | 0x0002 | 0x0010; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
            SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, flags);
            SetWindowPos(hwnd, HwndNoTopMost, 0, 0, 0, 0, flags);
            DebugLog($"EnsureVisibleOnDesktop: 已被 {hitClass} 覆盖，已提升回桌面之上");
        }
        catch (Exception ex)
        {
            App.Instance.LogError(ex, "桌面可见性校正失败");
        }
    }

    private void StartDesktopKeeperTimer()
    {
        _desktopKeeperTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _desktopKeeperTimer.Tick -= OnDesktopKeeperTick;
        _desktopKeeperTimer.Tick += OnDesktopKeeperTick;
        if (!_desktopKeeperTimer.IsEnabled)
        {
            _desktopKeeperTimer.Start();
        }
    }

    private void StopDesktopKeeperTimer()
    {
        if (_desktopKeeperTimer is null)
        {
            return;
        }

        _desktopKeeperTimer.Stop();
        _desktopKeeperTimer.Tick -= OnDesktopKeeperTick;
    }

    private void OnDesktopKeeperTick(object? sender, EventArgs e)
    {
        if (!_pinned || !IsVisible || IsActive || _moreMenu.IsOpen || _dragging || _resizing)
        {
            return;
        }

        EnsureVisibleOnDesktop();
    }

    /// <summary>Explorer 重启 / 分辨率变化后恢复桌面贴纸状态。</summary>
    public void ReapplyDesktopMode()
    {
        EnsureOnScreen();
    }

    /// <summary>调试日志：仅当设置环境变量 STICKER_DEBUG_LOG 指向文件路径时开启。</summary>
    private static void DebugLog(string message)
    {
        try
        {
            string? path = Environment.GetEnvironmentVariable("STICKER_DEBUG_LOG");
            if (!string.IsNullOrWhiteSpace(path))
            {
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 调试日志失败不影响主功能。
        }
    }

    private void RefreshPinUi()
    {
        PinGlyph.Text = _pinned ? "\uE718" : "\uE77A";
        PinButton.ToolTip = _pinned ? "已贴到桌面（点击取消）" : "贴到桌面";
        if (_pinned)
        {
            PinButton.Foreground = (Brush)FindResource("AccentBrush");
        }
        else
        {
            PinButton.ClearValue(ForegroundProperty);
        }

        RefreshMoreMenu();
        UpdateHeaderSummary();
        UiStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>切换开机自启；返回切换后的状态。</summary>
    public bool ToggleAutoStart()
    {
        bool currentlyEnabled = AutoStart.IsEnabled();
        bool success = currentlyEnabled ? AutoStart.Disable() : AutoStart.Enable();
        if (!success)
        {
            MessageBox.Show(
                this,
                "无法修改开机自启设置，请检查注册表权限后重试。",
                "贴纸待办",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        RefreshMoreMenu();
        UiStateChanged?.Invoke(this, EventArgs.Empty);
        return AutoStart.IsEnabled();
    }

    private ContextMenu BuildMoreMenu()
    {
        ContextMenu menu = new();

        _collapseMenuItem = new MenuItem { Header = "折叠成小贴纸" };
        _collapseMenuItem.Click += (_, _) => ToggleCollapsed();

        _pinMenuItem = new MenuItem { Header = "贴到桌面", IsCheckable = true };
        _pinMenuItem.Click += (_, _) => TogglePin();

        _autoStartMenuItem = new MenuItem { Header = "开机自启", IsCheckable = true };
        _autoStartMenuItem.Click += (_, _) => ToggleAutoStart();

        MenuItem folderItem = new() { Header = "打开数据文件夹" };
        folderItem.Click += (_, _) => OpenDataFolder();

        MenuItem aboutItem = new() { Header = "关于贴纸待办" };
        aboutItem.Click += (_, _) => ShowAbout();

        MenuItem exitItem = new() { Header = "退出" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(_collapseMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_pinMenuItem);
        menu.Items.Add(_autoStartMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(folderItem);
        menu.Items.Add(aboutItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        menu.Opened += (_, _) => RefreshMoreMenu();
        return menu;
    }

    private void RefreshMoreMenu()
    {
        if (_collapseMenuItem is null)
        {
            return;
        }

        _collapseMenuItem.Header = _collapsed ? "展开贴纸" : "折叠成小贴纸";
        _pinMenuItem.IsChecked = _pinned;
        _pinMenuItem.IsEnabled = !_noPin;
        _autoStartMenuItem.IsChecked = AutoStart.IsEnabled();
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.DataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_store.DataDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            App.Instance.LogError(ex, "打开数据文件夹失败");
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            this,
            $"贴纸待办 v{VersionText}\n\n" +
            "一个轻量的桌面待办清单：像贴纸一样粘在桌面上，不置顶、不挡路。\n\n" +
            "• Enter 添加任务，完成的任务会进入「已完成」页\n" +
            "• 右上角 ⌄ 按钮折叠 / 展开；标题栏按住可拖动窗口\n" +
            "• 顶部分类胶囊筛选；任务行右侧标签按钮可修改分类\n" +
            "• 按住任务行拖动可调整待办顺序\n" +
            "• 右下角拖动调整窗口大小；托盘图标：显示/隐藏、开机自启、退出\n\n" +
            $"数据目录：{_store.DataDirectory}",
            "关于贴纸待办",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // ================== 显隐 / 浮到前台 ==================

    /// <summary>显示窗口并浮到前台（桌面贴纸模式下，失去焦点后会自动沉回底部）。</summary>
    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        try
        {
            Activate();
            _pin?.BringToFront();
            if (_pinned)
            {
                StartDesktopKeeperTimer();
            }
        }
        catch (Exception ex)
        {
            App.Instance.LogError(ex, "激活窗口失败");
        }

        if (ShowActivated)
        {
            TaskInput.Focus();
        }

        UiStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_pinned)
        {
            return;
        }

        DebugLog("Deactivated");
    }

    /// <summary>在 UI 线程延迟执行。</summary>
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

                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(action));
                }
                catch
                {
                    // 窗口/应用退出期间忽略。
                }
            },
            TaskScheduler.Default);
    }

    /// <summary>隐藏到系统托盘。</summary>
    public void HideToTray()
    {

        try
        {
            Hide();
        }
        catch (Exception ex)
        {
            App.Instance.LogError(ex, "隐藏窗口失败");
        }

        UiStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ================== 关闭 / 退出 ==================

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    /// <summary>应用退出前调用：允许真正关闭窗口并释放桌面嵌入。</summary>
    public void PrepareForExit()
    {
        _allowClose = true;
        _store.Changed -= OnStoreChanged;
        DisposePin();
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            App.Instance.LogError(ex, "关闭窗口失败");
        }
    }

    /// <summary>Explorer 重启时旧句柄可能已失效：脱离旧窗口，由 App 重建新窗口。</summary>
    public void DetachForRestart()
    {
        _allowClose = true;
        _store.Changed -= OnStoreChanged;
        DisposePin();
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            App.Instance.LogError(ex, "Explorer 重启时关闭旧窗口失败");
        }
    }

    private void DisposePin()
    {
        if (_pin is null)
        {
            return;
        }

        try
        {
            _pin.Dispose();
        }
        catch (Exception ex)
        {
            App.Instance.LogError(ex, "释放桌面嵌入失败");
        }
        finally
        {
            _pin = null;
        }
    }
}
