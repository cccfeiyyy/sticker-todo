using System.Text.Json.Serialization;

namespace TodoSticker.Models;

/// <summary>一条待办任务。</summary>
public sealed class TodoItem
{
    /// <summary>唯一标识（GUID，无横线）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>任务内容。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>完成时间；未完成时为 null。</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>待办列表排序序号：越小越靠前（支持手动拖拽排序）。</summary>
    public long SortOrder { get; set; }

    /// <summary>分类 Id；null 表示未分类。</summary>
    public string? CategoryId { get; set; }

    /// <summary>分类显示名（由数据层根据当前分类表填充，不写入 JSON）。</summary>
    [JsonIgnore]
    public string CategoryName { get; set; } = string.Empty;

    /// <summary>分类颜色（#RRGGBB，不写入 JSON）。</summary>
    [JsonIgnore]
    public string CategoryColorHex { get; set; } = "#E2E8F0";

    /// <summary>是否已归类（不写入 JSON）。</summary>
    [JsonIgnore]
    public bool HasCategory { get; set; }

    [JsonIgnore]
    public bool IsCompleted => CompletedAt is not null;
}
