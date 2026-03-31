using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace UndoTheSpire2;

public sealed partial class UndoController
{
    private static void RebuildActionQueues(IReadOnlyList<Player> players)
    {
        ActionQueueSet actionQueueSet = RunManager.Instance.ActionQueueSet;
        Type queueSetType = actionQueueSet.GetType();
        if (FindField(queueSetType, "_actionQueues")?.GetValue(actionQueueSet) is not System.Collections.IList queues)
            throw new InvalidOperationException("Could not access ActionQueueSet._actionQueues.");

        queues.Clear();
        if (FindField(queueSetType, "_actionsWaitingForResumption")?.GetValue(actionQueueSet) is System.Collections.IList waiting)
            waiting.Clear();

        Type? queueType = queueSetType.GetNestedType("ActionQueue", BindingFlags.NonPublic);
        if (queueType == null)
            throw new InvalidOperationException("Could not access ActionQueueSet.ActionQueue type.");

        FieldInfo? actionsField = FindField(queueType, "actions");
        FieldInfo? ownerIdField = FindField(queueType, "ownerId");
        FieldInfo? cancellingPlayField = FindField(queueType, "isCancellingPlayCardActions");
        FieldInfo? cancellingDrivenField = FindField(queueType, "isCancellingPlayerDrivenCombatActions");
        FieldInfo? cancellingCombatField = FindField(queueType, "isCancellingCombatActions");
        FieldInfo? pausedField = FindField(queueType, "isPaused");
        if (actionsField == null || ownerIdField == null || cancellingCombatField == null || pausedField == null)
            throw new InvalidOperationException("Could not access ActionQueue fields.");

        foreach (Player player in players)
        {
            object queue = Activator.CreateInstance(queueType, true)
                ?? throw new InvalidOperationException("Could not create ActionQueue.");
            actionsField.SetValue(queue, new List<GameAction>());
            ownerIdField.SetValue(queue, player.NetId);
            cancellingPlayField?.SetValue(queue, false);
            cancellingDrivenField?.SetValue(queue, false);
            cancellingCombatField.SetValue(queue, false);
            pausedField.SetValue(queue, false);
            queues.Add(queue);
        }

        InvokePrivateMethod(actionQueueSet, "CheckIfQueuesEmpty");
    }

    private static UndoCombatCardDbState CaptureCombatCardDbState(RunState runState)
    {
        NetCombatCardDb db = NetCombatCardDb.Instance;
        if (GetPrivateFieldValue<System.Collections.IDictionary>(db, "_idToCard") is not { } idToCard)
            return new UndoCombatCardDbState();

        List<UndoCombatCardDbEntryState> entries = [];
        foreach (System.Collections.DictionaryEntry entry in idToCard)
        {
            if (entry.Key is not uint combatCardId || entry.Value is not CardModel card || !card.IsMutable)
                continue;

            entries.Add(new UndoCombatCardDbEntryState
            {
                CombatCardId = combatCardId,
                Card = UndoStableRefs.CaptureCardRef(runState, card)
            });
        }

        return new UndoCombatCardDbState
        {
            Entries = [.. entries.OrderBy(static entry => entry.CombatCardId)],
            NextId = FindField(db.GetType(), "_nextId")?.GetValue(db) is uint nextId ? nextId : 0U
        };
    }

    private static void RestoreCombatCardDbState(RunState runState, NetCombatCardDb db, UndoCombatCardDbState combatCardDbState)
    {
        if (GetPrivateFieldValue<System.Collections.IDictionary>(db, "_idToCard") is not { } idToCard
            || GetPrivateFieldValue<System.Collections.IDictionary>(db, "_cardToId") is not { } cardToId)
        {
            return;
        }

        List<CardModel> fallbackCards = cardToId.Keys.OfType<CardModel>().Where(static card => card.IsMutable).ToList();
        idToCard.Clear();
        cardToId.Clear();

        uint nextId = combatCardDbState.NextId;
        foreach (UndoCombatCardDbEntryState entry in combatCardDbState.Entries.OrderBy(static entry => entry.CombatCardId))
        {
            CardModel card = UndoStableRefs.ResolveCardRef(runState, entry.Card);
            if (!card.IsMutable || !IsCardInCombatPiles(runState, card) || cardToId.Contains(card) || idToCard.Contains(entry.CombatCardId))
                continue;

            idToCard[entry.CombatCardId] = card;
            cardToId[card] = entry.CombatCardId;
            if (entry.CombatCardId >= nextId)
                nextId = entry.CombatCardId + 1;
        }

        foreach (CardModel card in fallbackCards)
        {
            if (cardToId.Contains(card))
                continue;

            while (idToCard.Contains(nextId))
                nextId++;

            idToCard[nextId] = card;
            cardToId[card] = nextId;
            nextId++;
        }

        UndoReflectionUtil.TrySetFieldValue(db, "_nextId", nextId);
    }

