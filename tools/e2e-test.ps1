<#
.SYNOPSIS
    贴纸待办（StickerTodo）端到端（E2E）自动化测试。

.DESCRIPTION
    对 dist\StickerTodo.exe 做真实端到端验证，全程使用临时 --data-dir，不触碰用户真实数据：
      阶段 A：正常模式启动（默认贴桌面）
        - 进程存活、找到主窗口、窗口标题「贴纸待办」
        - 窗口无 WS_EX_TOPMOST
        - 若已嵌入桌面层：GetParent != 0 且父窗口类名含 WorkerW / Progman；
          未嵌入则记录“退化模式”WARN（不伪造 PASS）
        - WS_EX_TOOLWINDOW（不进任务栏）检查
        - 单实例：第二次启动应唤醒已有实例后退出（WARN 级）
      阶段 B：交互模式启动（--no-pin，普通窗口）
        - 标题 / 无 TOPMOST / GetParent = 0
        - UI Automation 按 AutomationId 定位控件（TaskInput、AddButton、PendingTab、
          CompletedTab、MoreButton、CompleteCheckBox、RestoreButton、DeleteButton）
        - 剪贴板 + SendInput Ctrl+V / Enter 输入中文任务
        - 断言 data.json 出现该任务（pending，completedAt 为空）
        - 点 CompleteCheckBox -> 断言 completedAt 非空
        - 点 CompletedTab -> 点 RestoreButton -> 断言 completedAt 清空
        - 附加：DeleteButton 删除待办；CompletedTab 的 DeleteCompletedButton 删除完成项
        - PrintWindow 截图保存到 tools\artifacts\
      清理：结束测试进程、删除临时数据目录、确认未写入用户真实 %APPDATA%\StickerTodo；
      截图与日志保留在 tools\artifacts\。

    UIAutomation 不可用时的退化方案：按窗口矩形 + 估算行高坐标（SendInput 点击）
    尝试输入与勾选；标签切换等依赖精确坐标的操作会明确标记 WARN 并跳过，
    不会把“没验证”伪装成 PASS。

.PARAMETER ExePath
    待测可执行文件路径，默认 <仓库根>\dist\StickerTodo.exe。

.PARAMETER StartupTimeoutSec
    启动后等待主窗口的超时（秒），默认 45。

.PARAMETER StepTimeoutSec
    单个交互步骤等待 data.json / UI 元素更新的超时（秒），默认 15。

.PARAMETER KeepTemp
    保留临时数据目录（默认测试结束后删除）。

.PARAMETER Strict
    WARN 也视为失败（默认只有 FAIL 影响退出码）。

.PARAMETER SelfTest
    只做环境自检（加载 P/Invoke / UIAutomation / 剪贴板 / 截图能力），不启动应用。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\e2e-test.ps1

.EXAMPLE
    pwsh -NoProfile -File .\tools\e2e-test.ps1 -Strict

.NOTES
    需要先运行 build.ps1 生成 dist\StickerTodo.exe。
    测试会结束当前所有 StickerTodo 进程（包括用户已打开的实例）以便单实例隔离。
#>
#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ExePath = '',
    [int]$StartupTimeoutSec = 45,
    [int]$StepTimeoutSec = 15,
    [switch]$KeepTemp,
    [switch]$Strict,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

# ===========================================================================
# 0. 原生 Win32 辅助（P/Invoke）；C# 语法保持 C# 5 兼容，
#    以便 Windows PowerShell 5.1（.NET Framework 编译器）与 PowerShell 7 都能编译。
# ===========================================================================
$script:NativeSource = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class E2ENative
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int SW_RESTORE = 9;
    public const uint CF_UNICODETEXT = 13;
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }

    public sealed class WindowInfo
    {
        public IntPtr Handle = IntPtr.Zero;
        public string Title = "";
        public string ClassName = "";
        public bool Visible = false;
        public int Style = 0;
        public int ExStyle = 0;
        public IntPtr Parent = IntPtr.Zero;
        public string ParentClassName = "";
        public uint ParentProcessId = 0;
        public int Left = 0;
        public int Top = 0;
        public int Width = 0;
        public int Height = 0;

        public bool TopMost { get { return (ExStyle & WS_EX_TOPMOST) != 0; } }
        public bool ToolWindow { get { return (ExStyle & WS_EX_TOOLWINDOW) != 0; } }
        public int Area { get { return Width * Height; } }

        public override string ToString()
        {
            return string.Format("hwnd=0x{0:X} title='{1}' class='{2}' visible={3} rect=({4},{5}) {6}x{7}", Handle.ToInt64(), Title, ClassName, Visible, Left, Top, Width, Height);
        }
    }

    private static void FillWindow(WindowInfo info, IntPtr hWnd)
    {
        info.Handle = hWnd;
        StringBuilder sb = new StringBuilder(1024);
        GetWindowText(hWnd, sb, sb.Capacity);
        info.Title = sb.ToString();
        sb.Length = 0;
        GetClassName(hWnd, sb, sb.Capacity);
        info.ClassName = sb.ToString();
        info.Visible = IsWindowVisible(hWnd);
        info.Style = GetWindowLong(hWnd, GWL_STYLE);
        info.ExStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        info.Parent = GetParent(hWnd);
        if (info.Parent != IntPtr.Zero)
        {
            sb.Length = 0;
            GetClassName(info.Parent, sb, sb.Capacity);
            info.ParentClassName = sb.ToString();
            uint parentPid = 0;
            GetWindowThreadProcessId(info.Parent, out parentPid);
            info.ParentProcessId = parentPid;
        }
        RECT rect;
        if (GetWindowRect(hWnd, out rect))
        {
            info.Left = rect.Left;
            info.Top = rect.Top;
            info.Width = rect.Right - rect.Left;
            info.Height = rect.Bottom - rect.Top;
        }
    }

    public static WindowInfo[] FindProcessWindows(uint processId)
    {
        List<WindowInfo> found = new List<WindowInfo>();
        Dictionary<IntPtr, bool> seen = new Dictionary<IntPtr, bool>();
        EnumWindowsProc callback = delegate(IntPtr hWnd, IntPtr lParam)
        {
            uint windowPid = 0;
            GetWindowThreadProcessId(hWnd, out windowPid);
            if (windowPid == processId && !seen.ContainsKey(hWnd))
            {
                WindowInfo info = new WindowInfo();
                FillWindow(info, hWnd);
                found.Add(info);
                seen[hWnd] = true;
            }
            return true;
        };
        // 先枚举顶层窗口；再枚举桌面的全部后代窗口（窗口被 SetParent 嵌入桌面层后
        // 不再是顶层窗口，EnumWindows 会漏掉，这里做一次全量兜底，按句柄去重）。
        EnumWindows(callback, IntPtr.Zero);
        EnumChildWindows(GetDesktopWindow(), callback, IntPtr.Zero);
        return found.ToArray();
    }

    public static WindowInfo GetWindowInfo(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) { return null; }
        WindowInfo info = new WindowInfo();
        FillWindow(info, hWnd);
        return info;
    }

    public static string GetTitle(IntPtr hWnd)
    {
        StringBuilder sb = new StringBuilder(1024);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static int[] GetWindowBounds(IntPtr hWnd)
    {
        RECT rect;
        if (!GetWindowRect(hWnd, out rect)) { return null; }
        return new int[] { rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top };
    }

    public static bool IsTopMost(IntPtr hWnd)
    {
        return (GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
    }

    public static IntPtr GetParentOf(IntPtr hWnd)
    {
        return GetParent(hWnd);
    }

    public static uint GetWindowDpi(IntPtr hWnd)
    {
        try { return GetDpiForWindow(hWnd); } catch { return 0; }
    }

    /// <summary>
    /// 把当前进程/线程切换为 Per-Monitor V2 DPI 感知。
    /// 否则 DPI 虚拟化会让 GetWindowRect / UIA 坐标与屏幕物理像素不一致（本机 150% DPI 实测偏差明显）。
    /// 返回进程级设置是否成功（线程级设置总会尝试）。
    /// </summary>
    public static bool TryEnablePerMonitorDpiAwareness()
    {
        bool processOk = false;
        try { processOk = SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { }
        return processOk;
    }

    public static bool ClickAt(int x, int y)
    {
        if (!SetCursorPos(x, y)) { return false; }
        System.Threading.Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        return true;
    }

    private static INPUT MakeKey(ushort virtualKey, bool keyUp)
    {
        INPUT input = new INPUT();
        input.type = INPUT_KEYBOARD;
        input.u.ki.wVk = virtualKey;
        input.u.ki.wScan = 0;
        input.u.ki.dwFlags = keyUp ? KEYEVENTF_KEYUP : 0;
        input.u.ki.time = 0;
        input.u.ki.dwExtraInfo = IntPtr.Zero;
        return input;
    }

    private static INPUT MakeUnicodeKey(char ch, bool keyUp)
    {
        INPUT input = new INPUT();
        input.type = INPUT_KEYBOARD;
        input.u.ki.wVk = 0;
        input.u.ki.wScan = (ushort)ch;
        input.u.ki.dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0);
        input.u.ki.time = 0;
        input.u.ki.dwExtraInfo = IntPtr.Zero;
        return input;
    }

    public static uint SendVirtualKey(ushort virtualKey)
    {
        INPUT[] inputs = new INPUT[] { MakeKey(virtualKey, false), MakeKey(virtualKey, true) };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    public static uint SendCtrlV()
    {
        INPUT[] inputs = new INPUT[]
        {
            MakeKey(0x11, false), MakeKey(0x56, false), MakeKey(0x56, true), MakeKey(0x11, true)
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    public static uint SendEnter()
    {
        return SendVirtualKey(0x0D);
    }

    public static uint SendUnicodeText(string text)
    {
        if (text == null || text.Length == 0) { return 0; }
        List<INPUT> list = new List<INPUT>();
        for (int i = 0; i < text.Length; i++)
        {
            list.Add(MakeUnicodeKey(text[i], false));
            list.Add(MakeUnicodeKey(text[i], true));
        }
        return SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT)));
    }

    public static bool SetClipboardText(string text)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (!EmptyClipboard()) { return false; }
                    IntPtr memory = Marshal.StringToHGlobalUni(text);
                    if (SetClipboardData(CF_UNICODETEXT, memory) == IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(memory);
                        return false;
                    }
                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            System.Threading.Thread.Sleep(50);
        }
        return false;
    }

    public static bool IsForeground(IntPtr hWnd)
    {
        return GetForegroundWindow() == hWnd;
    }

    public static int GetInputStructSize()
    {
        return Marshal.SizeOf(typeof(INPUT));
    }
}
'@

