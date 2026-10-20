namespace TodoSticker.Models;

/// <summary>窗口与界面偏好设置（持久化到 data.json 的 settings 节点）。</summary>
public sealed class AppSettings
{
    /// <summary>是否把窗口作为“贴纸”嵌到桌面层。</summary>
    public bool PinToDesktop { get; set; } = true;

    /// <summary>窗口左上角 X（屏幕物理像素）；null 表示首次运行自动摆放。</summary>
    public int? WindowX { get; set; }

    /// <summary>窗口左上角 Y（屏幕物理像素）；null 表示首次运行自动摆放。</summary>
    public int? WindowY { get; set; }

    /// <summary>窗口宽度（屏幕物理像素）。</summary>
    public int WindowWidth { get; set; } = 380;

    /// <summary>窗口高度（屏幕物理像素）。</summary>
    public int WindowHeight { get; set; } = 540;

    /// <summary>是否折叠成一条小贴纸。</summary>
    public bool Collapsed { get; set; }

    /// <summary>上次停留的页签："pending" / "completed"。</summary>
    public string LastTab { get; set; } = "pending";

    /// <summary>当前分类筛选：null=全部，"__none__"=未分类，否则为分类 Id。</summary>
    public string? CategoryFilter { get; set; }

    /// <summary>新增任务的默认分类 Id；null=未分类。</summary>
    public string? NewTaskCategoryId { get; set; }
}
