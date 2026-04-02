// 文件说明：承载 synthetic choice 的 UI 重开与特效表现逻辑。
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
    private SyntheticChoiceVfxRequest? CaptureSyntheticChoiceVfxRequest(
        UndoSyntheticChoiceSession session,
        UndoSnapshot synthesizedBranch,
        UndoChoiceResultKey selectedKey)
    {
        SyntheticChoiceVfxRequest request = new();
        TryCaptureDiscardChoiceVfx(session, synthesizedBranch, selectedKey, request);
        TryCaptureExhaustChoiceVfx(session, synthesizedBranch, selectedKey, request);
        TryCaptureTransformChoiceVfx(session, synthesizedBranch, selectedKey, request);
        return request.HasEffects ? request : null;
    }

    private bool TryCaptureDiscardChoiceVfx(
        UndoSyntheticChoiceSession session,
        UndoSnapshot synthesizedBranch,
        UndoChoiceResultKey selectedKey,
        SyntheticChoiceVfxRequest request)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
        NPlayerHand? hand = NCombatRoom.Instance?.Ui?.Hand;
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        if (me == null
            || hand == null
            || choiceSpec.Kind != UndoChoiceKind.HandSelection
            || choiceSpec.SourcePileType != PileType.Hand
            || selectedKey.OptionIndexes.Count == 0)
        {
            return false;
        }

        if (!TryGetComparablePlayerStates(
                session.AnchorSnapshot.CombatState.FullState,
                synthesizedBranch.CombatState.FullState,
                out _,
                out NetFullCombatState.PlayerState anchorPlayerState,
                out NetFullCombatState.PlayerState branchPlayerState))
        {
            return false;
        }

        int anchorHandPileIndex = FindPileIndex(anchorPlayerState.piles, PileType.Hand);
        int branchHandPileIndex = FindPileIndex(branchPlayerState.piles, PileType.Hand);
        int anchorDiscardPileIndex = FindPileIndex(anchorPlayerState.piles, PileType.Discard);
        int branchDiscardPileIndex = FindPileIndex(branchPlayerState.piles, PileType.Discard);
        if (anchorHandPileIndex < 0
            || branchHandPileIndex < 0
            || anchorDiscardPileIndex < 0
            || branchDiscardPileIndex < 0)
        {
            return false;
        }

        int selectedCount = selectedKey.OptionIndexes.Count;
        NetFullCombatState.CombatPileState anchorHandPileState = anchorPlayerState.piles[anchorHandPileIndex];
        NetFullCombatState.CombatPileState branchHandPileState = branchPlayerState.piles[branchHandPileIndex];
        NetFullCombatState.CombatPileState anchorDiscardPileState = anchorPlayerState.piles[anchorDiscardPileIndex];
        NetFullCombatState.CombatPileState branchDiscardPileState = branchPlayerState.piles[branchDiscardPileIndex];
        if (anchorHandPileState.cards.Count - branchHandPileState.cards.Count != selectedCount
            || branchDiscardPileState.cards.Count - anchorDiscardPileState.cards.Count < selectedCount)
        {
            return false;
        }

        IReadOnlyList<CardModel> liveHandCards = PileType.Hand.GetPile(me).Cards;
        foreach (int optionIndex in selectedKey.OptionIndexes)
        {
            if (optionIndex < 0 || optionIndex >= choiceSpec.SourcePileOptionIndexes.Count)
                return false;

            int handIndex = choiceSpec.SourcePileOptionIndexes[optionIndex];
            if (handIndex < 0 || handIndex >= liveHandCards.Count)
                return false;

            CardModel liveCard = liveHandCards[handIndex];
            NCardHolder? holder = hand.GetCardHolder(liveCard);
            Vector2 globalPosition = holder?.CardNode?.GlobalPosition ?? holder?.GlobalPosition ?? hand.GlobalPosition;
            request.DiscardCards.Add(new SyntheticDiscardVfxCard(liveCard.ToSerializable(), globalPosition));
        }

        return request.DiscardCards.Count > 0;
    }

    private bool TryCaptureExhaustChoiceVfx(
        UndoSyntheticChoiceSession session,
        UndoSnapshot synthesizedBranch,
        UndoChoiceResultKey selectedKey,
        SyntheticChoiceVfxRequest request)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
        NPlayerHand? hand = NCombatRoom.Instance?.Ui?.Hand;
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        if (me == null
            || hand == null
            || choiceSpec.Kind != UndoChoiceKind.HandSelection
            || choiceSpec.SourcePileType != PileType.Hand
            || selectedKey.OptionIndexes.Count == 0)
        {
            return false;
        }

        if (!TryGetComparablePlayerStates(
                session.AnchorSnapshot.CombatState.FullState,
                synthesizedBranch.CombatState.FullState,
                out _,
                out NetFullCombatState.PlayerState anchorPlayerState,
                out NetFullCombatState.PlayerState branchPlayerState))
        {
            return false;
        }

        int anchorHandPileIndex = FindPileIndex(anchorPlayerState.piles, PileType.Hand);
        int branchHandPileIndex = FindPileIndex(branchPlayerState.piles, PileType.Hand);
        int anchorExhaustPileIndex = FindPileIndex(anchorPlayerState.piles, PileType.Exhaust);
        int branchExhaustPileIndex = FindPileIndex(branchPlayerState.piles, PileType.Exhaust);
        if (anchorHandPileIndex < 0
            || branchHandPileIndex < 0
            || anchorExhaustPileIndex < 0
            || branchExhaustPileIndex < 0)
        {
            return false;
        }

        int selectedCount = selectedKey.OptionIndexes.Count;
        NetFullCombatState.CombatPileState anchorHandPileState = anchorPlayerState.piles[anchorHandPileIndex];
        NetFullCombatState.CombatPileState branchHandPileState = branchPlayerState.piles[branchHandPileIndex];
        NetFullCombatState.CombatPileState anchorExhaustPileState = anchorPlayerState.piles[anchorExhaustPileIndex];
        NetFullCombatState.CombatPileState branchExhaustPileState = branchPlayerState.piles[branchExhaustPileIndex];
        if (anchorHandPileState.cards.Count - branchHandPileState.cards.Count != selectedCount
            || branchExhaustPileState.cards.Count - anchorExhaustPileState.cards.Count < selectedCount)
        {
            return false;
        }

        IReadOnlyList<CardModel> liveHandCards = PileType.Hand.GetPile(me).Cards;
        foreach (int optionIndex in selectedKey.OptionIndexes)
        {
            if (optionIndex < 0 || optionIndex >= choiceSpec.SourcePileOptionIndexes.Count)
                return false;

            int handIndex = choiceSpec.SourcePileOptionIndexes[optionIndex];
            if (handIndex < 0 || handIndex >= liveHandCards.Count)
                return false;

            CardModel liveCard = liveHandCards[handIndex];
            NCardHolder? holder = hand.GetCardHolder(liveCard);
            Vector2 globalPosition = holder?.CardNode?.GlobalPosition ?? holder?.GlobalPosition ?? hand.GlobalPosition;
            request.ExhaustCards.Add(new SyntheticExhaustVfxCard(liveCard.ToSerializable(), globalPosition));
        }

        return request.ExhaustCards.Count > 0;
    }

    private bool TryCaptureTransformChoiceVfx(
        UndoSyntheticChoiceSession session,
        UndoSnapshot synthesizedBranch,
        UndoChoiceResultKey selectedKey,
        SyntheticChoiceVfxRequest request)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        if (me == null
            || !ShouldCaptureTransformChoiceVfx(choiceSpec)
            || choiceSpec.SourcePileType == null
            || selectedKey.OptionIndexes.Count == 0)
        {
            return false;
        }

        if (!TryGetComparablePlayerStates(
                session.AnchorSnapshot.CombatState.FullState,
                synthesizedBranch.CombatState.FullState,
                out _,
                out NetFullCombatState.PlayerState anchorPlayerState,
                out NetFullCombatState.PlayerState branchPlayerState))
        {
            return false;
        }

        PileType sourcePileType = choiceSpec.SourcePileType.Value;
        int anchorSourcePileIndex = FindPileIndex(anchorPlayerState.piles, sourcePileType);
        int branchSourcePileIndex = FindPileIndex(branchPlayerState.piles, sourcePileType);
        if (anchorSourcePileIndex < 0 || branchSourcePileIndex < 0)
            return false;

        NetFullCombatState.CombatPileState anchorSourcePileState = anchorPlayerState.piles[anchorSourcePileIndex];
        NetFullCombatState.CombatPileState branchSourcePileState = branchPlayerState.piles[branchSourcePileIndex];
        if (anchorSourcePileState.cards.Count != branchSourcePileState.cards.Count)
            return false;

        List<int> selectedSourceIndexes = [];
        foreach (int optionIndex in selectedKey.OptionIndexes)
        {
            if (optionIndex < 0 || optionIndex >= choiceSpec.SourcePileOptionIndexes.Count)
                return false;

            selectedSourceIndexes.Add(choiceSpec.SourcePileOptionIndexes[optionIndex]);
        }

        if (selectedSourceIndexes.Any(index => index < 0 || index >= anchorSourcePileState.cards.Count))
            return false;

        List<int> changedIndexes = FindUnmatchedCardIndexes(anchorSourcePileState.cards, branchSourcePileState.cards);
        changedIndexes.Sort();
        List<int> sortedSelectedIndexes = [.. selectedSourceIndexes.OrderBy(static index => index)];
        if (!changedIndexes.SequenceEqual(sortedSelectedIndexes))
            return false;

        IReadOnlyList<CardModel> liveSourceCards = sourcePileType.GetPile(me).Cards;
        if (liveSourceCards.Count != anchorSourcePileState.cards.Count)
            return false;

        request.TransformPileType = sourcePileType;
        foreach (int sourceIndex in selectedSourceIndexes)
            request.TransformCards.Add(new SyntheticTransformVfxCard(liveSourceCards[sourceIndex].ToSerializable(), sourceIndex));

        return request.TransformCards.Count > 0;
    }

    private static bool ShouldCaptureTransformChoiceVfx(UndoChoiceSpec choiceSpec)
    {
        return IsSourceChoice(choiceSpec, typeof(EntropyPower));
    }

    private async Task PlaySyntheticChoiceVfxAsync(SyntheticChoiceVfxRequest request)
    {
        await WaitOneFrameAsync();
        await RunOnMainThreadAsync<object?>(() =>
        {
            CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
            Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
            NCombatRoom? combatRoom = NCombatRoom.Instance;
            if (me == null || combatRoom == null)
                return null;

            for (int i = 0; i < request.DiscardCards.Count; i++)
            {
                SyntheticDiscardVfxCard discardCard = request.DiscardCards[i];
                CardModel card = CardModel.FromSerializable(ClonePacketSerializable(discardCard.Card));
                if (card.Owner == null)
                    card.Owner = me;

                NCard cardNode = CreateCardNode(card, PileType.Hand);
                combatRoom.Ui.AddChildSafely(cardNode);
                cardNode.GlobalPosition = discardCard.GlobalPosition;
                cardNode.ZIndex = 100;
                cardNode.RotationDegrees = (i % 2 == 0 ? -3f : 3f);

                Node vfxContainer = combatRoom.CombatVfxContainer;
                cardNode.Reparent(vfxContainer, true);
                Vector2 targetPosition = PileType.Discard.GetTargetPosition(cardNode);
                NCardFlyVfx? flyVfx = NCardFlyVfx.Create(cardNode, targetPosition, true, me.Character.TrailPath);
                if (flyVfx != null)
                    vfxContainer.AddChildSafely(flyVfx);
            }

            for (int i = 0; i < request.ExhaustCards.Count; i++)
            {
                SyntheticExhaustVfxCard exhaustCard = request.ExhaustCards[i];
                CardModel card = CardModel.FromSerializable(ClonePacketSerializable(exhaustCard.Card));
                if (card.Owner == null)
                    card.Owner = me;

                NCard cardNode = CreateCardNode(card, PileType.Hand);
                combatRoom.Ui.AddChildSafely(cardNode);
                cardNode.GlobalPosition = exhaustCard.GlobalPosition;
                cardNode.ZIndex = 100;
                cardNode.RotationDegrees = (i % 2 == 0 ? -3f : 3f);

                NExhaustVfx? exhaustVfx = NExhaustVfx.Create(cardNode);
                if (exhaustVfx != null)
                {
                    exhaustVfx.ZIndex = 130;
                    combatRoom.Ui.AddChildSafely(exhaustVfx);
                }

                float duration = SaveManager.Instance.PrefsSave.FastMode == FastModeType.Fast ? 0.2f : 0.3f;
                Tween tween = combatRoom.CreateTween();
                tween.SetParallel(true);
                tween.TweenProperty(cardNode, "modulate", StsColors.exhaustGray, duration);
                tween.Chain().TweenInterval(0.1f);
                tween.Chain().TweenCallback(Callable.From(cardNode.QueueFreeSafely));
            }
            if (request.TransformPileType != null)
            {
                IReadOnlyList<CardModel> liveSourceCards = request.TransformPileType.Value.GetPile(me).Cards;
                Vector2 transformCenter = combatRoom.GetViewportRect().GetCenter();
                float transformSpacing = request.TransformCards.Count <= 1 ? 0f : 260f;
                for (int i = 0; i < request.TransformCards.Count; i++)
                {
                    SyntheticTransformVfxCard transformCard = request.TransformCards[i];
                    if (transformCard.SourcePileIndex < 0 || transformCard.SourcePileIndex >= liveSourceCards.Count)
                        continue;

                    CardModel startCard = CardModel.FromSerializable(ClonePacketSerializable(transformCard.Card));
                    if (startCard.Owner == null)
                        startCard.Owner = me;

                    CardModel endCard = liveSourceCards[transformCard.SourcePileIndex];
                    NCardTransformVfx? transformVfx = NCardTransformVfx.Create(startCard, endCard, Array.Empty<RelicModel>());
                    if (transformVfx != null)
                    {
                        combatRoom.CombatVfxContainer.AddChildSafely(transformVfx);
                        float offsetX = (i - (request.TransformCards.Count - 1) * 0.5f) * transformSpacing;
                        float offsetY = 0f;
                        transformVfx.GlobalPosition = transformCenter + new Vector2(offsetX, offsetY);
                    }
                }
            }

            return null;
        });
    }

    private async Task<UndoChoiceResultKey?> ShowSyntheticChoiceSelectionAsync(UndoSyntheticChoiceSession session)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
        if (combatState == null || me == null)
            return null;

        if (await TryCompleteSyntheticRetainSelectionAsync(session, new UndoChoiceResultKey([])))
            return null;

        return session.ChoiceSpec.Kind switch
        {
            UndoChoiceKind.ChooseACard => await ShowSyntheticChooseACardAsync(session.ChoiceSpec, me),
            UndoChoiceKind.HandSelection => await ShowSyntheticHandSelectionAsync(session, me),
            UndoChoiceKind.SimpleGridSelection => await ShowSyntheticSimpleGridAsync(session.ChoiceSpec, me),
            _ => null
        };
    }

    private static async Task<UndoChoiceResultKey?> ShowSyntheticChooseACardAsync(UndoChoiceSpec choiceSpec, Player me)
    {
        IReadOnlyList<CardModel> options = choiceSpec.BuildOptionCards(me);
        NChooseACardSelectionScreen screen = NChooseACardSelectionScreen.ShowScreen(options, choiceSpec.CanSkip);
        CardModel? selected = (await screen.CardsSelected()).FirstOrDefault();
        return choiceSpec.TryMapDisplayedOptionSelection(options, selected == null ? [] : [selected]);
    }

    private async Task<UndoChoiceResultKey?> ShowSyntheticHandSelectionAsync(UndoSyntheticChoiceSession session, Player me)
    {
        NPlayerHand? hand = NCombatRoom.Instance?.Ui?.Hand;
        if (hand == null)
            return null;

        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        AbstractModel? source = null;
        if (session.RequiresAuthoritativeBranchExecution && IsOfficialFromHandDiscardChoice(choiceSpec))
        {
            source = ResolveSyntheticHandChoiceSourceModel(me, choiceSpec);
            PrimePendingHandChoiceUiTracking(me, source);
        }

        Task<IEnumerable<CardModel>>? selectionTask = await RunOnMainThreadAsync(() =>
        {
            hand.CancelAllCardPlay();
            return hand.SelectCards(choiceSpec.SelectionPrefs, choiceSpec.BuildHandFilter(me), source);
        });
        if (selectionTask == null)
            return null;

        IEnumerable<CardModel> selectedCards = await selectionTask;
        return choiceSpec.TryMapSyntheticSelection(me, selectedCards);
    }

    private static AbstractModel? ResolveSyntheticHandChoiceSourceModel(Player player, UndoChoiceSpec choiceSpec)
    {
        CardModel? sourceCard = PileType.Play.GetPile(player).Cards.LastOrDefault(card =>
            choiceSpec.SourceCombatCard == null || Equals(NetCombatCard.FromModel(card), choiceSpec.SourceCombatCard));
        if (sourceCard != null)
            return sourceCard;

        if (string.IsNullOrWhiteSpace(choiceSpec.SourceModelTypeName))
            return null;

        if (player.PlayerCombatState != null)
        {
            foreach (AbstractModel model in player.PlayerCombatState.AllCards.OfType<AbstractModel>())
            {
                if (MatchesSyntheticHandChoiceSourceModel(model, choiceSpec))
                    return model;
            }
        }

        foreach (AbstractModel model in player.Relics.OfType<AbstractModel>())
        {
            if (MatchesSyntheticHandChoiceSourceModel(model, choiceSpec))
                return model;
        }

        foreach (AbstractModel model in player.Creature.Powers.OfType<AbstractModel>())
        {
            if (MatchesSyntheticHandChoiceSourceModel(model, choiceSpec))
                return model;
        }

        for (int slotIndex = 0; slotIndex < player.MaxPotionCount; slotIndex++)
        {
            if (player.GetPotionAtSlotIndex(slotIndex) is AbstractModel potion
                && MatchesSyntheticHandChoiceSourceModel(potion, choiceSpec))
            {
                return potion;
            }
        }

        return null;
    }

    private static bool MatchesSyntheticHandChoiceSourceModel(AbstractModel model, UndoChoiceSpec choiceSpec)
    {
        if (!string.Equals(model.GetType().FullName, choiceSpec.SourceModelTypeName, StringComparison.Ordinal))
            return false;

        return string.IsNullOrWhiteSpace(choiceSpec.SourceModelId)
            || string.Equals(model.Id.Entry, choiceSpec.SourceModelId, StringComparison.Ordinal);
    }

    private static async Task<UndoChoiceResultKey?> ShowSyntheticSimpleGridAsync(UndoChoiceSpec choiceSpec, Player me)
    {
        IReadOnlyList<CardModel> options = choiceSpec.BuildDisplayedOptionCards(me);
        NSimpleCardSelectScreen screen = NSimpleCardSelectScreen.Create(options, choiceSpec.SelectionPrefs);
        NOverlayStack.Instance.Push(screen);
        IEnumerable<CardModel> selected = await screen.CardsSelected();
        return choiceSpec.TryMapDisplayedSimpleGridSelection(options, selected);
    }
}