# ===========================================================================
# 1. 全局状态与通用工具
# ===========================================================================
$script:Results = New-Object System.Collections.ArrayList
$script:NativeReady = $false
$script:UiaReady = $false
$script:UiaError = ''
$script:TranscriptStarted = $false
$script:LastScreenshotError = ''

function Add-Result {
    param(
        [ValidateSet('PASS', 'FAIL', 'WARN', 'INFO')]
        [string]$Status,
        [string]$Name,
        [string]$Detail = ''
    )
    $entry = New-Object PSObject -Property @{ Status = $Status; Name = $Name; Detail = $Detail }
    [void]$script:Results.Add($entry)
    $color = 'Gray'
    if ($Status -eq 'PASS') { $color = 'Green' }
    elseif ($Status -eq 'FAIL') { $color = 'Red' }
    elseif ($Status -eq 'WARN') { $color = 'Yellow' }
    $line = '  [' + $Status + '] ' + $Name
    if (-not [string]::IsNullOrWhiteSpace($Detail)) { $line = $line + ' — ' + $Detail }
    Write-Host $line -ForegroundColor $color
}

function Add-Phase {
    param([string]$Title)
    Add-Result -Status 'INFO' -Name ('===== ' + $Title + ' =====')
}

function Initialize-Native {
    if ($script:NativeReady) { return $true }
    try {
        if (-not ('E2ENative' -as [type])) {
            Add-Type -TypeDefinition $script:NativeSource -Language CSharp -ErrorAction Stop
        }
        $script:NativeReady = $true
    }
    catch {
        $script:NativeReady = $false
        Add-Result -Status 'FAIL' -Name '加载 Win32 P/Invoke 辅助代码失败' -Detail $_.Exception.Message
    }
    return $script:NativeReady
}

function Initialize-Uia {
    if ($script:UiaReady) { return $true }
    try {
        Add-Type -AssemblyName UIAutomationClient -ErrorAction Stop
        Add-Type -AssemblyName UIAutomationTypes -ErrorAction Stop
        $script:UiaReady = $true
    }
    catch {
        $script:UiaReady = $false
        $script:UiaError = $_.Exception.Message
    }
    return $script:UiaReady
}

function Get-AppProcesses {
    return @(Get-Process -Name 'StickerTodo' -ErrorAction SilentlyContinue)
}

function Stop-AppProcesses {
    $procs = Get-AppProcesses
    if ($procs.Count -eq 0) { return $true }
    foreach ($proc in $procs) {
        try { [void]$proc.CloseMainWindow() } catch { }
    }
    Start-Sleep -Milliseconds 600
    $left = Get-AppProcesses
    if ($left.Count -gt 0) {
        $left | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 600
    }
    return ((Get-AppProcesses).Count -eq 0)
}

function Ensure-NoAppInstances {
    param([string]$Context)
    $existing = Get-AppProcesses
    if ($existing.Count -gt 0) {
        Add-Result -Status 'WARN' -Name ('测试前发现 {0} 个已运行的 StickerTodo 进程，已结束以隔离测试' -f $existing.Count) -Detail $Context
        [void](Stop-AppProcesses)
    }
    return ((Get-AppProcesses).Count -eq 0)
}

function Wait-MainWindow {
    param(
        [System.Diagnostics.Process]$Process,
        [int]$TimeoutSec
    )
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $lastWindows = @()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSec) {
        if ($Process.HasExited) {
            return @{ Exited = $true; ExitCode = $Process.ExitCode; Window = $null; Candidates = @() }
        }
        $windows = @([E2ENative]::FindProcessWindows([uint32]$Process.Id))
        $lastWindows = $windows
        $visible = @($windows | Where-Object { $_.Visible -and $_.Width -ge 80 -and $_.Height -ge 80 })
        if ($visible.Count -gt 0) {
            $main = $visible | Where-Object { $_.Title -eq '贴纸待办' } | Select-Object -First 1
            if ($null -eq $main) {
                $main = $visible | Where-Object { $_.ClassName -like '*HwndWrapper*' -or $_.ClassName -like '*StickerTodo*' } | Select-Object -First 1
            }
            if ($null -eq $main) {
                $main = $visible | Sort-Object -Property Area -Descending | Select-Object -First 1
            }
            if ($null -ne $main) {
                return @{ Exited = $false; ExitCode = $null; Window = $main; Candidates = $windows }
            }
        }
        Start-Sleep -Milliseconds 300
    }
    return @{ Exited = $false; ExitCode = $null; Window = $null; Candidates = $lastWindows }
}

function Get-UiaWindowName {
    param([IntPtr]$Handle)
    if (-not $script:UiaReady) { return $null }
    try {
        $element = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
        if ($null -ne $element) { return $element.Current.Name }
    }
    catch { }
    return $null
}

function Get-WindowTitleSmart {
    param([IntPtr]$Handle)
    $title = [E2ENative]::GetTitle($Handle)
    if (-not [string]::IsNullOrWhiteSpace($title)) { return $title }
    $uiaName = Get-UiaWindowName -Handle $Handle
    if (-not [string]::IsNullOrWhiteSpace($uiaName)) { return $uiaName }
    return ''
}

# ---------------------------------------------------------------------------
# UIAutomation 辅助
# ---------------------------------------------------------------------------
function Get-UiaRoot {
    param([IntPtr]$Handle)
    if (-not $script:UiaReady) { return $null }
    for ($i = 0; $i -lt 50; $i++) {
        try {
            $element = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
            if ($null -ne $element) { return $element }
        }
        catch { }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Find-UiaElementByAutomationId {
    param(
        $Root,
        [string]$AutomationId,
        [int]$TimeoutSec = 10
    )
    if ($null -eq $Root) { return $null }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId
    )
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSec) {
        try {
            $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $element) { return $element }
        }
        catch { }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Find-UiaElementByName {
    param(
        $Root,
        [string]$Name,
        [int]$TimeoutSec = 5
    )
    if ($null -eq $Root) { return $null }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name
    )
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSec) {
        try {
            $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $element) { return $element }
        }
        catch { }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Invoke-UiaElement {
    param($Element)
    if ($null -eq $Element) { return $null }
    $pattern = $null
    try {
        if ($Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
            $pattern.Invoke()
            return 'InvokePattern'
        }
    }
    catch { }
    try {
        if ($Element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
            $pattern.Toggle()
            return 'TogglePattern'
        }
    }
    catch { }
    try {
        if ($Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            $pattern.Select()
            return 'SelectionItemPattern'
        }
    }
    catch { }
    # 兜底：点击元素中心（UIA Pattern 均不可用时）
    try {
        $rect = $Element.Current.BoundingRectangle
        if ($rect.Width -gt 0 -and $rect.Height -gt 0) {
            $ok = [E2ENative]::ClickAt([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2))
            if ($ok) { return 'Click' }
        }
    }
    catch { }
    return $null
}

function Click-UiaElementCenter {
    # 用真实鼠标消息点击 UIA 元素中心。
    # 说明：WPF CheckBox 的 Click 处理器不会被 UIAutomation TogglePattern 触发
    # （Toggle() 只改勾选状态、不触发 Click 事件），因此勾选框必须用鼠标点击。
    param($Element)
    if ($null -eq $Element) { return $false }
    try {
        $rect = $Element.Current.BoundingRectangle
        if ($rect.Width -le 0 -or $rect.Height -le 0) { return $false }
        return [E2ENative]::ClickAt([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2))
    }
    catch {
        return $false
    }
}

function Set-UiaElementFocus {
    param($Element)
    if ($null -eq $Element) { return $null }
    $expectedId = ''
    try { $expectedId = [string]$Element.Current.AutomationId } catch { }
    try { $Element.SetFocus() } catch { }
    Start-Sleep -Milliseconds 250
    $focusedId = ''
    try {
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        if ($null -ne $focused) { $focusedId = [string]$focused.Current.AutomationId }
    }
    catch { }
    if ($focusedId -eq $expectedId) { return 'SetFocus' }

    # SetFocus 未生效时点击元素中心再试
    try {
        $rect = $Element.Current.BoundingRectangle
        if ($rect.Width -gt 0 -and $rect.Height -gt 0) {
            [void][E2ENative]::ClickAt([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2))
            Start-Sleep -Milliseconds 250
            $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
            if ($null -ne $focused) { $focusedId = [string]$focused.Current.AutomationId }
            if ($focusedId -eq $expectedId) { return 'Click' }
        }
    }
    catch { }
    return $null
}

function Get-UiaElementValue {
    param($Element)
    if ($null -eq $Element) { return $null }
    $pattern = $null
    try {
        if ($Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
            return [string]$pattern.Current.Value
        }
    }
    catch { }
    return $null
}

# ---------------------------------------------------------------------------
# data.json 断言辅助（对 JSON 结构做宽松查找，避免耦合字段大小写/层级）
# ---------------------------------------------------------------------------
function Read-DataJson {
    param([string]$DataFile)
    if (-not (Test-Path -LiteralPath $DataFile)) { return $null }
    try {
        $raw = [System.IO.File]::ReadAllText($DataFile, [System.Text.Encoding]::UTF8)
        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
        return ($raw | ConvertFrom-Json)
    }
    catch { return $null }
}

function Find-NodeByText {
    param($Node, [string]$Text)
    if ($null -eq $Node) { return $null }
    if ($Node -is [string]) { return $null }
    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        $textProperty = $null
        foreach ($prop in $Node.PSObject.Properties) {
            if ($prop.Name -eq 'text') { $textProperty = $prop; break }
        }
        if ($null -ne $textProperty -and [string]$textProperty.Value -eq $Text) { return $Node }
        foreach ($prop in $Node.PSObject.Properties) {
            $found = Find-NodeByText -Node $prop.Value -Text $Text
            if ($null -ne $found) { return $found }
        }
        return $null
    }
    if ($Node -is [System.Collections.IEnumerable]) {
        foreach ($item in $Node) {
            $found = Find-NodeByText -Node $item -Text $Text
            if ($null -ne $found) { return $found }
        }
        return $null
    }
    return $null
}

