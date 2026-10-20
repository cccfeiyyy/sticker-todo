using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TodoSticker.Models;

namespace TodoSticker.Services;

/// <summary>
/// 任务的读写、持久化与查询。
/// 数据保存在 &lt;dataDirectory&gt;\data.json；每次变更同步、原子地落盘（先写 .tmp 再替换，并保留 .bak）。
/// 线程安全：所有读写均加锁；事件在锁外触发。
/// </summary>
public sealed class TaskStore
{
    public const int MaxTextLength = 200;
    public const int MaxCategoryNameLength = 8;
    public const string DefaultFolderName = "StickerTodo";
    public const string DataFileName = "data.json";

    /// <summary>分类筛选中表示“未分类”的特殊值。</summary>
    public const string UncategorizedFilter = "__none__";

    private static readonly (string Id, string Name, string Color)[] PresetCategories =
    {
        ("work", "工作", "#3D7EFF"),
        ("life", "生活", "#22C55E"),
        ("study", "学习", "#A855F7"),
    };

    private static readonly string[] CustomPalette =
    {
        "#F59E0B", "#EF4444", "#06B6D4", "#8B5CF6", "#10B981", "#F97316", "#EC4899", "#64748B",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private AppState _state = new();
    private bool _loading;

    public TaskStore(string? dataDirectory = null)
    {
        DataDirectory = ResolveDirectory(dataDirectory);
        DataFilePath = Path.Combine(DataDirectory, DataFileName);
        try
        {
            Directory.CreateDirectory(DataDirectory);
        }
        catch (Exception ex)
        {
            LastSaveError = ex.Message;
        }

        Load();
    }

    /// <summary>数据目录（绝对路径）。</summary>
    public string DataDirectory { get; }

    /// <summary>数据文件（绝对路径）。</summary>
    public string DataFilePath { get; }

    /// <summary>启动时是否检测到 data.json 损坏并做了恢复。</summary>
    public bool RecoveredFromCorruption { get; private set; }

    /// <summary>最近一次保存失败的原因；成功时为 null。</summary>
    public string? LastSaveError { get; private set; }

    /// <summary>数据或设置发生变化（已落盘后触发）。</summary>
    public event EventHandler? Changed;

    /// <summary>全部任务（存储顺序）。</summary>
    public IReadOnlyList<TodoItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _state.Items.ToArray();
            }
        }
    }

    /// <summary>未完成的任务（按手动排序；新任务默认在最前）。</summary>
    public IReadOnlyList<TodoItem> Pending
    {
        get
        {
            lock (_gate)
            {
                return _state.Items
                    .Where(i => i.CompletedAt is null)
                    .OrderBy(i => i.SortOrder)
                    .ThenByDescending(i => i.CreatedAt)
                    .ToArray();
            }
        }
    }

    /// <summary>已完成的任务（完成时间倒序，最近完成的在前）。</summary>
    public IReadOnlyList<TodoItem> Completed
    {
        get
        {
            lock (_gate)
            {
                return _state.Items
                    .Where(i => i.CompletedAt is not null)
                    .OrderByDescending(i => i.CompletedAt)
                    .ToArray();
            }
        }
    }

    /// <summary>全部分类（含预置与自定义，按用户顺序）。</summary>
    public IReadOnlyList<Category> Categories
    {
        get
        {
            lock (_gate)
            {
                return _state.Categories?.ToArray() ?? Array.Empty<Category>();
            }
        }
    }

    /// <summary>界面设置（请通过 UpdateSettings 修改，以确保持久化）。</summary>
    public AppSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _state.Settings;
            }
        }
    }

    /// <summary>从磁盘重新加载；文件缺失/损坏时回退为空数据且不抛异常。</summary>
    public void Load()
    {
        lock (_gate)
        {
            _loading = true;
            try
            {
                RecoveredFromCorruption = false;
                AppState? loaded = TryLoadFile(DataFilePath);

                if (loaded is null && File.Exists(DataFilePath))
                {
                    // 主文件损坏：备份后尝试 .bak，再不行就从空数据开始。
                    RecoveredFromCorruption = true;
                    TryQuarantineCorruptFile();
                    loaded = TryLoadFile(DataFilePath + ".bak");
                }

                _state = Normalize(loaded ?? new AppState());
            }
            finally
            {
                _loading = false;
            }
        }
    }

    /// <summary>新增任务；内容为空返回 null（不入列表）。可选指定分类 Id。</summary>
    public TodoItem? Add(string? text, string? categoryId = null)
    {
        string? cleaned = CleanText(text);
        if (cleaned is null)
        {
            return null;
        }

        TodoItem item;
        lock (_gate)
        {
            long minOrder = _state.Items
                .Where(i => i.CompletedAt is null)
                .Select(i => i.SortOrder)
                .DefaultIfEmpty(0)
                .Min();

            item = new TodoItem
            {
                Text = cleaned,
                SortOrder = minOrder - 1,
            };

            if (!string.IsNullOrWhiteSpace(categoryId)
                && _state.Categories is not null
                && _state.Categories.Any(c => c.Id == categoryId))
            {
                item.CategoryId = categoryId;
            }

            _state.Items.Add(item);
            RefreshCategoryDisplay(_state);
        }

        SaveAndNotify();
        return item;
    }

    /// <summary>把任务标记为已完成（记录完成时间）。</summary>
    public bool Complete(string? id)
    {
        bool changed = false;
        lock (_gate)
        {
            TodoItem? item = Find(id);
            if (item is not null && item.CompletedAt is null)
            {
                item.CompletedAt = DateTimeOffset.Now;
                changed = true;
            }
        }

        if (changed)
        {
            SaveAndNotify();
        }

        return changed;
    }

    /// <summary>把任务从“已完成”恢复为“待办”。</summary>
    public bool Restore(string? id)
    {
        bool changed = false;
        lock (_gate)
        {
            TodoItem? item = Find(id);
            if (item is not null && item.CompletedAt is not null)
            {
                item.CompletedAt = null;
                changed = true;
            }
        }

        if (changed)
        {
            SaveAndNotify();
        }

        return changed;
    }

    /// <summary>永久删除任务。</summary>
    public bool Delete(string? id)
    {
        bool removed = false;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            int index = _state.Items.FindIndex(i => i.Id == id);
            if (index >= 0)
            {
                _state.Items.RemoveAt(index);
                removed = true;
            }
        }

        if (removed)
        {
            SaveAndNotify();
        }

        return removed;
    }

    /// <summary>清空所有已完成任务，返回删除数量。</summary>
    public int ClearCompleted()
    {
        int removed;
        lock (_gate)
        {
            removed = _state.Items.RemoveAll(i => i.CompletedAt is not null);
        }

        if (removed > 0)
        {
            SaveAndNotify();
        }

        return removed;
    }

    /// <summary>设置任务分类；categoryId 为 null/空 表示未分类。</summary>
    public bool SetCategory(string? id, string? categoryId)
    {
        bool changed = false;
        lock (_gate)
        {
            TodoItem? item = Find(id);
            if (item is null)
            {
                return false;
            }

            string? normalized = null;
            if (!string.IsNullOrWhiteSpace(categoryId))
            {
                if (_state.Categories is null || !_state.Categories.Any(c => c.Id == categoryId))
                {
                    return false;
                }

                normalized = categoryId;
            }

            if (!string.Equals(item.CategoryId, normalized, StringComparison.Ordinal))
            {
                item.CategoryId = normalized;
                RefreshCategoryDisplay(_state);
                changed = true;
            }
        }

        if (changed)
        {
            SaveAndNotify();
        }

        return changed;
    }

    /// <summary>新增自定义分类；名称为空或重名返回 null。</summary>
    public Category? AddCategory(string? name, string? colorHex = null)
    {
        string? cleaned = CleanCategoryName(name);
        if (cleaned is null)
        {
            return null;
        }

        Category category;
        lock (_gate)
        {
            _state.Categories ??= new List<Category>();
            if (_state.Categories.Any(c => string.Equals(c.Name, cleaned, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            category = new Category
            {
                Name = cleaned,
                ColorHex = IsValidColor(colorHex) ? colorHex! : PickPaletteColor(_state.Categories.Count),
            };
            _state.Categories.Add(category);
            RefreshCategoryDisplay(_state);
        }

        SaveAndNotify();
        return category;
    }

    /// <summary>删除分类；该分类下的任务变为未分类。返回受影响的任务数。</summary>
    public int DeleteCategory(string? id)
    {
        int affected;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(id) || _state.Categories is null)
            {
                return 0;
            }

            int index = _state.Categories.FindIndex(c => c.Id == id);
            if (index < 0)
            {
                return 0;
            }

            _state.Categories.RemoveAt(index);
            affected = 0;
            foreach (TodoItem item in _state.Items)
            {
                if (string.Equals(item.CategoryId, id, StringComparison.Ordinal))
                {
                    item.CategoryId = null;
                    affected++;
                }
            }

            if (string.Equals(_state.Settings.NewTaskCategoryId, id, StringComparison.Ordinal))
            {
                _state.Settings.NewTaskCategoryId = null;
            }

            if (string.Equals(_state.Settings.CategoryFilter, id, StringComparison.Ordinal))
            {
                _state.Settings.CategoryFilter = null;
            }

            RefreshCategoryDisplay(_state);
        }

        SaveAndNotify();
        return affected;
    }

    /// <summary>重命名分类；新名称为空或与其它分类重名时返回 false。</summary>
    public bool RenameCategory(string? id, string? newName)
    {
        string? cleaned = CleanCategoryName(newName);
        if (cleaned is null || string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (_gate)
        {
            Category? category = _state.Categories?.Find(c => c.Id == id);
            if (category is null)
            {
                return false;
            }

            if (_state.Categories!.Any(c => c.Id != id && string.Equals(c.Name, cleaned, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            category.Name = cleaned;
            RefreshCategoryDisplay(_state);
        }

        SaveAndNotify();
        return true;
    }

    /// <summary>
    /// 应用待办列表的新顺序。orderedVisibleIds 为当前可见任务的完整 Id 序列；
    /// 在分类筛选状态下只调整可见任务之间的相对顺序，隐藏任务保持原相对位置。
    /// </summary>
    public bool ApplyPendingOrder(IReadOnlyList<string>? orderedVisibleIds)
    {
        if (orderedVisibleIds is null || orderedVisibleIds.Count < 2)
        {
            return false;
        }

        lock (_gate)
        {
            List<TodoItem> pending = _state.Items
                .Where(i => i.CompletedAt is null)
                .OrderBy(i => i.SortOrder)
                .ThenByDescending(i => i.CreatedAt)
                .ToList();
            if (pending.Count < 2)
            {
                return false;
            }

            Dictionary<string, TodoItem> byId = pending.ToDictionary(i => i.Id, StringComparer.Ordinal);
            HashSet<string> visible = new(StringComparer.Ordinal);
            foreach (string id in orderedVisibleIds)
            {
                if (!byId.ContainsKey(id) || !visible.Add(id))
                {
                    return false;
                }
            }

            List<TodoItem> reordered = new(pending.Count);
            int cursor = 0;
            for (int index = 0; index < pending.Count; index++)
            {
                TodoItem item = pending[index];
                if (visible.Contains(item.Id))
                {
                    reordered.Add(byId[orderedVisibleIds[cursor++]]);
                }
                else
                {
                    reordered.Add(item);
                }
            }

            if (cursor != orderedVisibleIds.Count)
            {
                return false;
            }

            bool changed = false;
            for (int index = 0; index < pending.Count; index++)
            {
                if (!ReferenceEquals(reordered[index], pending[index]))
                {
                    changed = true;
                    break;
                }
            }

            if (!changed)
            {
                return false;
            }

            for (int index = 0; index < reordered.Count; index++)
            {
                reordered[index].SortOrder = index;
            }
        }

        SaveAndNotify();
        return true;
    }

    /// <summary>修改设置并立即持久化。</summary>
    public void UpdateSettings(Action<AppSettings> mutate)
    {
        if (mutate is null)
        {
            return;
        }

        lock (_gate)
        {
            mutate(_state.Settings);
        }

        SaveAndNotify();
    }

    /// <summary>立即把当前数据写入磁盘。</summary>
    public void SaveNow()
    {
        lock (_gate)
        {
            SaveCore();
        }
    }

    /// <summary>SaveNow 的别名。</summary>
    public void Save() => SaveNow();

    private TodoItem? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _state.Items.Find(i => i.Id == id);
    }

    private void SaveAndNotify()
    {
        SaveNow();
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 界面回调异常不应影响数据层。
        }
    }

    private void SaveCore()
    {
        if (_loading)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(DataDirectory);
            string json = JsonSerializer.Serialize(_state, JsonOptions);
            string tmpPath = DataFilePath + ".tmp";
            File.WriteAllText(tmpPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(DataFilePath))
            {
                try
                {
                    File.Copy(DataFilePath, DataFilePath + ".bak", overwrite: true);
                }
                catch
                {
                    // 备份失败不影响主保存。
                }
            }

            File.Move(tmpPath, DataFilePath, overwrite: true);
            LastSaveError = null;
        }
        catch (Exception ex)
        {
            LastSaveError = ex.Message;
        }
    }

    private void TryQuarantineCorruptFile()
    {
        try
        {
            string badPath = Path.Combine(
                DataDirectory,
                $"{DataFileName}.bad-{DateTime.Now:yyyyMMddHHmmss}");
            File.Move(DataFilePath, badPath, overwrite: true);
        }
        catch
        {
            // 无法移动损坏文件时保持原样，后续保存会覆盖它。
        }
    }

    private static AppState? TryLoadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AppState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static AppState Normalize(AppState state)
    {
        state.Items ??= new List<TodoItem>();
        state.Settings ??= new AppSettings();

        AppSettings settings = state.Settings;
        if (settings.WindowWidth is < 280 or > 1200)
        {
            settings.WindowWidth = 380;
        }

        if (settings.WindowHeight is < 220 or > 1600)
        {
            settings.WindowHeight = 540;
        }

        if (settings.LastTab is not ("pending" or "completed"))
        {
            settings.LastTab = "pending";
        }

        List<Category> categories;
        if (state.Categories is null)
        {
            // 旧版数据没有分类节点：写入预置分类（工作 / 生活 / 学习）。
            categories = new List<Category>();
            foreach ((string id, string name, string color) in PresetCategories)
            {
                categories.Add(new Category { Id = id, Name = name, ColorHex = color });
            }
        }
        else
        {
            categories = new List<Category>();
            HashSet<string> seenCategoryIds = new(StringComparer.Ordinal);
            HashSet<string> seenCategoryNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (Category? category in state.Categories)
            {
                if (category is null)
                {
                    continue;
                }

                string name = (category.Name ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                if (name.Length > MaxCategoryNameLength)
                {
                    name = name[..MaxCategoryNameLength];
                }

                if (string.IsNullOrWhiteSpace(category.Id) || !seenCategoryIds.Add(category.Id))
                {
                    category.Id = Guid.NewGuid().ToString("N");
                    seenCategoryIds.Add(category.Id);
                }

                if (!seenCategoryNames.Add(name))
                {
                    continue;
                }

                category.Name = name;
                if (!IsValidColor(category.ColorHex))
                {
                    category.ColorHex = PickPaletteColor(categories.Count);
                }

                categories.Add(category);
            }
        }

        state.Categories = categories;

        if (string.IsNullOrEmpty(settings.CategoryFilter))
        {
            settings.CategoryFilter = null;
        }
        else if (settings.CategoryFilter != UncategorizedFilter
                 && !categories.Any(c => c.Id == settings.CategoryFilter))
        {
            settings.CategoryFilter = null;
        }

        if (string.IsNullOrWhiteSpace(settings.NewTaskCategoryId))
        {
            settings.NewTaskCategoryId = null;
        }
        else if (!categories.Any(c => c.Id == settings.NewTaskCategoryId))
        {
            settings.NewTaskCategoryId = null;
        }

        List<TodoItem> items = new(state.Items.Count);
        HashSet<string> seenIds = new(StringComparer.Ordinal);
        foreach (TodoItem? item in state.Items)
        {
            if (item is null)
            {
                continue;
            }

            string text = (item.Text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (text.Length > MaxTextLength)
            {
                text = text[..MaxTextLength];
            }

            if (string.IsNullOrWhiteSpace(item.Id) || !seenIds.Add(item.Id))
            {
                item.Id = Guid.NewGuid().ToString("N");
                seenIds.Add(item.Id);
            }

            if (item.CreatedAt == default)
            {
                item.CreatedAt = DateTimeOffset.Now;
            }

            item.Text = text;
            items.Add(item);
        }

        state.Items = items;
        RefreshCategoryDisplay(state);
        return state;
    }

    private static string? CleanCategoryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string cleaned = name.Trim();
        if (cleaned.Length > MaxCategoryNameLength)
        {
            cleaned = cleaned[..MaxCategoryNameLength];
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    private static bool IsValidColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color) || color.Length != 7 || color[0] != '#')
        {
            return false;
        }

        for (int index = 1; index < 7; index++)
        {
            if (!Uri.IsHexDigit(color[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string PickPaletteColor(int index)
    {
        return CustomPalette[Math.Abs(index) % CustomPalette.Length];
    }

    private static void RefreshCategoryDisplay(AppState state)
    {
        state.Categories ??= new List<Category>();
        Dictionary<string, Category> map = new(StringComparer.Ordinal);
        foreach (Category category in state.Categories)
        {
            map[category.Id] = category;
        }

        foreach (TodoItem item in state.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.CategoryId)
                && map.TryGetValue(item.CategoryId, out Category? category))
            {
                item.CategoryName = category.Name;
                item.CategoryColorHex = category.ColorHex;
                item.HasCategory = true;
            }
            else
            {
                item.CategoryId = null;
                item.CategoryName = string.Empty;
                item.CategoryColorHex = "#E2E8F0";
                item.HasCategory = false;
            }
        }
    }

    private static string? CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string cleaned = text.Trim();
        if (cleaned.Length > MaxTextLength)
        {
            cleaned = cleaned[..MaxTextLength];
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string ResolveDirectory(string? dataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataDirectory));
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, DefaultFolderName);
    }
}
