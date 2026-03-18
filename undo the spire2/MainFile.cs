// 文件说明：Mod 入口，负责注册补丁并初始化撤销控制器。
using System;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
namespace UndoTheSpire2;
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    internal const string ModId = "UndoTheSpire2";
    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);
    public static UndoController Controller { get; } = new();
    public static void Initialize()
    {
        UndoDebugLog.Initialize();
        UndoModSettings.Initialize();
        Harmony harmony = new(ModId);
        (int patchedClasses, int failedClasses) = PatchAllSafely(harmony, Assembly.GetExecutingAssembly());
        Logger.Info($"UndoTheSpire2 patching finished. patchedClasses={patchedClasses}, failedClasses={failedClasses}");
        UndoDebugLog.Write($"MainFile initialized. Debug log path={UndoDebugLog.CurrentPath}");
    }

    private static (int patchedClasses, int failedClasses) PatchAllSafely(Harmony harmony, Assembly assembly)
    {
        int patchedClasses = 0;
        int failedClasses = 0;
        foreach (Type type in assembly.GetTypes().Where(static t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0))
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
                patchedClasses++;
            }
            catch (Exception ex)
            {
                failedClasses++;
                Logger.Error($"Failed to patch {type.FullName}: {ex}");
                UndoDebugLog.Write($"Failed to patch {type.FullName}: {ex}");
            }
        }

        return (patchedClasses, failedClasses);
    }
}