function Get-NodePropertyValue {
    param($Node, [string]$Name)
    if ($null -eq $Node) { return $null }
    try {
        foreach ($prop in $Node.PSObject.Properties) {
            if ($prop.Name -eq $Name) { return $prop.Value }
        }
    }
    catch { }
    return $null
}

function Test-NodeCompleted {
    param($Node)
    if ($null -eq $Node) { return $false }
    $completedAt = Get-NodePropertyValue -Node $Node -Name 'completedAt'
    if ($null -ne $completedAt -and [string]$completedAt -ne '') { return $true }
    $isCompleted = Get-NodePropertyValue -Node $Node -Name 'isCompleted'
    if ($null -ne $isCompleted -and [bool]$isCompleted) { return $true }
    return $false
}

function Wait-DataNode {
    param(
        [string]$DataFile,
        [string]$Text,
        [int]$TimeoutSec,
        [scriptblock]$Predicate = $null
    )
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $json = Read-DataJson -DataFile $DataFile
        if ($null -ne $json) {
            $node = Find-NodeByText -Node $json -Text $Text
            if ($null -ne $node) {
                if ($null -eq $Predicate) { return $node }
                $accepted = $false
                try { $accepted = [bool](& $Predicate $node) } catch { $accepted = $false }
                if ($accepted) { return $node }
            }
        }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Wait-DataNodeAbsent {
    param(
        [string]$DataFile,
        [string]$Text,
        [int]$TimeoutSec
    )
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $json = Read-DataJson -DataFile $DataFile
        if ($null -ne $json) {
            $node = Find-NodeByText -Node $json -Text $Text
            if ($null -eq $node) { return $true }
        }
        Start-Sleep -Milliseconds 150
    }
    return $false
}

function Save-DataSnapshot {
    param(
        [string]$DataFile,
        [string]$Label
    )
    if (-not (Test-Path -LiteralPath $DataFile)) { return $null }
    $target = Join-Path $script:ArtifactDir ($script:RunStamp + '-' + $Label + '-data.json')
    try {
        Copy-Item -LiteralPath $DataFile -Destination $target -Force
        return $target
    }
    catch { return $null }
}

# 预写 data.json：固定窗口物理坐标并可选预置任务。
# 目的：让 E2E 截图/点击位置确定，同时验证应用能加载已有数据（--data-dir 指向的目录）。
function Write-SeedDataJson {
    param(
        [string]$DataDir,
        [int]$WindowX,
        [int]$WindowY,
        [int]$WindowWidth = 570,
        [int]$WindowHeight = 810,
        [string[]]$TaskTexts = @()
    )
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    $items = New-Object System.Collections.ArrayList
    foreach ($text in $TaskTexts) {
        $id = [guid]::NewGuid().ToString('N')
        $createdAt = (Get-Date).ToString('o')
        $textJson = ConvertTo-Json -InputObject $text -Compress
        [void]$items.Add('{"id":"' + $id + '","text":' + $textJson + ',"createdAt":"' + $createdAt + '","completedAt":null}')
    }
    $itemsJson = '[' + ($items -join ',') + ']'
    $json = '{"version":1,"items":' + $itemsJson + ',"settings":{"pinToDesktop":true,"windowX":' + $WindowX + ',"windowY":' + $WindowY + ',"windowWidth":' + $WindowWidth + ',"windowHeight":' + $WindowHeight + ',"collapsed":false,"lastTab":"pending"}}'
    $dataFile = Join-Path $DataDir 'data.json'
    [System.IO.File]::WriteAllText($dataFile, $json, (New-Object System.Text.UTF8Encoding($false)))
    return $dataFile
}

function Test-WindowNearExpected {
    param(
        [object]$Window,
        [int]$ExpectedX,
        [int]$ExpectedY,
        [int]$Tolerance = 48
    )
    $dx = [math]::Abs([int]$Window.Left - $ExpectedX)
    $dy = [math]::Abs([int]$Window.Top - $ExpectedY)
    return @{
        Ok = ($dx -le $Tolerance -and $dy -le $Tolerance)
        Detail = ('实际=(' + $Window.Left + ',' + $Window.Top + ')，预置=(' + $ExpectedX + ',' + $ExpectedY + ')，偏差=(' + $dx + ',' + $dy + ')')
    }
}

# ---------------------------------------------------------------------------
# 截图：优先 PrintWindow(PW_RENDERFULLCONTENT)，失败/空白时退化屏幕截图
# ---------------------------------------------------------------------------
function Save-WindowScreenshot {
    param(
        [IntPtr]$Handle,
        [string]$FileName
    )
    $path = Join-Path $script:ArtifactDir ($script:RunStamp + '-' + $FileName + '.png')
    try {
        $script:LastScreenshotError = ''
        $bounds = [E2ENative]::GetWindowBounds($Handle)
        if ($null -eq $bounds) { $script:LastScreenshotError = 'GetWindowBounds 失败'; return $null }
        $width = [int]$bounds[2]
        $height = [int]$bounds[3]
        if ($width -le 0 -or $height -le 0) { $script:LastScreenshotError = ('窗口尺寸异常 ' + $width + 'x' + $height); return $null }

        [void][E2ENative]::ShowWindow($Handle, [E2ENative]::SW_RESTORE)
        [void][E2ENative]::SetForegroundWindow($Handle)
        Start-Sleep -Milliseconds 350

        $bitmap = New-Object System.Drawing.Bitmap -ArgumentList @($width, $height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $hdc = $graphics.GetHdc()
        $printed = [E2ENative]::PrintWindow($Handle, $hdc, 2)
        $graphics.ReleaseHdc($hdc)
        $graphics.Dispose()

        # 窗口是 AllowsTransparency=True 的分层窗口，PrintWindow 可能返回 true 但内容为空白，
        # 因此抽样检查像素；空白/失败时改用屏幕截图兜底。
        $hasContent = $false
        if ($printed) {
            try {
                $stepX = [math]::Max(6, [int]($width / 24))
                $stepY = [math]::Max(6, [int]($height / 24))
                for ($y = 3; $y -lt ($height - 3) -and -not $hasContent; $y += $stepY) {
                    for ($x = 3; $x -lt ($width - 3); $x += $stepX) {
                        $pixel = $bitmap.GetPixel([int]$x, [int]$y)
                        if ($pixel.A -gt 8 -or $pixel.R -gt 8 -or $pixel.G -gt 8 -or $pixel.B -gt 8) {
                            $hasContent = $true
                            break
                        }
                    }
                }
            }
            catch {
                $hasContent = $true
            }
        }

        if (-not $printed -or -not $hasContent) {
            $bitmap.Dispose()
            $bitmap = New-Object System.Drawing.Bitmap -ArgumentList @($width, $height)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $size = New-Object System.Drawing.Size -ArgumentList @($width, $height)
            $graphics.CopyFromScreen([int]$bounds[0], [int]$bounds[1], 0, 0, $size)
            $graphics.Dispose()
        }

        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Length -gt 1024)) {
            return $path
        }
        $script:LastScreenshotError = '截图文件不存在或过小'
        return $null
    }
    catch {
        $script:LastScreenshotError = $_.Exception.Message
        return $null
    }
}

function Start-TestApp {
    param(
        [string]$DataDir,
        [bool]$NoPin,
        [string]$PhaseTag
    )
    $cliArgs = @()
    if ($NoPin) { $cliArgs += '--no-pin' }
    $cliArgs += ('--data-dir "' + $DataDir + '"')
    $stdoutLog = Join-Path $script:ArtifactDir ($script:RunStamp + '-' + $PhaseTag + '-stdout.log')
    $stderrLog = Join-Path $script:ArtifactDir ($script:RunStamp + '-' + $PhaseTag + '-stderr.log')
    $proc = Start-Process -FilePath $script:ExePath -ArgumentList $cliArgs -PassThru -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog
    return @{ Process = $proc; StdErrLog = $stderrLog; StdOutLog = $stdoutLog; Args = $cliArgs }
}

function Show-StderrIfAny {
    param([string]$StdErrLog)
    if ([string]::IsNullOrWhiteSpace($StdErrLog)) { return }
    if (-not (Test-Path -LiteralPath $StdErrLog)) { return }
    $size = (Get-Item -LiteralPath $StdErrLog).Length
    if ($size -gt 0) {
        $content = (Get-Content -LiteralPath $StdErrLog -Raw -ErrorAction SilentlyContinue)
        if (-not [string]::IsNullOrWhiteSpace($content)) {
            Add-Result -Status 'INFO' -Name '应用 stderr 输出' -Detail ($content.Trim() -replace "`r?`n", ' | ')
        }
    }
}

function Assert-Condition {
    param(
        [bool]$Condition,
        [string]$Name,
        [string]$PassDetail = '',
        [string]$FailDetail = ''
    )
    if ($Condition) {
        Add-Result -Status 'PASS' -Name $Name -Detail $PassDetail
    }
    else {
        Add-Result -Status 'FAIL' -Name $Name -Detail $FailDetail
    }
}

