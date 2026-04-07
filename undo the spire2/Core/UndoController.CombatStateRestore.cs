// 文件说明：把完整快照恢复到当前战斗，并重建同步边界。
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
using MegaCrit.Sts2.Core.Models.Monsters;
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

// In-place combat state restore and validation.
public sealed partial class UndoController
{
    private static bool CanApplyFullStateInPlace(
        NetFullCombatState snapshot,
        RunState runState,
        CombatState combatState,
        out string reason)
    {
        if (snapshot.Players.Count != runState.Players.Count)
        {
            reason = "player_count_changed";
            return false;
        }

        IReadOnlyList<Player> currentPlayers = runState.Players;
        for (int i = 0; i < snapshot.Players.Count; i++)
        {
            if (currentPlayers[i].NetId != snapshot.Players[i].playerId)
            {
                reason = $"player_mismatch_{i}";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static void RestorePlayers(RunState runState, CombatState combatState, UndoCombatFullState snapshotState)
    {
        foreach (NetFullCombatState.PlayerState playerState in snapshotState.FullState.Players)
        {
            Player player = runState.GetPlayer(playerState.playerId)
                ?? throw new InvalidOperationException($"Could not map player snapshot {playerState.playerId}.");
            UndoPlayerOrbState? playerOrbState = GetPlayerOrbState(snapshotState, player.NetId);

            player.PlayerRng.LoadFromSerializable(playerState.rngSet);
            player.PlayerOdds.LoadFromSerializable(playerState.oddsSet);
            player.RelicGrabBag.LoadFromSerializable(playerState.relicGrabBag);
            player.Gold = playerState.gold;
            if (playerOrbState != null)
                player.BaseOrbSlotCount = playerOrbState.BaseOrbSlotCount;

            RestoreRelics(runState, combatState, player, playerState, GetRelicRuntimeStatesForPlayer(snapshotState, player.NetId));
            RestorePotions(player, playerState, GetPlayerPotionState(snapshotState, player.NetId));
            RestorePlayerDeck(runState, player, GetPlayerDeckState(snapshotState, player.NetId));
            RestorePlayerCombatState(
                player,
                runState,
                combatState,
                playerState,
                GetCardCostStatesForPlayer(snapshotState, player.NetId),
                GetCardRuntimeStatesForPlayer(snapshotState, player.NetId),
                playerOrbState);
        }
    }

    private static void RestorePlayerDeck(RunState runState, Player player, UndoPlayerDeckState? deckState)
    {
        if (deckState == null)
            return;

        foreach (CardModel card in player.Deck.Cards.ToList())
        {
            card.RemoveFromCurrentPile();
            card.RemoveFromState();
        }

        foreach (SerializableCard serializableCard in deckState.Cards)
        {
            CardModel deckCard = runState.LoadCard(ClonePacketSerializable(serializableCard), player);
            player.Deck.AddInternal(deckCard, -1, true);
        }

        player.Deck.InvokeContentsChanged();
    }

    private static void RestoreRelics(RunState runState, CombatState combatState, Player player, NetFullCombatState.PlayerState playerState, IReadOnlyList<UndoRelicRuntimeState>? relicRuntimeStates)
    {
        foreach (RelicModel relic in player.Relics.ToList())
            player.RemoveRelicInternal(relic, true);

        foreach (NetFullCombatState.RelicState relicState in playerState.relics)
            player.AddRelicInternal(RelicModel.FromSerializable(relicState.relic), -1, true);

        if (relicRuntimeStates == null || relicRuntimeStates.Count == 0)
            return;

        UndoRuntimeRestoreContext context = new()
        {
            RunState = runState,
            CombatState = combatState
        };

        Dictionary<ModelId, int> ordinalsByRelicId = [];
        foreach (RelicModel relic in player.Relics)
        {
            int ordinal = ordinalsByRelicId.TryGetValue(relic.Id, out int existingOrdinal) ? existingOrdinal : 0;
            ordinalsByRelicId[relic.Id] = ordinal + 1;
            UndoRelicRuntimeState? runtimeState = relicRuntimeStates.FirstOrDefault(state => state.RelicId == relic.Id && state.Ordinal == ordinal);
            if (runtimeState == null)
                continue;

            bool normalizeTransientActivation = UndoRuntimeStateCodecRegistry.ShouldNormalizeRelicActivationForSavestate(relic);
            relic.Status = runtimeState.Status;
            bool? isActivating = normalizeTransientActivation && runtimeState.IsActivating.HasValue
                ? false
                : runtimeState.IsActivating;
            if (isActivating.HasValue)
            {
                if (!TrySetPrivateAutoPropertyBackingField(relic, "IsActivating", isActivating.Value))
                    SetPrivatePropertyValue(relic, "IsActivating", isActivating.Value);
            }

            RestoreRuntimeBoolProperties(relic, runtimeState.BoolProperties);
            RestoreRuntimeIntProperties(relic, runtimeState.IntProperties);
            RestoreRuntimeEnumProperties(relic, runtimeState.EnumProperties);
            UndoRuntimeStateCodecRegistry.RestoreRelicStates(relic, runtimeState.ComplexStates, context);
            UndoRuntimeStateCodecRegistry.NormalizeRelicDisplayForSavestateRestore(relic);
        }
    }

    private static void RestoreSpecialPowerRuntimeState(PowerModel power, UndoPowerRuntimeState runtimeState, CombatState combatState)
    {
        if (!UndoReflectionUtil.TryGetFieldValue(power, "_internalData", out object? internalData)
            || internalData == null)
            return;

        UndoNamedBoolState? isRevivingState = runtimeState.BoolProperties.FirstOrDefault(static state => state.Name == "IsReviving");
        FieldInfo? isRevivingField = FindField(internalData.GetType(), "isReviving");
        if (isRevivingState != null && isRevivingField?.FieldType == typeof(bool))
            isRevivingField.SetValue(internalData, isRevivingState.Value);

        if (power is not VitalSparkPower)
            return;

        object? triggeredPlayersValue = UndoReflectionUtil.TryGetFieldValue(internalData, "playersTriggeredThisTurn", out object? resolvedPlayers)
            ? resolvedPlayers
            : null;
        if (triggeredPlayersValue == null)
            return;

        MethodInfo? clearMethod = triggeredPlayersValue.GetType().GetMethod("Clear", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        MethodInfo? addMethod = triggeredPlayersValue.GetType().GetMethod("Add", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, [typeof(Player)], null);
        if (clearMethod == null || addMethod == null)
            return;

        clearMethod.Invoke(triggeredPlayersValue, null);
        foreach (ulong playerNetId in runtimeState.TriggeredPlayerNetIds)
        {
            Player? player = combatState.GetPlayer(playerNetId);
            if (player != null)
                addMethod.Invoke(triggeredPlayersValue, [player]);
        }
    }

    private static void RestoreRuntimeBoolProperties(object target, IReadOnlyList<UndoNamedBoolState> states)
    {
        foreach (UndoNamedBoolState state in states)
        {
            if (ShouldSkipRuntimeStateProperty(target.GetType(), state.Name))
                continue;

            PropertyInfo? property = FindProperty(target.GetType(), state.Name);
            if (property != null && property.PropertyType == typeof(bool))
            {
                if (TrySetRuntimePropertyValue(target, property, state.Name, state.Value))
                    continue;
            }

            FieldInfo? field = FindField(target.GetType(), state.Name);
            if (field != null && field.FieldType == typeof(bool))
                field.SetValue(target, state.Value);
        }
    }

    private static void RestoreRuntimeIntProperties(object target, IReadOnlyList<UndoNamedIntState> states)
    {
        foreach (UndoNamedIntState state in states)
        {
            if (ShouldSkipRuntimeStateProperty(target.GetType(), state.Name))
                continue;

            PropertyInfo? property = FindProperty(target.GetType(), state.Name);
            if (property != null && property.PropertyType == typeof(int))
            {
                if (TrySetRuntimePropertyValue(target, property, state.Name, state.Value))
                    continue;
            }

            FieldInfo? field = FindField(target.GetType(), state.Name);
            if (field != null && field.FieldType == typeof(int))
                field.SetValue(target, state.Value);
        }
    }

    private static void RestoreRuntimeEnumProperties(object target, IReadOnlyList<UndoNamedEnumState> states)
    {
        foreach (UndoNamedEnumState state in states)
        {
            if (ShouldSkipRuntimeStateProperty(target.GetType(), state.Name))
                continue;

            PropertyInfo? property = FindProperty(target.GetType(), state.Name);
            if (property == null || !property.PropertyType.IsEnum)
                continue;

            object value = Enum.ToObject(property.PropertyType, state.Value);
            if (TrySetRuntimePropertyValue(target, property, state.Name, value))
                continue;

            FieldInfo? field = FindField(target.GetType(), state.Name);
            if (field != null && field.FieldType.IsEnum)
                field.SetValue(target, value);
        }
    }

    private static void RestorePotions(Player player, NetFullCombatState.PlayerState playerState, UndoPlayerPotionState? playerPotionState)
    {
        int maxPotionDelta = playerState.maxPotionCount - player.MaxPotionCount;
        if (maxPotionDelta > 0)
            player.AddToMaxPotionCount(maxPotionDelta);
        else if (maxPotionDelta < 0)
            player.SubtractFromMaxPotionCount(-maxPotionDelta);

        for (int i = 0; i < player.MaxPotionCount; i++)
        {
            PotionModel? potion = player.GetPotionAtSlotIndex(i);
            if (potion != null)
                player.DiscardPotionInternal(potion, true);
        }

        if (playerPotionState != null && playerPotionState.Slots.Count > 0)
        {
            foreach (UndoPotionSlotState slotState in playerPotionState.Slots.OrderBy(static slot => slot.SlotIndex))
            {
                if (slotState.SlotIndex < 0 || slotState.SlotIndex >= player.MaxPotionCount || slotState.Potion == null)
                    continue;

                SerializablePotion serializablePotion = ClonePacketSerializable(slotState.Potion);
                serializablePotion.SlotIndex = slotState.SlotIndex;
                player.AddPotionInternal(PotionModel.FromSerializable(serializablePotion), slotState.SlotIndex, true);
            }

            return;
        }

        for (int i = 0; i < playerState.potions.Count; i++)
        {
            player.AddPotionInternal(
                PotionModel.FromSerializable(new SerializablePotion
                {
                    Id = playerState.potions[i].id,
                    SlotIndex = i
                }),
                i,
                true);
        }
    }

    private static void RestorePlayerCombatState(
        Player player,
        RunState runState,
        CombatState combatState,
        NetFullCombatState.PlayerState playerState,
        IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardCostState>>? cardCostStatesByPile,
        IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardRuntimeState>>? cardRuntimeStatesByPile,
        UndoPlayerOrbState? playerOrbState)
    {
        PlayerCombatState playerCombatState = player.PlayerCombatState
            ?? throw new InvalidOperationException($"Player {player.NetId} has no combat state.");

        foreach (CardModel card in playerCombatState.AllCards.ToList())
        {
            card.HasBeenRemovedFromState = true;
            if (combatState.ContainsCard(card))
                combatState.RemoveCard(card);
        }

        foreach (CardPile pile in playerCombatState.AllPiles)
            pile.Clear(true);

        Dictionary<PileType, NetFullCombatState.CombatPileState> pilesByType = playerState.piles.ToDictionary(static pile => pile.pileType);
        foreach (PileType pileType in CombatPileOrder)
        {
            CardPile pile = CardPile.Get(pileType, player)
                ?? throw new InvalidOperationException($"Pile {pileType} was not available for player {player.NetId}.");

            IReadOnlyList<UndoCardCostState>? pileCardCostStates = null;
            IReadOnlyList<UndoCardRuntimeState>? pileCardRuntimeStates = null;
            cardCostStatesByPile?.TryGetValue(pileType, out pileCardCostStates);
            cardRuntimeStatesByPile?.TryGetValue(pileType, out pileCardRuntimeStates);
            if (pilesByType.TryGetValue(pileType, out NetFullCombatState.CombatPileState pileState))
            {
                for (int cardIndex = 0; cardIndex < pileState.cards.Count; cardIndex++)
                {
                    NetFullCombatState.CardState cardState = pileState.cards[cardIndex];
                    CardModel card = CardModel.FromSerializable(cardState.card);
                    combatState.AddCard(card, player);
                    RestoreCardState(
                        runState,
                        combatState,
                        card,
                        cardState,
                        pileCardCostStates != null && cardIndex < pileCardCostStates.Count ? pileCardCostStates[cardIndex] : null,
                        pileCardRuntimeStates != null && cardIndex < pileCardRuntimeStates.Count ? pileCardRuntimeStates[cardIndex] : null);
                    pile.AddInternal(card, -1, true);
                }
            }

            pile.InvokeContentsChanged();
        }

        playerCombatState.Energy = playerState.energy;
        playerCombatState.Stars = playerState.stars;
        RestoreOrbQueue(player, playerState, playerOrbState);
        playerCombatState.RecalculateCardValues();
    }

    private static void RestoreCardState(RunState runState, CombatState combatState, CardModel card, NetFullCombatState.CardState cardState, UndoCardCostState? costState, UndoCardRuntimeState? runtimeState)
    {
        HashSet<CardKeyword> desiredKeywords = cardState.keywords != null
            ? [.. cardState.keywords]
            : [];

        foreach (CardKeyword keyword in card.Keywords.ToList())
        {
            if (!desiredKeywords.Contains(keyword))
                card.RemoveKeyword(keyword);
        }

        foreach (CardKeyword keyword in desiredKeywords)
        {
            if (!card.Keywords.Contains(keyword))
                card.AddKeyword(keyword);
        }

        if (card.Affliction != null)
            card.ClearAfflictionInternal();

        if (cardState.affliction != null)
        {
            card.AfflictInternal(
                ModelDb.GetById<AfflictionModel>(cardState.affliction).ToMutable(),
                cardState.afflictionCount);
        }

        RestoreAfflictionRuntimeState(card.Affliction, runtimeState?.AfflictionState);
        RestoreCardCostState(card, costState);
        RestoreCardRuntimeState(runState, combatState, card, runtimeState);
    }

    private static IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardCostState>>? GetCardCostStatesForPlayer(UndoCombatFullState snapshotState, ulong playerNetId)
    {
        Dictionary<PileType, IReadOnlyList<UndoCardCostState>> states = snapshotState.CardCostStates
            .Where(static state => state != null)
            .Where(state => state.PlayerNetId == playerNetId)
            .ToDictionary(static state => state.PileType, static state => state.Cards);

        return states.Count == 0 ? null : states;
    }

    private static UndoPlayerOrbState? GetPlayerOrbState(UndoCombatFullState snapshotState, ulong playerNetId)
    {
        return snapshotState.PlayerOrbStates.FirstOrDefault(state => state.PlayerNetId == playerNetId);
    }

    private static UndoPlayerDeckState? GetPlayerDeckState(UndoCombatFullState snapshotState, ulong playerNetId)
    {
        return snapshotState.PlayerDeckStates.FirstOrDefault(state => state.PlayerNetId == playerNetId);
    }

    private static UndoPlayerPotionState? GetPlayerPotionState(UndoCombatFullState snapshotState, ulong playerNetId)
    {
        return snapshotState.PlayerPotionStates.FirstOrDefault(state => state.PlayerNetId == playerNetId);
    }

    private static IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardRuntimeState>>? GetCardRuntimeStatesForPlayer(UndoCombatFullState snapshotState, ulong playerNetId)
    {
        Dictionary<PileType, IReadOnlyList<UndoCardRuntimeState>> states = snapshotState.CardRuntimeStates
            .Where(static state => state != null)
            .Where(state => state.PlayerNetId == playerNetId)
            .ToDictionary(static state => state.PileType, static state => state.Cards);

        return states.Count == 0 ? null : states;
    }

    private static IReadOnlyList<UndoRelicRuntimeState>? GetRelicRuntimeStatesForPlayer(UndoCombatFullState snapshotState, ulong playerNetId)
    {
        List<UndoRelicRuntimeState> states = snapshotState.RelicRuntimeStates
            .Where(static state => state != null)
            .Where(state => state.PlayerNetId == playerNetId)
            .ToList();

        return states.Count == 0 ? null : states;
    }

    private static void RestoreCardRuntimeState(RunState runState, CombatState combatState, CardModel card, UndoCardRuntimeState? runtimeState)
    {
        if (runtimeState == null)
            return;

        card.BaseReplayCount = runtimeState.BaseReplayCount;
        if (!TrySetPrivateAutoPropertyBackingField(card, "HasSingleTurnRetain", runtimeState.HasSingleTurnRetain))
            SetPrivatePropertyValue(card, "HasSingleTurnRetain", runtimeState.HasSingleTurnRetain);
        if (!TrySetPrivateAutoPropertyBackingField(card, "HasSingleTurnSly", runtimeState.HasSingleTurnSly))
            SetPrivatePropertyValue(card, "HasSingleTurnSly", runtimeState.HasSingleTurnSly);
        card.ExhaustOnNextPlay = runtimeState.ExhaustOnNextPlay;
        card.DeckVersion = runtimeState.DeckVersionRef == null
            ? null
            : UndoStableRefs.ResolveCardRef(runState, runtimeState.DeckVersionRef);
        RestoreEnchantmentRuntimeState(card, runtimeState.EnchantmentState);

        UndoRuntimeRestoreContext context = new()
        {
            RunState = runState,
            CombatState = combatState
        };
        UndoRuntimeStateCodecRegistry.RestoreCardStates(card, runtimeState.ComplexStates, context);
    }

    internal static void RestoreChoiceOptionState(Player player, CardModel card, UndoCardCostState? costState, UndoCardRuntimeState? runtimeState)
    {
        RestoreCardCostState(card, costState);

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        CombatState? combatState = player.Creature.CombatState ?? CombatManager.Instance.DebugOnlyGetState();
        if (runState == null || combatState == null || runtimeState == null)
            return;

        RestoreAfflictionRuntimeState(card.Affliction, runtimeState.AfflictionState);
        RestoreCardRuntimeState(runState, combatState, card, runtimeState);
    }

    private static void RestoreAfflictionRuntimeState(AfflictionModel? affliction, UndoAfflictionRuntimeState? runtimeState)
    {
        if (affliction == null || runtimeState == null)
            return;

        RestoreRuntimeBoolProperties(affliction, runtimeState.BoolProperties);
        RestoreRuntimeIntProperties(affliction, runtimeState.IntProperties);
        RestoreRuntimeEnumProperties(affliction, runtimeState.EnumProperties);
    }

    private static void RestoreEnchantmentRuntimeState(CardModel card, UndoEnchantmentRuntimeState? enchantmentState)
    {
        if (enchantmentState == null)
        {
            if (card.Enchantment != null)
                card.ClearEnchantmentInternal();

            return;
        }

        if (card.Enchantment == null && enchantmentState.Serializable != null)
        {
            EnchantmentModel enchantment = EnchantmentModel.FromSerializable(ClonePacketSerializable(enchantmentState.Serializable));
            card.EnchantInternal(enchantment, enchantment.Amount);
            card.Enchantment?.ModifyCard();
        }

        if (card.Enchantment == null)
            return;

        card.Enchantment.Status = enchantmentState.Status;
        RestoreRuntimeBoolProperties(card.Enchantment, enchantmentState.BoolProperties);
        RestoreRuntimeIntProperties(card.Enchantment, enchantmentState.IntProperties);
        RestoreRuntimeEnumProperties(card.Enchantment, enchantmentState.EnumProperties);
        card.Enchantment.RecalculateValues();
        card.DynamicVars.RecalculateForUpgradeOrEnchant();
    }

    private static void RestoreCardCostState(CardModel card, UndoCardCostState? costState)
    {
        if (costState == null)
            return;

        CardEnergyCost energyCost = card.EnergyCost;
        SetPrivateFieldValue(energyCost, "_base", costState.EnergyBaseCost);
        SetPrivateFieldValue(energyCost, "_capturedXValue", costState.CapturedXValue);
        if (!TrySetPrivateAutoPropertyBackingField(energyCost, "WasJustUpgraded", costState.EnergyWasJustUpgraded))
            SetPrivatePropertyValue(energyCost, "WasJustUpgraded", costState.EnergyWasJustUpgraded);
        SetPrivateFieldValue(
            energyCost,
            "_localModifiers",
            costState.EnergyLocalModifiers
                .Select(static modifier => new LocalCostModifier(modifier.Amount, modifier.Type, modifier.Expiration, modifier.IsReduceOnly))
                .ToList());

        SetPrivateFieldValue(card, "_starCostSet", costState.StarCostSet);
        SetPrivateFieldValue(card, "_baseStarCost", costState.BaseStarCost);
        SetPrivateFieldValue(card, "_wasStarCostJustUpgraded", costState.StarWasJustUpgraded);
        SetPrivateFieldValue(
            card,
            "_temporaryStarCosts",
            costState.TemporaryStarCosts.Select(CreateTemporaryStarCost).ToList());

        card.InvokeEnergyCostChanged();
        InvokeCardStarCostChanged(card);
    }

    private static TemporaryCardCost CreateTemporaryStarCost(UndoTemporaryStarCostState costState)
    {
        if (!costState.ClearsWhenTurnEnds && !costState.ClearsWhenCardIsPlayed)
            return TemporaryCardCost.ThisCombat(costState.Cost);

        if (costState.ClearsWhenTurnEnds)
            return TemporaryCardCost.ThisTurn(costState.Cost);

        return TemporaryCardCost.UntilPlayed(costState.Cost);
    }

    private static void InvokeCardStarCostChanged(CardModel card)
    {
        if (FindField(card.GetType(), "StarCostChanged")?.GetValue(card) is Action starCostChanged)
            starCostChanged();
    }

    private static void RestoreOrbQueue(Player player, NetFullCombatState.PlayerState playerState, UndoPlayerOrbState? playerOrbState)
    {
        OrbQueue orbQueue = player.PlayerCombatState!.OrbQueue;
        orbQueue.Clear();
        int desiredCapacity = playerOrbState?.Capacity ?? Math.Max(player.BaseOrbSlotCount, playerState.orbs.Count);
        orbQueue.AddCapacity(desiredCapacity);

        for (int i = 0; i < playerState.orbs.Count; i++)
        {
            OrbModel orb = ModelDb.GetById<OrbModel>(playerState.orbs[i].id).ToMutable();
            orb.Owner = player;
            orbQueue.Insert(i, orb);
            RestoreOrbRuntimeState(orb, playerOrbState != null && i < playerOrbState.Orbs.Count ? playerOrbState.Orbs[i] : null);
        }
    }

    private static void RestoreOrbRuntimeState(OrbModel orb, UndoOrbRuntimeState? runtimeState)
    {
        if (runtimeState == null || runtimeState.OrbId != orb.Id)
            return;

        if (orb is DarkOrb && runtimeState.DarkEvokeValue is decimal darkEvokeValue)
            SetPrivateFieldValue(orb, "_evokeVal", darkEvokeValue);

        if (orb is GlassOrb && runtimeState.GlassPassiveValue is decimal glassPassiveValue)
            SetPrivateFieldValue(orb, "_passiveVal", glassPassiveValue);
    }

    private static void ResetActionExecutorForRestore(bool quarantineOutstandingActions = true, bool trackQueueCompletion = false)
    {
        ActionExecutor actionExecutor = RunManager.Instance.ActionExecutor;
        Task? queueCompletionTask = trackQueueCompletion ? actionExecutor.FinishedExecutingActions() : null;
        actionExecutor.Pause();
        actionExecutor.Cancel();
        TrackRestoreTailTask(queueCompletionTask);
        if (quarantineOutstandingActions)
            QuarantineOutstandingActionsForRestore(actionExecutor);
        UndoReflectionUtil.TrySetPropertyValue(actionExecutor, "CurrentlyRunningAction", null);
        UndoReflectionUtil.TrySetFieldValue(actionExecutor, "_actionCancelToken", null);
        UndoReflectionUtil.TrySetFieldValue(actionExecutor, "_queueTaskCompletionSource", null);
    }

    private async Task<bool> ApplyFullStateSnapshotCoreAsync(UndoCombatFullState snapshot, RunState runState, CombatState combatState)
    {
        ResetActionExecutorForRestore();
        RunManager.Instance.ActionQueueSet.Reset();
        ResetActionSynchronizerForRestore();
        RebuildActionQueues(runState.Players);
        runState.Rng.LoadFromSerializable(snapshot.FullState.Rng);

        RestorePlayers(runState, combatState, snapshot);
        RestoreCreatures(runState, combatState, snapshot);
        UndoCombatRewardRuntime.ClearLiveExtraRewards(runState);
        UndoDelayedCombatRewardService.RestoreSnapshot(snapshot.PendingCombatRewardStates);

        combatState.RoundNumber = snapshot.RoundNumber;
        combatState.CurrentSide = snapshot.CurrentSide;
        UndoCombatHistoryCodec.Restore(
            runState,
            combatState,
            snapshot.CombatHistoryState,
            new CardResolutionIndex(runState, snapshot.CombatCardDbState));
        foreach (Player player in runState.Players)
            player.PlayerCombatState?.RecalculateCardValues();

        RunManager.Instance.ActionQueueSet.FastForwardNextActionId(snapshot.NextActionId);
        RunManager.Instance.ActionQueueSynchronizer.FastForwardHookId(snapshot.NextHookId);
        RunManager.Instance.ChecksumTracker.LoadReplayChecksums(GetReplayChecksumsFrom(snapshot.NextChecksumId), snapshot.NextChecksumId);
        RunManager.Instance.PlayerChoiceSynchronizer.FastForwardChoiceIds([.. snapshot.FullState.nextChoiceIds]);

        RestoreCapabilityReport topologyReport = UndoCreatureTopologyCodecRegistry.Restore(snapshot.CreatureTopologyStates, combatState.Creatures);
        if (topologyReport.IsFailure)
        {
            _lastRestoreFailureReason = topologyReport.Detail ?? topologyReport.Result.ToString();
            _lastRestoreCapabilityReport = topologyReport;
            return false;
        }

        RestoreCapabilityReport creatureStatusReport = UndoCreatureStatusCodecRegistry.Restore(snapshot.CreatureStatusRuntimeStates, combatState.Creatures);
        if (creatureStatusReport.IsFailure)
        {
            _lastRestoreFailureReason = creatureStatusReport.Detail ?? creatureStatusReport.Result.ToString();
            _lastRestoreCapabilityReport = creatureStatusReport;
            return false;
        }

        RestoreCapabilityReport creatureReconciliationReport = UndoCreatureReconciliationCodecRegistry.Restore(snapshot.MonsterStates, combatState.Creatures);
        if (creatureReconciliationReport.IsFailure)
        {
            _lastRestoreFailureReason = creatureReconciliationReport.Detail ?? creatureReconciliationReport.Result.ToString();
            _lastRestoreCapabilityReport = creatureReconciliationReport;
            return false;
        }

        RestoreCapabilityReport actionKernelReport = UndoActionKernelService.Restore(snapshot.ActionKernelState, runState);
        _lastRestoreCapabilityReport = actionKernelReport;
        if (actionKernelReport.IsFailure)
        {
            _lastRestoreFailureReason = actionKernelReport.Detail ?? actionKernelReport.Result.ToString();
            return false;
        }

        ResetActionExecutorForRestore(quarantineOutstandingActions: false);
        ActionSynchronizerCombatState effectiveSynchronizerState = GetEffectiveSynchronizerState(snapshot);
        if (!RestoreActionSynchronizationState(effectiveSynchronizerState, snapshot.ActionKernelState.BoundaryKind, out string? reason))
        {
            _lastRestoreFailureReason = reason;
            _lastRestoreCapabilityReport = new RestoreCapabilityReport
            {
                Result = RestoreCapabilityResult.QueueStateMismatch,
                Detail = reason
            };
            return false;
        }

        RebuildTransientCombatCaches(runState, snapshot.CombatCardDbState);
        NormalizeRelicInventoryUi(runState);
        ApplyFirstInSeriesPlayCountOverrides(snapshot);

        foreach (Player player in runState.Players)
        {
            if (player.Creature.IsAlive)
                player.ActivateHooks();
            else
                player.DeactivateHooks();
        }

        await RefreshCombatUiAsync(combatState, snapshot);
        return true;
    }

    private static void ResetActionSynchronizerForRestore()
    {
        ActionQueueSynchronizer synchronizer = RunManager.Instance.ActionQueueSynchronizer;
        if (UndoReflectionUtil.TryGetFieldValue(synchronizer, "_hookActions", out System.Collections.IList? hookActions)
            && hookActions != null)
            hookActions.Clear();

        if (UndoReflectionUtil.TryGetFieldValue(synchronizer, "_requestedActionsWaitingForPlayerTurn", out System.Collections.IList? deferredActions)
            && deferredActions != null)
            deferredActions.Clear();
    }

    private static ActionSynchronizerCombatState GetEffectiveSynchronizerState(UndoCombatFullState snapshot)
    {
        if (snapshot.ActionKernelState.BoundaryKind == ActionKernelBoundaryKind.StableBoundary
            && snapshot.CurrentSide == CombatSide.Player
            && snapshot.SynchronizerCombatState == ActionSynchronizerCombatState.NotPlayPhase)
        {
            return ActionSynchronizerCombatState.PlayPhase;
        }

        return snapshot.SynchronizerCombatState;
    }

    private static bool RestoreActionSynchronizationState(
        ActionSynchronizerCombatState targetState,
        ActionKernelBoundaryKind boundaryKind,
        out string? reason)
    {
        reason = null;
        if (!TryValidateActionQueueFrontStates(boundaryKind, out reason))
            return false;

        ActionQueueSynchronizer synchronizer = RunManager.Instance.ActionQueueSynchronizer;
        ActionQueueSet actionQueueSet = RunManager.Instance.ActionQueueSet;
        if (targetState == ActionSynchronizerCombatState.PlayPhase)
        {
            synchronizer.SetCombatState(ActionSynchronizerCombatState.NotPlayPhase);
            synchronizer.SetCombatState(ActionSynchronizerCombatState.PlayPhase);
            actionQueueSet.UnpauseAllPlayerQueues();
            RunManager.Instance.ActionExecutor.Unpause();
            return true;
        }

        synchronizer.SetCombatState(ActionSynchronizerCombatState.PlayPhase);
        synchronizer.SetCombatState(targetState);
        if (targetState == ActionSynchronizerCombatState.NotPlayPhase || targetState == ActionSynchronizerCombatState.EndTurnPhaseOne)
            actionQueueSet.PauseAllPlayerQueues();

        return true;
    }

    private static bool TryValidateActionQueueFrontStates(ActionKernelBoundaryKind boundaryKind, out string? reason)
    {
        reason = null;
        bool allowGatheringPlayerChoice = boundaryKind == ActionKernelBoundaryKind.PausedChoice;
        if (!UndoReflectionUtil.TryGetFieldValue(RunManager.Instance.ActionQueueSet, "_actionQueues", out System.Collections.IEnumerable? rawQueues)
            || rawQueues == null)
            return true;

        foreach (object rawQueue in rawQueues)
        {
            if (!UndoReflectionUtil.TryGetFieldValue(rawQueue, "actions", out System.Collections.IList? actions)
                || actions == null
                || actions.Count == 0
                || actions[0] is not GameAction frontAction)
            {
                continue;
            }

            bool legalState = frontAction.State == GameActionState.WaitingForExecution
                || frontAction.State == GameActionState.ReadyToResumeExecuting
                || (allowGatheringPlayerChoice && frontAction.State == GameActionState.GatheringPlayerChoice);
            if (legalState)
                continue;

            ulong ownerId = UndoReflectionUtil.TryGetFieldValue(rawQueue, "ownerId", out ulong owner) ? owner : 0UL;
            reason = $"front_action_invalid_state:{ownerId}:{frontAction.State}:{frontAction.GetType().Name}";
            return false;
        }

        return true;
    }
    private void EnsurePlayerChoiceUndoAnchor(UndoSnapshot restoredSnapshot, UndoChoiceSpec? preferredChoiceSpec = null, bool forceRefresh = false, UndoCombatFullState? anchorCombatStateOverride = null)
    {
        int replayEventCount = GetCurrentReplayEventCount();
        UndoChoiceSpec? choiceSpec = preferredChoiceSpec ?? ResolveCurrentChoiceSpec(restoredSnapshot, replayEventCount);
        if (!IsSupportedChoiceAnchorKind(choiceSpec))
            return;
        UndoChoiceSpec activeChoiceSpec = choiceSpec!;

        GameAction? action = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        UndoSnapshot? existing = _pastSnapshots.First?.Value;
        bool canRearm = forceRefresh
            || CanRestoreResolvedChoiceBranch(existing, action)
            || (restoredSnapshot.IsChoiceAnchor && CanRestoreResolvedChoiceBranch(restoredSnapshot, action))
            || (action?.State == GameActionState.GatheringPlayerChoice
                && combatUi != null
                && IsSupportedChoiceUiActive(combatUi));
        if (!canRearm)
            return;

        if (existing != null
            && existing.IsChoiceAnchor
            && existing.ReplayEventCount == replayEventCount)
        {
            if (!forceRefresh && existing.ChoiceSpec != null)
                return;
        }

        List<UndoSnapshot> replacedAnchors = DetachLeadingEquivalentChoiceAnchors(activeChoiceSpec, restoredSnapshot.ActionLabel, replayEventCount);
        if (replacedAnchors.Count > 0)
        {
            UndoDebugLog.Write(
                $"choice_anchor_pruned replayEvents={replayEventCount}"
                + $" removed={replacedAnchors.Count}"
                + $" label={restoredSnapshot.ActionLabel}");
        }

        UndoCombatFullState anchorCombatState = BuildChoiceAnchorCombatState(
            restoredSnapshot,
            activeChoiceSpec,
            replacedAnchors,
            replayEventCount,
            anchorCombatStateOverride);
        UndoSnapshot anchor = new(
            anchorCombatState,
            replayEventCount,
            UndoActionKind.PlayerChoice,
            _nextSequenceId++,
            restoredSnapshot.ActionLabel,
            isChoiceAnchor: true,
            choiceSpec: activeChoiceSpec);

        _pastSnapshots.AddFirst(anchor);
        TrimSnapshots(_pastSnapshots);
        MainFile.Logger.Info($"Re-armed player choice undo anchor. ReplayEvents={anchor.ReplayEventCount}, UndoCount={_pastSnapshots.Count}, ChoiceKind={activeChoiceSpec.Kind} forceRefresh={forceRefresh}");
    }

    private List<UndoSnapshot> DetachLeadingEquivalentChoiceAnchors(UndoChoiceSpec choiceSpec, string actionLabel, int replayEventCount)
    {
        List<UndoSnapshot> detachedAnchors = [];
        while (_pastSnapshots.First?.Value is UndoSnapshot snapshot
               && snapshot.IsChoiceAnchor
               && snapshot.ReplayEventCount <= replayEventCount
               && IsEquivalentChoiceAnchor(snapshot, choiceSpec, actionLabel))
        {
            _pastSnapshots.RemoveFirst();
            detachedAnchors.Add(snapshot);
        }

        return detachedAnchors;
    }

    private static bool IsEquivalentChoiceAnchor(UndoSnapshot snapshot, UndoChoiceSpec choiceSpec, string actionLabel)
    {
        return snapshot.IsChoiceAnchor
            && string.Equals(snapshot.ActionLabel, actionLabel, StringComparison.Ordinal)
            && AreEquivalentChoiceSpecs(snapshot.ChoiceSpec, choiceSpec);
    }

    private static bool AreEquivalentChoiceSpecs(UndoChoiceSpec? left, UndoChoiceSpec? right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left == null || right == null)
            return false;

        if (left.Kind != right.Kind
            || left.SourcePileType != right.SourcePileType
            || left.CanSkip != right.CanSkip
            || !AreEquivalentSelectionPrefs(left.SelectionPrefs, right.SelectionPrefs)
            || !string.Equals(left.SourceModelTypeName, right.SourceModelTypeName, StringComparison.Ordinal)
            || !string.Equals(left.SourceModelId, right.SourceModelId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!Nullable.Equals(left.SourceCombatCard, right.SourceCombatCard))
            return false;

        if (UsesStableSourcePileIdentity(left) || UsesStableSourcePileIdentity(right))
            return AreEquivalentSourcePileChoiceSpecs(left, right);

        return AreEquivalentGeneratedChoiceOptions(left, right);
    }

    private static bool AreEquivalentSelectionPrefs(CardSelectorPrefs left, CardSelectorPrefs right)
    {
        return left.MinSelect == right.MinSelect
            && left.MaxSelect == right.MaxSelect
            && left.RequireManualConfirmation == right.RequireManualConfirmation
            && left.Cancelable == right.Cancelable
            && left.UnpoweredPreviews == right.UnpoweredPreviews
            && left.PretendCardsCanBePlayed == right.PretendCardsCanBePlayed
            && AreEquivalentPrompt(left.Prompt, right.Prompt);
    }

    private static bool AreEquivalentPrompt(LocString? left, LocString? right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left == null || right == null)
            return false;

        return string.Equals(left.LocTable, right.LocTable, StringComparison.Ordinal)
            && string.Equals(left.LocEntryKey, right.LocEntryKey, StringComparison.Ordinal);
    }

    private static bool UsesStableSourcePileIdentity(UndoChoiceSpec spec)
    {
        return spec.Kind == UndoChoiceKind.HandSelection
            || spec.SourcePileType != null
            || spec.SourcePileCombatCards.Count > 0
            || spec.SourcePileOptionIndexes.Count > 0;
    }

    private static bool AreEquivalentSourcePileChoiceSpecs(UndoChoiceSpec left, UndoChoiceSpec right)
    {
        if (left.SourcePileCombatCards.Count != right.SourcePileCombatCards.Count
            || left.SourcePileOptionIndexes.Count != right.SourcePileOptionIndexes.Count)
        {
            return false;
        }

        for (int i = 0; i < left.SourcePileCombatCards.Count; i++)
        {
            if (!left.SourcePileCombatCards[i].Equals(right.SourcePileCombatCards[i]))
                return false;
        }

        for (int i = 0; i < left.SourcePileOptionIndexes.Count; i++)
        {
            if (left.SourcePileOptionIndexes[i] != right.SourcePileOptionIndexes[i])
                return false;
        }

        return true;
    }

    private static bool AreEquivalentGeneratedChoiceOptions(UndoChoiceSpec left, UndoChoiceSpec right)
    {
        if (left.OptionCards.Count != right.OptionCards.Count)
            return false;

        for (int i = 0; i < left.OptionCards.Count; i++)
        {
            if (!PacketDataEquals(left.OptionCards[i], right.OptionCards[i]))
                return false;
        }

        return true;
    }

    private UndoCombatFullState BuildChoiceAnchorCombatState(
        UndoSnapshot restoredSnapshot,
        UndoChoiceSpec choiceSpec,
        IReadOnlyList<UndoSnapshot> existingAnchors,
        int replayEventCount,
        UndoCombatFullState? anchorCombatStateOverride)
    {
        UndoCombatFullState anchorCombatState = anchorCombatStateOverride ?? CaptureCurrentCombatFullState(choiceSpec);
        if (anchorCombatStateOverride != null)
            return anchorCombatState;

        Dictionary<UndoChoiceResultKey, UndoChoiceBranchState> savedBranches = new();
        RememberChoiceBranchStates(savedBranches, anchorCombatState.ChoiceBranchStates);
        foreach (UndoSnapshot existingAnchor in existingAnchors)
            RememberChoiceBranchStates(savedBranches, existingAnchor.CombatState.ChoiceBranchStates);
        RememberChoiceBranchStates(savedBranches, restoredSnapshot.CombatState.ChoiceBranchStates);

        if (restoredSnapshot.ChoiceResultKey != null)
        {
            savedBranches[restoredSnapshot.ChoiceResultKey] = CaptureChoiceBranchState(restoredSnapshot);
        }
        else if (restoredSnapshot.ActionKind == UndoActionKind.PlayerChoice
            && _lastResolvedChoiceResultKey != null
            && existingAnchors.Any(static anchor => anchor.IsChoiceAnchor)
            && restoredSnapshot.ReplayEventCount == replayEventCount)
        {
            UndoSnapshot resolvedBranchSnapshot = new(
                anchorCombatState,
                replayEventCount,
                restoredSnapshot.ActionKind,
                restoredSnapshot.SequenceId,
                restoredSnapshot.ActionLabel,
                choiceResultKey: _lastResolvedChoiceResultKey);
            savedBranches[_lastResolvedChoiceResultKey] = CaptureChoiceBranchState(resolvedBranchSnapshot);
        }

        return savedBranches.Count == 0
            ? anchorCombatState
            : WithChoiceBranchStates(anchorCombatState, [.. savedBranches.Values.OrderBy(static branch => branch.ReplayEventCount)]);
    }

    private static void RememberChoiceBranchStates(
        Dictionary<UndoChoiceResultKey, UndoChoiceBranchState> destination,
        IReadOnlyList<UndoChoiceBranchState> branchStates)
    {
        foreach (UndoChoiceBranchState branchState in branchStates)
            destination[branchState.ChoiceResultKey] = branchState;
    }

    private static bool TryValidateRestoredState(UndoCombatFullState snapshot, bool runMayBeHidden, out string reason)
    {
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (runState == null || combatState == null)
        {
            reason = "missing_runtime_state";
            return false;
        }

        if (combatState.RoundNumber != snapshot.RoundNumber)
        {
            reason = "round_mismatch";
            return false;
        }

        if (combatState.CurrentSide != snapshot.CurrentSide)
        {
            reason = "side_mismatch";
            return false;
        }

        if (RunManager.Instance.ActionQueueSynchronizer.CombatState != GetEffectiveSynchronizerState(snapshot))
        {
            reason = "synchronizer_state_mismatch";
            return false;
        }

        if (combatState.Creatures.Count != snapshot.FullState.Creatures.Count)
        {
            reason = "creature_count_mismatch";
            return false;
        }

        if (CombatManager.Instance.History.Entries.Count() != snapshot.CombatHistoryState.Entries.Count)
        {
            reason = "history_count_mismatch";
            return false;
        }

        if (!UndoDelayedCombatRewardService.HasMatchingState(snapshot.PendingCombatRewardStates))
        {
            reason = "pending_combat_reward_state_mismatch";
            return false;
        }

        NRun? currentRun = NGame.Instance?.CurrentRunNode;
        if (!runMayBeHidden && currentRun != null && !currentRun.Visible)
        {
            reason = "run_hidden";
            return false;
        }

        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom != null)
        {
            Control? encounterSlots = GetPrivateFieldValue<Control>(combatRoom, "<EncounterSlots>k__BackingField")
                ?? GetPrivateFieldValue<Control>(combatRoom, "EncounterSlots");
            foreach (Creature creature in combatState.Creatures)
            {
                if (combatRoom.GetCreatureNode(creature) == null)
                {
                    reason = $"missing_creature_node:{creature}";
                    return false;
                }
            }

            foreach (Creature creature in combatState.Enemies)
            {
                if (string.IsNullOrWhiteSpace(creature.SlotName))
                    continue;

                if (encounterSlots == null || !encounterSlots.HasNode(creature.SlotName))
                {
                    reason = $"invalid_slot:{creature.SlotName}";
                    return false;
                }
            }

            Player? me = LocalContext.GetMe(combatState);
            if (me != null)
            {
                int handCount = PileType.Hand.GetPile(me).Cards.Count;
                int holderCount = combatRoom.Ui.Hand.CardHolderContainer.GetChildCount();
                if (holderCount != handCount)
                {
                    reason = $"hand_holder_mismatch:{holderCount}:{handCount}";
                    return false;
                }
            }

            bool choiceUiActive = IsSupportedChoiceUiActive(combatRoom.Ui);
            if (snapshot.SelectionSessionState?.SupportedChoiceUiActive != true && choiceUiActive)
            {
                reason = "unexpected_choice_ui";
                return false;
            }
        }

        if (NOverlayStack.Instance != null
            && NOverlayStack.Instance.ScreenCount > 0
            && snapshot.SelectionSessionState?.OverlaySelectionActive != true)
        {
            reason = "unexpected_overlay";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static void RestoreCreatures(RunState runState, CombatState combatState, UndoCombatFullState snapshotState)
    {
        SyncCombatCreaturesToSnapshot(runState, combatState, snapshotState);

        IReadOnlyList<Creature> creatures = combatState.Creatures;
        for (int i = 0; i < snapshotState.FullState.Creatures.Count; i++)
            RestoreCreatureState(creatures[i], snapshotState.FullState.Creatures[i]);

        RestorePowerRuntimeStates(runState, combatState, snapshotState.PowerRuntimeStates, snapshotState.CombatCardDbState);
        if (snapshotState.MonsterStates.Count > 0)
        {
            Dictionary<string, UndoMonsterState> monsterStatesByKey = snapshotState.MonsterStates.ToDictionary(static state => state.CreatureKey);
            for (int j = 0; j < creatures.Count; j++)
            {
                Creature creature = creatures[j];
                MonsterModel? monster = creature.Monster;
                if (monster?.MoveStateMachine == null)
                    continue;

                if (!monsterStatesByKey.TryGetValue(BuildCreatureKey(creature, j), out UndoMonsterState? monsterState))
                    continue;

                RestoreMonsterState(monster, monsterState);
            }
        }

        foreach (Player player in runState.Players)
        {
            if (player.Creature.IsAlive)
                player.ActivateHooks();
            else
                player.DeactivateHooks();
        }
    }

    // Creature ordering must match NetFullCombatState.Creatures exactly so later
    // per-creature state restore can address the correct live creature. Pets are
    // restored as allies owned by a player, never inferred as enemies.
    private static void SyncCombatCreaturesToSnapshot(RunState runState, CombatState combatState, UndoCombatFullState snapshotState)
    {
        List<Creature> currentAllies = combatState.Allies.ToList();
        List<Creature> currentEnemies = combatState.Enemies.ToList();
        HashSet<Creature> usedAllies = [];
        HashSet<Creature> usedEnemies = [];
        List<Creature> desiredAllies = [];
        List<Creature> desiredEnemies = [];
        List<CreatureTopologyState> topologyStates = snapshotState.CreatureTopologyStates.ToList();
        List<UndoMonsterState> snapshotMonsterStates = snapshotState.MonsterStates.ToList();
        int monsterIndex = 0;

        foreach (NetFullCombatState.CreatureState creatureState in snapshotState.FullState.Creatures)
        {
            if (creatureState.playerId is ulong playerNetId)
            {
                Creature playerCreature = runState.GetPlayer(playerNetId)?.Creature
                    ?? throw new InvalidOperationException($"Could not map player creature {playerNetId}.");
                desiredAllies.Add(playerCreature);
                usedAllies.Add(playerCreature);
                continue;
            }

            CreatureTopologyState? topologyState = monsterIndex < topologyStates.Count ? topologyStates[monsterIndex] : null;
            UndoMonsterState? monsterState = monsterIndex < snapshotMonsterStates.Count ? snapshotMonsterStates[monsterIndex] : null;
            monsterIndex++;

            Creature creature = ResolveSnapshotMonsterCreature(
                runState,
                combatState,
                creatureState,
                topologyState,
                monsterState,
                currentAllies,
                currentEnemies,
                usedAllies,
                usedEnemies);

            if (topologyState?.Role == CreatureRole.Pet)
            {
                desiredAllies.Add(creature);
                usedAllies.Add(creature);
            }
            else
            {
                desiredEnemies.Add(creature);
                usedEnemies.Add(creature);
            }
        }

        SyncPlayerPetCollections(runState, desiredAllies);

        foreach (Creature ally in currentAllies)
        {
            if (usedAllies.Contains(ally) || ally.IsPlayer)
                continue;

            ally.CombatState = null;
        }

        foreach (Creature enemy in currentEnemies)
        {
            if (usedEnemies.Contains(enemy))
                continue;

            enemy.CombatState = null;
        }

        ReplaceCombatCreatureList(combatState, "_allies", desiredAllies);
        ReplaceCombatCreatureList(combatState, "_enemies", desiredEnemies);
        NotifyCombatCreaturesChanged(combatState);
    }

    private static Creature ResolveSnapshotMonsterCreature(
        RunState runState,
        CombatState combatState,
        NetFullCombatState.CreatureState creatureState,
        CreatureTopologyState? topologyState,
        UndoMonsterState? monsterState,
        IReadOnlyList<Creature> currentAllies,
        IReadOnlyList<Creature> currentEnemies,
        ISet<Creature> usedAllies,
        ISet<Creature> usedEnemies)
    {
        ModelId monsterId = creatureState.monsterId ?? topologyState?.MonsterId
            ?? throw new InvalidOperationException("Snapshot creature state had no monster id.");
        string? desiredSlot = !string.IsNullOrWhiteSpace(topologyState?.SlotName)
            ? topologyState!.SlotName
            : string.IsNullOrWhiteSpace(monsterState?.SlotName) ? null : monsterState!.SlotName;

        if (topologyState?.Role == CreatureRole.Pet)
        {
            if (topologyState.PetOwnerPlayerNetId is not ulong ownerNetId)
                throw new InvalidOperationException($"Pet topology for {monsterId.Entry} was missing an owner.");

            Player owner = runState.GetPlayer(ownerNetId)
                ?? throw new InvalidOperationException($"Could not map pet owner {ownerNetId} for {monsterId.Entry}.");
            Creature? existingPet = currentAllies.FirstOrDefault(creature =>
                !usedAllies.Contains(creature)
                && !creature.IsPlayer
                && creature.Monster?.Id == monsterId
                && creature.Side == owner.Creature.Side
                && (creature.PetOwner == null || creature.PetOwner == owner)
                && string.Equals(creature.SlotName, desiredSlot, StringComparison.Ordinal));
            existingPet ??= currentAllies.FirstOrDefault(creature =>
                !usedAllies.Contains(creature)
                && !creature.IsPlayer
                && creature.Monster?.Id == monsterId
                && creature.Side == owner.Creature.Side
                && (creature.PetOwner == null || creature.PetOwner == owner));
            existingPet ??= CreateSnapshotCreature(combatState, monsterId, owner.Creature.Side, desiredSlot);
            existingPet.SlotName = desiredSlot;
            EnsurePetOwnership(owner, existingPet);
                        return existingPet;
        }

        Creature? existingEnemy = currentEnemies.FirstOrDefault(creature =>
            !usedEnemies.Contains(creature)
            && creature.Monster?.Id == monsterId
            && string.Equals(creature.SlotName, desiredSlot, StringComparison.Ordinal));
        if (existingEnemy == null && string.IsNullOrWhiteSpace(desiredSlot))
        {
            existingEnemy = currentEnemies.FirstOrDefault(creature =>
                !usedEnemies.Contains(creature)
                && creature.Monster?.Id == monsterId);
        }
        existingEnemy ??= CreateSnapshotCreature(combatState, monsterId, CombatSide.Enemy, desiredSlot);
        existingEnemy.SlotName = desiredSlot;
        return existingEnemy;
    }

    private static Creature CreateSnapshotCreature(CombatState combatState, ModelId monsterId, CombatSide side, string? desiredSlot)
    {
        MonsterModel monster = ModelDb.GetById<MonsterModel>(monsterId).ToMutable();
        Creature creature = combatState.CreateCreature(monster, side, desiredSlot);
        monster.SetUpForCombat();
        CombatManager.Instance.StateTracker.Subscribe(creature);
        return creature;
    }

    private static void EnsurePetOwnership(Player owner, Creature pet)
    {
        PlayerCombatState playerCombatState = owner.PlayerCombatState
            ?? throw new InvalidOperationException($"Player {owner.NetId} had no combat state while restoring pet {pet.Monster?.Id.Entry}.");
        if (pet.PetOwner != null && pet.PetOwner != owner)
            throw new InvalidOperationException($"Pet {pet.Monster?.Id.Entry} was already bound to a different owner.");

        if (!playerCombatState.Pets.Contains(pet))
            playerCombatState.AddPetInternal(pet);
    }

    private static void SyncPlayerPetCollections(RunState runState, IReadOnlyList<Creature> desiredAllies)
    {
        foreach (Player player in runState.Players)
        {
            PlayerCombatState? playerCombatState = player.PlayerCombatState;
            if (playerCombatState == null)
                continue;

            List<Creature> desiredPets = desiredAllies
                .Where(creature => creature.PetOwner == player)
                .ToList();
            if (FindField(typeof(PlayerCombatState), "_pets")?.GetValue(playerCombatState) is not System.Collections.IList petList)
                continue;

            for (int i = petList.Count - 1; i >= 0; i--)
            {
                if (petList[i] is Creature pet && !desiredPets.Contains(pet))
                    petList.RemoveAt(i);
            }

            foreach (Creature pet in desiredPets)
            {
                if (!petList.Contains(pet))
                    playerCombatState.AddPetInternal(pet);
            }
        }
    }
    private static void RestorePowerRuntimeStates(
        RunState runState,
        CombatState combatState,
        IReadOnlyList<UndoPowerRuntimeState> runtimeStates,
        UndoCombatCardDbState? combatCardDbState = null)
    {
        if (runtimeStates.Count == 0)
            return;

        UndoRuntimeRestoreContext context = new()
        {
            RunState = runState,
            CombatState = combatState,
            CardResolutionIndex = new CardResolutionIndex(runState, combatCardDbState)
        };

        Dictionary<string, Creature> creaturesByKey = BuildCreatureKeyMap(combatState.Creatures);
        for (int creatureIndex = 0; creatureIndex < combatState.Creatures.Count; creatureIndex++)
        {
            Creature creature = combatState.Creatures[creatureIndex];
            string ownerCreatureKey = BuildCreatureKey(creature, creatureIndex);
            Dictionary<ModelId, int> ordinalsByPowerId = [];
            foreach (PowerModel power in creature.Powers)
            {
                int ordinal = ordinalsByPowerId.TryGetValue(power.Id, out int existingOrdinal) ? existingOrdinal : 0;
                ordinalsByPowerId[power.Id] = ordinal + 1;
                UndoPowerRuntimeState? runtimeState = runtimeStates.FirstOrDefault(state =>
                    state.OwnerCreatureKey == ownerCreatureKey
                    && state.PowerId == power.Id
                    && state.Ordinal == ordinal);
                if (runtimeState == null)
                    continue;

                power.Target = ResolveCreatureByKey(creaturesByKey, runtimeState.TargetCreatureKey);
                power.Applier = ResolveCreatureByKey(creaturesByKey, runtimeState.ApplierCreatureKey);
                RestoreRuntimeBoolProperties(power, runtimeState.BoolProperties);
                RestoreRuntimeIntProperties(power, runtimeState.IntProperties);
                RestoreRuntimeEnumProperties(power, runtimeState.EnumProperties);
                RestoreSpecialPowerRuntimeState(power, runtimeState, combatState);
                UndoRuntimeStateCodecRegistry.RestorePowerStates(power, runtimeState.ComplexStates, context);
                if (power is not SwipePower swipe)
                    continue;

                if (runtimeState.StolenCard == null)
                {
                    swipe.StolenCard = null;
                    continue;
                }

                CardModel stolenCard = CardModel.FromSerializable(ClonePacketSerializable(runtimeState.StolenCard));
                if (power.Target?.Player != null)
                    stolenCard.Owner = power.Target.Player;
                if (runtimeState.StolenCardDeckVersion != null)
                {
                    CardModel deckVersion = CardModel.FromSerializable(ClonePacketSerializable(runtimeState.StolenCardDeckVersion));
                    if (deckVersion.Owner != null)
                        UndoReflectionUtil.TrySetPropertyValue(deckVersion, nameof(deckVersion.Owner), null);
                    stolenCard.DeckVersion = deckVersion;
                }
                swipe.StolenCard = stolenCard;
            }
        }
    }
    private static Dictionary<string, Creature> BuildCreatureKeyMap(IReadOnlyList<Creature> creatures)
    {
        Dictionary<string, Creature> creaturesByKey = [];
        for (int i = 0; i < creatures.Count; i++)
            creaturesByKey[BuildCreatureKey(creatures[i], i)] = creatures[i];

        return creaturesByKey;
    }

    private static Creature? ResolveCreatureByKey(IReadOnlyDictionary<string, Creature> creaturesByKey, string? creatureKey)
    {
        if (string.IsNullOrWhiteSpace(creatureKey))
            return null;

        return creaturesByKey.TryGetValue(creatureKey, out Creature? creature) ? creature : null;
    }

    private static void ReplaceCombatCreatureList(CombatState combatState, string fieldName, IEnumerable<Creature> desiredCreatures)
    {
        if (FindField(typeof(CombatState), fieldName)?.GetValue(combatState) is not System.Collections.IList list)
            throw new InvalidOperationException($"Could not access CombatState.{fieldName}.");

        list.Clear();
        foreach (Creature creature in desiredCreatures)
            list.Add(creature);
    }

    private static void NotifyCombatCreaturesChanged(CombatState combatState)
    {
        if (FindField(typeof(CombatState), "CreaturesChanged")?.GetValue(combatState) is Action<CombatState> creaturesChanged)
            creaturesChanged(combatState);
    }

    private static void RestoreCreatureState(Creature creature, NetFullCombatState.CreatureState saved)
    {
        creature.SetMaxHpInternal(saved.maxHp);
        creature.SetCurrentHpInternal(saved.currentHp);
        if (creature.Block < saved.block)
            creature.GainBlockInternal(saved.block - creature.Block);
        else if (creature.Block > saved.block)
            creature.LoseBlockInternal(creature.Block - saved.block);
        RestoreCreaturePowers(creature, saved);
    }

    private static void RestoreMonsterState(MonsterModel monster, UndoMonsterState state)
    {
        MonsterMoveStateMachine? moveStateMachine = monster.MoveStateMachine;
        if (moveStateMachine == null)
            return;

        monster.Creature.SlotName = state.SlotName;
        if (monster is ThievingHopper thievingHopper)
            thievingHopper.IsHovering = state.IsHovering;
        if (state.StarterMoveIndex is int starterMoveIndex)
            UndoMonsterMoveStateUtil.TrySetStarterMoveIndex(monster, starterMoveIndex);

        if (monster is TwoTailedRat twoTailedRat)
        {
            if (state.TurnsUntilSummonable is int turnsUntilSummonable)
                UndoReflectionUtil.TrySetFieldValue(twoTailedRat, "_turnsUntilSummonable", turnsUntilSummonable);

            if (state.CallForBackupCount is int callForBackupCount)
                twoTailedRat.CallForBackupCount = callForBackupCount;
        }

        RestoreSummonMonsterRuntimeState(monster, moveStateMachine, state);

        UndoReflectionUtil.TrySetPropertyValue(monster, "SpawnedThisTurn", state.SpawnedThisTurn);
        UndoReflectionUtil.TrySetFieldValue(moveStateMachine, "_performedFirstMove", state.PerformedFirstMove);
        if (moveStateMachine.StateLog is List<MonsterState> stateLog)
        {
            stateLog.Clear();
            foreach (string stateId in state.StateLogIds)
            {
                if (moveStateMachine.States.TryGetValue(stateId, out MonsterState? loggedState))
                    stateLog.Add(loggedState);
            }
        }

        bool isPendingRevive = UndoMonsterMoveStateUtil.IsPendingRevive(monster.Creature);

        if ((monster.Creature.IsDead || isPendingRevive)
            && FindProperty(monster.GetType(), "DeadState")?.GetValue(monster) is MoveState deadState)
        {
            if (UndoMonsterMoveStateUtil.ShouldKeepDeadState(moveStateMachine, state, deadState))
            {
                monster.SetMoveImmediate(deadState, true);
                moveStateMachine.ForceCurrentState(deadState);
                SetPrivateFieldValue(deadState, "_performedAtLeastOnce", state.NextMovePerformedAtLeastOnce);
                NCombatRoom.Instance?.SetCreatureIsInteractable(monster.Creature, false);
                return;
            }
        }

        if (state.CurrentStateId != null && moveStateMachine.States.TryGetValue(state.CurrentStateId, out MonsterState? currentState))
            moveStateMachine.ForceCurrentState(currentState);

        // Transient stunned moves are recreated during creature reconciliation
        // because the live MoveState carries monster-specific callbacks.
        if (state.NextMoveId == MonsterModel.stunnedMoveId)
            return;

        if (state.NextMoveId != null && moveStateMachine.States.TryGetValue(state.NextMoveId, out MonsterState? nextState) && nextState is MoveState moveState)
        {
            monster.SetMoveImmediate(moveState, true);
            if (state.CurrentStateId == null)
                moveStateMachine.ForceCurrentState(moveState);
            SetPrivateFieldValue(moveState, "_performedAtLeastOnce", state.NextMovePerformedAtLeastOnce);
        }

        if (monster.Creature.IsDead || isPendingRevive)
            NCombatRoom.Instance?.SetCreatureIsInteractable(monster.Creature, false);
    }

    private static void RestoreSummonMonsterRuntimeState(MonsterModel monster, MonsterMoveStateMachine moveStateMachine, UndoMonsterState state)
    {
        if (monster is Fabricator fabricator)
        {
            MonsterModel? lastSpawned = state.FabricatorLastSpawnedMonsterId is ModelId lastSpawnedId
                ? ModelDb.GetById<MonsterModel>(lastSpawnedId)
                : null;
            UndoReflectionUtil.TrySetFieldValue(fabricator, "_lastSpawned", lastSpawned);
        }

        if (monster is LivingFog livingFog && state.LivingFogBloatAmount is int bloatAmount)
            UndoReflectionUtil.TrySetPropertyValue(livingFog, "BloatAmount", bloatAmount);

        if (monster is ToughEgg toughEgg)
        {
            if (state.ToughEggIsHatched is bool isHatched)
                UndoReflectionUtil.TrySetPropertyValue(toughEgg, "IsHatched", isHatched);

            if (state.ToughEggVisualHatched is bool visualHatched)
                UndoReflectionUtil.TrySetFieldValue(toughEgg, "_hatched", visualHatched);

            if (state.ToughEggAfterHatchedStateId is string afterHatchedStateId
                && moveStateMachine.States.TryGetValue(afterHatchedStateId, out MonsterState? afterHatchedState))
            {
                UndoReflectionUtil.TrySetPropertyValue(toughEgg, "AfterHatchedState", afterHatchedState);
            }

            if (state.ToughEggHatchPos is Vector2 hatchPos)
                UndoReflectionUtil.TrySetPropertyValue(toughEgg, "HatchPos", hatchPos);
            else
                UndoReflectionUtil.TrySetPropertyValue(toughEgg, "HatchPos", null);
        }
    }


    private static void RestoreCreaturePowers(Creature creature, NetFullCombatState.CreatureState saved)
    {
        if (FindField(typeof(Creature), "_powers")?.GetValue(creature) is not System.Collections.IList powerList)
            throw new InvalidOperationException("Could not access Creature._powers.");

        List<PowerModel> remainingCurrentPowers = creature.Powers.ToList();
        List<PowerModel> restoredPowers = [];
        foreach (NetFullCombatState.PowerState powerState in saved.powers)
        {
            PowerModel? existingPower = remainingCurrentPowers.FirstOrDefault(power => power.Id == powerState.id);
            if (existingPower != null)
            {
                remainingCurrentPowers.Remove(existingPower);
                SetPrivateFieldValue(existingPower, "_amount", powerState.amount);
                existingPower.AmountOnTurnStart = powerState.amount;
                restoredPowers.Add(existingPower);
                continue;
            }

            PowerModel power = ModelDb.GetById<PowerModel>(powerState.id).ToMutable();
            SetPrivateFieldValue(power, "_owner", creature);
            SetPrivateFieldValue(power, "_amount", powerState.amount);
            power.AmountOnTurnStart = powerState.amount;
            restoredPowers.Add(power);
        }

        powerList.Clear();
        foreach (PowerModel power in restoredPowers)
            powerList.Add(power);
    }
}

