// 文件说明：承载 synthetic choice 的分支状态合成与 pile 差量推导逻辑。
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Cards;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoTheSpire2;

public sealed partial class UndoController
{
    private static bool TryCreateChooseACardCombatState(
        UndoSnapshot anchorSnapshot,
        UndoSnapshot templateSnapshot,
        UndoChoiceResultKey selectedKey,
        out UndoCombatFullState? combatState)
    {
        combatState = null;
        UndoChoiceSpec? choiceSpec = anchorSnapshot.ChoiceSpec;
        UndoChoiceResultKey? templateChoiceKey = templateSnapshot.ChoiceResultKey;
        if (choiceSpec == null
            || choiceSpec.Kind != UndoChoiceKind.ChooseACard
            || templateChoiceKey == null
            || templateChoiceKey.OptionIndexes.Count != 1
            || selectedKey.OptionIndexes.Count != 1)
        {
            return false;
        }

        int selectedOptionIndex = selectedKey.OptionIndexes[0];
        int templateOptionIndex = templateChoiceKey.OptionIndexes[0];
        if (selectedOptionIndex < -1 || selectedOptionIndex >= choiceSpec.OptionCards.Count)
            return false;
        if (templateOptionIndex < 0 || templateOptionIndex >= choiceSpec.OptionCards.Count)
            return false;

        NetFullCombatState fullState = CloneFullState(templateSnapshot.CombatState.FullState);
        if (!TryGetComparablePlayerStates(anchorSnapshot.CombatState.FullState, fullState, out int playerIndex, out NetFullCombatState.PlayerState anchorPlayerState, out NetFullCombatState.PlayerState branchPlayerState))
            return false;

        if (!TryFindGeneratedCombatCard(anchorPlayerState, branchPlayerState, out int pileIndex, out int cardIndex, out NetFullCombatState.CardState generatedCardState)
            && !TryFindChooseACardTemplateSlot(
                anchorPlayerState,
                templateSnapshot.CombatState.FullState.Players[playerIndex],
                choiceSpec.OptionCards[templateOptionIndex],
                out pileIndex,
                out cardIndex,
                out generatedCardState))
        {
            return false;
        }

        NetFullCombatState.CombatPileState branchPileState = branchPlayerState.piles[pileIndex];
        if (selectedOptionIndex < 0)
        {
            branchPileState.cards.RemoveAt(cardIndex);
        }
        else
        {
            branchPileState.cards[cardIndex] = CreateChooseACardCardState(choiceSpec.OptionCards[selectedOptionIndex], generatedCardState);
        }

        branchPlayerState.piles[pileIndex] = branchPileState;
        fullState.Players[playerIndex] = branchPlayerState;
        combatState = CreateDerivedCombatState(templateSnapshot.CombatState, anchorSnapshot.CombatState, fullState);
        if (combatState != null && selectedOptionIndex >= 0)
        {
            // For generated choose-a-card effects, the selected option can have its own
            // card cost/runtime state before the choice resolves, while the chosen card
            // may also receive extra post-choice cost changes (for example SetToFreeThisTurn).
            // Rebuilding from the selected option's packet alone drops both classes of
            // state; blindly copying the template-generated slot leaks the template
            // option's own cost onto the newly selected card. Rebuild from the selected
            // option snapshot and only overlay the template's post-choice cost delta.
            combatState = ApplyChooseACardOptionSupplementalOverrides(
                combatState,
                templateSnapshot.CombatState,
                choiceSpec,
                selectedOptionIndex,
                templateOptionIndex,
                branchPlayerState.playerId,
                branchPileState.pileType,
                cardIndex);
        }

        return true;
    }

    private static bool TryCreateHandSelectionCombatState(
        UndoSnapshot anchorSnapshot,
        UndoSnapshot templateSnapshot,
        UndoChoiceResultKey selectedKey,
        out UndoCombatFullState? combatState)
    {
        combatState = null;
        UndoChoiceSpec? choiceSpec = anchorSnapshot.ChoiceSpec;
        return choiceSpec != null
            && TryCreateSourcePileSelectionCombatState(anchorSnapshot, templateSnapshot, selectedKey, PileType.Hand, choiceSpec.SourcePileOptionIndexes, out combatState);
    }

    private static bool TryCreateSimpleGridSelectionCombatState(
        UndoSnapshot anchorSnapshot,
        UndoSnapshot templateSnapshot,
        UndoChoiceResultKey selectedKey,
        out UndoCombatFullState? combatState)
    {
        combatState = null;
        UndoChoiceSpec? choiceSpec = anchorSnapshot.ChoiceSpec;
        if (choiceSpec?.Kind != UndoChoiceKind.SimpleGridSelection || choiceSpec.SourcePileType == null)
            return false;

        // 计策在选牌后还会继续抽牌，直接套模板分支会把“被选中的牌”和“后续抽到的牌”重复保留下来。
        if (IsOrderedDrawSelectionChoiceSource(choiceSpec)
            && TryCreateStratagemCombatState(anchorSnapshot, templateSnapshot, selectedKey, out combatState))
        {
            return true;
        }

        return TryCreateSourcePileSelectionCombatState(anchorSnapshot, templateSnapshot, selectedKey, choiceSpec.SourcePileType.Value, choiceSpec.SourcePileOptionIndexes, out combatState);
    }