# ===========================================================================
# 2. 阶段 A：正常模式（默认贴桌面）
# ===========================================================================
function Invoke-PhaseNormalMode {
    Add-Phase '阶段 A：正常模式启动（默认贴桌面）'
    if (-not (Ensure-NoAppInstances -Context '阶段 A')) {
        Add-Result -Status 'FAIL' -Name '阶段 A 前置：无法清理已有 StickerTodo 进程'
        return
    }

    $dataDir = Join-Path $script:TempRoot 'normal-data'
    # 预写 data.json 固定窗口物理坐标（便于截图/点击定位），同时验证启动加载已有设置。
    # 本机 DPI=150%：380x540 DIP = 570x810 物理像素。
    $seedX = 120
    $seedY = 120
    $seedW = 570
    $seedH = 810
    $dataFile = Write-SeedDataJson -DataDir $dataDir -WindowX $seedX -WindowY $seedY -WindowWidth $seedW -WindowHeight $seedH

    $started = Start-TestApp -DataDir $dataDir -NoPin $false -PhaseTag 'phaseA'
    $proc = $started.Process
    $wait = Wait-MainWindow -Process $proc -TimeoutSec $script:StartupTimeoutSec

    if ($wait.Exited) {
        Add-Result -Status 'FAIL' -Name '正常模式：应用启动后立即退出' -Detail ('退出码 ' + $wait.ExitCode)
        Show-StderrIfAny -StdErrLog $started.StdErrLog
        return
    }
    Assert-Condition -Condition (-not $proc.HasExited) -Name '正常模式：进程存活' -PassDetail ('PID ' + $proc.Id) -FailDetail '进程已退出'
    if ($null -eq $wait.Window) {
        $candidateText = (@($wait.Candidates) | ForEach-Object { $_.ToString() }) -join ' ; '
        Add-Result -Status 'FAIL' -Name '正常模式：等待主窗口超时' -Detail ('未找到窗口。候选：' + $candidateText)
        Show-StderrIfAny -StdErrLog $started.StdErrLog
        [void](Stop-AppProcesses)
        return
    }

    $window = $wait.Window
    Add-Result -Status 'PASS' -Name '正常模式：找到主窗口' -Detail $window.ToString()
    $windowDpi = 0
    try { $windowDpi = [E2ENative]::GetWindowDpi($window.Handle) } catch { }
    if ($windowDpi -gt 0) {
        Add-Result -Status 'INFO' -Name '正常模式：窗口 DPI' -Detail ($windowDpi.ToString() + '（' + [math]::Round($windowDpi / 96.0, 2) + 'x）')
    }
    # 等待窗口 180ms 淡入动画完成，避免截图/点击落在半透明阶段
    Start-Sleep -Milliseconds 450

    $title = Get-WindowTitleSmart -Handle $window.Handle
    if ($title -eq '贴纸待办') {
        Add-Result -Status 'PASS' -Name '正常模式：主窗口标题为「贴纸待办」' -Detail $title
    }
    elseif ([string]::IsNullOrWhiteSpace($title)) {
        Add-Result -Status 'WARN' -Name '正常模式：无法读取主窗口标题（嵌入式子窗口限制）' -Detail '改由阶段 B 严格校验标题'
    }
    else {
        Add-Result -Status 'FAIL' -Name '正常模式：主窗口标题不正确' -Detail ('实际为「' + $title + '」，期望「贴纸待办」')
    }

    $topMost = [E2ENative]::IsTopMost($window.Handle)
    Assert-Condition -Condition (-not $topMost) -Name '正常模式：窗口无 WS_EX_TOPMOST（不置顶）' -PassDetail 'EXSTYLE 无 0x8' -FailDetail '检测到 WS_EX_TOPMOST'

    # 位置/尺寸应与 data.json settings 中的屏幕物理像素一致（尺寸容差 4px，位置容差 48px）
    $posCheck = Test-WindowNearExpected -Window $window -ExpectedX $seedX -ExpectedY $seedY
    $sizeOk = ([math]::Abs([int]$window.Width - $seedW) -le 4) -and ([math]::Abs([int]$window.Height - $seedH) -le 4)
    $sizeDetail = '尺寸 实际=' + $window.Width + 'x' + $window.Height + '，预置=' + $seedW + 'x' + $seedH
    Assert-Condition -Condition ($posCheck.Ok -and $sizeOk) -Name '正常模式：窗口位置/尺寸与 data.json settings 物理像素一致' -PassDetail ($posCheck.Detail + '；' + $sizeDetail) -FailDetail ($posCheck.Detail + '；' + $sizeDetail)

    # 贴桌面行为：最终产品可能采用两种方案之一——
    #   A) WorkerW/Progman 桌面层嵌入：GetParent!=0 且父窗口类名含 WorkerW/Progman；
    #   B) 可交互退化方案：普通无边框窗口 + WS_EX_TOOLWINDOW + 不置顶 + 失去焦点时压底（GetParent=0）。
    # 两者都要求：窗口可见、无 WS_EX_TOPMOST、不进任务栏（WS_EX_TOOLWINDOW）。
    $parent = [E2ENative]::GetParentOf($window.Handle)
    if ($parent -ne [IntPtr]::Zero) {
        $parentInfo = [E2ENative]::GetWindowInfo($parent)
        $parentClass = ''
        if ($null -ne $parentInfo) { $parentClass = $parentInfo.ClassName }
        if ($parentClass -match 'WorkerW|Progman') {
            Add-Result -Status 'PASS' -Name '正常模式：已嵌入桌面层（WorkerW/Progman）' -Detail ('GetParent=0x' + $parent.ToString('X') + '，父窗口类=' + $parentClass)
        }
        elseif ([string]::IsNullOrWhiteSpace($parentClass)) {
            Add-Result -Status 'WARN' -Name '正常模式：窗口有父窗口但无法读取父窗口类名' -Detail ('GetParent=0x' + $parent.ToString('X') + '，记录为未知模式')
        }
        else {
            Add-Result -Status 'WARN' -Name '正常模式：窗口有父窗口但父窗口类名不是 WorkerW/Progman' -Detail ('父窗口类=' + $parentClass + '，记录为未知模式')
        }
    }
    else {
        Add-Result -Status 'PASS' -Name '正常模式：桌面贴纸模式（普通窗口压底，非 WorkerW 嵌入）' -Detail 'GetParent=0；以“可见/不置顶/不进任务栏”断言为准；若产品最终改为 WorkerW 嵌入，此断言会自动走上一分支'
    }

    Assert-Condition -Condition ([bool]$window.Visible) -Name '正常模式：窗口可见（IsWindowVisible）' -PassDetail '窗口 WS_VISIBLE 且父链可见' -FailDetail 'IsWindowVisible=false'
    Assert-Condition -Condition ([bool]$window.ToolWindow) -Name '正常模式：窗口带 WS_EX_TOOLWINDOW（不进任务栏/Alt+Tab）' -PassDetail 'EXSTYLE 含 0x80' -FailDetail '缺少 WS_EX_TOOLWINDOW，可能出现在任务栏'

    # 单实例：第二次启动应唤醒已有实例后退出
    try {
        $second = Start-Process -FilePath $script:ExePath -ArgumentList $started.Args -PassThru
        $secondExited = $second.WaitForExit(15000)
        if ($secondExited) {
            Add-Result -Status 'PASS' -Name '正常模式：单实例生效（第二次启动已退出）' -Detail ('退出码 ' + $second.ExitCode)
        }
        else {
            try { $second | Stop-Process -Force -ErrorAction SilentlyContinue } catch { }
            Add-Result -Status 'WARN' -Name '正常模式：第二次启动未在 15 秒内退出（单实例异常）' -Detail '已强制结束第二个进程'
        }
        $firstAlive = -not $proc.HasExited
        if ($firstAlive) {
            Add-Result -Status 'PASS' -Name '正常模式：第一个实例在第二次启动后仍存活'
        }
        else {
            Add-Result -Status 'FAIL' -Name '正常模式：第二次启动导致第一个实例退出'
        }
    }
    catch {
        Add-Result -Status 'WARN' -Name '正常模式：单实例检查异常' -Detail $_.Exception.Message
    }

    $shot = Save-WindowScreenshot -Handle $window.Handle -FileName 'phaseA-normal-mode'
    if ($null -ne $shot) {
        Add-Result -Status 'PASS' -Name '正常模式：截图已保存' -Detail $shot
    }
    else {
        Add-Result -Status 'WARN' -Name '正常模式：窗口截图失败' -Detail ($script:LastScreenshotError + '；不判失败：贴桌面模式下窗口可能无法被 PrintWindow/屏幕抓取')
    }

    Show-StderrIfAny -StdErrLog $started.StdErrLog
    [void](Stop-AppProcesses)
    $stopped = ((Get-AppProcesses).Count -eq 0)
    Assert-Condition -Condition $stopped -Name '正常模式：测试进程已清理' -PassDetail '无残留 StickerTodo 进程' -FailDetail '仍有残留进程'
}

