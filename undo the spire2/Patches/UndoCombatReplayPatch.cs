using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Saves;

namespace UndoTheSpire2;

[HarmonyPatch]
public static class UndoCombatReplayPatch
{
    [HarmonyPatch(typeof(CombatReplayWriter), "RecordInitialState")]
    [HarmonyPostfix]
    public static void RecordInitialStatePostfix(SerializableRun serializableRun)
    {
        MainFile.Controller.OnCombatReplayInitialized(serializableRun);
    }

    [HarmonyPatch(typeof(CombatReplayWriter), "RecordGameAction")]
    [HarmonyPostfix]
    public static void RecordGameActionPostfix(GameAction gameAction)
    {
        MainFile.Controller.RecordReplayGameAction(gameAction);
    }

    [HarmonyPatch(typeof(CombatReplayWriter), "RecordActionResume")]
    [HarmonyPostfix]
    public static void RecordActionResumePostfix(uint actionId)
    {
        MainFile.Controller.RecordReplayActionResume(actionId);
    }

    [HarmonyPatch(typeof(CombatReplayWriter), "RecordPlayerChoice")]
    [HarmonyPostfix]
    public static void RecordPlayerChoicePostfix(Player player, uint choiceId, NetPlayerChoiceResult result)
    {
        MainFile.Controller.RecordReplayPlayerChoice(player, choiceId, result);
        MainFile.Controller.OnPlayerChoiceResolved(player, result);
    }

    [HarmonyPatch(typeof(CombatReplayWriter), "RecordChecksum")]
    [HarmonyPostfix]
    public static void RecordChecksumPostfix(NetChecksumData checksum, string context, NetFullCombatState fullCombatState)
    {
        MainFile.Controller.RecordReplayChecksum(checksum, context, fullCombatState);
    }
}