    private static bool TryCreateStratagemCombatState(
        UndoSnapshot anchorSnapshot,
        UndoSnapshot templateSnapshot,
        UndoChoiceResultKey selectedKey,
        out UndoCombatFullState? combatState)
    {
        combatState = null;
        UndoChoiceSpec? choiceSpec = anchorSnapshot.ChoiceSpec;
        if (choiceSpec == null
            || !IsOrderedDrawSelectionChoiceSource(choiceSpec)
            || choiceSpec.Kind != UndoChoiceKind.SimpleGridSelection
            || choiceSpec.SourcePileType != PileType.Draw)
        {
            return false;
        }

        NetFullCombatState fullState = CloneFullState(templateSnapshot.CombatState.FullState);
        if (!TryGetComparablePlayerStates(
                anchorSnapshot.CombatState.FullState,
                fullState,
                out int playerIndex,
                out NetFullCombatState.PlayerState anchorPlayerState,
                out NetFullCombatState.PlayerState branchPlayerState))
        {
            return false;
        }

        NetFullCombatState.PlayerState templatePlayerState = templateSnapshot.CombatState.FullState.Players[playerIndex];
        int anchorDrawPileIndex = FindPileIndex(anchorPlayerState.piles, PileType.Draw);
        int anchorHandPileIndex = FindPileIndex(anchorPlayerState.piles, PileType.Hand);
        int templateDrawPileIndex = FindPileIndex(templatePlayerState.piles, PileType.Draw);
        int templateHandPileIndex = FindPileIndex(templatePlayerState.piles, PileType.Hand);
        int branchDrawPileIndex = FindPileIndex(branchPlayerState.piles, PileType.Draw);
        int branchHandPileIndex = FindPileIndex(branchPlayerState.piles, PileType.Hand);
        if (anchorDrawPileIndex < 0
            || anchorHandPileIndex < 0
            || templateDrawPileIndex < 0
            || templateHandPileIndex < 0
            || branchDrawPileIndex < 0
            || branchHandPileIndex < 0)
        {
            return false;
        }

        NetFullCombatState.CombatPileState anchorDrawPileState = anchorPlayerState.piles[anchorDrawPileIndex];
        NetFullCombatState.CombatPileState anchorHandPileState = anchorPlayerState.piles[anchorHandPileIndex];
        NetFullCombatState.CombatPileState templateDrawPileState = templatePlayerState.piles[templateDrawPileIndex];
        NetFullCombatState.CombatPileState templateHandPileState = templatePlayerState.piles[templateHandPileIndex];

        List<int> selectedSourceIndexes = [];
        foreach (int optionIndex in selectedKey.OptionIndexes)
        {
            if (optionIndex < 0 || optionIndex >= choiceSpec.SourcePileOptionIndexes.Count)
                return false;

            int drawIndex = choiceSpec.SourcePileOptionIndexes[optionIndex];
            if (drawIndex < 0 || drawIndex >= anchorDrawPileState.cards.Count)
                return false;

            selectedSourceIndexes.Add(drawIndex);
        }

        if (selectedSourceIndexes.Count != selectedSourceIndexes.Distinct().Count())
            return false;

        List<NetFullCombatState.CardState> selectedCardStates = selectedSourceIndexes
            .Select(index => ClonePacketSerializable(anchorDrawPileState.cards[index]))
            .ToList();

        // 先从 anchor 的真实抽牌堆里移走这次新选的牌，后续抽牌必须基于这个修改后的堆顺序重建。
        HashSet<int> removedDrawIndexes = [.. selectedSourceIndexes];
        List<NetFullCombatState.CardState> drawAfterSelection = [];
        for (int i = 0; i < anchorDrawPileState.cards.Count; i++)
        {
            if (!removedDrawIndexes.Contains(i))
                drawAfterSelection.Add(ClonePacketSerializable(anchorDrawPileState.cards[i]));
        }

        // 模板分支只负责提供“选牌后官方还继续抽了多少张”，不再直接复用模板里的最终手牌内容。
        int cardsDrawnAfterChoice = templateHandPileState.cards.Count - anchorHandPileState.cards.Count - selectedCardStates.Count;
        if (cardsDrawnAfterChoice < 0 || cardsDrawnAfterChoice > drawAfterSelection.Count)
            return false;

        int expectedFinalDrawCount = drawAfterSelection.Count - cardsDrawnAfterChoice;
        if (templateDrawPileState.cards.Count != expectedFinalDrawCount)
            return false;

        NetFullCombatState.CombatPileState branchHandPileState = ClonePacketSerializable(anchorHandPileState);
        foreach (NetFullCombatState.CardState selectedCardState in selectedCardStates)
            branchHandPileState.cards.Add(ClonePacketSerializable(selectedCardState));
        for (int i = 0; i < cardsDrawnAfterChoice; i++)
            branchHandPileState.cards.Add(ClonePacketSerializable(drawAfterSelection[i]));

        if (branchHandPileState.cards.Count != templateHandPileState.cards.Count)
            return false;

        NetFullCombatState.CombatPileState branchDrawPileState = ClonePacketSerializable(anchorDrawPileState);
        branchDrawPileState.cards.Clear();
        for (int i = cardsDrawnAfterChoice; i < drawAfterSelection.Count; i++)
            branchDrawPileState.cards.Add(ClonePacketSerializable(drawAfterSelection[i]));

        branchPlayerState.piles[branchHandPileIndex] = branchHandPileState;
        branchPlayerState.piles[branchDrawPileIndex] = branchDrawPileState;
        fullState.Players[playerIndex] = branchPlayerState;
        combatState = CreateDerivedCombatState(templateSnapshot.CombatState, anchorSnapshot.CombatState, fullState);
        return true;
    }

    private static bool IsOrderedDrawSelectionChoiceSource(UndoChoiceSpec choiceSpec)
    {
        return IsSourceChoice(choiceSpec, typeof(StratagemPower))
            || IsSourceChoice(choiceSpec, typeof(ForegoneConclusionPower));
    }

    private static bool TryCreateSourcePileSelectionCombatState(
        UndoSnapshot anchorSnapshot,
        UndoSnapshot templateSnapshot,
        UndoChoiceResultKey selectedKey,
        PileType sourcePileType,
        IReadOnlyList<int> sourcePileOptionIndexes,
        out UndoCombatFullState? combatState)
    {
        combatState = null;
        UndoChoiceResultKey? templateChoiceKey = templateSnapshot.ChoiceResultKey;
        if (templateChoiceKey == null || templateChoiceKey.OptionIndexes.Count == 0)
            return false;

        if (TryCreateInPlaceSourcePileModificationCombatState(
                anchorSnapshot,
                templateSnapshot,
                templateChoiceKey,
                selectedKey,
                sourcePileType,
                sourcePileOptionIndexes,
                out combatState))
        {
            return true;
        }

        if (TryCreateLikeTemplateSourcePileSelectionCombatState(
                anchorSnapshot,
                templateSnapshot,
                templateChoiceKey,
                selectedKey,
                sourcePileType,
                sourcePileOptionIndexes,
                out combatState))
        {
            return true;
        }

        return TryCreateVariableCountSourcePileSelectionCombatState(
            anchorSnapshot,
            templateSnapshot,
            templateChoiceKey,
            selectedKey,
            sourcePileType,
            sourcePileOptionIndexes,
            out combatState);
    }