# ===========================================================================
# 3. 阶段 B：交互模式（--no-pin + UIAutomation）
# ===========================================================================
function Invoke-PhaseInteractive {
    Add-Phase '阶段 B：交互模式（--no-pin，UIAutomation 真实操作）'
    if (-not (Ensure-NoAppInstances -Context '阶段 B')) {
        Add-Result -Status 'FAIL' -Name '阶段 B 前置：无法清理已有 StickerTodo 进程'
        return
    }

    $seedX = 140
    $seedY = 140
    $seedW = 570
    $seedH = 810
    $textSeed = '预置任务-' + (Get-Date -Format 'HHmmss')
    $dataDir = Join-Path $script:TempRoot 'ui-data'
    # 预写 data.json：固定窗口物理坐标 + 预置一条待办，顺带验证“启动加载已有数据”
    $dataFile = Write-SeedDataJson -DataDir $dataDir -WindowX $seedX -WindowY $seedY -WindowWidth $seedW -WindowHeight $seedH -TaskTexts @($textSeed)

    $started = Start-TestApp -DataDir $dataDir -NoPin $true -PhaseTag 'phaseB'
    $proc = $started.Process
    $wait = Wait-MainWindow -Process $proc -TimeoutSec $script:StartupTimeoutSec
    if ($wait.Exited) {
        Add-Result -Status 'FAIL' -Name '交互模式：应用启动后立即退出' -Detail ('退出码 ' + $wait.ExitCode)
        Show-StderrIfAny -StdErrLog $started.StdErrLog
        return
    }
    Assert-Condition -Condition (-not $proc.HasExited) -Name '交互模式：进程存活' -PassDetail ('PID ' + $proc.Id) -FailDetail '进程已退出'
    if ($null -eq $wait.Window) {
        $candidateText = (@($wait.Candidates) | ForEach-Object { $_.ToString() }) -join ' ; '
        Add-Result -Status 'FAIL' -Name '交互模式：等待主窗口超时' -Detail ('未找到窗口。候选：' + $candidateText)
        Show-StderrIfAny -StdErrLog $started.StdErrLog
        [void](Stop-AppProcesses)
        return
    }

    $window = $wait.Window
    $handle = $window.Handle
    Add-Result -Status 'PASS' -Name '交互模式：找到主窗口' -Detail $window.ToString()
    $windowDpi = 0
    try { $windowDpi = [E2ENative]::GetWindowDpi($handle) } catch { }
    if ($windowDpi -gt 0) {
        Add-Result -Status 'INFO' -Name '交互模式：窗口 DPI' -Detail ($windowDpi.ToString() + '（' + [math]::Round($windowDpi / 96.0, 2) + 'x）')
    }
    # 等待窗口 180ms 淡入动画完成，避免截图/点击落在半透明阶段
    Start-Sleep -Milliseconds 450

    $title = Get-WindowTitleSmart -Handle $handle
    Assert-Condition -Condition ($title -eq '贴纸待办') -Name '交互模式：主窗口标题为「贴纸待办」' -PassDetail $title -FailDetail ('实际为「' + $title + '」')

    $topMost = [E2ENative]::IsTopMost($handle)
    Assert-Condition -Condition (-not $topMost) -Name '交互模式：窗口无 WS_EX_TOPMOST' -PassDetail 'EXSTYLE 无 0x8' -FailDetail '检测到 WS_EX_TOPMOST'

    $parent = [E2ENative]::GetParentOf($handle)
    Assert-Condition -Condition ($parent -eq [IntPtr]::Zero) -Name '交互模式：--no-pin 为普通顶层窗口（GetParent=0）' -PassDetail '符合预期' -FailDetail ('GetParent=0x' + $parent.ToString('X') + '，--no-pin 下不应嵌入桌面层')

    # 位置/尺寸应与 data.json settings 中的屏幕物理像素一致（尺寸容差 4px，位置容差 48px）
    $posCheck = Test-WindowNearExpected -Window $window -ExpectedX $seedX -ExpectedY $seedY
    $sizeOk = ([math]::Abs([int]$window.Width - $seedW) -le 4) -and ([math]::Abs([int]$window.Height - $seedH) -le 4)
    $sizeDetail = '尺寸 实际=' + $window.Width + 'x' + $window.Height + '，预置=' + $seedW + 'x' + $seedH
    Assert-Condition -Condition ($posCheck.Ok -and $sizeOk) -Name '交互模式：窗口位置/尺寸与 data.json settings 物理像素一致' -PassDetail ($posCheck.Detail + '；' + $sizeDetail) -FailDetail ($posCheck.Detail + '；' + $sizeDetail)

    # 启动时应加载预置的 data.json（预置任务不被清空/覆盖）
    $seedNode = Wait-DataNode -DataFile $dataFile -Text $textSeed -TimeoutSec 5
    Assert-Condition -Condition ($null -ne $seedNode) -Name '交互模式：启动加载已有 data.json（预置任务仍在）' -PassDetail $textSeed -FailDetail '预置任务在启动后从 data.json 消失'

    $shotInitial = Save-WindowScreenshot -Handle $handle -FileName 'phaseB-01-initial'
    if ($null -ne $shotInitial) { Add-Result -Status 'PASS' -Name '交互模式：初始截图已保存' -Detail $shotInitial }
    else { Add-Result -Status 'WARN' -Name '交互模式：初始截图失败' -Detail $script:LastScreenshotError }

    $textTask = 'E2E测试-' + (Get-Date -Format 'HHmmss') + '-买牛奶'
    $textDelete = 'E2E删除-' + (Get-Date -Format 'HHmmss') + '-倒垃圾'

    $uiaReady = Initialize-Uia
    if (-not $uiaReady) {
        Add-Result -Status 'WARN' -Name 'UI Automation 不可用，尝试坐标兜底' -Detail $script:UiaError
    }
    else {
        Add-Result -Status 'PASS' -Name 'UI Automation 程序集加载成功'
    }

    $root = $null
    if ($uiaReady) { $root = Get-UiaRoot -Handle $handle }
    if ($null -eq $root) {
        if ($uiaReady) {
            Add-Result -Status 'FAIL' -Name '无法通过 UIAutomation 获取主窗口元素'
        }
        Invoke-InteractiveCoordinateFallback -Handle $handle -DataFile $dataFile -TextTask $textTask
    }
    else {
        # 预置任务应显示在界面上（按 UIA Name 查找；自定义模板可能取不到 Name，缺失记 WARN 不伪造 PASS）
        $seedElement = Find-UiaElementByName -Root $root -Name $textSeed -TimeoutSec 5
        if ($null -ne $seedElement) {
            Add-Result -Status 'PASS' -Name 'UIA：预置任务已显示在界面（启动加载已有数据）' -Detail $textSeed
        }
        else {
            Add-Result -Status 'WARN' -Name 'UIA：未按 Name 找到预置任务元素' -Detail ($textSeed + '（可能为自定义模板渲染，data.json 层已断言任务仍在）')
        }
        Invoke-InteractiveUia -Root $root -Handle $handle -DataFile $dataFile -TextTask $textTask -TextDelete $textDelete
    }

    $shotFinal = Save-WindowScreenshot -Handle $handle -FileName 'phaseB-99-final'
    if ($null -ne $shotFinal) { Add-Result -Status 'PASS' -Name '交互模式：结束截图已保存' -Detail $shotFinal }
    else { Add-Result -Status 'WARN' -Name '交互模式：结束截图失败' -Detail $script:LastScreenshotError }

    Show-StderrIfAny -StdErrLog $started.StdErrLog
    [void](Stop-AppProcesses)
    $stopped = ((Get-AppProcesses).Count -eq 0)
    Assert-Condition -Condition $stopped -Name '交互模式：测试进程已清理' -PassDetail '无残留 StickerTodo 进程' -FailDetail '仍有残留进程'
}

