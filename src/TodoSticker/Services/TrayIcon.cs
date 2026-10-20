using System.Drawing;
using System.Windows.Forms;

namespace TodoSticker.Services;

/// <summary>
/// 系统托盘图标与菜单（使用 WinForms NotifyIcon，不额外引入依赖）。
/// 所有菜单动作以事件形式抛出，由 App 统一调度。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _showHideItem;
    private readonly ToolStripMenuItem _pinItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly Icon _icon;
    private bool _disposed;

    public TrayIcon(string tooltip)
    {
        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = true,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(55, 65, 81),
            Font = new Font("Microsoft YaHei UI", 9F),
            Renderer = new TrayMenuRenderer(),
            Padding = new Padding(2),
        };

        _showHideItem = new ToolStripMenuItem("隐藏贴纸");
        _showHideItem.Click += (_, _) => Raise(ToggleVisibilityRequested);
        _menu.Items.Add(_showHideItem);
        _menu.Items.Add(new ToolStripSeparator());

        _pinItem = new ToolStripMenuItem("贴到桌面") { CheckOnClick = false };
        _pinItem.Click += (_, _) => Raise(PinToggleRequested);
        _menu.Items.Add(_pinItem);

        _autoStartItem = new ToolStripMenuItem("开机自启") { CheckOnClick = false };
        _autoStartItem.Click += (_, _) => Raise(AutoStartToggleRequested);
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem clearItem = new("清空已完成");
        clearItem.Click += (_, _) => Raise(ClearCompletedRequested);
        _menu.Items.Add(clearItem);

        ToolStripMenuItem folderItem = new("打开数据文件夹");
        folderItem.Click += (_, _) => Raise(OpenDataFolderRequested);
        _menu.Items.Add(folderItem);
        _menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem exitItem = new("退出贴纸待办");
        exitItem.Click += (_, _) => Raise(ExitRequested);
        _menu.Items.Add(exitItem);

        Icon? extracted = TryExtractIcon();
        _icon = extracted ?? (Icon)SystemIcons.Application.Clone();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = string.IsNullOrWhiteSpace(tooltip) ? "贴纸待办" : tooltip,
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                Raise(ShowRequested);
            }
        };
    }

    /// <summary>左键单击托盘图标：显示窗口并浮到前台。</summary>
    public event EventHandler? ShowRequested;

    /// <summary>点击“显示/隐藏贴纸”。</summary>
    public event EventHandler? ToggleVisibilityRequested;

    /// <summary>切换“贴到桌面”。</summary>
    public event EventHandler? PinToggleRequested;

    /// <summary>切换“开机自启”。</summary>
    public event EventHandler? AutoStartToggleRequested;

    /// <summary>清空已完成任务。</summary>
    public event EventHandler? ClearCompletedRequested;

    /// <summary>打开数据文件夹。</summary>
    public event EventHandler? OpenDataFolderRequested;

    /// <summary>退出应用。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>刷新菜单勾选状态与文案。</summary>
    public void UpdateState(bool windowVisible, bool pinned, bool autoStart)
    {
        if (_disposed)
        {
            return;
        }

        _showHideItem.Text = windowVisible ? "隐藏贴纸" : "显示贴纸";
        _pinItem.Checked = pinned;
        _autoStartItem.Checked = autoStart;
    }

    /// <summary>Explorer 重启后重新挂载托盘图标。</summary>
    public void ReAddIcon()
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Visible = true;
    }

    private static void Raise(EventHandler? handler)
    {
        try
        {
            handler?.Invoke(null, EventArgs.Empty);
        }
        catch
        {
            // 托盘菜单回调异常不应导致进程退出。
        }
    }

    private static Icon? TryExtractIcon()
    {
        try
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            {
                Icon? icon = Icon.ExtractAssociatedIcon(processPath);
                return icon;
            }
        }
        catch
        {
            // 回退到系统默认图标。
        }

        return null;
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
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _menu.Dispose();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _icon.Dispose();
        }
        catch
        {
            // 忽略。
        }
    }

    /// <summary>浅色扁平化菜单渲染，避免 Windows 经典灰。</summary>
    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer()
            : base(new TrayColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using SolidBrush brush = new(Color.White);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using Pen pen = new(Color.FromArgb(232, 235, 240));
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
            {
                return;
            }

            using SolidBrush brush = new(Color.FromArgb(243, 245, 248));
            e.Graphics.FillRectangle(brush, new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled
                ? (e.Item.Selected ? Color.FromArgb(31, 41, 55) : Color.FromArgb(55, 65, 81))
                : Color.FromArgb(160, 168, 180);
            base.OnRenderItemText(e);
        }
    }

    private sealed class TrayColorTable : ProfessionalColorTable
    {
        public override Color CheckBackground => Color.FromArgb(235, 242, 255);

        public override Color CheckSelectedBackground => Color.FromArgb(235, 242, 255);

        public override Color CheckPressedBackground => Color.FromArgb(235, 242, 255);

        public override Color MenuItemBorder => Color.Transparent;

        public override Color MenuBorder => Color.FromArgb(232, 235, 240);

        public override Color ToolStripDropDownBackground => Color.White;

        public override Color ImageMarginGradientBegin => Color.White;

        public override Color ImageMarginGradientMiddle => Color.White;

        public override Color ImageMarginGradientEnd => Color.White;

        public override Color MenuItemSelected => Color.FromArgb(243, 245, 248);

        public override Color SeparatorDark => Color.FromArgb(232, 235, 240);

        public override Color SeparatorLight => Color.White;
    }
}