    private static bool TryCreateInPlaceSourcePileModificationCombatState(
        UndoSnapshot anchorSnapshot,
        UndoSnapshot templateSnapshot,
        UndoChoiceResultKey templateChoiceKey,
        UndoChoiceResultKey selectedKey,
        PileType sourcePileType,
        IReadOnlyList<int> sourcePileOptionIndexes,
        out UndoCombatFullState? combatState)
    {
        combatState = null;
        if (selectedKey.OptionIndexes.Count == 0
            || selectedKey.OptionIndexes.Count != templateChoiceKey.OptionIndexes.Count)
        {
            return false;
        }

        List<int> selectedOptionIndexes = [.. selectedKey.OptionIndexes];
        List<int> templateOptionIndexes = [.. templateChoiceKey.OptionIndexes];
        if (selectedOptionIndexes.Any(index => index < 0 || index >= sourcePileOptionIndexes.Count)
            || templateOptionIndexes.Any(index => index < 0 || index >= sourcePileOptionIndexes.Count))
        {
            return false;
        }

        NetFullCombatState fullState = CloneFullState(templateSnapshot.CombatState.FullState);
        if (!TryGetComparablePlayerStates(anchorSnapshot.CombatState.FullState, fullState, out int playerIndex, out NetFullCombatState.PlayerState anchorPlayerState, out NetFullCombatState.PlayerState branchPlayerState))
            return false;

        NetFullCombatState.PlayerState templatePlayerState = templateSnapshot.CombatState.FullState.Players[playerIndex];
        int anchorSourcePileIndex = FindPileIndex(anchorPlayerState.piles, sourcePileType);
        int templateSourcePileIndex = FindPileIndex(templatePlayerState.piles, sourcePileType);
        int branchSourcePileIndex = FindPileIndex(branchPlayerState.piles, sourcePileType);
        if (anchorSourcePileIndex < 0 || templateSourcePileIndex < 0 || branchSourcePileIndex < 0)
            return false;

        NetFullCombatState.CombatPileState anchorSourcePileState = anchorPlayerState.piles[anchorSourcePileIndex];
        NetFullCombatState.CombatPileState templateSourcePileState = templatePlayerState.piles[templateSourcePileIndex];
        if (anchorSourcePileState.cards.Count != templateSourcePileState.cards.Count)
            return false;

        List<int> selectedSourceIndexes = [];
        List<int> templateSourceIndexes = [];
        foreach (int optionIndex in selectedOptionIndexes)
            selectedSourceIndexes.Add(sourcePileOptionIndexes[optionIndex]);
        foreach (int optionIndex in templateOptionIndexes)
            templateSourceIndexes.Add(sourcePileOptionIndexes[optionIndex]);

        if (selectedSourceIndexes.Any(index => index < 0 || index >= anchorSourcePileState.cards.Count)
            || templateSourceIndexes.Any(index => index < 0 || index >= anchorSourcePileState.cards.Count))
        {
            return false;
        }

        List<int> changedSourceIndexes = FindUnmatchedCardIndexes(anchorSourcePileState.cards, templateSourcePileState.cards);
        if (changedSourceIndexes.Count != templateSourceIndexes.Count)
            return false;

        changedSourceIndexes.Sort();
        List<int> sortedTemplateSourceIndexes = [.. templateSourceIndexes.OrderBy(static index => index)];
        if (!changedSourceIndexes.SequenceEqual(sortedTemplateSourceIndexes))
            return false;

        List<NetFullCombatState.CardState> templateReplacementStates = templateSourceIndexes
            .Select(index => ClonePacketSerializable(templateSourcePileState.cards[index]))
            .ToList();

        NetFullCombatState.CombatPileState branchSourcePileState = ClonePacketSerializable(anchorSourcePileState);
        for (int i = 0; i < selectedSourceIndexes.Count; i++)
            branchSourcePileState.cards[selectedSourceIndexes[i]] = ClonePacketSerializable(templateReplacementStates[i]);

        branchPlayerState.piles[branchSourcePileIndex] = branchSourcePileState;
        fullState.Players[playerIndex] = branchPlayerState;
        combatState = CreateDerivedCombatState(templateSnapshot.CombatState, anchorSnapshot.CombatState, fullState);
        return true;
    }

    private static bool TryCreateLikeTemplateSourcePileSelectionCombatState(
        UndoSnapshot anchorSnapshot,
        UndoSnapshot templateSnapshot,
        UndoChoiceResultKey templateChoiceKey,
        UndoChoiceResultKey selectedKey,
        PileType sourcePileType,
        IReadOnlyList<int> sourcePileOptionIndexes,
        out UndoCombatFullState? combatState)
    {
        combatState = null;
        if (selectedKey.OptionIndexes.Count != templateChoiceKey.OptionIndexes.Count)
            return false;

        List<int> selectedOptionIndexes = [.. selectedKey.OptionIndexes];
        List<int> templateOptionIndexes = [.. templateChoiceKey.OptionIndexes];
        if (selectedOptionIndexes.Any(index => index < 0 || index >= sourcePileOptionIndexes.Count)
            || templateOptionIndexes.Any(index => index < 0 || index >= sourcePileOptionIndexes.Count))
        {
            return false;
        }

        NetFullCombatState fullState = CloneFullState(templateSnapshot.CombatState.FullState);
        if (!TryGetComparablePlayerStates(anchorSnapshot.CombatState.FullState, fullState, out int playerIndex, out NetFullCombatState.PlayerState anchorPlayerState, out NetFullCombatState.PlayerState branchPlayerState))
            return false;

        int anchorSourcePileIndex = FindPileIndex(anchorPlayerState.piles, sourcePileType);
        int branchSourcePileIndex = FindPileIndex(branchPlayerState.piles, sourcePileType);
        if (anchorSourcePileIndex < 0 || branchSourcePileIndex < 0)
            return false;

        NetFullCombatState.CombatPileState anchorSourcePileState = anchorPlayerState.piles[anchorSourcePileIndex];
        List<int> selectedSourceIndexes = [];
        List<int> templateSourceIndexes = [];
        foreach (int optionIndex in selectedOptionIndexes)
            selectedSourceIndexes.Add(sourcePileOptionIndexes[optionIndex]);
        foreach (int optionIndex in templateOptionIndexes)
            templateSourceIndexes.Add(sourcePileOptionIndexes[optionIndex]);

        if (selectedSourceIndexes.Any(index => index < 0 || index >= anchorSourcePileState.cards.Count)
            || templateSourceIndexes.Any(index => index < 0 || index >= anchorSourcePileState.cards.Count))
        {
            return false;
        }

        List<NetFullCombatState.CardState> selectedSourceCardStates = selectedSourceIndexes
            .Select(index => ClonePacketSerializable(anchorSourcePileState.cards[index]))
            .ToList();
        List<NetFullCombatState.CardState> templateSelectedCardStates = templateSourceIndexes
            .Select(index => anchorSourcePileState.cards[index])
            .ToList();

        NetFullCombatState.CombatPileState branchSourcePileState = ClonePacketSerializable(anchorSourcePileState);
        foreach (int sourceIndex in selectedSourceIndexes.OrderByDescending(static index => index))
            branchSourcePileState.cards.RemoveAt(sourceIndex);
        branchPlayerState.piles[branchSourcePileIndex] = branchSourcePileState;

        if (!TryFindSourceSelectionDestinationSlots(
                anchorPlayerState,
                templateSnapshot.CombatState.FullState.Players[playerIndex],
                sourcePileType,
                templateSelectedCardStates,
                out List<(int PileIndex, int CardIndex, int TemplateSelectionIndex)> destinationSlots))
        {
            return false;
        }

        if (destinationSlots.Count != selectedSourceCardStates.Count)
            return false;

        foreach ((int pileIndex, int cardIndex, int templateSelectionIndex) in destinationSlots)
        {
            NetFullCombatState.CombatPileState destinationPileState = branchPlayerState.piles[pileIndex];
            if (cardIndex < 0 || cardIndex >= destinationPileState.cards.Count)
                return false;

            destinationPileState.cards[cardIndex] = ClonePacketSerializable(selectedSourceCardStates[templateSelectionIndex]);
            branchPlayerState.piles[pileIndex] = destinationPileState;
        }

        fullState.Players[playerIndex] = branchPlayerState;
        UndoCombatFullState derivedCombatState = CreateDerivedCombatState(templateSnapshot.CombatState, anchorSnapshot.CombatState, fullState);
        combatState = ApplySourceSelectionSupplementalOverrides(
            derivedCombatState,
            anchorSnapshot.CombatState,
            templateSnapshot.CombatState.FullState.Players[playerIndex],
            anchorPlayerState.playerId,
            sourcePileType,
            selectedSourceIndexes,
            destinationSlots);
        return true;
    }

