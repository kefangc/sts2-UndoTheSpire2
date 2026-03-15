// 文件说明：定义 mod 设置项及其读取逻辑。
using System.Text.Json;

namespace UndoTheSpire2;

internal static class UndoModSettings
{
    private sealed class UndoModSettingsData
    {
        public bool EnableChoiceUndo { get; set; } = true;
        public bool EnableUnifiedEffectMode { get; set; } = true;
    }

    private static readonly Lock Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static UndoModSettingsData _data = new();
    private static string? _settingsPath;

    public static event Action? Changed;

    public static bool EnableChoiceUndo => _data.EnableChoiceUndo;

    public static bool EnableUnifiedEffectMode => _data.EnableUnifiedEffectMode;

    public static string CurrentPath => _settingsPath ?? string.Empty;

    public static void Initialize()
    {
        lock (Sync)
        {
            _settingsPath = ResolveSettingsPath();
            _data = LoadCore(_settingsPath);
        }
    }

    public static void SetEnableChoiceUndo(bool value)
    {
        UpdateCore(data => data.EnableChoiceUndo = value);
    }

    public static void SetEnableUnifiedEffectMode(bool value)
    {
        UpdateCore(data => data.EnableUnifiedEffectMode = value);
    }

    private static void UpdateCore(Action<UndoModSettingsData> update)
    {
        bool changed;
        lock (Sync)
        {
            _settingsPath ??= ResolveSettingsPath();
            UndoModSettingsData before = Clone(_data);
            update(_data);
            changed = before.EnableChoiceUndo != _data.EnableChoiceUndo
                || before.EnableUnifiedEffectMode != _data.EnableUnifiedEffectMode;
            if (!changed)
                return;

            SaveCore(_settingsPath, _data);
        }

        Changed?.Invoke();
        UndoDebugLog.Write($"Settings updated. choiceUndo={EnableChoiceUndo} unifiedEffectMode={EnableUnifiedEffectMode}");
    }

    private static UndoModSettingsData LoadCore(string settingsPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            if (!File.Exists(settingsPath))
            {
                UndoModSettingsData defaults = CreateDefault();
                SaveCore(settingsPath, defaults);
                return defaults;
            }

            UndoModSettingsData? loaded = JsonSerializer.Deserialize<UndoModSettingsData>(File.ReadAllText(settingsPath));
            return loaded ?? CreateDefault();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to load undo settings. {ex.Message}");
            return CreateDefault();
        }
    }

    private static void SaveCore(string settingsPath, UndoModSettingsData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to save undo settings. {ex.Message}");
        }
    }

    private static UndoModSettingsData Clone(UndoModSettingsData data)
    {
        return new UndoModSettingsData
        {
            EnableChoiceUndo = data.EnableChoiceUndo,
            EnableUnifiedEffectMode = data.EnableUnifiedEffectMode
        };
    }

    private static UndoModSettingsData CreateDefault()
    {
        return new UndoModSettingsData
        {
            EnableChoiceUndo = true,
            EnableUnifiedEffectMode = true
        };
    }

    private static string ResolveSettingsPath()
    {
        List<string> candidates = [];
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
            candidates.Add(Path.Combine(appData, "SlayTheSpire2", "undo-the-spire2", "settings.json"));

        candidates.Add(Path.Combine("C:\\undo-the-spire2", "settings.json"));

        foreach (string candidate in candidates)
        {
            try
            {
                string? directory = Path.GetDirectoryName(candidate);
                if (string.IsNullOrWhiteSpace(directory))
                    continue;

                Directory.CreateDirectory(directory);
                return candidate;
            }
            catch
            {
            }
        }

        return Path.Combine("C:\\undo-the-spire2", "settings.json");
    }
}