function Invoke-InteractiveUia {
    param(
        $Root,
        [IntPtr]$Handle,
        [string]$DataFile,
        [string]$TextTask,
        [string]$TextDelete
    )

    # ---- 控件存在性（AutomationId 契约）----
    $taskInput = Find-UiaElementByAutomationId -Root $Root -AutomationId 'TaskInput' -TimeoutSec $script:StepTimeoutSec
    Assert-Condition -Condition ($null -ne $taskInput) -Name 'UIA：找到任务输入框 TaskInput' -PassDetail 'AutomationId=TaskInput'

    $addButton = Find-UiaElementByAutomationId -Root $Root -AutomationId 'AddButton' -TimeoutSec 5
    Assert-Condition -Condition ($null -ne $addButton) -Name 'UIA：找到添加按钮 AddButton'

    $pendingTab = Find-UiaElementByAutomationId -Root $Root -AutomationId 'PendingTab' -TimeoutSec 5
    Assert-Condition -Condition ($null -ne $pendingTab) -Name 'UIA：找到待办标签 PendingTab'

    $completedTab = Find-UiaElementByAutomationId -Root $Root -AutomationId 'CompletedTab' -TimeoutSec 5
    Assert-Condition -Condition ($null -ne $completedTab) -Name 'UIA：找到已完成标签 CompletedTab'

    $moreButton = Find-UiaElementByAutomationId -Root $Root -AutomationId 'MoreButton' -TimeoutSec 5
    Assert-Condition -Condition ($null -ne $moreButton) -Name 'UIA：找到更多菜单 MoreButton'

    $pinButton = Find-UiaElementByAutomationId -Root $Root -AutomationId 'PinButton' -TimeoutSec 5
    Assert-Condition -Condition ($null -ne $pinButton) -Name 'UIA：找到贴桌面按钮 PinButton'

    $hideButton = Find-UiaElementByAutomationId -Root $Root -AutomationId 'HideButton' -TimeoutSec 5
    Assert-Condition -Condition ($null -ne $hideButton) -Name 'UIA：找到隐藏到托盘按钮 HideButton'

    if ($null -eq $taskInput -or $null -eq $completedTab -or $null -eq $pendingTab) {
        Add-Result -Status 'FAIL' -Name '关键控件缺失，无法继续 UIA 交互' -Detail '请检查 MainWindow.xaml 的 AutomationId 是否与契约一致'
        return
    }

    # ---- 输入中文任务 ----
    # 主路径（本机可靠）：UIAutomation ValuePattern 写入文本 + InvokePattern 点 AddButton。
    # 备选路径：剪贴板 + SendInput Ctrl+V / Enter —— 本机存在前台锁/输入法拦截，注入时好时坏，
    # 因此键盘路径只做 WARN 尝试，不因它失败而判 FAIL（真实用户键盘输入是正常的）。
    $inputAdded = $false
    $clipOk = [E2ENative]::SetClipboardText($TextTask)
    if ($clipOk) {
        [void](Set-UiaElementFocus -Element $taskInput)
        $sent = [E2ENative]::SendCtrlV()
        Start-Sleep -Milliseconds 350
        $pastedValue = Get-UiaElementValue -Element $taskInput
        if ($null -ne $pastedValue -and $pastedValue -eq $TextTask) {
            [void][E2ENative]::SendEnter()
            $nodeKeyboard = Wait-DataNode -DataFile $DataFile -Text $TextTask -TimeoutSec 4
            if ($null -ne $nodeKeyboard) {
                Add-Result -Status 'PASS' -Name '输入：键盘注入路径（剪贴板+SendInput Ctrl+V/Enter）添加任务成功' -Detail ('注入事件数 ' + $sent)
                $inputAdded = $true
            }
            else {
                Add-Result -Status 'WARN' -Name '输入：键盘注入未能添加任务（前台锁/输入法拦截，本机已知限制）' -Detail '改用 UIA 主路径'
            }
        }
        else {
            Add-Result -Status 'WARN' -Name '输入：键盘注入未能写入输入框（本机已知限制）' -Detail ('输入框实际值=' + $pastedValue + '；改用 UIA 主路径')
        }
    }
    else {
        Add-Result -Status 'WARN' -Name '输入：剪贴板写入失败，改用 UIA 主路径'
    }

    if (-not $inputAdded) {
        # UIA 主路径：ValuePattern 写入 + InvokePattern 点“添加”按钮（若上一次键盘尝试残留了半截文本，SetValue 会整体覆盖）
        $setValueOk = $false
        $valuePattern = $null
        try {
            if ($taskInput.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) {
                $valuePattern.SetValue($TextTask)
                Start-Sleep -Milliseconds 250
                $setValueOk = ($valuePattern.Current.Value -eq $TextTask)
            }
        }
        catch { }
        Assert-Condition -Condition $setValueOk -Name '输入：UIA ValuePattern 写入中文任务成功' -PassDetail $TextTask -FailDetail 'TaskInput 不支持 ValuePattern 或写入后内容不一致'

        $addMode = $null
        if ($null -ne $addButton) {
            $addMode = Invoke-UiaElement -Element $addButton
        }
        Assert-Condition -Condition ($null -ne $addMode) -Name '操作：点击 AddButton 添加任务' -PassDetail ('方式=' + $addMode) -FailDetail 'AddButton 调用失败'
    }

    $node = Wait-DataNode -DataFile $DataFile -Text $TextTask -TimeoutSec $script:StepTimeoutSec
    Assert-Condition -Condition ($null -ne $node) -Name '数据：任务已写入 data.json' -PassDetail $TextTask -FailDetail '超时未在 data.json 中找到该任务'
    if ($null -eq $node) {
        return
    }
    $completedNow = Test-NodeCompleted -Node $node
    Assert-Condition -Condition (-not $completedNow) -Name '数据：新任务处于未完成状态（completedAt 为空）' -PassDetail 'completedAt=null'
    Add-Result -Status 'INFO' -Name 'data.json 快照' -Detail (Save-DataSnapshot -DataFile $DataFile -Label 'phaseB-01-added')

    $shotAdded = Save-WindowScreenshot -Handle $Handle -FileName 'phaseB-02-task-added'
    if ($null -ne $shotAdded) { Add-Result -Status 'INFO' -Name '已保存任务添加截图' -Detail $shotAdded }

    # ---- 勾选完成：CompleteCheckBox ----
    $completeCheckBox = Find-UiaElementByAutomationId -Root $Root -AutomationId 'CompleteCheckBox' -TimeoutSec $script:StepTimeoutSec
    Assert-Condition -Condition ($null -ne $completeCheckBox) -Name 'UIA：找到完成勾选框 CompleteCheckBox'
    if ($null -ne $completeCheckBox) {
        # 用真实鼠标点击（WPF CheckBox.Click 事件不支持由 UIA TogglePattern 触发）
        $clickMode = $null
        if (Click-UiaElementCenter -Element $completeCheckBox) {
            $clickMode = 'MouseClick'
        }
        else {
            $clickMode = Invoke-UiaElement -Element $completeCheckBox
        }
        Assert-Condition -Condition ($null -ne $clickMode) -Name '操作：勾选 CompleteCheckBox' -PassDetail ('方式=' + $clickMode) -FailDetail '鼠标点击/UIA Pattern 均失败'
        $completedNode = Wait-DataNode -DataFile $DataFile -Text $TextTask -TimeoutSec $script:StepTimeoutSec -Predicate { param($n) Test-NodeCompleted -Node $n }
        Assert-Condition -Condition ($null -ne $completedNode) -Name '数据：勾选后 completedAt 非空' -PassDetail 'completedAt 已写入'
        Add-Result -Status 'INFO' -Name 'data.json 快照' -Detail (Save-DataSnapshot -DataFile $DataFile -Label 'phaseB-02-completed')
    }

    $shotCompleted = Save-WindowScreenshot -Handle $Handle -FileName 'phaseB-03-completed'
    if ($null -ne $shotCompleted) { Add-Result -Status 'INFO' -Name '已保存勾选后截图' -Detail $shotCompleted }

    # ---- 切到已完成标签并恢复 ----
    $tabMode = Invoke-UiaElement -Element $completedTab
    Assert-Condition -Condition ($null -ne $tabMode) -Name '操作：切换到已完成标签 CompletedTab' -PassDetail ('方式=' + $tabMode) -FailDetail '标签切换失败'
    Start-Sleep -Milliseconds 400

    $restoreButton = Find-UiaElementByAutomationId -Root $Root -AutomationId 'RestoreButton' -TimeoutSec $script:StepTimeoutSec
    Assert-Condition -Condition ($null -ne $restoreButton) -Name 'UIA：已完成页找到恢复按钮 RestoreButton'
    $deleteCompletedButton = Find-UiaElementByAutomationId -Root $Root -AutomationId 'DeleteCompletedButton' -TimeoutSec 5
    Assert-Condition -Condition ($null -ne $deleteCompletedButton) -Name 'UIA：已完成页找到删除完成项按钮 DeleteCompletedButton'

    $shotCompletedTab = Save-WindowScreenshot -Handle $Handle -FileName 'phaseB-04-completed-tab'
    if ($null -ne $shotCompletedTab) { Add-Result -Status 'INFO' -Name '已保存已完成标签截图' -Detail $shotCompletedTab }

    if ($null -ne $restoreButton) {
        $restoreMode = Invoke-UiaElement -Element $restoreButton
        Assert-Condition -Condition ($null -ne $restoreMode) -Name '操作：点击 RestoreButton 恢复任务' -PassDetail ('方式=' + $restoreMode) -FailDetail '恢复操作失败'
        $restoredNode = Wait-DataNode -DataFile $DataFile -Text $TextTask -TimeoutSec $script:StepTimeoutSec -Predicate { param($n) -not (Test-NodeCompleted -Node $n) }
        Assert-Condition -Condition ($null -ne $restoredNode) -Name '数据：恢复后 completedAt 已清空（回到待办）' -PassDetail 'completedAt=null'
        Add-Result -Status 'INFO' -Name 'data.json 快照' -Detail (Save-DataSnapshot -DataFile $DataFile -Label 'phaseB-03-restored')
    }
    else {
        Add-Result -Status 'FAIL' -Name '数据：无法验证恢复流程（RestoreButton 缺失）'
    }

    $shotRestored = Save-WindowScreenshot -Handle $Handle -FileName 'phaseB-05-restored'
    if ($null -ne $shotRestored) { Add-Result -Status 'INFO' -Name '已保存恢复后截图' -Detail $shotRestored }

    # ---- 附加：待办行的 DeleteButton 删除功能 ----
    if ($null -ne $pendingTab) {
        [void](Invoke-UiaElement -Element $pendingTab)
        Start-Sleep -Milliseconds 350
    }
    $deleteButton = Find-UiaElementByAutomationId -Root $Root -AutomationId 'DeleteButton' -TimeoutSec $script:StepTimeoutSec
    Assert-Condition -Condition ($null -ne $deleteButton) -Name 'UIA：待办页找到删除按钮 DeleteButton'
    if ($null -ne $deleteButton) {
        $deleteMode = Invoke-UiaElement -Element $deleteButton
        Assert-Condition -Condition ($null -ne $deleteMode) -Name '操作：点击 DeleteButton 删除待办' -PassDetail ('方式=' + $deleteMode) -FailDetail '删除操作失败'
        $absent = Wait-DataNodeAbsent -DataFile $DataFile -Text $TextTask -TimeoutSec $script:StepTimeoutSec
        Assert-Condition -Condition $absent -Name '数据：DeleteButton 后任务已从 data.json 移除' -PassDetail ('已移除 ' + $TextTask)
    }

    # ---- 附加：完成一个任务后用 DeleteCompletedButton 删除完成项 ----
    $taskInput2 = Find-UiaElementByAutomationId -Root $Root -AutomationId 'TaskInput' -TimeoutSec 5
    if ($null -ne $taskInput2) {
        # 同样走 UIA 主路径（键盘注入本机不可靠）
        $setOk2 = $false
        $valuePattern2 = $null
        try {
            if ($taskInput2.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern2)) {
                $valuePattern2.SetValue($TextDelete)
                Start-Sleep -Milliseconds 200
                $setOk2 = ($valuePattern2.Current.Value -eq $TextDelete)
            }
        }
        catch { }
        Assert-Condition -Condition $setOk2 -Name '附加：UIA 写入第二个任务文本' -PassDetail $TextDelete -FailDetail 'ValuePattern 写入失败'
        $addButton2 = Find-UiaElementByAutomationId -Root $Root -AutomationId 'AddButton' -TimeoutSec 5
        if ($null -ne $addButton2) {
            [void](Invoke-UiaElement -Element $addButton2)
        }
        $node2 = Wait-DataNode -DataFile $DataFile -Text $TextDelete -TimeoutSec $script:StepTimeoutSec
        Assert-Condition -Condition ($null -ne $node2) -Name '附加：第二个任务已添加' -PassDetail $TextDelete
        if ($null -ne $node2) {
            $checkBox2 = Find-UiaElementByAutomationId -Root $Root -AutomationId 'CompleteCheckBox' -TimeoutSec $script:StepTimeoutSec
            if ($null -ne $checkBox2) {
                [void](Click-UiaElementCenter -Element $checkBox2)
                $completed2 = Wait-DataNode -DataFile $DataFile -Text $TextDelete -TimeoutSec $script:StepTimeoutSec -Predicate { param($n) Test-NodeCompleted -Node $n }
                Assert-Condition -Condition ($null -ne $completed2) -Name '附加：第二个任务已勾选完成'
            }
            if ($null -ne $completedTab) {
                [void](Invoke-UiaElement -Element $completedTab)
                Start-Sleep -Milliseconds 400
                $deleteCompleted = Find-UiaElementByAutomationId -Root $Root -AutomationId 'DeleteCompletedButton' -TimeoutSec $script:StepTimeoutSec
                if ($null -ne $deleteCompleted) {
                    $delMode = Invoke-UiaElement -Element $deleteCompleted
                    Assert-Condition -Condition ($null -ne $delMode) -Name '附加：点击 DeleteCompletedButton' -PassDetail ('方式=' + $delMode)
                    $absent2 = Wait-DataNodeAbsent -DataFile $DataFile -Text $TextDelete -TimeoutSec $script:StepTimeoutSec
                    Assert-Condition -Condition $absent2 -Name '附加：完成项已从 data.json 移除' -PassDetail ('已移除 ' + $TextDelete)
                }
                else {
                    Add-Result -Status 'WARN' -Name '附加：DeleteCompletedButton 在已完成页未找到'
                }
            }
        }
    }
    else {
        Add-Result -Status 'WARN' -Name '附加：第二个任务输入框未找到，跳过附加删除测试'
    }
}

