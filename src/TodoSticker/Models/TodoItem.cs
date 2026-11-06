using System.Text.Json.Serialization;

namespace TodoSticker.Models;

/// <summary>任务上的标签（分类）展示信息（由数据层根据当前分类表填充，不写入 JSON）。</summary>
public sealed class TaskTag
{
    /// <summary>标签（分类）Id。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>标签名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>标签颜色（#RRGGBB）。</summary>
    public string ColorHex { get; set; } = "#94A3B8";
}

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

    /// <summary>任务标签（分类）Id 列表；一个任务可同时属于多个标签。</summary>
    public List<string> CategoryIds { get; set; } = new();

    /// <summary>旧版单分类字段：仅用于兼容读取旧版数据；新版保存时为空。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CategoryId { get; set; }

    /// <summary>标签展示列表（由数据层填充，不写入 JSON）。</summary>
    [JsonIgnore]
    public List<TaskTag> Tags { get; set; } = new();

    /// <summary>是否有标签（不写入 JSON）。</summary>
    [JsonIgnore]
    public bool HasTags => Tags.Count > 0;

    /// <summary>主颜色（第一个标签的颜色；无标签时为灰色，不写入 JSON）。</summary>
    [JsonIgnore]
    public string PrimaryColorHex { get; set; } = "#E2E8F0";

    [JsonIgnore]
    public bool IsCompleted => CompletedAt is not null;
}
