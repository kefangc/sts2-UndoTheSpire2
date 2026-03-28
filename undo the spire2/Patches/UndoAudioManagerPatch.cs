using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Audio;

namespace UndoTheSpire2;

[HarmonyPatch]
internal static class UndoAudioManagerPatch
{
    [HarmonyPatch(typeof(NAudioManager), nameof(NAudioManager.PlayLoop))]
    [HarmonyPrefix]
    private static bool OnPlayLoop(string path, bool usesLoopParam)
    {
        return UndoAudioLoopTracker.ShouldPlayLoop(path, usesLoopParam);
    }

    [HarmonyPatch(typeof(NAudioManager), nameof(NAudioManager.StopLoop))]
    [HarmonyPrefix]
    private static void OnStopLoop(string path)
    {
        UndoAudioLoopTracker.OnStopLoop(path);
    }

    [HarmonyPatch(typeof(NAudioManager), nameof(NAudioManager.SetParam))]
    [HarmonyPrefix]
    private static void OnSetParam(string path, string param, float value)
    {
        UndoAudioLoopTracker.OnSetLoopParam(path, param, value);
    }

    [HarmonyPatch(typeof(NAudioManager), nameof(NAudioManager.StopAllLoops))]
    [HarmonyPrefix]
    private static void OnStopAllLoops()
    {
        UndoAudioLoopTracker.Clear();
    }
}
