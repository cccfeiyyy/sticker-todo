namespace TodoSticker.Models;

/// <summary>任务分类（含预置与自定义）。</summary>
public sealed class Category
{
    /// <summary>唯一标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>分类名称（1-8 个字符）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>分类颜色（#RRGGBB）。</summary>
    public string ColorHex { get; set; } = "#3D7EFF";
}