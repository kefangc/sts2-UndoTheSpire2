// 文件说明：恢复战斗 UI、手牌和视觉层状态。
// Coordinates undo/redo history and restore transactions.
// Capture/restore details should live in dedicated services; this type is the orchestrator.
using System.Reflection;
using Godot;
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
        }
        await WaitOneFrameAsync();
        if (NCombatRoom.Instance != null)
        {
            ApplySnapshotCreatureNodeVisuals(combatState, snapshotState);
            ReconcileSovereignBladeVfx(combatState, NCombatRoom.Instance);
            ForceCombatUiInteractiveState(NCombatRoom.Instance.Ui, combatState, LocalContext.GetMe(combatState));
            SnapEnemyCreatureNodesToSlots(combatState);
            UndoSpecialCreatureVisualNormalizer.Refresh(combatState, NCombatRoom.Instance);
        }

        if (runState != null)
            RebuildPotionContainer(runState);

        NotifyCombatStateChangedMethod?.Invoke(CombatManager.Instance.StateTracker, ["UndoRefreshCombatUiAsync"]);
    }

    private static async Task RefreshCombatUiAfterHandDiscardChoiceAsync(CombatState combatState)
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
        _ = ReconcileHandDiscardChoiceUiAfterSettleAsync(combatState);
    }

    private static async Task ReconcileHandDiscardChoiceUiAfterSettleAsync(CombatState combatState)
    {
        try
        {
            for (int frame = 0; frame < 120; frame++)
            {
                NCombatRoom? combatRoom = NCombatRoom.Instance;
                if (combatRoom == null || !CombatManager.Instance.IsInProgress)
                    return;

                NPlayerHand hand = combatRoom.Ui.Hand;
                bool hasValidCurrentCardPlay = HasValidCurrentCardPlay(hand);
                bool hasTransientCardFlyVfx = HasTransientCardFlyVfx(combatRoom.CombatVfxContainer)
                    || HasTransientCardFlyVfx(NRun.Instance?.GlobalUi?.TopBar?.TrailContainer);
                if (!hasValidCurrentCardPlay && !hasTransientCardFlyVfx)
                    break;

                await WaitOneFrameAsync();
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
        ResetPlayerHandUi(hand);
        foreach (CardModel card in PileType.Hand.GetPile(me).Cards)
            hand.Add(CreateCardNode(card, PileType.Hand), -1);
        SnapHandHolders(hand);
        SyncPlayContainerCards(ui, me);
        ForceCombatUiInteractiveState(ui, combatState, me);
        NotifyCombatStateChangedMethod?.Invoke(CombatManager.Instance.StateTracker, ["UndoHandDiscardChoiceUiRecovery"]);
    }

    private static bool NeedsHandDiscardChoiceUiRecovery(NPlayerHand hand, Player player)
    {
        if (!GodotObject.IsInstanceValid(hand))
            return false;

        if (HasValidCurrentCardPlay(hand))
            return false;

        int expectedHandCount = PileType.Hand.GetPile(player).Cards.Count;
        int holderCount = hand.CardHolderContainer.GetChildCount();
        int selectedHolderCount = (GetPrivateFieldValue<NSelectedHandCardContainer>(hand, "_selectedHandCardContainer")
            ?? hand.GetNodeOrNull<NSelectedHandCardContainer>("%SelectedHandCardContainer"))
            ?.Holders.Count
            ?? 0;
        int awaitingHolderCount = GetPrivateFieldValue<System.Collections.IDictionary>(hand, "_holdersAwaitingQueue")?.Count ?? 0;

        if (selectedHolderCount > 0 || awaitingHolderCount > 0)
            return true;

        return holderCount != expectedHandCount;
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
        RunManager.Instance.HoveredModelTracker.OnLocalCardDeselected();
        RunManager.Instance.HoveredModelTracker.OnLocalCardUnhovered();
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
        ResetPlayerHandUi(ui.Hand);
        ClearPlayQueueUi(ui.PlayQueue);
        ClearNodeChildren(ui.PlayContainer);
        Player? me = LocalContext.GetMe(combatState);
        if (me == null)
            return;
        foreach (CardModel card in PileType.Hand.GetPile(me).Cards)
            ui.Hand.Add(CreateCardNode(card, PileType.Hand), -1);
        SnapHandHolders(ui.Hand);
        SyncPlayContainerCards(ui, me);
    }

    // choice undo 时，屏幕中央“本次打出的牌”和左侧眼睛预览都依赖 PlayContainer。
    // 如果每次 restore 都直接清掉再用普通 NCard 重建，很容易丢掉官方的显示状态，
    // 并把卡留在左上角默认位置。这里优先复用已在场上的节点，只有不匹配时才重建。
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
            .Where(static state => state.VisualDefaultScale.HasValue || state.VisualHue.HasValue || state.TempScale.HasValue)
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
        }

        RelayoutEnemyCreatureNodes(combatRoom, combatState);
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
    private static void ResetPlayerHandUi(NPlayerHand hand)
    {
        InvokePrivateMethod(hand, "CancelHandSelectionIfNecessary");
        hand.CancelAllCardPlay();
        ClearDetachedHandHolderNodes(hand);
        hand.PeekButton.SetPeeking(false);
        hand.PeekButton.Disable();
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
        HideControl(hand, "%UpgradePreviewContainer");
        HideControl(hand, "%SelectionHeader");
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
    private static void ForceCombatUiInteractiveState(NCombatUi ui, CombatState combatState, Player? me)
    {
        SetPrivateFieldValue(ui.Hand, "_combatState", combatState);
        SetHandPresentation(ui.Hand, combatState.CurrentSide == CombatSide.Player && me != null && me.Creature.IsAlive);
        ui.Hand.EnableControllerNavigation();
        ui.Hand.ForceRefreshCardIndices();
        SnapHandHolders(ui.Hand);
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
    private static void SetHandPresentation(NPlayerHand hand, bool shouldBeEnabled)
    {
        ClearTween(hand, "_animInTween");
        ClearTween(hand, "_animOutTween");
        ClearTween(hand, "_animEnableTween");
        hand.Position = shouldBeEnabled
            ? GetStaticFieldValue<Vector2>(typeof(NPlayerHand), "_showPosition")
            : GetStaticFieldValue<Vector2>(typeof(NPlayerHand), "_disablePosition");
        hand.Modulate = shouldBeEnabled
            ? Colors.White
            : GetStaticFieldValue<Color>(typeof(NPlayerHand), "_disableModulate");
        SetPrivateFieldValue(hand, "_isDisabled", !shouldBeEnabled);
        hand.CardHolderContainer.FocusMode = Control.FocusModeEnum.All;
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
    private static void SnapHandHolders(NPlayerHand hand)
    {
        foreach (Node child in hand.CardHolderContainer.GetChildren())
        {
            if (child is not NHandCardHolder holder)
                continue;
            holder.SetDefaultTargets();
            holder.Position = holder.TargetPosition;
            holder.SetAngleInstantly(holder.TargetAngle);
            object? targetScale = FindField(holder.GetType(), "_targetScale")?.GetValue(holder);
            holder.SetScaleInstantly(targetScale is Vector2 scale ? scale : Vector2.One);
            holder.SetClickable(true);
            holder.FocusMode = Control.FocusModeEnum.All;
            holder.Hitbox.SetEnabled(true);
            holder.Hitbox.MouseFilter = Control.MouseFilterEnum.Stop;
            holder.ZIndex = 0;
        }
    }
    private static void ClearTween(object instance, string fieldName)
    {
        if (GetPrivateFieldValue<Tween>(instance, fieldName) is { } tween)
            tween.Kill();
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

        selectedContainer.Hand = hand;
        ulong id = selectedContainer.GetInstanceId();
        if (SelectedHandContainerDefaultPositions.TryGetValue(id, out Vector2 defaultPosition))
            selectedContainer.Position = defaultPosition;
        if (SelectedHandContainerDefaultScales.TryGetValue(id, out Vector2 defaultScale))
            selectedContainer.Scale = defaultScale;
        else
            selectedContainer.Scale = Vector2.One;
        selectedContainer.FocusMode = Control.FocusModeEnum.None;
        ClearNodeChildren(selectedContainer);
        InvokePrivateMethod(selectedContainer, "RefreshHolderPositions");
        InvokePrivateMethod(hand, "UpdateSelectedCardContainer", 0);
    }
}

