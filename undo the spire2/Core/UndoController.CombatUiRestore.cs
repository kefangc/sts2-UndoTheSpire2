// 文件说明：恢复战斗 UI、手牌和视觉层状态。
// Coordinates undo/redo history and restore transactions.
// Capture/restore details should live in dedicated services; this type is the orchestrator.
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
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
using MegaCrit.Sts2.Core.Models.Cards;
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

// Combat UI rebuild and presentation normalization after restore.
public sealed partial class UndoController
{
    private static async Task RefreshCombatUiAsync(CombatState combatState, UndoCombatFullState? snapshotState = null)
    {
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        foreach (Player player in combatState.Players)
            player.PlayerCombatState?.RecalculateCardValues();
        if (NCombatRoom.Instance != null)
            UndoSpecialCreatureVisualNormalizer.DetachStateDisplayTracking(NCombatRoom.Instance);
        ClearTransientCardVisuals();
        NormalizeCombatInteractionState(combatState);
        RebuildCombatCreatureNodesIfNeeded(combatState);
        RefreshCreaturePowerDisplays(combatState);
        ApplySnapshotCreatureNodeVisuals(combatState, snapshotState);
        RefreshCreatureStateDisplays(combatState, snapshotState);
        RefreshPlayerOrbManagers(combatState);
        RestoreThievingHopperDisplayCards(combatState);
        RebuildCombatUiCards(combatState);
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom != null)
        {
            ReconcileSovereignBladeVfx(combatState, combatRoom);
            Player? me = LocalContext.GetMe(combatState);
            ForceCombatUiInteractiveState(combatRoom.Ui, combatState, me);
            if (me != null)
                RefreshCombatPileCounts(combatRoom.Ui, me);
            foreach (Creature creature in combatState.Enemies)
            {
                NCreature? creatureNode = combatRoom.GetCreatureNode(creature);
                if (creatureNode == null)
                    continue;

                if (UndoMonsterMoveStateUtil.HasVisibleNextIntent(creature))
                    await creatureNode.RefreshIntents();
                else
                    ClearCreatureIntentUi(creatureNode);
            }

            SnapEnemyCreatureNodesToSlots(combatState);
            UndoSpecialCreatureVisualNormalizer.Refresh(combatState, combatRoom);
            ApplySnapshotPresentationState(combatState, snapshotState);
            RefreshCreatureStateDisplays(combatState, snapshotState);
        }
        await WaitOneFrameAsync();
        if (NCombatRoom.Instance != null)
        {
            ApplySnapshotCreatureNodeVisuals(combatState, snapshotState);
            ReconcileSovereignBladeVfx(combatState, NCombatRoom.Instance);
            ForceCombatUiInteractiveState(NCombatRoom.Instance.Ui, combatState, LocalContext.GetMe(combatState));
            SnapEnemyCreatureNodesToSlots(combatState);
            UndoSpecialCreatureVisualNormalizer.Refresh(combatState, NCombatRoom.Instance);
            ApplySnapshotPresentationState(combatState, snapshotState);
            RefreshCreatureStateDisplays(combatState, snapshotState);
        }

        if (runState != null)
            RebuildPotionContainer(runState);

