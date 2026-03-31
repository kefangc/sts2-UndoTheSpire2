// 文件说明：处理 modding 界面相关的 undo 生命周期。
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace UndoTheSpire2;

[HarmonyPatch(typeof(NModdingScreen), nameof(NModdingScreen.OnRowSelected))]
internal static class UndoModdingScreenPatch
{
    private static readonly string[] ModIdentityCandidates =
    [
        MainFile.ModId,
        $"{MainFile.ModId}.dll",
        "Undo the Spire 2"
    ];

    [HarmonyPostfix]
    private static void OnRowSelectedPostfix(NModdingScreen __instance, NModMenuRow row)
    {
        NModInfoContainer? infoContainer = __instance.GetNodeOrNull<NModInfoContainer>("%ModInfoContainer");
        if (infoContainer == null)
            return;

        UndoModSettingsPanel? existing = infoContainer.GetNodeOrNull<UndoModSettingsPanel>(nameof(UndoModSettingsPanel));
        bool isOurMod = IsOurMod(row.Mod);
        if (!isOurMod)
        {
            existing?.QueueFree();
            return;
        }

        existing ??= new UndoModSettingsPanel();
        if (existing.GetParent() == null)
            infoContainer.AddChild(existing);

        existing.Bind(infoContainer);
    }

    private static bool IsOurMod(Mod? mod)
    {
        if (mod == null)
            return false;

        foreach (string memberName in new[] { "id", "modId", "name", "dllName", "dllFilename" })
        {
            if (MatchesOurMod(UndoReflectionUtil.ReadStringMember(mod, memberName)))
                return true;
        }

        object? manifest = UndoReflectionUtil.ReadMember(mod, "manifest") ?? UndoReflectionUtil.ReadMember(mod, "modManifest");
        if (manifest == null)
            return false;

        foreach (string memberName in new[] { "id", "name", "dllName", "dllFilename" })
        {
            if (MatchesOurMod(UndoReflectionUtil.ReadStringMember(manifest, memberName)))
                return true;
        }

        return false;
    }

    private static bool MatchesOurMod(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (string candidate in ModIdentityCandidates)
        {
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