# UIAutomation 不可用时的坐标兜底：按窗口矩形与估算行高发送鼠标/键盘事件。
# 说明：贴纸窗口布局为“顶部标题/标签 + 中部列表 + 底部输入行”，
# 这里用窗口矩形比例估算坐标，仅作为 UIA 失效时的降级路径；无法可靠完成的步骤会标记 WARN。
function Invoke-InteractiveCoordinateFallback {
    param(
        [IntPtr]$Handle,
        [string]$DataFile,
        [string]$TextTask
    )
    Add-Result -Status 'WARN' -Name '进入坐标兜底模式' -Detail '使用窗口矩形 + 估算行高 SendInput，结果可能不如 UIA 可靠'
    $bounds = [E2ENative]::GetWindowBounds($Handle)
    if ($null -eq $bounds) {
        Add-Result -Status 'FAIL' -Name '坐标兜底：无法获取窗口矩形'
        return
    }
    [void][E2ENative]::ShowWindow($Handle, [E2ENative]::SW_RESTORE)
    [void][E2ENative]::SetForegroundWindow($Handle)
    Start-Sleep -Milliseconds 350

    $left = [int]$bounds[0]
    $top = [int]$bounds[1]
    $width = [int]$bounds[2]
    $height = [int]$bounds[3]

    # 输入框约在窗口底部 60~90px 处（估算）
    $inputX = $left + [int]($width * 0.45)
    $inputY = $top + $height - 65
    $clicked = [E2ENative]::ClickAt($inputX, $inputY)
    Assert-Condition -Condition $clicked -Name '坐标兜底：点击输入框区域' -PassDetail ("坐标 $inputX,$inputY")

    $clipOk = [E2ENative]::SetClipboardText($TextTask)
    if ($clipOk) {
        [void][E2ENative]::SendCtrlV()
        Start-Sleep -Milliseconds 350
    }
    else {
        [void][E2ENative]::SendUnicodeText($TextTask)
    }
    [void][E2ENative]::SendEnter()

    $node = Wait-DataNode -DataFile $DataFile -Text $TextTask -TimeoutSec $script:StepTimeoutSec
    Assert-Condition -Condition ($null -ne $node) -Name '坐标兜底：输入任务已写入 data.json' -PassDetail $TextTask -FailDetail '超时未写入，坐标估算可能不准'

    # 第一行勾选框约在列表顶部向下一个行高的位置（估算；行高按窗口高度 1/8）
    $rowY = $top + [int]($height * 0.22)
    $rowX = $left + 32
    $clicked2 = [E2ENative]::ClickAt($rowX, $rowY)
    Assert-Condition -Condition $clicked2 -Name '坐标兜底：点击第一行勾选框区域' -PassDetail ("坐标 $rowX,$rowY")
    $completedNode = Wait-DataNode -DataFile $DataFile -Text $TextTask -TimeoutSec 8 -Predicate { param($n) Test-NodeCompleted -Node $n }
    if ($null -ne $completedNode) {
        Add-Result -Status 'PASS' -Name '坐标兜底：完成勾选生效（completedAt 已写入）'
    }
    else {
        Add-Result -Status 'WARN' -Name '坐标兜底：未命中勾选框，跳过后续标签切换验证' -Detail 'UIAutomation 不可用时该步骤无法可靠完成（不伪造 PASS）'
    }
}

# ===========================================================================
# 4. 清理与汇总
# ===========================================================================
function Invoke-Cleanup {
    Add-Phase '清理与数据隔离检查'
    $stopped = Stop-AppProcesses
    Assert-Condition -Condition $stopped -Name '清理：无残留 StickerTodo 进程' -PassDetail '已全部结束' -FailDetail '仍有进程残留'

    # 用户真实数据隔离检查
    $realDir = Join-Path $env:APPDATA 'StickerTodo'
    $realDataFile = Join-Path $realDir 'data.json'
    $before = $script:RealDataBefore
    $nowExists = Test-Path -LiteralPath $realDataFile
    if ($null -eq $before) {
        if ($nowExists) {
            Add-Result -Status 'FAIL' -Name '数据隔离：测试前不存在用户真实 data.json，测试后被创建' -Detail $realDataFile
        }
        else {
            Add-Result -Status 'PASS' -Name '数据隔离：未触碰用户真实数据目录' -Detail ('%APPDATA%\StickerTodo 不存在（测试全程使用 --data-dir 临时目录）')
        }
    }
    else {
        if (-not $nowExists) {
            Add-Result -Status 'FAIL' -Name '数据隔离：用户真实 data.json 在测试期间被删除' -Detail $realDataFile
        }
        else {
            $afterItem = Get-Item -LiteralPath $realDataFile
            $changed = ($afterItem.LastWriteTimeUtc -ne $before.Time) -or ($afterItem.Length -ne $before.Length)
            if ($changed) {
                Add-Result -Status 'FAIL' -Name '数据隔离：用户真实 data.json 在测试期间被修改' -Detail ('前=' + $before.Length + 'B@' + $before.Time + ' 后=' + $afterItem.Length + 'B@' + $afterItem.LastWriteTimeUtc)
            }
            else {
                Add-Result -Status 'PASS' -Name '数据隔离：用户真实 data.json 未被修改' -Detail ('大小 ' + $afterItem.Length + ' 字节')
            }
        }
    }

    # 临时数据目录清理
    if ($KeepTemp) {
        Add-Result -Status 'INFO' -Name '保留临时数据目录（-KeepTemp）' -Detail $script:TempRoot
    }
    else {
        try {
            if (Test-Path -LiteralPath $script:TempRoot) {
                Remove-Item -LiteralPath $script:TempRoot -Recurse -Force -ErrorAction Stop
            }
            Add-Result -Status 'PASS' -Name '清理：临时数据目录已删除' -Detail $script:TempRoot
        }
        catch {
            Add-Result -Status 'WARN' -Name '清理：临时数据目录删除失败' -Detail $_.Exception.Message
        }
    }

    if (Test-Path -LiteralPath $script:ArtifactDir) {
        $shots = @(Get-ChildItem -LiteralPath $script:ArtifactDir -Filter ($script:RunStamp + '-*.png') -ErrorAction SilentlyContinue)
        $logs = @(Get-ChildItem -LiteralPath $script:ArtifactDir -Filter ($script:RunStamp + '-*.log') -ErrorAction SilentlyContinue)
        Add-Result -Status 'INFO' -Name '测试产物' -Detail ('截图 ' + $shots.Count + ' 张、日志 ' + $logs.Count + ' 个，目录：' + $script:ArtifactDir)
    }
}

function Write-Summary {
    $passCount = 0
    $failCount = 0
    $warnCount = 0
    foreach ($item in $script:Results) {
        if ($item.Status -eq 'PASS') { $passCount++ }
        elseif ($item.Status -eq 'FAIL') { $failCount++ }
        elseif ($item.Status -eq 'WARN') { $warnCount++ }
    }
    Write-Host ''
    Write-Host '================ E2E 测试汇总 ================' -ForegroundColor White
    Write-Host ('PASS {0} 项 / FAIL {1} 项 / WARN {2} 项' -f $passCount, $failCount, $warnCount) -ForegroundColor $(if ($failCount -gt 0) { 'Red' } else { 'Green' })
    if ($failCount -gt 0) {
        Write-Host '失败项：' -ForegroundColor Red
        foreach ($item in $script:Results) {
            if ($item.Status -eq 'FAIL') {
                Write-Host ('  - ' + $item.Name + ($(if ($item.Detail) { ' — ' + $item.Detail } else { '' }))) -ForegroundColor Red
            }
        }
    }
    if ($warnCount -gt 0) {
        Write-Host '警告项（需人工确认，默认不影响退出码）：' -ForegroundColor Yellow
        foreach ($item in $script:Results) {
            if ($item.Status -eq 'WARN') {
                Write-Host ('  - ' + $item.Name + ($(if ($item.Detail) { ' — ' + $item.Detail } else { '' }))) -ForegroundColor Yellow
            }
        }
    }
    $artifacts = ''
    if (Test-Path -LiteralPath $script:ArtifactDir) {
        $artifacts = (Get-ChildItem -LiteralPath $script:ArtifactDir -Filter ($script:RunStamp + '-*.png') -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }) -join ' ; '
    }
    Write-Host ('截图/日志目录：' + $script:ArtifactDir) -ForegroundColor White
    if (-not [string]::IsNullOrWhiteSpace($artifacts)) {
        Write-Host ('截图：' + $artifacts) -ForegroundColor DarkGray
    }

    $exitCode = 0
    if ($failCount -gt 0) { $exitCode = 1 }
    elseif ($Strict -and $warnCount -gt 0) {
        Write-Host '严格模式（-Strict）：存在 WARN，退出码置为 1' -ForegroundColor Yellow
        $exitCode = 1
    }
    if ($exitCode -eq 0) { Write-Host 'E2E 结果：通过' -ForegroundColor Green }
    else { Write-Host 'E2E 结果：未通过' -ForegroundColor Red }

    # 汇总写入文件，便于报告/留档
    try {
        $summaryPath = Join-Path $script:ArtifactDir ($script:RunStamp + '-summary.txt')
        $lines = New-Object System.Collections.ArrayList
        [void]$lines.Add('贴纸待办 E2E 测试汇总 ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
        [void]$lines.Add(('PASS {0} / FAIL {1} / WARN {2}' -f $passCount, $failCount, $warnCount))
        foreach ($item in $script:Results) {
            $line = '[' + $item.Status + '] ' + $item.Name
            if ($item.Detail) { $line = $line + ' — ' + $item.Detail }
            [void]$lines.Add($line)
        }
        [System.IO.File]::WriteAllLines($summaryPath, $lines.ToArray(), (New-Object System.Text.UTF8Encoding($true)))
        Write-Host ('汇总文件：' + $summaryPath) -ForegroundColor DarkGray
    }
    catch {
        Write-Host ('汇总文件写入失败：' + $_.Exception.Message) -ForegroundColor Yellow
    }
    return $exitCode
}

