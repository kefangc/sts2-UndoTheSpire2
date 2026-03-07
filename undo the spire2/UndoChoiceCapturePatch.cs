using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace UndoTheSpire2;

[HarmonyPatch]
public static class UndoChoiceCapturePatch
{
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen))]
    [HarmonyPrefix]
    public static void ChooseACardPrefix(PlayerChoiceContext context, IReadOnlyList<CardModel> cards, Player player, bool canSkip = false)
    {
        MainFile.Controller.RegisterPendingChooseACardChoice(player, cards, canSkip);
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHand))]
    [HarmonyPrefix]
    public static void HandChoicePrefix(PlayerChoiceContext context, Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel source)
    {
        MainFile.Controller.RegisterPendingHandChoice(player, prefs, filter);
    }
}