    private static bool TryCreateVariableCountSourcePileSelectionCombatState(
        UndoSnapshot anchorSnapshot,
        UndoSnapshot templateSnapshot,
        UndoChoiceResultKey templateChoiceKey,
        UndoChoiceResultKey selectedKey,
        PileType sourcePileType,
        IReadOnlyList<int> sourcePileOptionIndexes,
        out UndoCombatFullState? combatState)
    {
        combatState = null;
        List<int> selectedOptionIndexes = [.. selectedKey.OptionIndexes];
        List<int> templateOptionIndexes = [.. templateChoiceKey.OptionIndexes];
        if (selectedOptionIndexes.Any(index => index < 0 || index >= sourcePileOptionIndexes.Count)
            || templateOptionIndexes.Any(index => index < 0 || index >= sourcePileOptionIndexes.Count))
        {
            return false;
        }

        NetFullCombatState fullState = CloneFullState(templateSnapshot.CombatState.FullState);
        if (!TryGetComparablePlayerStates(anchorSnapshot.CombatState.FullState, fullState, out int playerIndex, out NetFullCombatState.PlayerState anchorPlayerState, out NetFullCombatState.PlayerState branchPlayerState))
            return false;

        NetFullCombatState.PlayerState templatePlayerState = templateSnapshot.CombatState.FullState.Players[playerIndex];
        int anchorSourcePileIndex = FindPileIndex(anchorPlayerState.piles, sourcePileType);
        int branchSourcePileIndex = FindPileIndex(branchPlayerState.piles, sourcePileType);
        if (anchorSourcePileIndex < 0 || branchSourcePileIndex < 0)
            return false;

        NetFullCombatState.CombatPileState anchorSourcePileState = anchorPlayerState.piles[anchorSourcePileIndex];
        List<int> selectedSourceIndexes = [];
        List<int> templateSourceIndexes = [];
        foreach (int optionIndex in selectedOptionIndexes)
            selectedSourceIndexes.Add(sourcePileOptionIndexes[optionIndex]);
        foreach (int optionIndex in templateOptionIndexes)
            templateSourceIndexes.Add(sourcePileOptionIndexes[optionIndex]);

        if (selectedSourceIndexes.Any(index => index < 0 || index >= anchorSourcePileState.cards.Count)
            || templateSourceIndexes.Any(index => index < 0 || index >= anchorSourcePileState.cards.Count))
        {
            return false;
        }

        List<NetFullCombatState.CardState> selectedSourceCardStates = selectedSourceIndexes
            .Select(index => ClonePacketSerializable(anchorSourcePileState.cards[index]))
            .ToList();
        List<NetFullCombatState.CardState> templateSelectedCardStates = templateSourceIndexes
            .Select(index => anchorSourcePileState.cards[index])
            .ToList();

        NetFullCombatState.CombatPileState branchSourcePileState = ClonePacketSerializable(anchorSourcePileState);
        foreach (int sourceIndex in selectedSourceIndexes.OrderByDescending(static index => index))
            branchSourcePileState.cards.RemoveAt(sourceIndex);
        branchPlayerState.piles[branchSourcePileIndex] = branchSourcePileState;

        if (!TryApplyVariableCountSourceSelectionDestinationPattern(
                anchorPlayerState,
                templatePlayerState,
                branchPlayerState,
                sourcePileType,
                templateSelectedCardStates,
                selectedSourceCardStates))
        {
            return false;
        }

        fullState.Players[playerIndex] = branchPlayerState;
        UndoCombatFullState derivedCombatState = CreateDerivedCombatState(templateSnapshot.CombatState, anchorSnapshot.CombatState, fullState);
        if (TryFindSourceSelectionDestinationSlots(
                anchorPlayerState,
                templatePlayerState,
                sourcePileType,
                templateSelectedCardStates,
                out List<(int PileIndex, int CardIndex, int TemplateSelectionIndex)> destinationSlots))
        {
            combatState = ApplySourceSelectionSupplementalOverrides(
                derivedCombatState,
                anchorSnapshot.CombatState,
                templatePlayerState,
                anchorPlayerState.playerId,
                sourcePileType,
                selectedSourceIndexes,
                destinationSlots);
        }
        else
        {
            combatState = derivedCombatState;
        }

        return true;
    }

