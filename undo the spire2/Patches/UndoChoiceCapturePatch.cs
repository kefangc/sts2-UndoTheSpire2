// 文件说明：在官方选牌入口处记录可恢复的 choice 元数据。
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace UndoTheSpire2;

[HarmonyPatch]
public static class UndoChoiceCapturePatch
{
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen))]
    [HarmonyPrefix]
    public static void ChooseACardPrefix(PlayerChoiceContext context, IReadOnlyList<CardModel> cards, Player player, bool canSkip = false)
    {
        MainFile.Controller.RegisterPendingChooseACardChoice(player, cards, canSkip, context.LastInvolvedModel);
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHand))]
    [HarmonyPrefix]
    public static void HandChoicePrefix(PlayerChoiceContext context, Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel source)
    {
        MainFile.Controller.RegisterPendingHandChoice(player, prefs, filter, source ?? context.LastInvolvedModel);
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForUpgrade))]
    [HarmonyPrefix]
    public static void HandUpgradePrefix(PlayerChoiceContext context, Player player, AbstractModel source)
    {
        if (CombatManager.Instance.IsOverOrEnding)
            return;

        IReadOnlyList<CardModel> handCards = PileType.Hand.GetPile(player).Cards;
        int upgradableCount = 0;
        foreach (CardModel card in handCards)
        {
            if (!card.IsUpgradable)
                continue;

            upgradableCount++;
            if (upgradableCount > 1)
                break;
        }

        if (upgradableCount <= 1)
            return;

        CardSelectorPrefs prefs = new(new LocString("gameplay_ui", "CHOOSE_CARD_UPGRADE_HEADER"), 1);
        MainFile.Controller.RegisterPendingHandChoice(player, prefs, static card => card.IsUpgradable, source);
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGrid))]
    [HarmonyPrefix]
    public static void SimpleGridPrefix(PlayerChoiceContext context, IReadOnlyList<CardModel> cards, Player player, CardSelectorPrefs prefs)
    {
        MainFile.Controller.RegisterPendingSimpleGridChoice(player, cards, prefs, context.LastInvolvedModel);
    }
}
