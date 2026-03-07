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
        Harmony harmony = new(ModId);
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.Info("UndoTheSpire2 patches applied.");
        UndoDebugLog.Write($"MainFile initialized. Debug log path={UndoDebugLog.CurrentPath}");
    }
}