    // 对“从某个 pile 选牌并把原牌实例移动到别处”的分支，选中的牌应继续携带自己
    // 的费用和 runtime 状态，而不是复用模板分支目标槽位上原来那张牌的补充状态。
    private static UndoCombatFullState ApplySourceSelectionSupplementalOverrides(
        UndoCombatFullState derivedCombatState,
        UndoCombatFullState anchorCombatState,
        NetFullCombatState.PlayerState templatePlayerState,
        ulong playerNetId,
        PileType sourcePileType,
        IReadOnlyList<int> selectedSourceIndexes,
        IReadOnlyList<(int PileIndex, int CardIndex, int TemplateSelectionIndex)> destinationSlots)
    {
        IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardCostState>>? anchorCostStatesByPile = GetCardCostStatesForPlayer(anchorCombatState, playerNetId);
        IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardRuntimeState>>? anchorRuntimeStatesByPile = GetCardRuntimeStatesForPlayer(anchorCombatState, playerNetId);
        IReadOnlyList<UndoCardCostState>? sourceCostStates = null;
        IReadOnlyList<UndoCardRuntimeState>? sourceRuntimeStates = null;
        anchorCostStatesByPile?.TryGetValue(sourcePileType, out sourceCostStates);
        anchorRuntimeStatesByPile?.TryGetValue(sourcePileType, out sourceRuntimeStates);
        if ((sourceCostStates == null || sourceCostStates.Count == 0) && (sourceRuntimeStates == null || sourceRuntimeStates.Count == 0))
            return derivedCombatState;

        Dictionary<PileType, Dictionary<int, UndoCardCostState>> costOverridesByPile = [];
        Dictionary<PileType, Dictionary<int, UndoCardRuntimeState>> runtimeOverridesByPile = [];
        foreach ((int pileIndex, int cardIndex, int templateSelectionIndex) in destinationSlots)
        {
            if (templateSelectionIndex < 0 || templateSelectionIndex >= selectedSourceIndexes.Count)
                continue;

            int sourceIndex = selectedSourceIndexes[templateSelectionIndex];
            PileType destinationPileType = templatePlayerState.piles[pileIndex].pileType;
            if (sourceCostStates != null && sourceIndex >= 0 && sourceIndex < sourceCostStates.Count)
            {
                if (!costOverridesByPile.TryGetValue(destinationPileType, out Dictionary<int, UndoCardCostState>? pileOverrides))
                {
                    pileOverrides = [];
                    costOverridesByPile[destinationPileType] = pileOverrides;
                }

                pileOverrides[cardIndex] = sourceCostStates[sourceIndex];
            }

            if (sourceRuntimeStates != null && sourceIndex >= 0 && sourceIndex < sourceRuntimeStates.Count)
            {
                if (!runtimeOverridesByPile.TryGetValue(destinationPileType, out Dictionary<int, UndoCardRuntimeState>? pileOverrides))
                {
                    pileOverrides = [];
                    runtimeOverridesByPile[destinationPileType] = pileOverrides;
                }

                pileOverrides[cardIndex] = sourceRuntimeStates[sourceIndex];
            }
        }

        if (costOverridesByPile.Count == 0 && runtimeOverridesByPile.Count == 0)
            return derivedCombatState;

        List<UndoPlayerPileCardCostState> updatedCostStates = [];
        foreach (UndoPlayerPileCardCostState pileState in derivedCombatState.CardCostStates)
        {
            if (pileState.PlayerNetId != playerNetId
                || !costOverridesByPile.TryGetValue(pileState.PileType, out Dictionary<int, UndoCardCostState>? pileOverrides))
            {
                updatedCostStates.Add(pileState);
                continue;
            }

            List<UndoCardCostState> cards = [.. pileState.Cards];
            foreach ((int cardIndex, UndoCardCostState costState) in pileOverrides)
            {
                if (cardIndex >= 0 && cardIndex < cards.Count)
                    cards[cardIndex] = costState;
            }

            updatedCostStates.Add(new UndoPlayerPileCardCostState
            {
                PlayerNetId = pileState.PlayerNetId,
                PileType = pileState.PileType,
                Cards = cards
            });
        }

        List<UndoPlayerPileCardRuntimeState> updatedRuntimeStates = [];
        foreach (UndoPlayerPileCardRuntimeState pileState in derivedCombatState.CardRuntimeStates)
        {
            if (pileState.PlayerNetId != playerNetId
                || !runtimeOverridesByPile.TryGetValue(pileState.PileType, out Dictionary<int, UndoCardRuntimeState>? pileOverrides))
            {
                updatedRuntimeStates.Add(pileState);
                continue;
            }

            List<UndoCardRuntimeState> cards = [.. pileState.Cards];
            foreach ((int cardIndex, UndoCardRuntimeState runtimeState) in pileOverrides)
            {
                if (cardIndex >= 0 && cardIndex < cards.Count)
                    cards[cardIndex] = runtimeState;
            }

            updatedRuntimeStates.Add(new UndoPlayerPileCardRuntimeState
            {
                PlayerNetId = pileState.PlayerNetId,
                PileType = pileState.PileType,
                Cards = cards
            });
        }

        return CreateDerivedCombatState(derivedCombatState, derivedCombatState.FullState, updatedCostStates, updatedRuntimeStates);
    }

    private static UndoCombatFullState ApplyChooseACardOptionSupplementalOverrides(
        UndoCombatFullState derivedCombatState,
        UndoCombatFullState templateCombatState,
        UndoChoiceSpec choiceSpec,
        int selectedOptionIndex,
        int templateOptionIndex,
        ulong playerNetId,
        PileType pileType,
        int cardIndex)
    {
        IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardCostState>>? templateCostStatesByPile = GetCardCostStatesForPlayer(templateCombatState, playerNetId);
        IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardRuntimeState>>? templateRuntimeStatesByPile = GetCardRuntimeStatesForPlayer(templateCombatState, playerNetId);
        IReadOnlyList<UndoCardCostState>? templatePileCostStates = null;
        IReadOnlyList<UndoCardRuntimeState>? templatePileRuntimeStates = null;
        templateCostStatesByPile?.TryGetValue(pileType, out templatePileCostStates);
        templateRuntimeStatesByPile?.TryGetValue(pileType, out templatePileRuntimeStates);

        UndoCardCostState? templateGeneratedCostState = templatePileCostStates != null && cardIndex >= 0 && cardIndex < templatePileCostStates.Count
            ? templatePileCostStates[cardIndex]
            : null;
        UndoCardRuntimeState? templateGeneratedRuntimeState = templatePileRuntimeStates != null && cardIndex >= 0 && cardIndex < templatePileRuntimeStates.Count
            ? templatePileRuntimeStates[cardIndex]
            : null;
        UndoCardCostState? selectedOptionCostState = selectedOptionIndex >= 0 && selectedOptionIndex < choiceSpec.OptionCardCostStates.Count
            ? choiceSpec.OptionCardCostStates[selectedOptionIndex]
            : null;
        UndoCardCostState? templateOptionCostState = templateOptionIndex >= 0 && templateOptionIndex < choiceSpec.OptionCardCostStates.Count
            ? choiceSpec.OptionCardCostStates[templateOptionIndex]
            : null;
        UndoCardRuntimeState? selectedOptionRuntimeState = selectedOptionIndex >= 0 && selectedOptionIndex < choiceSpec.OptionCardRuntimeStates.Count
            ? choiceSpec.OptionCardRuntimeStates[selectedOptionIndex]
            : null;

        UndoCardCostState? mergedCostState = MergeChooseACardCostState(
            selectedOptionCostState,
            templateOptionCostState,
            templateGeneratedCostState);
        UndoCardRuntimeState? mergedRuntimeState = selectedOptionRuntimeState ?? templateGeneratedRuntimeState;
        if (mergedCostState == null && mergedRuntimeState == null)
            return derivedCombatState;

        bool costUpdated = false;
        List<UndoPlayerPileCardCostState> updatedCostStates = [];
        foreach (UndoPlayerPileCardCostState pileState in derivedCombatState.CardCostStates)
        {
            if (mergedCostState == null
                || pileState.PlayerNetId != playerNetId
                || pileState.PileType != pileType
                || cardIndex < 0
                || cardIndex >= pileState.Cards.Count)
            {
                updatedCostStates.Add(pileState);
                continue;
            }

            List<UndoCardCostState> cards = [.. pileState.Cards];
            cards[cardIndex] = mergedCostState;
            updatedCostStates.Add(new UndoPlayerPileCardCostState
            {
                PlayerNetId = pileState.PlayerNetId,
                PileType = pileState.PileType,
                Cards = cards
            });
            costUpdated = true;
        }

        bool runtimeUpdated = false;
        List<UndoPlayerPileCardRuntimeState> updatedRuntimeStates = [];
        foreach (UndoPlayerPileCardRuntimeState pileState in derivedCombatState.CardRuntimeStates)
        {
            if (mergedRuntimeState == null
                || pileState.PlayerNetId != playerNetId
                || pileState.PileType != pileType
                || cardIndex < 0
                || cardIndex >= pileState.Cards.Count)
            {
                updatedRuntimeStates.Add(pileState);
                continue;
            }

            List<UndoCardRuntimeState> cards = [.. pileState.Cards];
            cards[cardIndex] = mergedRuntimeState;
            updatedRuntimeStates.Add(new UndoPlayerPileCardRuntimeState
            {
                PlayerNetId = pileState.PlayerNetId,
                PileType = pileState.PileType,
                Cards = cards
            });
            runtimeUpdated = true;
        }

        if (!costUpdated && !runtimeUpdated)
            return derivedCombatState;

        return CreateDerivedCombatState(
            derivedCombatState,
            derivedCombatState.FullState,
            costUpdated ? updatedCostStates : derivedCombatState.CardCostStates,
            runtimeUpdated ? updatedRuntimeStates : derivedCombatState.CardRuntimeStates);
    }

