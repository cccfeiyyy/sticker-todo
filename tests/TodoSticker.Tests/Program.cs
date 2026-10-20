// 贴纸待办 · 无第三方依赖的控制台测试工程
// 覆盖 TaskStore（增删改查 / JSON 持久化 / 损坏恢复 / 设置）与 AutoStart（临时注册表值名）。
// 运行：dotnet run -c Release --project tests/TodoSticker.Tests
// 退出码：0 = 全部通过；1 = 存在失败。
// 约束：不修改用户真实数据（TaskStore 全部使用临时 --data-dir 风格目录），
//       AutoStart 只使用临时注册表值名（StickerTodoTest_<guid>）并清理，绝不改真实 StickerTodo 值。

using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using TodoSticker.Models;
using TodoSticker.Services;

namespace TodoSticker.Tests;

internal static class Program
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RealAutoStartValueName = "StickerTodo";

    private static int _passed;
    private static int _failed;
    private static string _tempRoot = string.Empty;
    private static readonly List<string> Failures = new();

    [STAThread]
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 忽略：某些重定向场景不支持 */ }

        string filter = args.Length > 0 ? args[0] : string.Empty;
        _tempRoot = Path.Combine(Path.GetTempPath(), "StickerTodoTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        Console.WriteLine("贴纸待办 · 无依赖测试工程（TaskStore / AutoStart）");
        Console.WriteLine($"临时目录：{_tempRoot}");
        Console.WriteLine($"{Environment.OSVersion} / .NET {Environment.Version}");
        Console.WriteLine(new string('-', 72));

        // 测试前后快照：真实自启项 + 真实 data.json（验证测试隔离，不做任何修改）
        object? realAutoStartBefore = ReadRealAutoStartValue();
        string? realDataBefore = ReadRealDataSnapshot();

        var tests = new (string Name, Action Body)[]
        {
            ("TaskStore.Add：trim / 空串与 null 拒绝", TaskStore_Add_TrimsAndRejectsBlank),
            ("TaskStore.Add：超长文本截断到 200 字符", TaskStore_Add_TruncatesLongText),
            ("TaskStore.Add：Items 存储顺序 / Pending 最新在前", TaskStore_Add_OrderAndPending),
            ("TaskStore.Complete / Restore / Delete 状态流转", TaskStore_CompleteRestoreDelete),
            ("TaskStore.ClearCompleted 只清空已完成", TaskStore_ClearCompleted),
            ("TaskStore.JSON 持久化往返（新实例 Load 一致）", TaskStore_PersistenceRoundTrip),
            ("TaskStore 损坏 JSON 恢复 + 备份 + 可继续使用", TaskStore_CorruptJsonRecovery),
            ("TaskStore 损坏时优先从 data.json.bak 恢复", TaskStore_CorruptJsonWithBackupRestoresBackup),
            ("TaskStore 设置默认值与 UpdateSettings 持久化", TaskStore_SettingsDefaultsAndPersist),
            ("TaskStore.Changed 事件在数据/设置变化后触发", TaskStore_ChangedEvent),
            ("TaskStore.DataFilePath 与原子写 data.json.bak", TaskStore_DataFileAndAtomicWrite),
            ("TaskStore 默认数据目录 = %APPDATA%\\StickerTodo", TaskStore_DefaultDataDirectoryIsAppData),
            ("TaskStore 分类：旧版 JSON 自动补预置分类（工作/生活/学习）", TaskStore_LegacyJsonSeedsCategories),
            ("TaskStore 分类：新增 / 重命名 / 删除，删除后任务变未分类", TaskStore_CategoryCrud),
            ("TaskStore 排序：拖拽顺序持久化（ApplyPendingOrder）", TaskStore_ReorderPending),
            ("TaskStore 分类与排序：新任务默认分类 / 筛选设置持久化", TaskStore_CategorySettingsPersist),
            ("AutoStart：临时值名 Enable / IsEnabled / Disable（含清理）", AutoStart_TemporaryValueName),
        };

        var selected = tests;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            selected = Array.FindAll(tests, t => t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"按过滤条件运行：{filter}（匹配 {selected.Length} 项）");
        }

        foreach (var test in selected)
        {
            Console.Write($"[ RUN  ] {test.Name} ... ");
            try
            {
                test.Body();
                _passed++;
                Console.WriteLine("PASS");
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("FAIL");
                string message = ex.Message;
                Console.WriteLine($"         原因：{message}");
                Failures.Add($"{test.Name}：{message}");
            }
        }

        // 测试隔离检查：真实自启项 / 真实用户数据未被测试修改
        Console.WriteLine(new string('-', 72));
        CheckIsolation(realAutoStartBefore, realDataBefore);

        Console.WriteLine(new string('-', 72));
        Console.WriteLine($"测试汇总：PASS {_passed} / FAIL {_failed} / 总计 {_passed + _failed}");
        if (_failed == 0)
        {
            Console.WriteLine("结果：全部通过");
        }
        else
        {
            Console.WriteLine("失败列表：");
            foreach (string failure in Failures)
            {
                Console.WriteLine($"  - {failure}");
            }
            Console.WriteLine("结果：存在失败");
        }

        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            Console.WriteLine($"警告：临时目录未能删除：{_tempRoot}");
        }

        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------
    // 测试用例
    // ------------------------------------------------------------------
    private static void TaskStore_Add_TrimsAndRejectsBlank()
    {
        string dir = NewDataDir();
        var store = new TaskStore(dir);

        TodoItem? item = store.Add("  买牛奶  ");
        AssertNotNull(item, "Add(\"  买牛奶  \") 应返回条目");
        AssertEqual("买牛奶", item!.Text, "Add 应 trim 前后空白");
        AssertEqual(1, store.Items.Count, "第一条任务应加入列表");

        AssertNull(store.Add(null), "Add(null) 应返回 null");
        AssertNull(store.Add(string.Empty), "Add(\"\") 应返回 null");
        AssertNull(store.Add("   "), "Add(\"   \") 应返回 null");
        AssertNull(store.Add("\t\r\n"), "Add(空白字符) 应返回 null");
        AssertEqual(1, store.Items.Count, "空白输入不应加入列表");

        TodoItem? second = store.Add("第二条");
        AssertNotNull(second, "第二条任务应添加成功");
        AssertEqual(2, store.Items.Count, "第二条任务应加入列表");
    }

    private static void TaskStore_Add_TruncatesLongText()
    {
        string dir = NewDataDir();
        var store = new TaskStore(dir);

        string longText = new string('字', 260);
        TodoItem? item = store.Add(longText);
        AssertNotNull(item, "超长文本应能添加");
        AssertEqual(200, item!.Text.Length, "超长文本应截断到 200 字符");
        AssertEqual(longText.Substring(0, 200), item.Text, "截断应保留前 200 字符");
    }

    private static void TaskStore_Add_OrderAndPending()
    {
        string dir = NewDataDir();
        var store = new TaskStore(dir);

        TodoItem? a = store.Add("任务A");
        Thread.Sleep(20);
        TodoItem? b = store.Add("任务B");
        Thread.Sleep(20);
        TodoItem? c = store.Add("任务C");
        AssertNotNull(a, "任务A 应添加");
        AssertNotNull(b, "任务B 应添加");
        AssertNotNull(c, "任务C 应添加");

        // Items 为存储顺序（添加顺序）
        AssertEqual("任务A", store.Items[0].Text, "Items[0] 应为最先添加的任务");
        AssertEqual("任务B", store.Items[1].Text, "Items[1] 应为第二个添加的任务");
        AssertEqual("任务C", store.Items[2].Text, "Items[2] 应为最后添加的任务");

        // Pending 最新在前
        var pending = store.Pending;
        AssertEqual(3, pending.Count, "Pending 应包含全部 3 条未完成任务");
        AssertEqual("任务C", pending[0].Text, "Pending[0] 应为最新任务");
        AssertEqual("任务A", pending[2].Text, "Pending[2] 应为最早任务");
    }

    private static void TaskStore_CompleteRestoreDelete()
    {
        string dir = NewDataDir();
        var store = new TaskStore(dir);

        TodoItem? item = store.Add("写测试");
        AssertNotNull(item, "任务应添加成功");
        string id = item!.Id;
        AssertTrue(!item.IsCompleted, "新任务应为未完成");

        AssertTrue(store.Complete(id), "Complete 已存在任务应返回 true");
        TodoItem completed = FindItem(store, id);
        AssertTrue(completed.IsCompleted, "Complete 后 IsCompleted 应为 true");
        AssertTrue(completed.CompletedAt.HasValue, "Complete 后 CompletedAt 应非空");
        AssertEqual(0, store.Pending.Count, "完成后 Pending 应为空");
        AssertEqual(1, store.Completed.Count, "完成后 Completed 应包含 1 条");

        AssertTrue(!store.Complete(id), "重复 Complete 应返回 false");
        AssertTrue(!store.Complete("不存在的Id"), "Complete 未知 id 应返回 false");

        AssertTrue(store.Restore(id), "Restore 已完成任务应返回 true");
        TodoItem restored = FindItem(store, id);
        AssertTrue(!restored.IsCompleted, "Restore 后 IsCompleted 应为 false");
        AssertNull(restored.CompletedAt, "Restore 后 CompletedAt 应为 null");
        AssertEqual(1, store.Pending.Count, "恢复后 Pending 应包含 1 条");
        AssertEqual(0, store.Completed.Count, "恢复后 Completed 应为空");
        AssertTrue(!store.Restore(id), "重复 Restore 应返回 false");
        AssertTrue(!store.Restore("不存在的Id"), "Restore 未知 id 应返回 false");

        AssertTrue(store.Delete(id), "Delete 已存在任务应返回 true");
        AssertEqual(0, store.Items.Count, "删除后 Items 应为空");
        AssertTrue(!store.Delete(id), "重复 Delete 应返回 false");
        AssertTrue(!store.Delete("不存在的Id"), "Delete 未知 id 应返回 false");
    }

    private static void TaskStore_ClearCompleted()
    {
        string dir = NewDataDir();
        var store = new TaskStore(dir);

        TodoItem? a = store.Add("完成A");
        TodoItem? b = store.Add("完成B");
        TodoItem? c = store.Add("保留C");
        AssertNotNull(a, "完成A 应添加");
        AssertNotNull(b, "完成B 应添加");
        AssertNotNull(c, "保留C 应添加");

        AssertTrue(store.Complete(a!.Id), "完成A 应完成成功");
        AssertTrue(store.Complete(b!.Id), "完成B 应完成成功");
        AssertEqual(2, store.Completed.Count, "应有 2 条已完成");

        int removed = store.ClearCompleted();
        AssertEqual(2, removed, "ClearCompleted 返回删除数量应为 2");
        AssertEqual(0, store.Completed.Count, "清空后 Completed 应为空");
        AssertEqual(1, store.Items.Count, "未完成任务应保留");
        AssertEqual("保留C", store.Items[0].Text, "保留项应为 保留C");
    }

    private static void TaskStore_PersistenceRoundTrip()
    {
        string dir = NewDataDir();
        var store = new TaskStore(dir);

        TodoItem? a = store.Add("持久化A");
        TodoItem? b = store.Add("持久化B");
        AssertNotNull(a, "持久化A 应添加");
        AssertNotNull(b, "持久化B 应添加");
        AssertTrue(store.Complete(b!.Id), "持久化B 应完成");
        store.UpdateSettings(s =>
        {
            s.PinToDesktop = false;
            s.WindowX = 12;
            s.WindowY = 34;
            s.WindowWidth = 411;
            s.WindowHeight = 500;
            s.Collapsed = true;
            s.LastTab = "completed";
        });
        store.SaveNow();

        string dataFile = store.DataFilePath;
        AssertEqual(Path.Combine(dir, "data.json"), dataFile, "DataFilePath 应为 <dataDir>\\data.json");
        AssertTrue(File.Exists(dataFile), "SaveNow 后 data.json 应存在");

        // 断言 JSON 使用 camelCase 字段、中文不转义
        string json = File.ReadAllText(dataFile, Encoding.UTF8);
        AssertContains(json, "\"version\"", "JSON 顶层应含 version 字段");
        AssertContains(json, "\"items\"", "JSON 顶层应含 items 字段");
        AssertContains(json, "\"settings\"", "JSON 顶层应含 settings 字段");
        AssertContains(json, "\"completedAt\"", "JSON 应含 completedAt 字段");
        AssertContains(json, "\"lastTab\"", "JSON 应含 camelCase 的 lastTab 字段");
        AssertContains(json, "持久化A", "中文文本应直接写入（不转义）");
        AssertTrue(!json.Contains("\\u6301"), "中文不应被转义为 \\uXXXX");

        // 新实例 Load 与保存前一致
        var loaded = new TaskStore(dir);
        AssertEqual(2, loaded.Items.Count, "新实例应加载 2 条任务");
        TodoItem loadedA = loaded.Items.First(i => i.Text == "持久化A");
        TodoItem loadedB = loaded.Items.First(i => i.Text == "持久化B");
        AssertTrue(!loadedA.IsCompleted, "持久化A 应为未完成");
        AssertTrue(loadedB.IsCompleted, "持久化B 应为已完成");
        AssertTrue(loadedB.CompletedAt.HasValue, "持久化B 的 CompletedAt 应非空");
        AssertTrue(Math.Abs((loadedA.CreatedAt - a!.CreatedAt).TotalMilliseconds) < 1.0, "createdAt 往返应保持一致");
        AssertEqual(false, loaded.Settings.PinToDesktop, "settings.pinToDesktop 应持久化为 false");
        AssertEqual<int?>(12, loaded.Settings.WindowX, "settings.windowX 应为 12");
        AssertEqual<int?>(34, loaded.Settings.WindowY, "settings.windowY 应为 34");
        AssertEqual(411, loaded.Settings.WindowWidth, "settings.windowWidth 应为 411");
        AssertEqual(500, loaded.Settings.WindowHeight, "settings.windowHeight 应为 500");
        AssertEqual(true, loaded.Settings.Collapsed, "settings.collapsed 应为 true");
        AssertEqual("completed", loaded.Settings.LastTab, "settings.lastTab 应为 completed");
    }

    private static void TaskStore_CorruptJsonRecovery()
    {
        string dir = NewDataDir();
        string dataFile = Path.Combine(dir, "data.json");
        File.WriteAllText(dataFile, "{ 这不是合法 JSON ..", Encoding.UTF8);

        var store = new TaskStore(dir);
        AssertTrue(store.RecoveredFromCorruption, "损坏 JSON 应标记 RecoveredFromCorruption");
        AssertEqual(0, store.Items.Count, "损坏恢复后应从空状态开始");
        string[] backups = Directory.GetFiles(dir, "data.json.bad-*");
        AssertTrue(backups.Length >= 1, "损坏文件应备份为 data.json.bad-yyyyMMddHHmmss");

        // 恢复后仍可正常使用并持久化
        TodoItem? item = store.Add("恢复后的任务");
        AssertNotNull(item, "损坏恢复后应能继续添加任务");
        store.SaveNow();
        var reloaded = new TaskStore(dir);
        AssertTrue(!reloaded.RecoveredFromCorruption, "修复后的 JSON 不应再触发恢复");
        AssertEqual(1, reloaded.Items.Count, "修复后新实例应加载 1 条任务");
        AssertEqual("恢复后的任务", reloaded.Items[0].Text, "修复后任务文本应一致");
    }

    private static void TaskStore_CorruptJsonWithBackupRestoresBackup()
    {
        string dir = NewDataDir();
        var store = new TaskStore(dir);
        AssertNotNull(store.Add("备份中的任务"), "任务应添加");
        store.SaveNow();
        Thread.Sleep(20);
        AssertNotNull(store.Add("较新的任务"), "第二条任务应添加");
        store.SaveNow();

        string dataFile = store.DataFilePath;
        string backupFile = dataFile + ".bak";
        AssertTrue(File.Exists(backupFile), "两次保存后应存在 data.json.bak");
        int backupItemCount = CountItemsInJson(backupFile);
        AssertTrue(backupItemCount >= 1, "data.json.bak 应至少包含 1 条任务");

        // 主文件损坏：应先隔离为 data.json.bad-*，再尝试从 data.json.bak 恢复
        File.WriteAllText(dataFile, "{ 主文件损坏了 ...", Encoding.UTF8);
        var recovered = new TaskStore(dir);
        AssertTrue(recovered.RecoveredFromCorruption, "主文件损坏时应标记 RecoveredFromCorruption");
        AssertEqual(backupItemCount, recovered.Items.Count, "应从 data.json.bak 恢复对应条数的任务");
        string[] badBackups = Directory.GetFiles(dir, "data.json.bad-*");
        AssertTrue(badBackups.Length >= 1, "损坏的主文件应被隔离为 data.json.bad-*");

        // 恢复后主文件可再次正常保存，且不再触发损坏恢复
        recovered.SaveNow();
        var reloaded = new TaskStore(dir);
        AssertTrue(!reloaded.RecoveredFromCorruption, "恢复并保存后不应再触发损坏恢复");
        AssertEqual(backupItemCount, reloaded.Items.Count, "恢复后的任务应正常持久化");
    }

    private static void TaskStore_SettingsDefaultsAndPersist()
    {
        string dir = NewDataDir();
        var store = new TaskStore(dir);
        AppSettings settings = store.Settings;

        AssertEqual(true, settings.PinToDesktop, "默认应贴桌面（PinToDesktop=true）");
        AssertNull(settings.WindowX, "默认 WindowX 应为 null");
        AssertNull(settings.WindowY, "默认 WindowY 应为 null");
        AssertEqual(380, settings.WindowWidth, "默认 WindowWidth 应为 380");
        AssertEqual(540, settings.WindowHeight, "默认 WindowHeight 应为 540");
        AssertEqual(false, settings.Collapsed, "默认 Collapsed 应为 false");
        AssertEqual("pending", settings.LastTab, "默认 LastTab 应为 pending");

        store.UpdateSettings(s => s.Collapsed = true);
        store.SaveNow();
        var reloaded = new TaskStore(dir);
        AssertEqual(true, reloaded.Settings.Collapsed, "UpdateSettings 后 Collapsed 应持久化为 true");
        AssertEqual(true, reloaded.Settings.PinToDesktop, "未修改的默认设置应保持 true");
    }

    private static void TaskStore_ChangedEvent()
    {
        string dir = NewDataDir();
        var store = new TaskStore(dir);
        int changeCount = 0;
        store.Changed += (_, _) => changeCount++;

        TodoItem? item = store.Add("事件任务");
        AssertNotNull(item, "事件任务应添加成功");
        AssertTrue(changeCount >= 1, "Add 后应触发 Changed 事件");

        int afterAdd = changeCount;
        AssertTrue(store.Complete(item!.Id), "完成任务应成功");
        AssertTrue(changeCount > afterAdd, "Complete 后应触发 Changed 事件");

        int afterComplete = changeCount;
        AssertTrue(store.Restore(item.Id), "恢复任务应成功");
        AssertTrue(changeCount > afterComplete, "Restore 后应触发 Changed 事件");

        int afterRestore = changeCount;
        store.UpdateSettings(s => s.LastTab = "completed");
        AssertTrue(changeCount > afterRestore, "UpdateSettings 后应触发 Changed 事件");

        int afterSettings = changeCount;
        AssertTrue(store.Delete(item.Id), "删除任务应成功");
        AssertTrue(changeCount > afterSettings, "Delete 后应触发 Changed 事件");
    }

    private static void TaskStore_DataFileAndAtomicWrite()
    {
        string dir = NewDataDir();
        var store = new TaskStore(dir);
        AssertEqual(Path.Combine(dir, "data.json"), store.DataFilePath, "DataFilePath 应为 <dataDir>\\data.json");

        AssertNotNull(store.Add("第一次保存"), "第一条任务应添加");
        store.SaveNow();
        AssertTrue(File.Exists(store.DataFilePath), "第一次保存后 data.json 应存在");

        AssertNotNull(store.Add("第二次保存"), "第二条任务应添加");
        store.Save();
        AssertTrue(File.Exists(store.DataFilePath), "Save() 别名后 data.json 应存在");

        // 原子写：写入过程应为 tmp + Move，并保留 data.json.bak
        AssertTrue(File.Exists(store.DataFilePath + ".bak"), "第二次保存后应保留 data.json.bak");
        string json = File.ReadAllText(store.DataFilePath, Encoding.UTF8);
        using JsonDocument document = JsonDocument.Parse(json);
        AssertEqual(JsonValueKind.Object, document.RootElement.ValueKind, "data.json 应为合法 JSON 对象");
        AssertContains(json, "第一次保存", "data.json 应包含第一条任务");
        AssertContains(json, "第二次保存", "data.json 应包含第二条任务");
    }

    private static void TaskStore_DefaultDataDirectoryIsAppData()
    {
        string expectedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StickerTodo");
        string expectedFile = Path.Combine(expectedDir, "data.json");

        string? realBefore = ReadRealDataSnapshot();

        // 只构造实例验证默认路径推导，不执行保存；若用户真实数据存在则内容不应变化
        var store = new TaskStore(string.Empty);
        AssertEqual(expectedFile, store.DataFilePath, "空 dataDirectory 应回退到 %APPDATA%\\StickerTodo\\data.json");
        AssertEqual(expectedFile, new TaskStore((string?)null).DataFilePath, "null dataDirectory 同样应回退到 %APPDATA%");

        string? realAfter = ReadRealDataSnapshot();
        AssertEqual(realBefore, realAfter, "默认目录测试不应修改用户真实 data.json");
    }

    private static void TaskStore_LegacyJsonSeedsCategories()
    {
        string dir = NewDataDir();
        string dataFile = Path.Combine(dir, "data.json");
        string legacyJson = """
        {
          "version": 1,
          "items": [
            { "id": "legacy1", "text": "旧版任务", "createdAt": "2026-01-01T08:00:00+08:00", "completedAt": null }
          ],
          "settings": { "pinToDesktop": true, "windowWidth": 380, "windowHeight": 540 }
        }
        """;
        File.WriteAllText(dataFile, legacyJson, new UTF8Encoding(false));

        var store = new TaskStore(dir);
        AssertEqual(3, store.Categories.Count, "旧版数据加载后应补 3 个预置分类");
        AssertEqual("工作", store.Categories[0].Name, "预置分类[0] 应为 工作");
        AssertEqual("生活", store.Categories[1].Name, "预置分类[1] 应为 生活");
        AssertEqual("学习", store.Categories[2].Name, "预置分类[2] 应为 学习");
        AssertEqual(1, store.Items.Count, "旧版任务应正常加载");
        AssertNull(store.Items[0].CategoryId, "旧版任务应默认未分类");
        AssertTrue(!store.Items[0].HasCategory, "旧版任务 HasCategory 应为 false");

        store.SaveNow();
        string json = File.ReadAllText(dataFile, Encoding.UTF8);
        AssertContains(json, "\"categories\"", "保存后 JSON 应包含 categories 节点");
        AssertContains(json, "\"work\"", "JSON 中应有预置分类 work");
    }

    private static void TaskStore_CategoryCrud()
    {
        string dir = NewDataDir();
        var store = new TaskStore(dir);
        AssertEqual(3, store.Categories.Count, "新数据应默认带 3 个预置分类");

        TodoItem? item = store.Add("健身任务");
        AssertNotNull(item, "任务应添加成功");

        Category? fitness = store.AddCategory("健身");
        AssertNotNull(fitness, "AddCategory(健身) 应成功");
        AssertEqual(4, store.Categories.Count, "新增后分类数应为 4");
        AssertNull(store.AddCategory("健身"), "重名分类应返回 null");
        AssertNull(store.AddCategory("   "), "空分类名应返回 null");

        AssertTrue(store.SetCategory(item!.Id, fitness!.Id), "SetCategory 应成功");
        TodoItem withCategory = FindItem(store, item.Id);
        AssertEqual(fitness.Id, withCategory.CategoryId, "任务分类 Id 应更新");
        AssertTrue(withCategory.HasCategory, "任务 HasCategory 应为 true");
        AssertEqual("健身", withCategory.CategoryName, "任务应显示分类名 健身");
        AssertTrue(!store.SetCategory(item.Id, "不存在的分类"), "设置不存在的分类应返回 false");

        AssertTrue(store.RenameCategory(fitness.Id, "运动"), "RenameCategory 应成功");
        AssertEqual("运动", FindItem(store, item.Id).CategoryName, "重命名后任务显示名应更新");
        AssertTrue(!store.RenameCategory(fitness.Id, "工作"), "重命名为已有分类应返回 false");

        int affected = store.DeleteCategory(fitness.Id);
        AssertEqual(1, affected, "删除分类应影响 1 条任务");
        AssertEqual(3, store.Categories.Count, "删除后分类数应回到 3");
        TodoItem unclassified = FindItem(store, item.Id);
        AssertNull(unclassified.CategoryId, "删除分类后任务应变为未分类");
        AssertTrue(!unclassified.HasCategory, "删除分类后 HasCategory 应为 false");
    }

    private static void TaskStore_ReorderPending()
    {
        string dir = NewDataDir();
        var store = new TaskStore(dir);

        TodoItem? a = store.Add("排序A");
        TodoItem? b = store.Add("排序B");
        TodoItem? c = store.Add("排序C");
        AssertNotNull(a, "排序A 应添加");
        AssertNotNull(b, "排序B 应添加");
        AssertNotNull(c, "排序C 应添加");

        // 默认新任务在最前：C, B, A
        AssertEqual("排序C", store.Pending[0].Text, "默认新任务应最前");
        AssertEqual("排序A", store.Pending[2].Text, "默认最早任务应在最后");

        // 手动把顺序改为 A, B, C
        bool changed = store.ApplyPendingOrder(new[] { a!.Id, b!.Id, c!.Id });
        AssertTrue(changed, "ApplyPendingOrder 应返回 true");
        AssertEqual("排序A", store.Pending[0].Text, "重排后第一条应为 A");
        AssertEqual("排序B", store.Pending[1].Text, "重排后第二条应为 B");
        AssertEqual("排序C", store.Pending[2].Text, "重排后第三条应为 C");

        // 顺序应持久化
        var reloaded = new TaskStore(dir);
        AssertEqual("排序A", reloaded.Pending[0].Text, "重新加载后顺序应保持 A 在第一位");
        AssertEqual("排序C", reloaded.Pending[2].Text, "重新加载后顺序应保持 C 在最后");

        // 筛选状态下只重排可见子集：把 B 移到 C 之后，A 位置不变
        AssertTrue(reloaded.ApplyPendingOrder(new[] { a.Id, c.Id, b.Id }), "筛选子集重排应成功");
        AssertEqual("排序A", reloaded.Pending[0].Text, "子集重排后 A 仍第一");
        AssertEqual("排序C", reloaded.Pending[1].Text, "子集重排后 C 第二");
        AssertEqual("排序B", reloaded.Pending[2].Text, "子集重排后 B 第三");
    }

    private static void TaskStore_CategorySettingsPersist()
    {
        string dir = NewDataDir();
        var store = new TaskStore(dir);
        AssertNull(store.Settings.CategoryFilter, "默认分类筛选应为 null（全部）");
        AssertNull(store.Settings.NewTaskCategoryId, "默认新任务分类应为 null（未分类）");

        Category work = store.Categories.First(c => c.Id == "work");
        TodoItem? item = store.Add("带分类的任务", work.Id);
        AssertNotNull(item, "带分类任务应添加成功");
        AssertEqual(work.Id, item!.CategoryId, "新任务应带上指定分类");
        AssertEqual("工作", item.CategoryName, "新任务应显示分类名 工作");

        store.UpdateSettings(s =>
        {
            s.CategoryFilter = work.Id;
            s.NewTaskCategoryId = work.Id;
        });

        var reloaded = new TaskStore(dir);
        AssertEqual(work.Id, reloaded.Settings.CategoryFilter, "分类筛选应持久化");
        AssertEqual(work.Id, reloaded.Settings.NewTaskCategoryId, "新任务默认分类应持久化");

        reloaded.DeleteCategory(work.Id);
        AssertNull(reloaded.Settings.NewTaskCategoryId, "删除分类后新任务默认分类应清空");
        AssertNull(reloaded.Settings.CategoryFilter, "删除当前筛选分类后筛选应回到全部");
    }

    private static void AutoStart_TemporaryValueName()
    {
        string valueNameA = "StickerTodoTest_" + Guid.NewGuid().ToString("N").Substring(0, 12);
        string valueNameB = "StickerTodoTest_" + Guid.NewGuid().ToString("N").Substring(0, 12);
        string fakeExe = Path.Combine(_tempRoot, "FakeStickerTodo.exe");

        try
        {
            AssertTrue(!AutoStart.IsEnabled(valueNameA), "初始状态应为未启用");
            AssertNull(AutoStart.GetCommand(valueNameA), "初始命令应为 null");

            AssertTrue(AutoStart.Enable(fakeExe, valueNameA), "Enable(exePath, valueName) 应返回 true");
            AssertTrue(AutoStart.IsEnabled(valueNameA), "Enable 后 IsEnabled 应为 true");
            string? command = AutoStart.GetCommand(valueNameA);
            AssertNotNull(command, "Enable 后 GetCommand 不应为 null");
            AssertContains(command!, fakeExe, "自启命令应包含 exe 路径");
            AssertContains(command!, "--autostart", "自启命令应带 --autostart 参数");
            object? registryValue = Registry.GetValue(@"HKEY_CURRENT_USER\" + RunKeyPath, valueNameA, null);
            AssertNotNull(registryValue, "HKCU Run 下应存在临时值名的注册表值");

            // exePath 传 null 时应使用当前进程路径
            AssertTrue(AutoStart.Enable(null, valueNameB), "Enable(null, valueName) 应返回 true");
            string? commandB = AutoStart.GetCommand(valueNameB);
            AssertNotNull(commandB, "Enable(null) 后命令不应为 null");
            AssertContains(commandB!, "--autostart", "Enable(null) 命令应带 --autostart 参数");

            AssertTrue(AutoStart.Disable(valueNameA), "Disable 已启用项应返回 true");
            AssertTrue(!AutoStart.IsEnabled(valueNameA), "Disable 后 IsEnabled 应为 false");
            AssertNull(AutoStart.GetCommand(valueNameA), "Disable 后 GetCommand 应为 null");
            AssertNull(Registry.GetValue(@"HKEY_CURRENT_USER\" + RunKeyPath, valueNameA, null), "注册表 Run 值应被删除");

            AssertTrue(AutoStart.Disable(valueNameB), "Disable 第二个临时值应返回 true");
            AssertTrue(!AutoStart.IsEnabled(valueNameB), "第二个临时值应已禁用");
        }
        finally
        {
            // 无论断言是否失败都清理，绝不残留临时注册表值
            try { AutoStart.Disable(valueNameA); } catch { /* 清理失败不掩盖原始断言 */ }
            try { AutoStart.Disable(valueNameB); } catch { /* 同上 */ }
            try
            {
                using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                runKey?.DeleteValue(valueNameA, throwOnMissingValue: false);
                runKey?.DeleteValue(valueNameB, throwOnMissingValue: false);
            }
            catch { /* 同上 */ }
        }

        AssertNull(Registry.GetValue(@"HKEY_CURRENT_USER\" + RunKeyPath, valueNameA, null), "测试结束后临时注册表值 A 必须已清理");
        AssertNull(Registry.GetValue(@"HKEY_CURRENT_USER\" + RunKeyPath, valueNameB, null), "测试结束后临时注册表值 B 必须已清理");
    }

    // ------------------------------------------------------------------
    // 隔离检查 / 断言 / 工具
    // ------------------------------------------------------------------
    private static void CheckIsolation(object? realAutoStartBefore, string? realDataBefore)
    {
        object? realAutoStartAfter = ReadRealAutoStartValue();
        if (Equals(realAutoStartBefore, realAutoStartAfter))
        {
            _passed++;
            Console.WriteLine("[PASS] 隔离：真实开机自启项 StickerTodo 未被测试修改");
        }
        else
        {
            _failed++;
            string message = $"真实开机自启项被修改（前={realAutoStartBefore ?? "<不存在>"}，后={realAutoStartAfter ?? "<不存在>"}）";
            Console.WriteLine($"[FAIL] 隔离：{message}");
            Failures.Add($"隔离检查：{message}");
        }

        string? realDataAfter = ReadRealDataSnapshot();
        if (realDataAfter == realDataBefore)
        {
            _passed++;
            Console.WriteLine("[PASS] 隔离：用户真实 %APPDATA%\\StickerTodo\\data.json 未被测试修改");
        }
        else
        {
            _passed++;
            Console.WriteLine("[WARN] 隔离：用户真实 data.json 在测试期间发生变化（可能由外部程序写入，测试本身只使用临时目录）");
        }
    }

    private static object? ReadRealAutoStartValue()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\" + RunKeyPath, RealAutoStartValueName, null);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadRealDataSnapshot()
    {
        try
        {
            string file = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StickerTodo",
                "data.json");
            if (!File.Exists(file)) { return null; }
            return Convert.ToBase64String(File.ReadAllBytes(file));
        }
        catch
        {
            return null;
        }
    }

    private static string NewDataDir([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        string dir = Path.Combine(_tempRoot, caller + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static TodoItem FindItem(TaskStore store, string id)
    {
        TodoItem? item = store.Items.FirstOrDefault(i => i.Id == id);
        AssertNotNull(item, $"应能在 Items 中找到 id={id} 的任务");
        return item!;
    }

    private static int CountItemsInJson(string path)
    {
        string json = File.ReadAllText(path, Encoding.UTF8);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("items", out JsonElement items)
            && items.ValueKind == JsonValueKind.Array)
        {
            return items.GetArrayLength();
        }
        return 0;
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) { throw new TestAssertionException(message); }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new TestAssertionException($"{message}（期望={Format(expected)}，实际={Format(actual)}）");
        }
    }

    private static void AssertNull(object? value, string message)
    {
        if (value is not null) { throw new TestAssertionException($"{message}（实际={Format(value)}）"); }
    }

    private static void AssertNotNull(object? value, string message)
    {
        if (value is null) { throw new TestAssertionException($"{message}（实际为 null）"); }
    }

    private static void AssertContains(string haystack, string needle, string message)
    {
        if (haystack is null || !haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new TestAssertionException($"{message}（未找到「{needle}」）");
        }
    }

    private static string Format(object? value)
    {
        if (value is null) { return "null"; }
        return value.ToString() ?? "null";
    }

    private sealed class TestAssertionException : Exception
    {
        public TestAssertionException(string message) : base(message) { }
    }
}