    private static bool IsCardInCombatPiles(RunState runState, CardModel card)
    {
        foreach (Player player in runState.Players)
        {
            if (player.PlayerCombatState == null)
                continue;

            foreach (CardPile pile in player.PlayerCombatState.AllPiles)
            {
                if (pile.Cards.Contains(card))
                    return true;
            }
        }

        return false;
    }

    private static void RebuildTransientCombatCaches(RunState runState, UndoCombatCardDbState? combatCardDbState = null)
    {
        RebuildNetCombatCardDb(runState, combatCardDbState);
        ResetLocalHoveredModelState();
        RebuildPotionContainer(runState);
    }

    private static void ResetLocalHoveredModelState()
    {
        object? hoveredModelTracker = RunManager.Instance?.HoveredModelTracker;
        if (hoveredModelTracker == null)
            return;

        SetPrivateFieldValue(hoveredModelTracker, "_localSelectedCard", null);
        SetPrivateFieldValue(hoveredModelTracker, "_localSelectedPotion", null);
        SetPrivateFieldValue(hoveredModelTracker, "_localHoveredCard", null);
        SetPrivateFieldValue(hoveredModelTracker, "_localHoveredRelic", null);
        SetPrivateFieldValue(hoveredModelTracker, "_localHoveredPotion", null);

        try
        {
            if (GetPrivateFieldValue<object>(hoveredModelTracker, "_inputSynchronizer") is { } inputSynchronizer)
            {
                inputSynchronizer.GetType()
                    .GetMethod("SyncLocalHoveredModel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.Invoke(inputSynchronizer, [null]);
            }
        }
        catch (TargetInvocationException ex)
        {
            UndoDebugLog.Write($"hover_reset_sync_failed:{ex.InnerException ?? ex}");
        }
        catch (Exception ex)
        {
            UndoDebugLog.Write($"hover_reset_sync_failed:{ex}");
        }
    }

    private static void RebuildNetCombatCardDb(RunState runState, UndoCombatCardDbState? combatCardDbState)
    {
        NetCombatCardDb db = NetCombatCardDb.Instance;
        InvokePrivateMethod(db, "OnCombatEnded", new object?[] { null });
        db.ClearCardsForTesting();
        db.StartCombat(runState.Players);

        if (combatCardDbState != null)
            RestoreCombatCardDbState(runState, db, combatCardDbState);
    }

    private static void RebuildPotionContainer(RunState runState)
    {
        NPotionContainer? potionContainer = NRun.Instance?.GlobalUi?.TopBar?.PotionContainer;
        if (potionContainer == null)
            return;

        Control? potionHolders = GetPrivateFieldValue<Control>(potionContainer, "_potionHolders");
        Vector2 potionHolderBasePosition = potionHolders?.Position ?? Vector2.Zero;
        if (GetPrivateFieldValue<Tween>(potionContainer, "_potionsFullTween") is { } potionsFullTween)
        {
            if (potionsFullTween.IsRunning())
            {
                object? holderInitPos = FindField(potionContainer.GetType(), "_potionHolderInitPos")?.GetValue(potionContainer);
                if (holderInitPos is Vector2 initPosition)
                    potionHolderBasePosition = initPosition;
            }

            potionsFullTween.Kill();
        }

        if (potionHolders != null)
        {
            foreach (Node child in potionHolders.GetChildren().Cast<Node>().ToList())
            {
                child.GetParent()?.RemoveChild(child);
                child.QueueFree();
            }

            potionHolders.Position = potionHolderBasePosition;
        }

        if (GetPrivateFieldValue<System.Collections.IList>(potionContainer, "_holders") is { } holders)
            holders.Clear();

        if (GetPrivateFieldValue<Control>(potionContainer, "_potionErrorBg") is { } potionErrorBg)
            potionErrorBg.Modulate = Colors.Transparent;

        SetPrivateFieldValue(potionContainer, "_focusedHolder", null);
        SetPrivateFieldValue(potionContainer, "_potionsFullTween", null);
        potionContainer.Initialize(runState);
        InvokePrivateMethod(potionContainer, "UpdateNavigation");
    }

    private static void NormalizeRelicInventoryUi(RunState runState)
    {
        NRelicInventory? relicInventory = NRun.Instance?.GlobalUi?.RelicInventory;
        if (relicInventory == null)
            return;

        ResetLocalHoveredModelState();
        if (GetPrivateFieldValue<System.Collections.IList>(relicInventory, "_relicNodes") is { } relicNodes)
        {
            foreach (Node node in relicNodes.Cast<Node>().ToList())
            {
                if (!GodotObject.IsInstanceValid(node))
                    continue;

                node.GetParent()?.RemoveChild(node);
                node.QueueFree();
            }

            relicNodes.Clear();
        }

        ClearNodeChildren(relicInventory);
        relicInventory.Initialize(runState);
        InvokePrivateMethod(relicInventory, "UpdateNavigation");
    }
}