    private static UndoCardCostState? MergeChooseACardCostState(
        UndoCardCostState? selectedOptionCostState,
        UndoCardCostState? templateOptionCostState,
        UndoCardCostState? templateGeneratedCostState)
    {
        if (selectedOptionCostState == null)
            return templateGeneratedCostState;

        if (templateOptionCostState == null || templateGeneratedCostState == null)
            return selectedOptionCostState;

        return new UndoCardCostState
        {
            EnergyBaseCost = ChooseGeneratedDelta(
                selectedOptionCostState.EnergyBaseCost,
                templateOptionCostState.EnergyBaseCost,
                templateGeneratedCostState.EnergyBaseCost),
            CapturedXValue = ChooseGeneratedDelta(
                selectedOptionCostState.CapturedXValue,
                templateOptionCostState.CapturedXValue,
                templateGeneratedCostState.CapturedXValue),
            EnergyWasJustUpgraded = ChooseGeneratedDelta(
                selectedOptionCostState.EnergyWasJustUpgraded,
                templateOptionCostState.EnergyWasJustUpgraded,
                templateGeneratedCostState.EnergyWasJustUpgraded),
            EnergyLocalModifiers = ChooseGeneratedDelta(
                selectedOptionCostState.EnergyLocalModifiers,
                templateOptionCostState.EnergyLocalModifiers,
                templateGeneratedCostState.EnergyLocalModifiers,
                AreLocalCostModifierListsEquivalent),
            StarCostSet = ChooseGeneratedDelta(
                selectedOptionCostState.StarCostSet,
                templateOptionCostState.StarCostSet,
                templateGeneratedCostState.StarCostSet),
            BaseStarCost = ChooseGeneratedDelta(
                selectedOptionCostState.BaseStarCost,
                templateOptionCostState.BaseStarCost,
                templateGeneratedCostState.BaseStarCost),
            StarWasJustUpgraded = ChooseGeneratedDelta(
                selectedOptionCostState.StarWasJustUpgraded,
                templateOptionCostState.StarWasJustUpgraded,
                templateGeneratedCostState.StarWasJustUpgraded),
            TemporaryStarCosts = ChooseGeneratedDelta(
                selectedOptionCostState.TemporaryStarCosts,
                templateOptionCostState.TemporaryStarCosts,
                templateGeneratedCostState.TemporaryStarCosts,
                AreTemporaryStarCostListsEquivalent)
        };
    }

    private static T ChooseGeneratedDelta<T>(T selectedValue, T templateOptionValue, T templateGeneratedValue) where T : IEquatable<T>
    {
        return templateGeneratedValue.Equals(templateOptionValue) ? selectedValue : templateGeneratedValue;
    }

    private static T ChooseGeneratedDelta<T>(
        T selectedValue,
        T templateOptionValue,
        T templateGeneratedValue,
        Func<T, T, bool> comparer)
    {
        return comparer(templateGeneratedValue, templateOptionValue) ? selectedValue : templateGeneratedValue;
    }

