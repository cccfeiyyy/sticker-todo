namespace TodoSticker.Models;

/// <summary>持久化根对象（data.json）。</summary>
public sealed class AppState
{
    /// <summary>数据结构版本。</summary>
    public int Version { get; set; } = 1;

    /// <summary>全部任务（含已完成）。</summary>
    public List<TodoItem> Items { get; set; } = new();

    /// <summary>分类列表。null 表示旧版数据（加载时会自动写入预置分类）；空数组表示用户删光了自定义分类。</summary>
    public List<Category>? Categories { get; set; }

    /// <summary>界面/窗口设置。</summary>
    public AppSettings Settings { get; set; } = new();
}
