using Microsoft.Win32;

namespace TodoSticker.Services;

/// <summary>
/// 开机自启管理（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）。
/// 无需管理员权限；所有方法均不抛异常，失败返回 false。
/// </summary>
public static class AutoStart
{
    /// <summary>注册表值名（生产使用）。</summary>
    public const string DefaultValueName = "StickerTodo";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>指定注册表值是否存在（自启是否已开启）。</summary>
    public static bool IsEnabled(string valueName = DefaultValueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            string? value = key?.GetValue(valueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取自启命令行；未设置时返回 null。</summary>
    public static string? GetCommand(string valueName = DefaultValueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 开启开机自启。exePath 为空时使用当前进程路径；会写入 "<exe>" --autostart。
    /// </summary>
    public static bool Enable(string? exePath = null, string valueName = DefaultValueName)
    {
        try
        {
            string? path = string.IsNullOrWhiteSpace(exePath) ? Environment.ProcessPath : exePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string command = $"\"{path}\" --autostart";
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(valueName, command, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>关闭开机自启。</summary>
    public static bool Disable(string valueName = DefaultValueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前注册表中的自启项是否指向本程序（用于界面显示与修复）。</summary>
    public static bool IsRegisteredForCurrentExe(string valueName = DefaultValueName)
    {
        string? command = GetCommand(valueName);
        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return command.Contains($"\"{path}\"", StringComparison.OrdinalIgnoreCase);
    }
}
