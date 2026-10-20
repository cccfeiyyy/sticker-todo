using System.IO;

namespace TodoSticker.Services;

/// <summary>启动参数解析结果。</summary>
public sealed class StartupOptions
{
    /// <summary>默认数据目录：%APPDATA%\StickerTodo。</summary>
    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), TaskStore.DefaultFolderName);

    /// <summary>数据目录（绝对路径）。</summary>
    public string DataDirectory { get; init; } = DefaultDataDirectory;

    /// <summary>本次启动不嵌入桌面层（调试/自动化用）。</summary>
    public bool NoPin { get; init; }

    /// <summary>由开机自启拉起（不抢焦点、不显示首次提示）。</summary>
    public bool AutoStart { get; init; }

    /// <summary>解析命令行：--data-dir &lt;path&gt; / --data-dir=&lt;path&gt; / --no-pin / --autostart。</summary>
    public static StartupOptions Parse(string[]? args)
    {
        string dataDirectory = DefaultDataDirectory;
        bool noPin = false;
        bool autoStart = false;

        args ??= Array.Empty<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i] ?? string.Empty;
            if (arg.Equals("--no-pin", StringComparison.OrdinalIgnoreCase))
            {
                noPin = true;
            }
            else if (arg.Equals("--autostart", StringComparison.OrdinalIgnoreCase))
            {
                autoStart = true;
            }
            else if (arg.Equals("--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    dataDirectory = args[++i];
                }
            }
            else if (arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
            {
                dataDirectory = arg["--data-dir=".Length..];
            }
        }

        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            dataDirectory = DefaultDataDirectory;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataDirectory.Trim().Trim('"')));
        }
        catch
        {
            fullPath = DefaultDataDirectory;
        }

        return new StartupOptions
        {
            DataDirectory = fullPath,
            NoPin = noPin,
            AutoStart = autoStart,
        };
    }
}