        NotifyCombatStateChangedMethod?.Invoke(CombatManager.Instance.StateTracker, ["UndoRefreshCombatUiAsync"]);
    }

    private static async Task RefreshCombatUiAfterHandDiscardChoiceAsync(CombatState combatState, bool officialHandChoiceUiSettled)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return;

        RefreshCreaturePowerDisplays(combatState);
        Player? me = LocalContext.GetMe(combatState);
        if (me != null)
        {
            RefreshCombatPileCounts(combatRoom.Ui, me);
            if (!HasActiveHandInteraction(combatRoom.Ui.Hand))
                SyncPlayContainerCards(combatRoom.Ui, me);
        }

        ForceCombatUiInteractiveState(combatRoom.Ui, combatState, me);
        await WaitOneFrameAsync();
        if (!officialHandChoiceUiSettled)
            _ = ReconcileHandDiscardChoiceUiAfterSettleAsync(combatState);
    }

    private static async Task ReconcileHandDiscardChoiceUiAfterSettleAsync(CombatState combatState)
    {
        try
        {
            Player? me = LocalContext.GetMe(combatState);
            for (int frame = 0; frame < 120; frame++)
            {
                NCombatRoom? combatRoom = NCombatRoom.Instance;
                if (combatRoom == null || !CombatManager.Instance.IsInProgress)
                    return;

                NPlayerHand hand = combatRoom.Ui.Hand;
                bool hasValidCurrentCardPlay = HasValidCurrentCardPlay(hand);
                bool hasTransientCardFlyVfx = HasTransientCardFlyVfx(combatRoom.CombatVfxContainer)
                    || HasTransientCardFlyVfx(NRun.Instance?.GlobalUi?.TopBar?.TrailContainer);
                bool waitingForOfficialSelectedHolderReturn = MainFile.Controller.IsAwaitingOfficialHandChoiceSourceFinish(hand, me);
                if (!hasValidCurrentCardPlay && !hasTransientCardFlyVfx && !waitingForOfficialSelectedHolderReturn)
                    break;

                await WaitOneFrameAsync();
            }

            if (NCombatRoom.Instance?.Ui?.Hand is { } handAfterWait
                && MainFile.Controller.IsAwaitingOfficialHandChoiceSourceFinish(handAfterWait, me)
                && TryCompletePendingHandDiscardChoiceUiViaOfficialPath(handAfterWait))
            {
                await WaitOneFrameAsync();
                await MainFile.Controller.WaitForOfficialHandChoiceUiSettleAsync(handAfterWait, me, maxFrames: 60);
            }

            RecoverHandDiscardChoiceUiIfNeeded(combatState);
        }
        catch (Exception ex)
        {
            UndoDebugLog.Write($"deferred_hand_discard_ui_reconcile_failed:{ex}");
        }
    }

    private static void RecoverHandDiscardChoiceUiIfNeeded(CombatState combatState)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return;

        Player? me = LocalContext.GetMe(combatState);
        if (me == null)
            return;

        NCombatUi ui = combatRoom.Ui;
        NPlayerHand hand = ui.Hand;
        if (!NeedsHandDiscardChoiceUiRecovery(hand, me))
        {
            SyncPlayContainerCards(ui, me);
            ForceCombatUiInteractiveState(ui, combatState, me);
            return;
        }

        WriteInteractionLog("hand_discard_ui_recovered", $"expectedHand={PileType.Hand.GetPile(me).Cards.Count} holders={hand.CardHolderContainer.GetChildCount()}");
        TrySyncExistingHandUi(hand, me, normalizeLayout: true);
        SyncPlayContainerCards(ui, me);
        ForceCombatUiInteractiveState(ui, combatState, me, normalizeHandLayout: true);
        NotifyCombatStateChangedMethod?.Invoke(CombatManager.Instance.StateTracker, ["UndoHandDiscardChoiceUiRecovery"]);
    }

    private static bool TryCompletePendingHandDiscardChoiceUiViaOfficialPath(NPlayerHand hand)
    {
        AbstractModel? pendingSource = MainFile.Controller._pendingHandChoiceSource;
        if (!GodotObject.IsInstanceValid(hand)
            || pendingSource == null
            || MainFile.Controller._pendingHandChoiceUiSettle?.CallbackObserved == true
            || GetSelectedHandHolderCount(hand) == 0)
        {
            return false;
        }

        MethodInfo? handlerMethod = FindMethod(hand.GetType(), "OnSelectModeSourceFinished");
        if (handlerMethod == null)
            return false;

        try
        {
            handlerMethod.Invoke(hand, [pendingSource]);
            UndoDebugLog.Write(
                $"hand_discard_ui_recovered_via_official source={pendingSource.GetType().Name}"
                + $" holders={hand.CardHolderContainer.GetChildCount()} selected={GetSelectedHandHolderCount(hand)}");
            return true;
        }
        catch (Exception ex)
        {
            UndoDebugLog.Write($"hand_discard_ui_official_recover_failed:{ex}");
            return false;
        }
    }

    private static bool NeedsHandDiscardChoiceUiRecovery(NPlayerHand hand, Player player)
    {
        if (!GodotObject.IsInstanceValid(hand))
            return false;

        if (HasValidCurrentCardPlay(hand))
            return false;

        if (MainFile.Controller.IsAwaitingOfficialHandChoiceSourceFinish(hand, player))
            return false;

        if (GetSelectedHandHolderCount(hand) > 0)
            return true;

        if (GetAwaitingHandHolderCount(hand) > 0)
            return true;

        int expectedHandCount = PileType.Hand.GetPile(player).Cards.Count;
        if (hand.CardHolderContainer.GetChildCount() != expectedHandCount)
            return true;

        return HasDetachedHandHolders(hand) || !TryGetReusableHandHolders(hand, player, out _);
    }

    private static bool HasValidCurrentCardPlay(NPlayerHand hand)
    {
        return GetPrivateFieldValue<Node>(hand, "_currentCardPlay") is Node currentCardPlay
            && GodotObject.IsInstanceValid(currentCardPlay);
    }

    private static void RefreshPlayerOrbManagers(CombatState combatState)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return;

        foreach (Player player in combatState.Players)
        {
            if (player.PlayerCombatState == null)
                continue;

            NOrbManager? orbManager = combatRoom.GetCreatureNode(player.Creature)?.OrbManager;
            if (orbManager == null)
                continue;

            RebuildOrbManagerNodes(orbManager, player.PlayerCombatState.OrbQueue);
        }
    }

    private static void RefreshCreaturePowerDisplays(CombatState combatState)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return;

        foreach (Creature creature in combatState.Creatures)
        {
            NCreature? creatureNode = combatRoom.GetCreatureNode(creature);
            NCreatureStateDisplay? stateDisplay = GetPrivateFieldValue<NCreatureStateDisplay>(creatureNode, "_stateDisplay");
            NPowerContainer? powerContainer = stateDisplay == null
                ? null
                : GetPrivateFieldValue<NPowerContainer>(stateDisplay, "_powerContainer");
            System.Collections.IList? powerNodes = powerContainer == null
                ? null
                : GetPrivateFieldValue<System.Collections.IList>(powerContainer, "_powerNodes");
            if (powerContainer == null || powerNodes == null)
                continue;

            ClearNodeChildren(powerContainer);
            powerNodes.Clear();

            foreach (PowerModel power in creature.Powers)
            {
                if (!power.IsVisible)
                    continue;

                NPower powerNode = NPower.Create(power);
                powerNode.Container = powerContainer;
                powerNodes.Add(powerNode);
                powerContainer.AddChildSafely(powerNode);
            }

            InvokePrivateMethod(powerContainer, "UpdatePositions");
        }
    }

    private static void RebuildOrbManagerNodes(NOrbManager orbManager, OrbQueue orbQueue)
    {
        Control? orbContainer = GetPrivateFieldValue<Control>(orbManager, "_orbContainer");
        System.Collections.IList? orbNodes = GetPrivateFieldValue<System.Collections.IList>(orbManager, "_orbs");
        if (orbContainer == null || orbNodes == null)
            return;

        if (OrbManagerMatchesState(orbNodes, orbQueue))
        {
            orbManager.UpdateVisuals(OrbEvokeType.None);
            return;
        }

        GetPrivateFieldValue<Tween>(orbManager, "_curTween")?.Kill();
        SetPrivateFieldValue(orbManager, "_curTween", null);
        ClearNodeChildren(orbContainer);
        orbNodes.Clear();

        for (int i = 0; i < orbQueue.Capacity; i++)
        {
            NOrb orbNode = NOrb.Create(orbManager.IsLocal);
            orbContainer.AddChildSafely(orbNode);
            orbNodes.Add(orbNode);
            orbNode.Position = Vector2.Zero;
        }

        for (int i = 0; i < orbQueue.Orbs.Count; i++)
        {
            if (orbNodes[i] is not NOrb orbNode)
                continue;

            orbNode.ReplaceOrb(orbQueue.Orbs[i]);
            FinalizeOrbNodeVisuals(orbNode);
        }

        LayoutOrbManagerNodesInstantly(orbManager, orbNodes, orbQueue.Capacity);
        InvokePrivateMethod(orbManager, "UpdateControllerNavigation");
        orbManager.UpdateVisuals(OrbEvokeType.None);
    }

    private static void LayoutOrbManagerNodesInstantly(NOrbManager orbManager, System.Collections.IList orbNodes, int capacity)
    {
        if (capacity <= 0)
            return;

        float spread = 125f;
        float step = capacity > 1 ? spread / (capacity - 1) : 0f;
        float radius = Mathf.Lerp(225f, 300f, (capacity - 3f) / 7f);
        if (!orbManager.IsLocal)
            radius *= 0.75f;

        for (int i = 0; i < capacity; i++)
        {
            if (orbNodes[i] is not NOrb orbNode)
                continue;

            float angle = float.DegreesToRadians(-25f - spread);
            orbNode.Position = new Vector2(-Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            spread -= step;
        }
    }

    private static void FinalizeOrbNodeVisuals(NOrb orbNode)
    {
        GetPrivateFieldValue<Tween>(orbNode, "_curTween")?.Kill();
        SetPrivateFieldValue(orbNode, "_curTween", null);
        if (GetPrivateFieldValue<Node2D>(orbNode, "_sprite") is Node2D sprite)
            sprite.Scale = Vector2.One;
    }

    private static bool OrbManagerMatchesState(System.Collections.IList orbNodes, OrbQueue orbQueue)
    {
        if (orbNodes.Count != orbQueue.Capacity)
            return false;

        for (int i = 0; i < orbQueue.Orbs.Count; i++)
        {
            if (orbNodes[i] is not NOrb orbNode || !ReferenceEquals(orbNode.Model, orbQueue.Orbs[i]))
                return false;
        }

        for (int i = orbQueue.Orbs.Count; i < orbNodes.Count; i++)
        {
            if (orbNodes[i] is not NOrb orbNode || orbNode.Model != null)
                return false;
        }

        return true;
    }

    private static void RestoreThievingHopperDisplayCards(CombatState combatState)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return;

        foreach (Creature creature in combatState.Enemies)
        {
            NCreature? creatureNode = combatRoom.GetCreatureNode(creature);
            if (creatureNode == null)
                continue;

            Marker2D? stolenCardPos = creatureNode.Visuals?.GetNodeOrNull<Marker2D>("%StolenCardPos");
            if (stolenCardPos == null)
                continue;

            ClearNodeChildren(stolenCardPos);
            SwipePower? swipePower = creature.Powers.OfType<SwipePower>().FirstOrDefault(static power => power.StolenCard != null);
            if (swipePower?.StolenCard == null)
                continue;

            if (swipePower.StolenCard.Owner == null && swipePower.Target?.Player != null)
                swipePower.StolenCard.Owner = swipePower.Target.Player;

            NCard? cardNode = NCard.Create(swipePower.StolenCard, ModelVisibility.Visible);
            if (cardNode == null)
                continue;

            stolenCardPos.AddChild(cardNode);
            cardNode.Position += cardNode.Size * 0.5f;
            cardNode.UpdateVisuals(PileType.Deck, CardPreviewMode.Normal);
        }
    }
    private static void NormalizeCombatInteractionState(CombatState combatState)
    {
        NTargetManager.Instance?.CancelTargeting();
        ResetLocalHoveredModelState();
        ClearCombatManagerCollection("_playersReadyToEndTurn");
        ClearCombatManagerCollection("_playersReadyToBeginEnemyTurn");
        ClearCombatManagerCollection("_playersTakingExtraTurn");
        bool isPlayerTurn = combatState.CurrentSide == CombatSide.Player;
        SetCombatManagerProperty("IsPlayPhase", isPlayerTurn);
        SetCombatManagerProperty("IsEnemyTurnStarted", !isPlayerTurn);
        SetCombatManagerProperty("EndingPlayerTurnPhaseOne", false);
        SetCombatManagerProperty("EndingPlayerTurnPhaseTwo", false);
        SetCombatManagerProperty("PlayerActionsDisabled", !isPlayerTurn);
        Player? me = LocalContext.GetMe(combatState);
        if (me != null && isPlayerTurn)
            CombatManager.Instance.UndoReadyToEndTurn(me);
    }
    private static void RebuildCombatUiCards(CombatState combatState)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return;
        NCombatUi ui = combatRoom.Ui;
        ClearPlayQueueUi(ui.PlayQueue);
        Player? me = LocalContext.GetMe(combatState);
        if (me == null)
            return;

        TrySyncExistingHandUi(ui.Hand, me);

        SyncPlayContainerCards(ui, me);
    }

    // choice undo 时，屏幕中央“本次打出的牌”和左侧眼睛预览都依赖 PlayContainer。
    // 如果每次 restore 都直接清掉再用普通 NCard 重建，很容易丢掉官方的显示状态，
    // 并把卡留在左上角默认位置。这里优先复用已在场上的节点，只有不匹配时才重建。
    private static bool TrySyncExistingHandUi(NPlayerHand hand, Player player, bool normalizeLayout = false)
    {
        if (!TryGetReusableHandHolders(hand, player, out List<NHandCardHolder> holders))
            return TryReconcileHandUiInPlace(hand, player, normalizeLayout);

        foreach (NHandCardHolder holder in holders)
            NormalizeHandHolderCard(holder);

        if (normalizeLayout)
            SnapHandHolders(hand, preserveHoveredHolders: true);
        else
            RefreshHandHolderInteractionState(hand);
        return true;
    }

    private static bool TryReconcileHandUiInPlace(NPlayerHand hand, Player player, bool normalizeLayout = false)
    {
        if (!GodotObject.IsInstanceValid(hand))
            return false;

        ClearDetachedHandHolderNodes(hand);
        ClearTransientHandUiStateForRestore(hand);

        List<Node> rawChildren = hand.CardHolderContainer.GetChildren().Cast<Node>().ToList();
        List<NHandCardHolder> existingHolders = [];
        foreach (Node child in rawChildren)
        {
            if (child is not NHandCardHolder holder)
            {
                child.GetParent()?.RemoveChildSafely(child);
                QueueFreeNodeSafelyOnce(child);
                continue;
            }

            NCard? cardNode = holder.CardNode;
            if (!GodotObject.IsInstanceValid(holder)
                || cardNode == null
                || !GodotObject.IsInstanceValid(cardNode)
                || cardNode.Model == null)
            {
                RemoveHandHolderWithoutRefresh(hand, holder);
                continue;
            }

            existingHolders.Add(holder);
        }

        Dictionary<CardModel, Queue<NHandCardHolder>> holdersByCard = new(ReferenceEqualityComparer.Instance);
        foreach (NHandCardHolder holder in existingHolders)
        {
            NCard? cardNode = holder.CardNode;
            if (cardNode?.Model == null)
                continue;

            if (!holdersByCard.TryGetValue(cardNode.Model, out Queue<NHandCardHolder>? queuedHolders))
            {
                queuedHolders = new Queue<NHandCardHolder>();
                holdersByCard[cardNode.Model] = queuedHolders;
            }

            queuedHolders.Enqueue(holder);
        }

        List<CardModel> handCards = PileType.Hand.GetPile(player).Cards.ToList();
        Dictionary<CardModel, NHandCardHolder> matchedHoldersByCard = new(ReferenceEqualityComparer.Instance);
        HashSet<NHandCardHolder> retainedHolders = [];
        List<NHandCardHolder> returnedHolders = [];
        foreach (CardModel card in handCards)
        {
            if (holdersByCard.TryGetValue(card, out Queue<NHandCardHolder>? queuedHolders)
                && queuedHolders.Count > 0)
            {
                NHandCardHolder holder = queuedHolders.Dequeue();
                retainedHolders.Add(holder);
                matchedHoldersByCard[card] = holder;
            }
        }

        foreach (NHandCardHolder holder in existingHolders)
        {
            if (!retainedHolders.Contains(holder))
                RemoveHandHolderWithoutRefresh(hand, holder);
        }

        for (int index = 0; index < handCards.Count; index++)
        {
            CardModel card = handCards[index];
            if (matchedHoldersByCard.TryGetValue(card, out NHandCardHolder? holder))
            {
                if (hand.CardHolderContainer.GetChild(index) != holder)
                    hand.CardHolderContainer.MoveChild(holder, index);
                NormalizeHandHolderCard(holder);
                continue;
            }

            NHandCardHolder newHolder = hand.Add(CreateCardNode(card, PileType.Hand), index);
            NormalizeHandHolderCard(newHolder);
            returnedHolders.Add(newHolder);
        }

        InvokePrivateMethod(hand, "RefreshLayout");
        FinalizeReturnedHandHoldersInstantly(returnedHolders);
        hand.ForceRefreshCardIndices();
        if (normalizeLayout)
            SnapHandHolders(hand, preserveHoveredHolders: true);
        else
            RefreshHandHolderInteractionState(hand);
        return TryGetReusableHandHolders(hand, player, out _);
    }

    private static void ClearTransientHandUiStateForRestore(NPlayerHand hand)
    {
        if (!GodotObject.IsInstanceValid(hand))
            return;

        if (GetPrivateFieldValue<Node>(hand, "_currentCardPlay") is Node currentCardPlay
            && GodotObject.IsInstanceValid(currentCardPlay))
        {
            currentCardPlay.GetParent()?.RemoveChildSafely(currentCardPlay);
            QueueFreeNodeSafelyOnce(currentCardPlay);
        }

        GetPrivateFieldValue<System.Collections.IDictionary>(hand, "_holdersAwaitingQueue")?.Clear();
        GetPrivateFieldValue<System.Collections.IList>(hand, "_selectedCards")?.Clear();
        SetPrivateFieldValue(hand, "_currentCardPlay", null);
        SetPrivateFieldValue(hand, "_draggedHolderIndex", -1);
        SetPrivateFieldValue(hand, "_lastFocusedHolderIdx", -1);
        SetPrivatePropertyValue(hand, "FocusedHolder", null);
    }

    private static void RemoveHandHolderWithoutRefresh(NPlayerHand hand, NHandCardHolder holder)
    {
        if (!GodotObject.IsInstanceValid(holder))
            return;

        GetPrivateFieldValue<System.Collections.IDictionary>(hand, "_holdersAwaitingQueue")?.Remove(holder);
        if (ReferenceEquals(GetPrivatePropertyValue<NHandCardHolder>(hand, "FocusedHolder"), holder))
            SetPrivatePropertyValue(hand, "FocusedHolder", null);

        holder.Clear();
        holder.GetParent()?.RemoveChildSafely(holder);
        holder.QueueFreeSafely();
    }

    private static bool TryGetReusableHandHolders(NPlayerHand hand, Player player, out List<NHandCardHolder> holders)
    {
        holders = [];
        if (!GodotObject.IsInstanceValid(hand))
            return false;

        if (HasDetachedHandHolders(hand)
            || HasValidCurrentCardPlay(hand)
            || GetSelectedHandHolderCount(hand) > 0
            || GetAwaitingHandHolderCount(hand) > 0)
            return false;

        List<CardModel> handCards = PileType.Hand.GetPile(player).Cards.ToList();
        holders = hand.CardHolderContainer.GetChildren().OfType<NHandCardHolder>().ToList();
        if (holders.Count != handCards.Count)
            return false;

        for (int i = 0; i < handCards.Count; i++)
        {
            NHandCardHolder holder = holders[i];
            if (!GodotObject.IsInstanceValid(holder))
                return false;

            NCard? cardNode = holder.CardNode;
            if (cardNode == null || !GodotObject.IsInstanceValid(cardNode))
                return false;

            if (!ReferenceEquals(cardNode.Model, handCards[i]))
                return false;
        }

        return true;
    }

    private static void NormalizeHandHolderCard(NHandCardHolder holder)
    {
        ClearObjectTweenFields(holder);
        NCard? cardNode = holder.CardNode;
        if (cardNode != null && GodotObject.IsInstanceValid(cardNode))
        {
            ClearObjectTweenFields(cardNode);
            InvokePrivateMethod(cardNode, "Reload");
            cardNode.Position = Vector2.Zero;
            cardNode.Visible = true;
            cardNode.Modulate = Colors.White;
        }

        holder.UpdateCard();
        holder.SetClickable(true);
        holder.FocusMode = Control.FocusModeEnum.All;
        holder.Hitbox.SetEnabled(true);
        holder.Hitbox.MouseFilter = Control.MouseFilterEnum.Stop;
    }

    private static void FinalizeReturnedHandHoldersInstantly(IEnumerable<NHandCardHolder> holders)
    {
        foreach (NHandCardHolder holder in holders.Distinct())
        {
            if (!GodotObject.IsInstanceValid(holder))
                continue;

            NormalizeHandHolderCard(holder);
            holder.SetDefaultTargets();
            holder.Position = holder.TargetPosition;
            holder.SetAngleInstantly(holder.TargetAngle);
            object? targetScale = FindField(holder.GetType(), "_targetScale")?.GetValue(holder);
            holder.SetScaleInstantly(targetScale is Vector2 scale ? scale : Vector2.One);
            holder.ZIndex = 0;
        }
    }

    private static void SyncPlayContainerCards(NCombatUi ui, Player player)
    {
        List<CardModel> playCards = PileType.Play.GetPile(player).Cards.ToList();
        List<NCard> existingCards = ui.PlayContainer.GetChildren().OfType<NCard>().ToList();
        bool canReuseExistingNodes = existingCards.Count == playCards.Count;
        if (canReuseExistingNodes)
        {
            for (int i = 0; i < playCards.Count; i++)
            {
                if (!ReferenceEquals(existingCards[i].Model, playCards[i]))
                {
                    canReuseExistingNodes = false;
                    break;
                }
            }
        }

        if (!canReuseExistingNodes)
        {
            ClearNodeChildren(ui.PlayContainer);
            foreach (CardModel card in playCards)
            {
                NCard cardNode = CreateCardNode(card, PileType.Play);
                ui.AddToPlayContainer(cardNode);
                NormalizePlayContainerCard(cardNode);
            }
            return;
        }

        foreach (NCard cardNode in existingCards)
            NormalizePlayContainerCard(cardNode);
    }

    private static void NormalizePlayContainerCard(NCard cardNode)
    {
        InvokePrivateMethod(cardNode, "Reload");
        cardNode.UpdateVisuals(PileType.Play, CardPreviewMode.Normal);
        cardNode.PlayPileTween?.Kill();
        cardNode.PlayPileTween = null;
        cardNode.Position = PileType.Play.GetTargetPosition(cardNode);
        cardNode.Scale = Vector2.One * 0.8f;
        cardNode.Visible = true;
        cardNode.Modulate = Colors.White;
    }

    private static void RebuildCombatCreatureNodesIfNeeded(CombatState combatState)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return;

        List<Creature> creatures = combatState.Creatures.ToList();
        List<NCreature> creatureNodes = combatRoom.CreatureNodes.ToList();
        List<NCreature> removingNodes = combatRoom.RemovingCreatureNodes.ToList();
        Control? encounterSlots = GetPrivateFieldValue<Control>(combatRoom, "<EncounterSlots>k__BackingField")
            ?? GetPrivateFieldValue<Control>(combatRoom, "EncounterSlots");
        bool hasInvalidEnemySlots = combatState.Enemies.Any(creature =>
            !string.IsNullOrWhiteSpace(creature.SlotName)
            && (encounterSlots == null || !encounterSlots.HasNode(creature.SlotName)));
        bool forceRebuild = combatState.Enemies.Any(static creature => creature.HasPower<ReattachPower>());
        bool slotPositionMismatch = false;
        if (!hasInvalidEnemySlots && encounterSlots != null)
        {
            foreach (Creature creature in combatState.Enemies)
            {
                if (string.IsNullOrWhiteSpace(creature.SlotName))
                    continue;

                NCreature? node = combatRoom.GetCreatureNode(creature);
                if (node == null || !encounterSlots.HasNode(creature.SlotName))
                    continue;

                Vector2 expectedPosition = encounterSlots.GetNode<Marker2D>(creature.SlotName).GlobalPosition;
                if (node.GlobalPosition.DistanceTo(expectedPosition) > 1f)
                {
                    slotPositionMismatch = true;
                    break;
                }
            }
        }

        bool topologyMismatch = forceRebuild
            || creatureNodes.Count != creatures.Count
            || removingNodes.Count > 0
            || creatures.Any(creature => combatRoom.GetCreatureNode(creature) == null)
            || slotPositionMismatch
            || hasInvalidEnemySlots;
        if (!topologyMismatch)
            return;

        foreach (NCreature node in creatureNodes.Concat(removingNodes).Distinct())
        {
            node.GetParent()?.RemoveChild(node);
            QueueFreeNodeSafelyOnce(node);
        }

        GetPrivateFieldValue<System.Collections.IList>(combatRoom, "_creatureNodes")?.Clear();
        GetPrivateFieldValue<System.Collections.IList>(combatRoom, "_removingCreatureNodes")?.Clear();

        if (GetPrivateFieldValue<Control>(combatRoom, "_allyContainer") is { } allyContainer)
            ClearNodeChildren(allyContainer);

        if (GetPrivateFieldValue<Control>(combatRoom, "_enemyContainer") is { } enemyContainer)
            ClearNodeChildren(enemyContainer);

        SetPrivatePropertyValue(combatRoom, "EncounterSlots", null);
        InvokePrivateMethod(combatRoom, "CreateAllyNodes");
        if (hasInvalidEnemySlots)
            RebuildEnemyNodesWithFallbackLayout(combatRoom, combatState);
        else
            InvokePrivateMethod(combatRoom, "CreateEnemyNodes");
        InvokePrivateMethod(combatRoom, "AdjustCreatureScaleForAspectRatio");
        InvokePrivateMethod(combatRoom, "UpdateCreatureNavigation");
    }

    private static void ApplySnapshotCreatureNodeVisuals(CombatState combatState, UndoCombatFullState? snapshotState)
    {
        if (snapshotState == null)
            return;

        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return;

        Dictionary<string, UndoCreatureVisualState> creatureVisualStatesByKey = snapshotState.CreatureVisualStates
            .Where(static state => state.VisualDefaultScale.HasValue
                || state.VisualHue.HasValue
                || state.TempScale.HasValue
                || state.TrackStates.Count > 0
                || state.CanvasStates.Count > 0
                || state.ParticleStates.Count > 0
                || state.ShaderParamStates.Count > 0
                || state.StateDisplayState != null)
            .ToDictionary(static state => state.CreatureKey, static state => state);
        if (creatureVisualStatesByKey.Count == 0 && snapshotState.MonsterStates.Count > 0)
        {
            creatureVisualStatesByKey = snapshotState.MonsterStates
                .Where(static state => state.VisualDefaultScale.HasValue || state.VisualHue.HasValue)
                .ToDictionary(
                    static state => state.CreatureKey,
                    static state => new UndoCreatureVisualState
                    {
                        CreatureKey = state.CreatureKey,
                        VisualDefaultScale = state.VisualDefaultScale,
                        VisualHue = state.VisualHue
                    });
        }

        if (creatureVisualStatesByKey.Count == 0)
            return;

        for (int creatureIndex = 0; creatureIndex < combatState.Creatures.Count; creatureIndex++)
        {
            Creature creature = combatState.Creatures[creatureIndex];
            if (!creatureVisualStatesByKey.TryGetValue(BuildCreatureKey(creature, creatureIndex), out UndoCreatureVisualState? creatureVisualState))
                continue;

            NCreature? creatureNode = combatRoom.GetCreatureNode(creature);
            NCreatureVisuals? creatureVisuals = creatureNode?.Visuals;
            if (creatureNode == null || creatureVisuals == null)
                continue;

            float scale = creatureVisualState.VisualDefaultScale ?? creatureVisuals.DefaultScale;
            float hue = creatureVisualState.VisualHue
                ?? (FindField(creatureVisuals.GetType(), "_hue")?.GetValue(creatureVisuals) is float currentHue ? currentHue : 0f);
            RestoreCreatureVisualStateInstantly(creatureNode, scale, hue, creatureVisualState.TempScale);
            RestoreCreatureSceneVisualState(creatureVisuals, creatureVisualState);
            RestoreCreatureAnimatorState(creatureNode, creatureVisualState.AnimatorState);
        }

        RelayoutEnemyCreatureNodes(combatRoom, combatState);
    }

    private static void ApplySnapshotPresentationState(CombatState combatState, UndoCombatFullState? snapshotState)
    {
        if (snapshotState == null)
            return;

        ApplySnapshotCreatureNodeVisuals(combatState, snapshotState);
        UndoAudioLoopTracker.ApplySnapshot(snapshotState.AudioLoopStates);
    }

    private static void RestoreCreatureVisualStateInstantly(NCreature creatureNode, float defaultScale, float hue, float? tempScale)
    {
        GetPrivateFieldValue<Tween>(creatureNode, "_scaleTween")?.Kill();
        SetPrivateFieldValue(creatureNode, "_scaleTween", null);

        creatureNode.SetScaleAndHue(defaultScale, hue);
        if (!tempScale.HasValue)
            return;

        float resolvedTempScale = tempScale.Value;
        SetPrivateFieldValue(creatureNode, "_tempScale", resolvedTempScale);
        creatureNode.Visuals.Scale = Vector2.One * resolvedTempScale * defaultScale;
        InvokePrivateMethod(creatureNode, "SetOrbManagerPosition");
        InvokePrivateMethodExact(creatureNode, "UpdateBounds", [typeof(Node)], creatureNode.Visuals);
    }

    private static void RestoreCreatureSceneVisualState(NCreatureVisuals root, UndoCreatureVisualState state)
    {
        RestoreCreatureCanvasStates(root, state.CanvasStates);
        RestoreCreatureParticleStates(root, state.ParticleStates);
        RestoreCreatureShaderParams(root, state.ShaderParamStates);
        RestoreCreatureTrackStates(root, state.TrackStates);
    }

    private static void RestoreCreatureCanvasStates(Node root, IReadOnlyList<UndoCreatureCanvasState> states)
    {
        foreach (UndoCreatureCanvasState state in states)
        {
            if (ResolveCreatureVisualNode(root, state.RelativePath) is CanvasItem canvasItem)
                canvasItem.Visible = state.Visible;
        }
    }

    private static void RestoreCreatureParticleStates(Node root, IReadOnlyList<UndoCreatureParticleState> states)
    {
        foreach (UndoCreatureParticleState state in states)
        {
            switch (ResolveCreatureVisualNode(root, state.RelativePath))
            {
                case GpuParticles2D gpuParticles:
                    gpuParticles.Emitting = state.Emitting;
                    break;
                case CpuParticles2D cpuParticles:
                    cpuParticles.Emitting = state.Emitting;
                    break;
            }
        }
    }

    private static void RestoreCreatureShaderParams(Node root, IReadOnlyList<UndoCreatureShaderParamState> states)
    {
        foreach (UndoCreatureShaderParamState state in states)
        {
            if (ResolveCreatureShaderMaterial(root, state) is not ShaderMaterial material)
                continue;

            switch (state.ValueKind)
            {
                case UndoCreatureShaderParamValueKind.Bool:
                    material.SetShaderParameter(state.ParamName, state.BoolValue);
                    break;
                case UndoCreatureShaderParamValueKind.Int:
                    material.SetShaderParameter(state.ParamName, state.IntValue);
                    break;
                case UndoCreatureShaderParamValueKind.Float:
                    material.SetShaderParameter(state.ParamName, state.FloatValue);
                    break;
                case UndoCreatureShaderParamValueKind.Vector2:
                    material.SetShaderParameter(state.ParamName, state.Vector2Value);
                    break;
                case UndoCreatureShaderParamValueKind.Color:
                    material.SetShaderParameter(state.ParamName, state.ColorValue);
                    break;
            }
        }
    }

    private static ShaderMaterial? ResolveCreatureShaderMaterial(Node root, UndoCreatureShaderParamState state)
    {
        Node? node = ResolveCreatureVisualNode(root, state.RelativePath);
        if (node == null)
            return null;

        return state.BindingKind switch
        {
            UndoCreatureShaderMaterialBindingKind.CanvasItemMaterial => node is CanvasItem canvasItem ? canvasItem.Material as ShaderMaterial : null,
            UndoCreatureShaderMaterialBindingKind.SpineNormalMaterial => node is Node2D node2D && string.Equals(node2D.GetClass(), "SpineSprite", StringComparison.Ordinal)
                ? new MegaSprite(node2D).GetNormalMaterial() as ShaderMaterial
                : null,
            UndoCreatureShaderMaterialBindingKind.SpineSlotNormalMaterial => string.Equals(node.GetClass(), "SpineSlotNode", StringComparison.Ordinal)
                ? new MegaSlotNode(node).GetNormalMaterial() as ShaderMaterial
                : null,
            _ => null
        };
    }

    private static void RestoreCreatureTrackStates(Node root, IReadOnlyList<UndoCreatureTrackState> states)
    {
        Dictionary<string, List<UndoCreatureTrackState>> statesByPath = states
            .Where(static state => state.TrackIndex >= 0)
            .GroupBy(static state => state.RelativePath, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(track => track.TrackIndex).ToList(), StringComparer.Ordinal);

        foreach (Node node in EnumerateCreatureVisualNodes(root))
        {
            if (node is not Node2D node2D || !string.Equals(node2D.GetClass(), "SpineSprite", StringComparison.Ordinal))
                continue;

            string relativePath = ReferenceEquals(root, node) ? "." : root.GetPathTo(node).ToString();
            MegaAnimationState animationState = new MegaSprite(node2D).GetAnimationState();
            statesByPath.TryGetValue(relativePath, out List<UndoCreatureTrackState>? pathStates);
            HashSet<int> trackIndexes = pathStates == null
                ? []
                : [.. pathStates.Select(static state => state.TrackIndex)];
            for (int trackIndex = 1; trackIndex < 4; trackIndex++)
            {
                if (!trackIndexes.Contains(trackIndex))
                    TryClearCreatureTrack(animationState, trackIndex);
            }

            if (pathStates == null)
                continue;

            foreach (UndoCreatureTrackState trackState in pathStates)
            {
                MegaTrackEntry? trackEntry = animationState.SetAnimation(trackState.AnimationName, trackState.Loop ?? true, trackState.TrackIndex);
                if (trackEntry == null)
                    continue;

                if (trackState.Loop.HasValue)
                    trackEntry.SetLoop(trackState.Loop.Value);
                if (trackState.TrackTime.HasValue)
                    trackEntry.SetTrackTime(trackState.TrackTime.Value);
            }
        }
    }

    private static void RestoreCreatureAnimatorState(NCreature creatureNode, UndoCreatureAnimatorState? state)
    {
        if (state == null
            || GetPrivateFieldValue<CreatureAnimator>(creatureNode, "_spineAnimator") is not CreatureAnimator animator)
        {
            return;
        }

        AnimState? targetState = FindCreatureAnimatorState(animator, state.StateId);
        if (targetState == null)
            return;

        SetPrivateFieldValue(animator, "_currentState", targetState);
        if (state.HasLooped.HasValue)
            UndoReflectionUtil.TrySetFieldValue(targetState, "<HasLooped>k__BackingField", state.HasLooped.Value);

        if (!string.IsNullOrWhiteSpace(targetState.BoundsContainer))
            InvokePrivateMethodExact(creatureNode, "UpdateBounds", [typeof(string)], targetState.BoundsContainer);
    }

    private static AnimState? FindCreatureAnimatorState(CreatureAnimator animator, string stateId)
    {
        Queue<AnimState> pending = new();
        HashSet<AnimState> visited = [];

        if (FindField(animator.GetType(), "_currentState")?.GetValue(animator) is AnimState currentState)
            pending.Enqueue(currentState);
        if (FindField(animator.GetType(), "_anyState")?.GetValue(animator) is AnimState anyState)
            pending.Enqueue(anyState);

        while (pending.Count > 0)
        {
            AnimState state = pending.Dequeue();
            if (!visited.Add(state))
                continue;

            if (string.Equals(state.Id, stateId, StringComparison.Ordinal))
                return state;

            if (state.NextState != null)
                pending.Enqueue(state.NextState);

            foreach (AnimState branchState in EnumerateAnimStateBranches(state))
                pending.Enqueue(branchState);
        }

        return null;
    }

    private static IEnumerable<AnimState> EnumerateAnimStateBranches(AnimState state)
    {
        if (FindField(state.GetType(), "_branchedStates")?.GetValue(state) is not System.Collections.IDictionary branchMap)
            yield break;

        foreach (object? branchList in branchMap.Values)
        {
            if (branchList is not System.Collections.IEnumerable branches)
                continue;

            foreach (object? branch in branches)
            {
                if (branch != null
                    && FindField(branch.GetType(), "state")?.GetValue(branch) is AnimState branchState)
                {
                    yield return branchState;
                }
            }
        }
    }

    private static void TryClearCreatureTrack(MegaAnimationState animationState, int trackIndex)
    {
        try
        {
            animationState.AddEmptyAnimation(trackIndex);
        }
        catch
        {
            try
            {
                if (UndoReflectionUtil.FindProperty(animationState.GetType(), "BoundObject")?.GetValue(animationState) is GodotObject boundObject
                    && boundObject.HasMethod("clear_track"))
                {
                    boundObject.Call("clear_track", trackIndex);
                }
            }
            catch
            {
            }
        }
    }

    private static Node? ResolveCreatureVisualNode(Node root, string relativePath)
    {
        return relativePath == "." ? root : root.GetNodeOrNull(relativePath);
    }

    private static void RefreshCreatureStateDisplays(CombatState combatState, UndoCombatFullState? snapshotState = null)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return;

        Dictionary<string, UndoCreatureStateDisplayState> stateDisplaysByKey = snapshotState?.CreatureVisualStates
            .Where(static state => state.StateDisplayState != null)
            .ToDictionary(static state => state.CreatureKey, static state => state.StateDisplayState!, StringComparer.Ordinal)
            ?? [];

        for (int creatureIndex = 0; creatureIndex < combatState.Creatures.Count; creatureIndex++)
        {
            Creature creature = combatState.Creatures[creatureIndex];
            NCreature? creatureNode = combatRoom.GetCreatureNode(creature);
            if (creatureNode == null)
                continue;

            string creatureKey = BuildCreatureKey(creature, creatureIndex);
            stateDisplaysByKey.TryGetValue(creatureKey, out UndoCreatureStateDisplayState? stateDisplayState);
            NormalizeCreatureStateDisplayLayout(creatureNode, stateDisplayState);
        }
    }

    private static void NormalizeCreatureStateDisplayLayout(NCreature creatureNode, UndoCreatureStateDisplayState? snapshotState)
    {
        NCreatureStateDisplay? stateDisplay = GetPrivateFieldValue<NCreatureStateDisplay>(creatureNode, "_stateDisplay");
        if (stateDisplay == null)
            return;

        GetPrivateFieldValue<Tween>(stateDisplay, "_showHideTween")?.Kill();
        SetPrivateFieldValue(stateDisplay, "_showHideTween", null);

        if (snapshotState?.OriginalPosition is Vector2 snapshotOriginalPosition)
            SetPrivateFieldValue(stateDisplay, "_originalPosition", snapshotOriginalPosition);

        if (snapshotState?.CurrentPosition is Vector2 snapshotCurrentPosition)
            stateDisplay.Position = snapshotCurrentPosition;
        else if (UndoReflectionUtil.FindField(stateDisplay.GetType(), "_originalPosition")?.GetValue(stateDisplay) is Vector2 originalPosition)
            stateDisplay.Position = originalPosition;

        if (snapshotState?.Visible is bool visible)
            stateDisplay.Visible = visible;
        if (snapshotState?.Modulate is Color modulate)
            stateDisplay.Modulate = modulate;

        NHealthBar? healthBar = GetPrivateFieldValue<NHealthBar>(stateDisplay, "_healthBar");
        if (healthBar != null)
            NormalizeHealthBarLayout(healthBar, snapshotState?.HealthBarState);

        InvokePrivateMethodExact(stateDisplay, "SetCreatureBounds", [typeof(Control)], creatureNode.Hitbox);
        InvokePrivateMethod(stateDisplay, "RefreshValues");
    }

    private static void NormalizeHealthBarLayout(NHealthBar healthBar, UndoCreatureHealthBarState? snapshotState)
    {
        GetPrivateFieldValue<Tween>(healthBar, "_blockTween")?.Kill();
        SetPrivateFieldValue(healthBar, "_blockTween", null);

        Control? blockContainer = GetPrivateFieldValue<Control>(healthBar, "_blockContainer");
        if (snapshotState?.OriginalBlockPosition is Vector2 snapshotOriginalBlockPosition)
            SetPrivateFieldValue(healthBar, "_originalBlockPosition", snapshotOriginalBlockPosition);

        if (blockContainer != null)
        {
            if (snapshotState?.CurrentBlockPosition is Vector2 snapshotCurrentBlockPosition)
                blockContainer.Position = snapshotCurrentBlockPosition;
            else if (UndoReflectionUtil.FindField(healthBar.GetType(), "_originalBlockPosition")?.GetValue(healthBar) is Vector2 originalBlockPosition)
                blockContainer.Position = originalBlockPosition;

            if (snapshotState?.BlockVisible is bool blockVisible)
                blockContainer.Visible = blockVisible;
            if (snapshotState?.BlockModulate is Color blockModulate)
                blockContainer.Modulate = blockModulate;
            else
                blockContainer.Modulate = Colors.White;
        }

        if (GetPrivateFieldValue<Control>(healthBar, "_blockOutline") is { } blockOutline && snapshotState?.BlockOutlineVisible is bool blockOutlineVisible)
            blockOutline.Visible = blockOutlineVisible;
    }

    private static void RelayoutEnemyCreatureNodes(NCombatRoom combatRoom, CombatState combatState)
    {
        Control? encounterSlots = GetPrivateFieldValue<Control>(combatRoom, "<EncounterSlots>k__BackingField")
            ?? GetPrivateFieldValue<Control>(combatRoom, "EncounterSlots");
        if (encounterSlots != null)
        {
            SnapEnemyCreatureNodesToSlots(combatState);
            return;
        }

        List<NCreature> enemyNodes = combatState.Enemies
            .Select(combatRoom.GetCreatureNode)
            .Where(static node => node != null)
            .Cast<NCreature>()
            .ToList();
        if (enemyNodes.Count == 0)
            return;

        InvokePrivateMethod(combatRoom, "PositionEnemies", enemyNodes, GetCombatRoomEncounterScaling(combatRoom));
    }

    private static void ReconcileSovereignBladeVfx(CombatState combatState, NCombatRoom combatRoom)
    {
        foreach (Player player in combatState.Players)
        {
            NCreature? playerNode = combatRoom.GetCreatureNode(player.Creature);
            if (playerNode == null)
                continue;

            List<SovereignBlade> activeBlades = player.PlayerCombatState?.AllCards
                .OfType<SovereignBlade>()
                .Where(static blade => !blade.IsDupe && blade.Pile?.Type != PileType.Exhaust)
                .ToList()
                ?? [];
            List<CardModel> validCards = activeBlades
                .Select(static blade => (CardModel)(blade.DupeOf ?? blade))
                .ToList();

            foreach (NSovereignBladeVfx bladeVfx in playerNode.GetChildren().OfType<NSovereignBladeVfx>().ToList())
            {
                CardModel? trackedCard = bladeVfx.Card?.DupeOf ?? bladeVfx.Card;
                if (trackedCard != null && validCards.Any(validCard => ReferenceEquals(validCard, trackedCard)))
                    continue;

                bladeVfx.GetParent()?.RemoveChild(bladeVfx);
                QueueFreeNodeSafelyOnce(bladeVfx);
            }

            List<NSovereignBladeVfx> bladeNodes = [];
            foreach (SovereignBlade blade in activeBlades)
            {
                CardModel trackedCard = blade.DupeOf ?? blade;
                NSovereignBladeVfx? bladeVfx = playerNode.GetChildren()
                    .OfType<NSovereignBladeVfx>()
                    .FirstOrDefault(existing => ReferenceEquals(existing.Card?.DupeOf ?? existing.Card, trackedCard));
                if (bladeVfx == null)
                {
                    bladeVfx = NSovereignBladeVfx.Create(blade);
                    if (bladeVfx == null)
                        continue;

                    playerNode.AddChildSafely(bladeVfx);
                    bladeVfx.Position = Vector2.Zero;
                }

                NormalizeSovereignBladeVfx(bladeVfx, blade);
                bladeNodes.Add(bladeVfx);
            }

            for (int i = 0; i < bladeNodes.Count; i++)
                bladeNodes[i].OrbitProgress = bladeNodes.Count == 0 ? 0d : (double)i / bladeNodes.Count;
        }
    }

    private static void NormalizeSovereignBladeVfx(NSovereignBladeVfx bladeVfx, SovereignBlade blade)
    {
        bladeVfx.Position = Vector2.Zero;

        Node2D? spineNode = GetPrivateFieldValue<Node2D>(bladeVfx, "_spineNode");
        if (spineNode == null || !GodotObject.IsInstanceValid(spineNode))
            return;

        float bladeDamage = (float)blade.DynamicVars.Damage.IntValue;
        float bladeSize = Mathf.Clamp(Mathf.Lerp(0f, 1f, bladeDamage / 200f), 0f, 1f);
        Vector2 targetScale = Vector2.One * Mathf.Lerp(0.9f, 2f, bladeSize);
        spineNode.Visible = true;
        spineNode.Scale = targetScale;
        spineNode.Rotation = 0f;

        GetPrivateFieldValue<Tween>(bladeVfx, "_attackTween")?.Kill();
        GetPrivateFieldValue<Tween>(bladeVfx, "_scaleTween")?.Kill();
        GetPrivateFieldValue<Tween>(bladeVfx, "_sparkDelay")?.Kill();
        GetPrivateFieldValue<Tween>(bladeVfx, "_glowTween")?.Kill();
        SetPrivateFieldValue(bladeVfx, "_attackTween", null);
        SetPrivateFieldValue(bladeVfx, "_scaleTween", null);
        SetPrivateFieldValue(bladeVfx, "_sparkDelay", null);
        SetPrivateFieldValue(bladeVfx, "_glowTween", null);
        SetPrivateFieldValue(bladeVfx, "_bladeSize", bladeSize);
        SetPrivateFieldValue(bladeVfx, "_isForging", false);
        SetPrivateFieldValue(bladeVfx, "_isAttacking", false);

        if (GetPrivateFieldValue<Line2D>(bladeVfx, "_trail") is { } trail && GodotObject.IsInstanceValid(trail))
        {
            trail.Visible = false;
            trail.ClearPoints();
            trail.Modulate = Colors.White;
        }

        if (GetPrivateFieldValue<Node2D>(bladeVfx, "_bladeGlow") is { } bladeGlow && GodotObject.IsInstanceValid(bladeGlow))
        {
            bladeGlow.Visible = false;
            bladeGlow.Modulate = Colors.Transparent;
        }

        bool usePrimaryHilt = bladeSize < 0.3f;
        if (GetPrivateFieldValue<TextureRect>(bladeVfx, "_hilt") is { } hilt && GodotObject.IsInstanceValid(hilt))
            hilt.Visible = usePrimaryHilt;
        if (GetPrivateFieldValue<TextureRect>(bladeVfx, "_hilt2") is { } hilt2 && GodotObject.IsInstanceValid(hilt2))
            hilt2.Visible = !usePrimaryHilt;
        if (GetPrivateFieldValue<TextureRect>(bladeVfx, "_detail") is { } detail && GodotObject.IsInstanceValid(detail))
            detail.Visible = bladeDamage >= 0.66f;

        int chargeAmount = (int)(bladeSize * 30f);
        if (GetPrivateFieldValue<GpuParticles2D>(bladeVfx, "_chargeParticles") is { } chargeParticles && GodotObject.IsInstanceValid(chargeParticles))
        {
            chargeParticles.Amount = chargeAmount;
            chargeParticles.Emitting = chargeAmount > 0;
        }

        SyncSovereignBladeParticles(bladeVfx, "_spikeParticles", usePrimaryHilt);
        SyncSovereignBladeParticles(bladeVfx, "_spikeCircle", usePrimaryHilt);
        SyncSovereignBladeParticles(bladeVfx, "_spikeParticles2", !usePrimaryHilt);
        SyncSovereignBladeParticles(bladeVfx, "_spikeCircle2", !usePrimaryHilt);
    }

    private static void SyncSovereignBladeParticles(NSovereignBladeVfx bladeVfx, string fieldName, bool visible)
    {
        if (GetPrivateFieldValue<GpuParticles2D>(bladeVfx, fieldName) is not { } particles || !GodotObject.IsInstanceValid(particles))
            return;

        particles.Visible = visible;
        particles.Emitting = visible;
    }

    private static void RebuildEnemyNodesWithFallbackLayout(NCombatRoom combatRoom, CombatState combatState)
    {
        Dictionary<Creature, string?> originalSlots = combatState.Enemies.ToDictionary(static creature => creature, static creature => creature.SlotName);
        foreach (Creature creature in combatState.Enemies)
            creature.SlotName = null;

        foreach (Creature creature in combatState.Enemies)
            combatRoom.AddCreature(creature);

        foreach ((Creature creature, string? slotName) in originalSlots)
            creature.SlotName = slotName;

        List<NCreature> enemyNodes = combatState.Enemies
            .Select(combatRoom.GetCreatureNode)
            .Where(static node => node != null)
            .Cast<NCreature>()
            .ToList();
        InvokePrivateMethod(combatRoom, "PositionEnemies", enemyNodes, GetCombatRoomEncounterScaling(combatRoom));
        InvokePrivateMethod(combatRoom, "RandomizeEnemyScalesAndHues");
    }

    private static float GetCombatRoomEncounterScaling(NCombatRoom combatRoom)
    {
        object? visuals = GetPrivateFieldValue<object>(combatRoom, "_visuals");
        object? encounter = visuals == null ? null : FindProperty(visuals.GetType(), "Encounter")?.GetValue(visuals);
        object? scaling = encounter == null ? null : FindMethod(encounter.GetType(), "GetCameraScaling")?.Invoke(encounter, null);
        return scaling is float floatScaling ? floatScaling : 1f;
    }

    private static bool HasDetachedHandHolders(NPlayerHand hand)
    {
        return hand.GetChildren().OfType<NHandCardHolder>().Any();
    }

    private static int GetSelectedHandHolderCount(NPlayerHand hand)
    {
        return (GetPrivateFieldValue<NSelectedHandCardContainer>(hand, "_selectedHandCardContainer")
            ?? hand.GetNodeOrNull<NSelectedHandCardContainer>("%SelectedHandCardContainer"))
            ?.Holders.Count
            ?? 0;
    }

    private static int GetAwaitingHandHolderCount(NPlayerHand hand)
    {
        return GetPrivateFieldValue<System.Collections.IDictionary>(hand, "_holdersAwaitingQueue")?.Count ?? 0;
    }

    private static HashSet<NHandCardHolder> GetHoveredHandHolders(NPlayerHand hand)
    {
        HashSet<NHandCardHolder> hoveredHolders = [];
        if (!GodotObject.IsInstanceValid(hand))
            return hoveredHolders;

        if (GetPrivatePropertyValue<NHandCardHolder>(hand, "FocusedHolder") is { } focusedHolder
            && GodotObject.IsInstanceValid(focusedHolder))
            hoveredHolders.Add(focusedHolder);

        foreach (NHandCardHolder holder in hand.CardHolderContainer.GetChildren().OfType<NHandCardHolder>())
        {
            if (FindField(holder.GetType(), "_isHovered")?.GetValue(holder) as bool? == true)
                hoveredHolders.Add(holder);
        }

        return hoveredHolders;
    }

    private static void ResetPlayerHandUi(NPlayerHand hand)
    {
        InvokePrivateMethod(hand, "CancelHandSelectionIfNecessary");
        hand.CancelAllCardPlay();
        ClearDetachedHandHolderNodes(hand);
        NormalizePeekButtonForRestore(hand, selectionActive: false);
        ClearTween(hand, "_animInTween");
        ClearTween(hand, "_animOutTween");
        ClearTween(hand, "_animEnableTween");
        ClearTween(hand, "_selectedCardScaleTween");
        ClearSelectedHandCardsUi(hand);
        Node? currentCardPlay = GetPrivateFieldValue<Node>(hand, "_currentCardPlay");
        if (currentCardPlay != null && GodotObject.IsInstanceValid(currentCardPlay))
        {
            currentCardPlay.GetParent()?.RemoveChild(currentCardPlay);
            QueueFreeNodeSafelyOnce(currentCardPlay);
        }

        GetPrivateFieldValue<System.Collections.IDictionary>(hand, "_holdersAwaitingQueue")?.Clear();
        GetPrivateFieldValue<System.Collections.IList>(hand, "_selectedCards")?.Clear();
        SetPrivateFieldValue(hand, "_currentCardPlay", null);
        SetPrivateFieldValue(hand, "_draggedHolderIndex", -1);
        SetPrivateFieldValue(hand, "_lastFocusedHolderIdx", -1);
        SetPrivateFieldValue(hand, "_currentMode", NPlayerHand.Mode.Play);
        SetPrivateFieldValue(hand, "_isDisabled", false);
        SetPrivateFieldValue(hand, "_selectionCompletionSource", null);
        SetPrivateFieldValue(hand, "_currentSelectionFilter", null);
        SetPrivateFieldValue(hand, "_prefs", default(CardSelectorPrefs));
        SetPrivatePropertyValue(hand, "FocusedHolder", null);
        hand.CardHolderContainer.FocusMode = Control.FocusModeEnum.All;
        hand.Position = GetStaticFieldValue<Vector2>(typeof(NPlayerHand), "_showPosition");
        hand.Modulate = Colors.White;
        ClearNodeChildren(hand.CardHolderContainer);
        ResetSelectedHandCardContainerState(hand);
        HideControl(hand, "%SelectModeBackstop", Control.MouseFilterEnum.Ignore);
        HideControl(hand, "%SelectModeConfirmButton", Control.MouseFilterEnum.Ignore);
        HideControl(hand, "%UpgradePreviewContainer");
        HideControl(hand, "%SelectionHeader");
        DisableControl(hand, "%SelectModeConfirmButton");
        if (GetPrivateFieldValue<object>(hand, "_upgradePreview") is { } upgradePreview)
            SetPrivatePropertyValue(upgradePreview, "Card", null);
    }

    private static void ClearDetachedHandHolderNodes(NPlayerHand hand)
    {
        foreach (Node child in hand.GetChildren().Cast<Node>().ToList())
        {
            if (child is not NHandCardHolder holder)
                continue;

            holder.Clear();
            holder.GetParent()?.RemoveChildSafely(holder);
            holder.QueueFreeSafely();
        }
    }
    private static void ClearPlayQueueUi(NCardPlayQueue playQueue)
    {
        GetPrivateFieldValue<System.Collections.IList>(playQueue, "_playQueue")?.Clear();
        ClearNodeChildren(playQueue);
    }
    private static void RefreshCombatPileCounts(NCombatUi ui, Player player)
    {
        RefreshTopBarDeckCount(player);
        RefreshCombatPileCount(ui.DrawPile, PileType.Draw.GetPile(player).Cards.Count);
        RefreshCombatPileCount(ui.DiscardPile, PileType.Discard.GetPile(player).Cards.Count);
        RefreshCombatPileCount(ui.ExhaustPile, PileType.Exhaust.GetPile(player).Cards.Count);
    }

    private static void RefreshTopBarDeckCount(Player player)
    {
        var deckButton = NRun.Instance?.GlobalUi?.TopBar?.Deck;
        CardPile deckPile = PileType.Deck.GetPile(player);
        if (deckButton == null || deckPile == null)
            return;

        CardPile? previousPile = GetPrivateFieldValue<CardPile>(deckButton, "_pile");
        if (!ReferenceEquals(previousPile, deckPile))
        {
            MethodInfo? onPileContentsChangedMethod = FindMethod(deckButton.GetType(), "OnPileContentsChanged");
            System.Action? onPileContentsChanged = onPileContentsChangedMethod == null
                ? null
                : System.Delegate.CreateDelegate(typeof(System.Action), deckButton, onPileContentsChangedMethod, false) as System.Action;
            if (onPileContentsChanged != null)
            {
                if (previousPile != null)
                {
                    previousPile.CardAddFinished -= onPileContentsChanged;
                    previousPile.CardRemoveFinished -= onPileContentsChanged;
                }

                deckPile.CardAddFinished -= onPileContentsChanged;
                deckPile.CardRemoveFinished -= onPileContentsChanged;
                deckPile.CardAddFinished += onPileContentsChanged;
                deckPile.CardRemoveFinished += onPileContentsChanged;
            }

            SetPrivateFieldValue(deckButton, "_player", player);
            SetPrivateFieldValue(deckButton, "_pile", deckPile);
        }

        SetPrivateFieldValue(deckButton, "_count", (float)deckPile.Cards.Count);
        GetPrivateFieldValue<object>(deckButton, "_countLabel")
            ?.GetType()
            .GetMethod("SetTextAutoSize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(GetPrivateFieldValue<object>(deckButton, "_countLabel"), [deckPile.Cards.Count.ToString()]);
    }

    private static void RefreshCombatPileCount(Node pileNode, int count)
    {
        SetPrivateFieldValue(pileNode, "_currentCount", count);
        object? countLabel = GetPrivateFieldValue<object>(pileNode, "_countLabel");
        countLabel?.GetType().GetMethod("SetTextAutoSize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(countLabel, [count.ToString()]);
    }
    private static void ForceCombatUiInteractiveState(NCombatUi ui, CombatState combatState, Player? me, bool normalizeHandLayout = false)
    {
        SetPrivateFieldValue(ui.Hand, "_combatState", combatState);
        SetHandPresentation(ui.Hand, combatState.CurrentSide == CombatSide.Player && me != null && me.Creature.IsAlive);
        NormalizePeekButtonForRestore(ui.Hand, selectionActive: ui.Hand.IsInCardSelection);
        ui.Hand.EnableControllerNavigation();
        if (normalizeHandLayout || NeedsHandCardIndexRefresh(ui.Hand, me))
            ui.Hand.ForceRefreshCardIndices();
        if (normalizeHandLayout)
            SnapHandHolders(ui.Hand, preserveHoveredHolders: true);
        else
            RefreshHandHolderInteractionState(ui.Hand);
        ui.EndTurnButton.Initialize(combatState);
        ForceEndTurnButtonState(ui.EndTurnButton, combatState, me);
        WriteInteractionLog("force_ui_interactive", $"side={combatState.CurrentSide} me={(me == null ? "null" : me.NetId)}");
    }

    private static bool HasActiveHandInteraction(NPlayerHand hand)
    {
        if (!GodotObject.IsInstanceValid(hand))
            return false;

        if (hand.InCardPlay)
            return true;

        return GetPrivateFieldValue<System.Collections.IDictionary>(hand, "_holdersAwaitingQueue")?.Count > 0;
    }

    private static bool NeedsHandCardIndexRefresh(NPlayerHand hand, Player? player)
    {
        if (!GodotObject.IsInstanceValid(hand) || player == null)
            return true;

        return !TryGetReusableHandHolders(hand, player, out _);
    }

    private static void SetHandPresentation(NPlayerHand hand, bool shouldBeEnabled)
    {
        object? disabledValue = FindField(hand.GetType(), "_isDisabled")?.GetValue(hand);
        bool isCurrentlyDisabled = disabledValue is bool disabled && disabled;
        if (isCurrentlyDisabled != !shouldBeEnabled)
        {
            ClearTween(hand, "_animInTween");
            ClearTween(hand, "_animOutTween");
            ClearTween(hand, "_animEnableTween");
        }

        hand.Position = shouldBeEnabled
            ? GetStaticFieldValue<Vector2>(typeof(NPlayerHand), "_showPosition")
            : GetStaticFieldValue<Vector2>(typeof(NPlayerHand), "_disablePosition");
        hand.Modulate = shouldBeEnabled
            ? Colors.White
            : GetStaticFieldValue<Color>(typeof(NPlayerHand), "_disableModulate");
        SetPrivateFieldValue(hand, "_isDisabled", !shouldBeEnabled);
        hand.CardHolderContainer.FocusMode = Control.FocusModeEnum.All;
    }

    private static void NormalizePeekButtonForRestore(NPlayerHand hand, bool selectionActive)
    {
        NPeekButton? peekButton = hand.PeekButton;
        if (!GodotObject.IsInstanceValid(peekButton))
            return;

        bool wasPeeking = peekButton.IsPeeking;
        ClearTween(peekButton, "_hoverTween");
        ClearTween(peekButton, "_wiggleTween");
        SetPrivateFieldValue(peekButton, "_isPressed", false);

        if (FindField(peekButton.GetType(), "_hiddenTargets")?.GetValue(peekButton) is System.Collections.IEnumerable hiddenTargets)
        {
            foreach (Control target in hiddenTargets.OfType<Control>())
                target.Visible = true;

            hiddenTargets.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(hiddenTargets, null);
        }

        if (GetPrivateFieldValue<TextureRect>(peekButton, "_flash") is { } flash)
        {
            flash.Visible = false;
            Color flashModulate = flash.Modulate;
            flashModulate.A = 0f;
            flash.Modulate = flashModulate;
        }

        if (GetPrivateFieldValue<Control>(peekButton, "_visuals") is { } visuals)
        {
            visuals.Scale = Vector2.One;
            visuals.RotationDegrees = 0f;
            if (visuals.Material is ShaderMaterial shaderMaterial)
                shaderMaterial.SetShaderParameter("pulse_strength", 0);
        }

        SetPrivatePropertyValue(peekButton, "IsPeeking", false);

        if (wasPeeking && NCombatRoom.Instance?.Ui is { } combatUi)
            ResetCombatUiPeekModeForRestore(combatUi);

        if (selectionActive)
        {
            if (!peekButton.IsEnabled)
                peekButton.Enable();
            peekButton.MouseFilter = Control.MouseFilterEnum.Stop;
        }
        else
        {
            peekButton.Disable();
            peekButton.MouseFilter = Control.MouseFilterEnum.Ignore;
        }
    }

    private static void ResetCombatUiPeekModeForRestore(NCombatUi ui)
    {
        ClearTween(ui, "_playContainerPeekModeTween");
        ui.PlayQueue.Show();

        if (FindField(ui.GetType(), "_originalPlayContainerCardPositions")?.GetValue(ui) is System.Collections.IDictionary originalPositions
            && FindField(ui.GetType(), "_originalPlayContainerCardScales")?.GetValue(ui) is System.Collections.IDictionary originalScales)
        {
            foreach (NCard card in ui.PlayContainer.GetChildren().OfType<NCard>())
            {
                if (!GodotObject.IsInstanceValid(card))
                    continue;

                if (originalPositions.Contains(card) && originalPositions[card] is Vector2 position)
                    card.Position = position;
                if (originalScales.Contains(card) && originalScales[card] is Vector2 scale)
                    card.Scale = scale;
            }

            originalPositions.Clear();
            originalScales.Clear();
        }

        ActiveScreenContext.Instance.Update();
    }
    private static void ForceEndTurnButtonState(NEndTurnButton endTurnButton, CombatState combatState, Player? me)
    {
        ClearTween(endTurnButton, "_positionTween");
        ClearTween(endTurnButton, "_hoverTween");
        ClearTween(endTurnButton, "_glowEnableTween");
        ClearTween(endTurnButton, "_glowVfxTween");
        FieldInfo? stateField = FindField(endTurnButton.GetType(), "_state");
        if (combatState.CurrentSide == CombatSide.Player && me != null && me.Creature.IsAlive)
        {
            endTurnButton.Position = GetPrivatePropertyValue<Vector2>(endTurnButton, "ShowPos");
            if (stateField != null)
                stateField.SetValue(endTurnButton, Enum.ToObject(stateField.FieldType, 0));
            InvokePrivateMethod(endTurnButton, "AfterPlayerUnendedTurn", me);
        }
        else
        {
            endTurnButton.Position = GetPrivatePropertyValue<Vector2>(endTurnButton, "HidePos");
            if (stateField != null)
                stateField.SetValue(endTurnButton, Enum.ToObject(stateField.FieldType, 2));
        }
        endTurnButton.RefreshEnabled();
    }

    private static void RefreshHandHolderInteractionState(NPlayerHand hand)
    {
        foreach (Node child in hand.CardHolderContainer.GetChildren())
        {
            if (child is not NHandCardHolder holder)
                continue;

            holder.SetClickable(true);
            holder.FocusMode = Control.FocusModeEnum.All;
            holder.Hitbox.SetEnabled(true);
            holder.Hitbox.MouseFilter = Control.MouseFilterEnum.Stop;
        }
    }

    private static void SnapHandHolders(NPlayerHand hand, bool preserveHoveredHolders = false)
    {
        HashSet<NHandCardHolder>? preservedHolders = preserveHoveredHolders
            ? GetHoveredHandHolders(hand)
            : null;
        foreach (Node child in hand.CardHolderContainer.GetChildren())
        {
            if (child is not NHandCardHolder holder)
                continue;

            bool preserveHover = preservedHolders?.Contains(holder) == true;
            if (!preserveHover)
            {
                ClearObjectTweenFields(holder);
                if (holder.CardNode != null)
                    ClearObjectTweenFields(holder.CardNode);
                holder.SetDefaultTargets();
                holder.Position = holder.TargetPosition;
                holder.SetAngleInstantly(holder.TargetAngle);
                object? targetScale = FindField(holder.GetType(), "_targetScale")?.GetValue(holder);
                holder.SetScaleInstantly(targetScale is Vector2 scale ? scale : Vector2.One);
                holder.ZIndex = 0;
            }
        }

        RefreshHandHolderInteractionState(hand);
    }
    private static void ClearTween(object instance, string fieldName)
    {
        if (GetPrivateFieldValue<Tween>(instance, fieldName) is { } tween)
            tween.Kill();
    }

    private static void ClearObjectTweenFields(object? instance)
    {
        if (instance == null)
            return;

        Type type = instance.GetType();
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!typeof(Tween).IsAssignableFrom(field.FieldType))
                continue;

            try
            {
                if (field.GetValue(instance) is Tween tween && GodotObject.IsInstanceValid(tween))
                    tween.Kill();
                if (!field.IsInitOnly && !field.IsLiteral)
                    field.SetValue(instance, null);
            }
            catch
            {
            }
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!typeof(Tween).IsAssignableFrom(property.PropertyType)
                || !property.CanRead
                || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            try
            {
                if (property.GetValue(instance) is Tween tween && GodotObject.IsInstanceValid(tween))
                    tween.Kill();
                if (property.CanWrite)
                    property.SetValue(instance, null);
            }
            catch
            {
            }
        }
    }

    private static void ClearHandChoiceUiTweens(NPlayerHand hand)
    {
        if (!GodotObject.IsInstanceValid(hand))
            return;

        ClearObjectTweenFields(hand);
        foreach (NHandCardHolder holder in hand.CardHolderContainer.GetChildren().OfType<NHandCardHolder>())
        {
            ClearObjectTweenFields(holder);
            if (holder.CardNode != null)
                ClearObjectTweenFields(holder.CardNode);
        }

        foreach (NHandCardHolder holder in hand.GetChildren().OfType<NHandCardHolder>())
        {
            ClearObjectTweenFields(holder);
            if (holder.CardNode != null)
                ClearObjectTweenFields(holder.CardNode);
        }

        NSelectedHandCardContainer? selectedContainer = GetPrivateFieldValue<NSelectedHandCardContainer>(hand, "_selectedHandCardContainer")
            ?? hand.GetNodeOrNull<NSelectedHandCardContainer>("%SelectedHandCardContainer");
        if (selectedContainer == null || !GodotObject.IsInstanceValid(selectedContainer))
            return;

        ClearObjectTweenFields(selectedContainer);
        foreach (Node child in selectedContainer.GetChildren().Cast<Node>())
        {
            ClearObjectTweenFields(child);
            object? cardNode = FindProperty(child.GetType(), "CardNode")?.GetValue(child)
                ?? FindField(child.GetType(), "CardNode")?.GetValue(child)
                ?? FindField(child.GetType(), "_cardNode")?.GetValue(child);
            if (cardNode != null)
                ClearObjectTweenFields(cardNode);
        }
    }

    private static bool IsTweenRunning(object instance, string fieldName)
    {
        if (GetPrivateFieldValue<Tween>(instance, fieldName) is not { } tween)
            return false;

        return GodotObject.IsInstanceValid(tween) && tween.IsRunning();
    }

    private static bool ContainsDescendantOfType<TNode>(Node? node) where TNode : Node
    {
        if (node == null || !GodotObject.IsInstanceValid(node))
            return false;

        foreach (Node child in node.GetChildren())
        {
            if (child is TNode)
                return true;

            if (ContainsDescendantOfType<TNode>(child))
                return true;
        }

        return false;
    }
    private static void CaptureSelectedHandContainerDefaults(NPlayerHand hand)
    {
        NSelectedHandCardContainer? selectedContainer = GetPrivateFieldValue<NSelectedHandCardContainer>(hand, "_selectedHandCardContainer")
            ?? hand.GetNodeOrNull<NSelectedHandCardContainer>("%SelectedHandCardContainer");
        if (selectedContainer == null || !GodotObject.IsInstanceValid(selectedContainer))
            return;

        ulong id = selectedContainer.GetInstanceId();
        if (!SelectedHandContainerDefaultPositions.ContainsKey(id))
            SelectedHandContainerDefaultPositions[id] = selectedContainer.Position;
        if (!SelectedHandContainerDefaultScales.ContainsKey(id))
            SelectedHandContainerDefaultScales[id] = selectedContainer.Scale;
    }

    private static void ClearCreatureIntentUi(NCreature creatureNode)
    {
        creatureNode.AnimHideIntent(0f);
        ClearNodeChildren(creatureNode.IntentContainer);
    }

    private static void SnapEnemyCreatureNodesToSlots(CombatState combatState)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return;

        Control? encounterSlots = GetPrivateFieldValue<Control>(combatRoom, "<EncounterSlots>k__BackingField")
            ?? GetPrivateFieldValue<Control>(combatRoom, "EncounterSlots");
        if (encounterSlots == null)
            return;

        foreach (Creature creature in combatState.Enemies)
        {
            if (string.IsNullOrWhiteSpace(creature.SlotName) || !encounterSlots.HasNode(creature.SlotName))
                continue;

            NCreature? node = combatRoom.GetCreatureNode(creature);
            if (node == null)
                continue;

            node.GlobalPosition = encounterSlots.GetNode<Marker2D>(creature.SlotName).GlobalPosition;
        }
    }
    private static void ClearTransientCardVisuals()
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom != null)
        {
            ClearNodeChildren(combatRoom.BackCombatVfxContainer);
            ClearNodeChildren(combatRoom.CombatVfxContainer);
            ClearNodeChildren(combatRoom.Ui.CardPreviewContainer);
            ClearNodeChildren(combatRoom.Ui.MessyCardPreviewContainer);
        }

        var globalUi = NRun.Instance?.GlobalUi;
        if (globalUi == null)
            return;

        ClearNodeChildren(globalUi.CardPreviewContainer);
        ClearNodeChildren(globalUi.MessyCardPreviewContainer);
        ClearNodeChildren(globalUi.GridCardPreviewContainer);
        ClearNodeChildren(globalUi.EventCardPreviewContainer);
        RemoveCardFlyVfxNodes(globalUi.TopBar?.TrailContainer);
    }

    private static void RemoveCardFlyVfxNodes(Node? root)
    {
        if (root == null || !GodotObject.IsInstanceValid(root))
            return;

        foreach (Node child in root.GetChildren().Cast<Node>().ToList())
        {
            RemoveCardFlyVfxNodes(child);
            if (child is NCardTrailVfx orphanTrailVfx)
            {
                orphanTrailVfx.GetParent()?.RemoveChild(orphanTrailVfx);
                QueueFreeNodeSafelyOnce(orphanTrailVfx);
                continue;
            }

            if (child is NCard orphanCardNode)
            {
                orphanCardNode.GetParent()?.RemoveChild(orphanCardNode);
                QueueFreeNodeSafelyOnce(orphanCardNode);
                continue;
            }

            if (child is not NCardFlyVfx flyVfx)
                continue;

            if (GetPrivateFieldValue<Node>(flyVfx, "_vfx") is { } trailVfx && GodotObject.IsInstanceValid(trailVfx))
            {
                trailVfx.GetParent()?.RemoveChild(trailVfx);
                QueueFreeNodeSafelyOnce(trailVfx);
            }

            if (GetPrivateFieldValue<NCard>(flyVfx, "_card") is { } cardNode && GodotObject.IsInstanceValid(cardNode))
            {
                cardNode.GetParent()?.RemoveChild(cardNode);
                QueueFreeNodeSafelyOnce(cardNode);
            }

            flyVfx.GetParent()?.RemoveChild(flyVfx);
            QueueFreeNodeSafelyOnce(flyVfx);
        }
    }

    private void DetachPendingHandSelectionSource(NPlayerHand hand)
    {
        if (_pendingHandChoiceSource == null)
            return;

        MethodInfo? handlerMethod = FindMethod(hand.GetType(), "OnSelectModeSourceFinished");
        if (handlerMethod == null)
        {
            _pendingHandChoiceSource = null;
            return;
        }

        try
        {
            Action<AbstractModel> handler = (Action<AbstractModel>)handlerMethod.CreateDelegate(typeof(Action<AbstractModel>), hand);
            _pendingHandChoiceSource.ExecutionFinished -= handler;
        }
        catch
        {
        }

        _pendingHandChoiceSource = null;
    }
    // restore/撤销清理旧选牌 UI 时，不要把已选牌临时加回手牌。
    // hand.Add(...) 会立刻触发官方 RefreshLayout，若此时手牌 holder 还没清空，
    // 就会出现日志里的 "Hand size 12 is greater than 11"。
    private static void ClearSelectedHandCardsUi(NPlayerHand hand)
    {
        NSelectedHandCardContainer? selectedContainer = GetPrivateFieldValue<NSelectedHandCardContainer>(hand, "_selectedHandCardContainer")
            ?? hand.GetNodeOrNull<NSelectedHandCardContainer>("%SelectedHandCardContainer");
        if (selectedContainer == null || !GodotObject.IsInstanceValid(selectedContainer))
            return;

        foreach (NSelectedHandCardHolder selectedHolder in selectedContainer.Holders.ToList())
        {
            selectedHolder.SetClickable(false);
            selectedHolder.FocusMode = Control.FocusModeEnum.None;
            selectedHolder.MouseFilter = Control.MouseFilterEnum.Ignore;
            selectedHolder.Hitbox.SetEnabled(false);
            selectedHolder.Hitbox.MouseFilter = Control.MouseFilterEnum.Ignore;
            ClearObjectTweenFields(selectedHolder);
            if (selectedHolder.CardNode != null)
                ClearObjectTweenFields(selectedHolder.CardNode);
            selectedHolder.GetParent()?.RemoveChild(selectedHolder);
            QueueFreeNodeSafelyOnce(selectedHolder);
        }
    }

    private static void ResetSelectedHandCardContainerState(NPlayerHand hand)
    {
        NSelectedHandCardContainer? selectedContainer = GetPrivateFieldValue<NSelectedHandCardContainer>(hand, "_selectedHandCardContainer")
            ?? hand.GetNodeOrNull<NSelectedHandCardContainer>("%SelectedHandCardContainer");
        if (selectedContainer == null || !GodotObject.IsInstanceValid(selectedContainer))
            return;

        ClearObjectTweenFields(selectedContainer);
        selectedContainer.Hand = hand;
        ulong id = selectedContainer.GetInstanceId();
        if (SelectedHandContainerDefaultPositions.TryGetValue(id, out Vector2 defaultPosition))
            selectedContainer.Position = defaultPosition;
        if (SelectedHandContainerDefaultScales.TryGetValue(id, out Vector2 defaultScale))
            selectedContainer.Scale = defaultScale;
        else
            selectedContainer.Scale = Vector2.One;
        selectedContainer.FocusMode = Control.FocusModeEnum.None;
        selectedContainer.MouseFilter = Control.MouseFilterEnum.Ignore;
        ClearNodeChildren(selectedContainer);
        InvokePrivateMethod(selectedContainer, "RefreshHolderPositions");
        InvokePrivateMethod(hand, "UpdateSelectedCardContainer", 0);
    }
}