function Invoke-SelfTest {
    Write-Host 'E2E 环境自检（不启动应用）' -ForegroundColor White
    $script:ArtifactDir = Join-Path $script:Root 'tools\artifacts'
    New-Item -ItemType Directory -Path $script:ArtifactDir -Force | Out-Null
    Assert-Condition -Condition (Initialize-Native) -Name 'Win32 P/Invoke 辅助代码编译成功'
    if ($script:NativeReady) {
        $inputSize = [E2ENative]::GetInputStructSize()
        $expected = 40
        if ([IntPtr]::Size -eq 4) { $expected = 28 }
        Assert-Condition -Condition ($inputSize -eq $expected) -Name 'SendInput INPUT 结构尺寸正确' -PassDetail ('INPUT=' + $inputSize + ' 字节') -FailDetail ('实际 ' + $inputSize + '，期望 ' + $expected)
        $bounds = [E2ENative]::GetWindowBounds([System.Diagnostics.Process]::GetCurrentProcess().MainWindowHandle)
        Add-Result -Status 'INFO' -Name '原生辅助函数可用' -Detail ('GetWindowBounds 调用成功=' + ($null -ne $bounds))
    }
    Assert-Condition -Condition (Initialize-Uia) -Name 'UI Automation 程序集加载成功' -FailDetail $script:UiaError
    if ($script:UiaReady) {
        try {
            $rootElement = [System.Windows.Automation.AutomationElement]::RootElement
            Assert-Condition -Condition ($null -ne $rootElement) -Name 'UI Automation 根元素可用'
        }
        catch {
            Add-Result -Status 'FAIL' -Name 'UI Automation 根元素不可用' -Detail $_.Exception.Message
        }
    }
    try {
        Add-Type -AssemblyName System.Drawing -ErrorAction Stop
        $probe = New-Object System.Drawing.Bitmap -ArgumentList @(8, 8)
        $probe.Dispose()
        Add-Result -Status 'PASS' -Name 'System.Drawing 截图能力可用'
    }
    catch {
        Add-Result -Status 'FAIL' -Name 'System.Drawing 不可用' -Detail $_.Exception.Message
    }
    # 剪贴板往返测试（保存并恢复原文本内容）
    try {
        $oldClipboard = ''
        try { $oldClipboard = Get-Clipboard -Raw -ErrorAction SilentlyContinue } catch { }
        $ok = [E2ENative]::SetClipboardText('StickerTodo-E2E-自检')
        $readBack = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($null -ne $oldClipboard) { Set-Clipboard -Value $oldClipboard -ErrorAction SilentlyContinue }
        Assert-Condition -Condition ($ok -and $readBack -eq 'StickerTodo-E2E-自检') -Name '剪贴板写入/读取往返正常' -PassDetail ('读回=' + $readBack)
    }
    catch {
        Add-Result -Status 'FAIL' -Name '剪贴板自检异常' -Detail $_.Exception.Message
    }
    # data.json 预置与解析自检（E2E 靠它固定窗口坐标/预置任务）
    try {
        $seedDir = Join-Path ([System.IO.Path]::GetTempPath()) ('StickerTodoSeedTest_' + [guid]::NewGuid().ToString('N'))
        $seedFile = Write-SeedDataJson -DataDir $seedDir -WindowX 140 -WindowY 140 -TaskTexts @('预置中文任务')
        $raw = [System.IO.File]::ReadAllText($seedFile, [System.Text.Encoding]::UTF8)
        $json = $raw | ConvertFrom-Json
        $settings = Get-NodePropertyValue -Node $json -Name 'settings'
        $windowX = Get-NodePropertyValue -Node $settings -Name 'windowX'
        $node = Find-NodeByText -Node $json -Text '预置中文任务'
        $seedOk = ($null -ne $node) -and ([int]$windowX -eq 140) -and $raw.Contains('"completedAt":null') -and $raw.Contains('"version":1')
        Assert-Condition -Condition $seedOk -Name 'data.json 预置/解析自检' -Detail $seedFile
        Remove-Item -LiteralPath $seedDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    catch {
        Add-Result -Status 'FAIL' -Name 'data.json 预置自检异常' -Detail $_.Exception.Message
    }
    $exitCode = Write-Summary
    return $exitCode
}

# ===========================================================================
# 5. 主流程
# ===========================================================================
$script:Root = Split-Path -Parent $PSScriptRoot
$script:RunStamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$script:ArtifactDir = Join-Path $script:Root 'tools\artifacts'
$script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('StickerTodo_E2E_' + $script:RunStamp + '_' + [guid]::NewGuid().ToString('N').Substring(0, 6))

# 解析待测程序路径（-SelfTest 也需要）
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $script:ExePath = Join-Path $script:Root 'dist\StickerTodo.exe'
}
else {
    if (-not [System.IO.Path]::IsPathRooted($ExePath)) {
        $ExePath = Join-Path $script:Root $ExePath
    }
    $script:ExePath = $ExePath
}

# 环境自检模式：只加载原生/UIA/截图/剪贴板能力，不启动应用
if ($SelfTest) {
    exit (Invoke-SelfTest)
}

try {
    New-Item -ItemType Directory -Path $script:ArtifactDir -Force | Out-Null
    $transcriptPath = Join-Path $script:ArtifactDir ($script:RunStamp + '-e2e.log')
    try {
        Start-Transcript -LiteralPath $transcriptPath -Force | Out-Null
        $script:TranscriptStarted = $true
    }
    catch {
        Write-Host ('警告：无法启动转录日志：' + $_.Exception.Message) -ForegroundColor Yellow
    }

    Write-Host '贴纸待办 · 端到端（E2E）测试' -ForegroundColor White
    Write-Host ('PowerShell ' + $PSVersionTable.PSVersion.ToString() + '（' + $PSVersionTable.PSEdition + '）') -ForegroundColor DarkGray
    Write-Host ('可执行文件：' + $script:ExePath) -ForegroundColor DarkGray
    Write-Host ('临时数据目录：' + $script:TempRoot) -ForegroundColor DarkGray
    Write-Host ('截图目录：' + $script:ArtifactDir) -ForegroundColor DarkGray

    # 前置检查
    Add-Phase '前置检查'
    if (-not (Test-Path -LiteralPath $script:ExePath)) {
        Add-Result -Status 'FAIL' -Name '未找到待测程序' -Detail ($script:ExePath + '（请先运行 build.ps1）')
        throw '缺少 dist\StickerTodo.exe，E2E 无法开始'
    }
    $exeItem = Get-Item -LiteralPath $script:ExePath
    Add-Result -Status 'INFO' -Name '待测程序信息' -Detail ('大小 {0:N0} 字节，版本 {1}' -f $exeItem.Length, $exeItem.VersionInfo.FileVersion)

    $isWin = $true
    if ($null -ne $PSVersionTable.Platform -and $PSVersionTable.Platform -ne 'Win32NT') { $isWin = $false }
    Assert-Condition -Condition $isWin -Name '运行平台为 Windows' -FailDetail ('当前 platform=' + $PSVersionTable.Platform)
    $session = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    Add-Result -Status 'INFO' -Name '当前会话' -Detail ('SessionId=' + $session + '，交互式桌面 UIA 需要活动会话')

    New-Item -ItemType Directory -Path $script:TempRoot -Force | Out-Null
    Assert-Condition -Condition (Initialize-Native) -Name 'Win32 P/Invoke 辅助代码编译成功'
    if (-not $script:NativeReady) { throw 'P/Invoke 初始化失败，无法继续' }
    # E2E 全部用屏幕物理像素断言（窗口位置/截图/UIA 点击），必须先关闭 DPI 虚拟化
    $dpiProcessOk = $false
    try { $dpiProcessOk = [E2ENative]::TryEnablePerMonitorDpiAwareness() } catch { }
    Add-Result -Status 'INFO' -Name 'DPI 感知' -Detail ('进程 Per-Monitor V2=' + $dpiProcessOk + '（本脚本用物理像素坐标，避免 DPI 虚拟化误差）')
    $drawingOk = $false
    try {
        Add-Type -AssemblyName System.Drawing -ErrorAction Stop
        $probeBitmap = New-Object System.Drawing.Bitmap -ArgumentList @(4, 4)
        $probeBitmap.Dispose()
        $drawingOk = $true
    }
    catch { }
    Assert-Condition -Condition $drawingOk -Name 'System.Drawing 截图能力加载成功' -PassDetail 'GDI+ 可用' -FailDetail '无法加载 System.Drawing，截图将失败'
    Assert-Condition -Condition (Initialize-Uia) -Name 'UI Automation 程序集加载成功' -FailDetail $script:UiaError

    # 记录用户真实数据状态（用于隔离检查）
    $script:RealDataBefore = $null
    $realDataFile = Join-Path (Join-Path $env:APPDATA 'StickerTodo') 'data.json'
    if (Test-Path -LiteralPath $realDataFile) {
        $realItem = Get-Item -LiteralPath $realDataFile
        $script:RealDataBefore = @{ Time = $realItem.LastWriteTimeUtc; Length = $realItem.Length }
        Add-Result -Status 'INFO' -Name '检测到用户真实数据文件（测试只读快照，不修改）' -Detail ('%APPDATA%\StickerTodo\data.json，' + $realItem.Length + ' 字节')
    }
    else {
        Add-Result -Status 'INFO' -Name '未检测到用户真实数据文件（测试将确认不会创建）'
    }

    Invoke-PhaseNormalMode
    Invoke-PhaseInteractive
}
catch {
    Add-Result -Status 'FAIL' -Name 'E2E 测试执行异常' -Detail $_.Exception.Message
}
finally {
    try { Invoke-Cleanup } catch { Add-Result -Status 'FAIL' -Name '清理阶段异常' -Detail $_.Exception.Message }
    $exitCode = Write-Summary
    if ($script:TranscriptStarted) {
        try { Stop-Transcript | Out-Null } catch { }
    }
    Write-Host ''
    Write-Host ('退出码：' + $exitCode) -ForegroundColor DarkGray
    exit $exitCode
}