    private static bool AreLocalCostModifierListsEquivalent(
        IReadOnlyList<UndoLocalCostModifierState> left,
        IReadOnlyList<UndoLocalCostModifierState> right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            UndoLocalCostModifierState leftEntry = left[i];
            UndoLocalCostModifierState rightEntry = right[i];
            if (leftEntry.Amount != rightEntry.Amount
                || leftEntry.Type != rightEntry.Type
                || leftEntry.Expiration != rightEntry.Expiration
                || leftEntry.IsReduceOnly != rightEntry.IsReduceOnly)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreTemporaryStarCostListsEquivalent(
        IReadOnlyList<UndoTemporaryStarCostState> left,
        IReadOnlyList<UndoTemporaryStarCostState> right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            UndoTemporaryStarCostState leftEntry = left[i];
            UndoTemporaryStarCostState rightEntry = right[i];
            if (leftEntry.Cost != rightEntry.Cost
                || leftEntry.ClearsWhenTurnEnds != rightEntry.ClearsWhenTurnEnds
                || leftEntry.ClearsWhenCardIsPlayed != rightEntry.ClearsWhenCardIsPlayed)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryApplyVariableCountSourceSelectionDestinationPattern(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState templatePlayerState,
        NetFullCombatState.PlayerState branchPlayerState,
        PileType sourcePileType,
        IReadOnlyList<NetFullCombatState.CardState> templateSelectedCardStates,
        IReadOnlyList<NetFullCombatState.CardState> selectedSourceCardStates)
    {
        return TryApplyTemplateMatchedSourceSelectionDestinationPattern(
                anchorPlayerState,
                templatePlayerState,
                branchPlayerState,
                sourcePileType,
                templateSelectedCardStates,
                selectedSourceCardStates)
            || TryApplyCountDeltaSourceSelectionDestinationPattern(
                anchorPlayerState,
                templatePlayerState,
                branchPlayerState,
                sourcePileType,
                templateSelectedCardStates,
                selectedSourceCardStates);
    }

    private static bool TryApplyTemplateMatchedSourceSelectionDestinationPattern(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState templatePlayerState,
        NetFullCombatState.PlayerState branchPlayerState,
        PileType sourcePileType,
        IReadOnlyList<NetFullCombatState.CardState> templateSelectedCardStates,
        IReadOnlyList<NetFullCombatState.CardState> selectedSourceCardStates)
    {
        List<(int TemplatePileIndex, int CardIndex, int TemplateSelectionIndex)> matchedTemplateSlots = [];
        bool[] usedTemplateSelections = new bool[templateSelectedCardStates.Count];

        for (int i = 0; i < templatePlayerState.piles.Count; i++)
        {
            NetFullCombatState.CombatPileState templatePileState = templatePlayerState.piles[i];
            if (templatePileState.pileType == sourcePileType)
                continue;

            int anchorPileIndex = FindPileIndex(anchorPlayerState.piles, templatePileState.pileType);
            if (anchorPileIndex < 0)
                return false;

            NetFullCombatState.CombatPileState anchorPileState = anchorPlayerState.piles[anchorPileIndex];
            List<int> unmatchedIndexes = FindUnmatchedCardIndexes(anchorPileState.cards, templatePileState.cards);
            foreach (int unmatchedIndex in unmatchedIndexes)
            {
                for (int selectionIndex = 0; selectionIndex < templateSelectedCardStates.Count; selectionIndex++)
                {
                    if (usedTemplateSelections[selectionIndex]
                        || !PacketDataEquals(templatePileState.cards[unmatchedIndex], templateSelectedCardStates[selectionIndex]))
                    {
                        continue;
                    }

                    usedTemplateSelections[selectionIndex] = true;
                    matchedTemplateSlots.Add((i, unmatchedIndex, selectionIndex));
                    break;
                }
            }
        }

        if (!usedTemplateSelections.All(static used => used))
            return false;

        if (matchedTemplateSlots.Count == 0)
            return selectedSourceCardStates.Count == 0;

        List<IGrouping<int, (int TemplatePileIndex, int CardIndex, int TemplateSelectionIndex)>> destinationGroups = matchedTemplateSlots
            .GroupBy(static slot => slot.TemplatePileIndex)
            .ToList();
        if (destinationGroups.Count != 1)
            return false;

        IGrouping<int, (int TemplatePileIndex, int CardIndex, int TemplateSelectionIndex)> destinationGroup = destinationGroups[0];
        List<(int TemplatePileIndex, int CardIndex, int TemplateSelectionIndex)> orderedSlots = destinationGroup
            .OrderBy(slot => slot.CardIndex)
            .ToList();

        int insertionIndex = orderedSlots[0].CardIndex;
        for (int i = 0; i < orderedSlots.Count; i++)
        {
            if (orderedSlots[i].CardIndex != insertionIndex + i)
                return false;
        }

        List<int> templateSelectionOrder = orderedSlots
            .Select(slot => slot.TemplateSelectionIndex)
            .ToList();
        bool isForwardOrder = templateSelectionOrder.SequenceEqual(Enumerable.Range(0, templateSelectionOrder.Count));
        bool isReverseOrder = templateSelectionOrder.SequenceEqual(Enumerable.Range(0, templateSelectionOrder.Count).Reverse());
        if (!isForwardOrder && !isReverseOrder)
            return false;

        int templateDestinationPileIndex = destinationGroup.Key;
        PileType destinationPileType = templatePlayerState.piles[templateDestinationPileIndex].pileType;
        int branchDestinationPileIndex = FindPileIndex(branchPlayerState.piles, destinationPileType);
        if (branchDestinationPileIndex < 0)
            return false;

        NetFullCombatState.CombatPileState branchDestinationPileState = ClonePacketSerializable(branchPlayerState.piles[branchDestinationPileIndex]);
        foreach ((int _, int cardIndex, _) in orderedSlots.OrderByDescending(static slot => slot.CardIndex))
            branchDestinationPileState.cards.RemoveAt(cardIndex);

        List<NetFullCombatState.CardState> cardsToInsert = selectedSourceCardStates
            .Select(ClonePacketSerializable)
            .ToList();
        if (isReverseOrder)
            cardsToInsert.Reverse();

        for (int i = 0; i < cardsToInsert.Count; i++)
            branchDestinationPileState.cards.Insert(insertionIndex + i, cardsToInsert[i]);

        branchPlayerState.piles[branchDestinationPileIndex] = branchDestinationPileState;
        return true;
    }

    private static bool TryApplyCountDeltaSourceSelectionDestinationPattern(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState templatePlayerState,
        NetFullCombatState.PlayerState branchPlayerState,
        PileType sourcePileType,
        IReadOnlyList<NetFullCombatState.CardState> templateSelectedCardStates,
        IReadOnlyList<NetFullCombatState.CardState> selectedSourceCardStates)
    {
        int anchorSourcePileIndex = FindPileIndex(anchorPlayerState.piles, sourcePileType);
        int templateSourcePileIndex = FindPileIndex(templatePlayerState.piles, sourcePileType);
        if (anchorSourcePileIndex < 0 || templateSourcePileIndex < 0)
            return false;

        NetFullCombatState.CombatPileState anchorSourcePileState = anchorPlayerState.piles[anchorSourcePileIndex];
        NetFullCombatState.CombatPileState templateSourcePileState = templatePlayerState.piles[templateSourcePileIndex];
        if (anchorSourcePileState.cards.Count - templateSourcePileState.cards.Count != templateSelectedCardStates.Count)
            return false;

        int templateDestinationPileIndex = -1;
        PileType destinationPileType = default;
        List<int>? destinationUnmatchedIndexes = null;

        for (int i = 0; i < templatePlayerState.piles.Count; i++)
        {
            NetFullCombatState.CombatPileState templatePileState = templatePlayerState.piles[i];
            if (templatePileState.pileType == sourcePileType)
                continue;

            int anchorPileIndex = FindPileIndex(anchorPlayerState.piles, templatePileState.pileType);
            if (anchorPileIndex < 0)
                return false;

            NetFullCombatState.CombatPileState anchorPileState = anchorPlayerState.piles[anchorPileIndex];
            int countDelta = templatePileState.cards.Count - anchorPileState.cards.Count;
            if (countDelta < 0)
                return false;
            if (countDelta == 0)
                continue;
            if (countDelta < templateSelectedCardStates.Count || templateDestinationPileIndex >= 0)
                return false;

            List<int> unmatchedIndexes = FindUnmatchedCardIndexes(anchorPileState.cards, templatePileState.cards);
            if (unmatchedIndexes.Count != countDelta)
                return false;

            templateDestinationPileIndex = i;
            destinationPileType = templatePileState.pileType;
            destinationUnmatchedIndexes = unmatchedIndexes;
        }

        if (templateDestinationPileIndex < 0 || destinationUnmatchedIndexes == null)
            return selectedSourceCardStates.Count == 0;

        destinationUnmatchedIndexes.Sort();
        int insertionIndex = destinationUnmatchedIndexes[0];
        int contiguousSelectionSpan = Math.Min(templateSelectedCardStates.Count, destinationUnmatchedIndexes.Count);
        for (int i = 0; i < contiguousSelectionSpan; i++)
        {
            if (destinationUnmatchedIndexes[i] != insertionIndex + i)
                return false;
        }

        int branchDestinationPileIndex = FindPileIndex(branchPlayerState.piles, destinationPileType);
        if (branchDestinationPileIndex < 0)
            return false;

        bool reverseOrder = destinationPileType == PileType.Deck && insertionIndex == 0 && selectedSourceCardStates.Count > 1;
        NetFullCombatState.CombatPileState branchDestinationPileState = ClonePacketSerializable(branchPlayerState.piles[branchDestinationPileIndex]);
        for (int i = contiguousSelectionSpan - 1; i >= 0; i--)
            branchDestinationPileState.cards.RemoveAt(insertionIndex + i);

        List<NetFullCombatState.CardState> cardsToInsert = selectedSourceCardStates
            .Select(ClonePacketSerializable)
            .ToList();
        if (reverseOrder)
            cardsToInsert.Reverse();

        for (int i = 0; i < cardsToInsert.Count; i++)
            branchDestinationPileState.cards.Insert(insertionIndex + i, cardsToInsert[i]);

        branchPlayerState.piles[branchDestinationPileIndex] = branchDestinationPileState;
        return true;
    }

    private static bool TryFindSourceSelectionDestinationSlots(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState templatePlayerState,
        PileType sourcePileType,
        IReadOnlyList<NetFullCombatState.CardState> templateSelectedCardStates,
        out List<(int PileIndex, int CardIndex, int TemplateSelectionIndex)> destinationSlots)
    {
        destinationSlots = [];
        List<(int PileIndex, int CardIndex, NetFullCombatState.CardState CardState)> unmatchedSlots = [];

        for (int i = 0; i < templatePlayerState.piles.Count; i++)
        {
            NetFullCombatState.CombatPileState templatePileState = templatePlayerState.piles[i];
            if (templatePileState.pileType == sourcePileType)
                continue;

            int anchorPileIndex = FindPileIndex(anchorPlayerState.piles, templatePileState.pileType);
            if (anchorPileIndex < 0)
                return false;

            NetFullCombatState.CombatPileState anchorPileState = anchorPlayerState.piles[anchorPileIndex];
            List<int> unmatchedIndexes = FindUnmatchedCardIndexes(anchorPileState.cards, templatePileState.cards);
            foreach (int unmatchedIndex in unmatchedIndexes)
                unmatchedSlots.Add((i, unmatchedIndex, templatePileState.cards[unmatchedIndex]));
        }

        if (unmatchedSlots.Count != templateSelectedCardStates.Count)
            return false;

        bool[] usedTemplateSelections = new bool[templateSelectedCardStates.Count];
        foreach ((int pileIndex, int cardIndex, NetFullCombatState.CardState cardState) in unmatchedSlots)
        {
            int matchedSelectionIndex = -1;
            for (int i = 0; i < templateSelectedCardStates.Count; i++)
            {
                if (usedTemplateSelections[i] || !PacketDataEquals(cardState, templateSelectedCardStates[i]))
                    continue;

                matchedSelectionIndex = i;
                usedTemplateSelections[i] = true;
                break;
            }

            if (matchedSelectionIndex < 0)
                return false;

            destinationSlots.Add((pileIndex, cardIndex, matchedSelectionIndex));
        }

        return usedTemplateSelections.All(static used => used);
    }

    private static bool TryFindDeterministicSourceSelectionDestinationSlot(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState templatePlayerState,
        PileType sourcePileType,
        out int destinationPileIndex,
        out int destinationCardIndex)
    {
        destinationPileIndex = -1;
        destinationCardIndex = -1;
        bool foundDestination = false;

        for (int i = 0; i < templatePlayerState.piles.Count; i++)
        {
            NetFullCombatState.CombatPileState templatePileState = templatePlayerState.piles[i];
            if (templatePileState.pileType == sourcePileType)
                continue;

            int anchorPileIndex = FindPileIndex(anchorPlayerState.piles, templatePileState.pileType);
            if (anchorPileIndex < 0)
                return false;

            NetFullCombatState.CombatPileState anchorPileState = anchorPlayerState.piles[anchorPileIndex];
            List<int> unmatchedIndexes = FindUnmatchedCardIndexes(anchorPileState.cards, templatePileState.cards);
            if (unmatchedIndexes.Count == 0)
                continue;
            if (unmatchedIndexes.Count != 1)
                return false;
            if (foundDestination)
                return false;

            foundDestination = true;
            destinationPileIndex = i;
            destinationCardIndex = unmatchedIndexes[0];
        }

        return foundDestination;
    }

    private static bool TryFindSourceSelectionDestinationSlot(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState templatePlayerState,
        PileType sourcePileType,
        NetFullCombatState.CardState templateSelectedCardState,
        out int destinationPileIndex,
        out int destinationCardIndex)
    {
        destinationPileIndex = -1;
        destinationCardIndex = -1;
        bool foundDestination = false;

        for (int i = 0; i < templatePlayerState.piles.Count; i++)
        {
            NetFullCombatState.CombatPileState templatePileState = templatePlayerState.piles[i];
            if (templatePileState.pileType == sourcePileType)
                continue;

            int anchorPileIndex = FindPileIndex(anchorPlayerState.piles, templatePileState.pileType);
            if (anchorPileIndex < 0)
                return false;

            NetFullCombatState.CombatPileState anchorPileState = anchorPlayerState.piles[anchorPileIndex];
            List<int> unmatchedIndexes = FindUnmatchedCardIndexes(anchorPileState.cards, templatePileState.cards);
            foreach (int unmatchedIndex in unmatchedIndexes)
            {
                if (!PacketDataEquals(templatePileState.cards[unmatchedIndex], templateSelectedCardState))
                    continue;

                if (foundDestination)
                    return false;

                foundDestination = true;
                destinationPileIndex = i;
                destinationCardIndex = unmatchedIndex;
            }
        }

        return true;
    }

    private static List<int> FindUnmatchedCardIndexes(
        IReadOnlyList<NetFullCombatState.CardState> anchorCards,
        IReadOnlyList<NetFullCombatState.CardState> templateCards)
    {
        bool[] used = new bool[templateCards.Count];
        foreach (NetFullCombatState.CardState anchorCard in anchorCards)
        {
            int matchIndex = FindMatchingCardStateIndex(templateCards, anchorCard, used);
            if (matchIndex >= 0)
                used[matchIndex] = true;
        }

        List<int> unmatchedIndexes = [];
        for (int i = 0; i < used.Length; i++)
        {
            if (!used[i])
                unmatchedIndexes.Add(i);
        }

        return unmatchedIndexes;
    }
}
