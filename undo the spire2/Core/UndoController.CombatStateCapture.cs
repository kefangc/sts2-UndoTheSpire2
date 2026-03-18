// 文件说明：捕获战斗完整快照和运行时补充状态。
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

// Combat snapshot capture and runtime-state extraction.
public sealed partial class UndoController
{
    private UndoCombatFullState CaptureCurrentCombatFullState(UndoChoiceSpec? choiceSpecOverride = null)
    {
        RunState runState = RunManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Run state was not available while capturing undo snapshot.");
        CombatState combatState = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Combat state was not available while capturing undo snapshot.");
        UndoSelectionSessionState selectionSessionState = CaptureSelectionSessionState(choiceSpecOverride);
        UndoChoiceSpec? activeChoiceSpec = choiceSpecOverride ?? selectionSessionState.ChoiceSpec;

        return new UndoCombatFullState(
            CloneFullState(NetFullCombatState.FromRun(runState, null)),
            combatState.RoundNumber,
            combatState.CurrentSide,
            RunManager.Instance.ActionQueueSynchronizer.CombatState,
            RunManager.Instance.ActionQueueSet.NextActionId,
            RunManager.Instance.ActionQueueSynchronizer.NextHookId,
            RunManager.Instance.ChecksumTracker.NextId,
            UndoCombatHistoryCodec.Capture(runState, combatState),
            UndoActionKernelService.Capture(runState, activeChoiceSpec),
            CaptureMonsterStates(combatState.Creatures),
            CaptureCardCostStates(runState),
            CaptureCardRuntimeStates(runState, combatState),
            CapturePowerRuntimeStates(runState, combatState),
            CaptureRelicRuntimeStates(runState, combatState),
            selectionSessionState,
            CaptureFirstInSeriesPlayCounts(combatState),
            creatureTopologyStates: UndoCreatureTopologyCodecRegistry.Capture(combatState.Creatures),
            creatureStatusRuntimeStates: UndoCreatureStatusCodecRegistry.Capture(combatState.Creatures),
            combatCardDbState: CaptureCombatCardDbState(runState),
            playerOrbStates: CapturePlayerOrbStates(runState),
            playerDeckStates: CapturePlayerDeckStates(runState));
    }

    private static IReadOnlyList<UndoPlayerOrbState> CapturePlayerOrbStates(RunState runState)
    {
        return [.. runState.Players.Select(player => new UndoPlayerOrbState
        {
            PlayerNetId = player.NetId,
            BaseOrbSlotCount = player.BaseOrbSlotCount,
            Capacity = player.PlayerCombatState?.OrbQueue.Capacity ?? player.BaseOrbSlotCount,
            Orbs = player.PlayerCombatState == null
                ? []
                : [.. player.PlayerCombatState.OrbQueue.Orbs.Select(CaptureOrbRuntimeState)]
        })];
    }

    private static UndoOrbRuntimeState CaptureOrbRuntimeState(OrbModel orb)
    {
        return new UndoOrbRuntimeState
        {
            OrbId = orb.Id,
            DarkEvokeValue = orb is DarkOrb darkOrb && FindField(darkOrb.GetType(), "_evokeVal")?.GetValue(darkOrb) is decimal darkEvokeValue
                ? darkEvokeValue
                : null,
            GlassPassiveValue = orb is GlassOrb glassOrb && FindField(glassOrb.GetType(), "_passiveVal")?.GetValue(glassOrb) is decimal glassPassiveValue
                ? glassPassiveValue
                : null
        };
    }

    private static IReadOnlyList<UndoPlayerDeckState> CapturePlayerDeckStates(RunState runState)
    {
        return [.. runState.Players.Select(player => new UndoPlayerDeckState
        {
            PlayerNetId = player.NetId,
            Cards = [.. player.Deck.Cards.Select(card => ClonePacketSerializable(card.ToSerializable()))]
        })];
    }

    private static IReadOnlyList<UndoMonsterState> CaptureMonsterStates(IReadOnlyList<Creature> creatures)
    {
        List<UndoMonsterState> states = [];
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            MonsterModel? monster = creature.Monster;
            if (monster?.MoveStateMachine == null)
                continue;

            MonsterMoveStateMachine moveStateMachine = monster.MoveStateMachine;
            string creatureKey = BuildCreatureKey(creature, i);
            string? currentStateId = GetPrivateFieldValue<MonsterState>(moveStateMachine, "_currentState")?.Id;
            bool performedFirstMove = FindField(moveStateMachine.GetType(), "_performedFirstMove")?.GetValue(moveStateMachine) is true;
            bool nextMovePerformedAtLeastOnce = monster.NextMove != null
                && FindField(monster.NextMove.GetType(), "_performedAtLeastOnce")?.GetValue(monster.NextMove) is true;
            string? transientNextMoveFollowUpId = monster.NextMove?.Id == MonsterModel.stunnedMoveId
                ? monster.NextMove.FollowUpState?.Id ?? monster.NextMove.FollowUpStateId
                : null;
            string? specialNodeStateKey = creature.Powers.OfType<SwipePower>().Any(static power => power.StolenCard != null)
                ? "%StolenCardPos"
                : null;
            NCreatureVisuals? creatureVisuals = NCombatRoom.Instance?.GetCreatureNode(creature)?.Visuals;
            float? visualHue = creatureVisuals == null
                ? null
                : FindField(creatureVisuals.GetType(), "_hue")?.GetValue(creatureVisuals) is float hue
                    ? hue
                    : null;
            states.Add(new UndoMonsterState
            {
                CreatureKey = creatureKey,
                SlotName = string.IsNullOrWhiteSpace(creature.SlotName) ? null : creature.SlotName,
                VisualDefaultScale = creatureVisuals?.DefaultScale,
                VisualHue = visualHue,
                CurrentStateId = currentStateId,
                NextMoveId = monster.NextMove?.Id,
                IsHovering = monster is ThievingHopper thievingHopper && thievingHopper.IsHovering,
                SpawnedThisTurn = monster.SpawnedThisTurn,
                PerformedFirstMove = performedFirstMove,
                NextMovePerformedAtLeastOnce = nextMovePerformedAtLeastOnce,
                TransientNextMoveFollowUpId = transientNextMoveFollowUpId,
                SpecialNodeStateKey = specialNodeStateKey,
                StarterMoveIndex = UndoMonsterMoveStateUtil.TryGetStarterMoveIndex(monster, out int starterMoveIndex)
                    ? starterMoveIndex
                    : null,
                TurnsUntilSummonable = monster is TwoTailedRat
                    && FindField(monster.GetType(), "_turnsUntilSummonable")?.GetValue(monster) is int turnsUntilSummonable
                    ? turnsUntilSummonable
                    : null,
                CallForBackupCount = monster is TwoTailedRat twoTailedRatWithBackup ? twoTailedRatWithBackup.CallForBackupCount : null,
                StateLogIds = [.. moveStateMachine.StateLog.Select(static state => state.Id)]
            });
        }

        return states;
    }

    private static string BuildCreatureKey(Creature creature, int index)
    {
        if (creature.Player != null)
            return $"player:{index}:{creature.Player.NetId}";

        if (creature.PetOwner != null && creature.Monster != null)
            return $"pet:{index}:{creature.PetOwner.NetId}:{creature.Monster.Id.Entry}";

        if (creature.Monster != null)
            return $"monster:{index}:{creature.Monster.Id.Entry}";

        return $"creature:{index}";
    }

    private static string? TryResolveCreatureKey(IReadOnlyList<Creature> creatures, Creature? target)
    {
        if (target == null)
            return null;

        for (int i = 0; i < creatures.Count; i++)
        {
            if (ReferenceEquals(creatures[i], target))
                return BuildCreatureKey(target, i);
        }

        if (target.PetOwner != null && target.Monster != null)
        {
            for (int i = 0; i < creatures.Count; i++)
            {
                Creature candidate = creatures[i];
                if (candidate.PetOwner?.NetId != target.PetOwner.NetId)
                    continue;

                if (candidate.Monster?.Id != target.Monster.Id)
                    continue;

                return BuildCreatureKey(candidate, i);
            }
        }

        if (target.Player != null)
        {
            for (int i = 0; i < creatures.Count; i++)
            {
                if (creatures[i].Player?.NetId == target.Player.NetId)
                    return BuildCreatureKey(creatures[i], i);
            }
        }

        if (target.Monster != null)
        {
            for (int i = 0; i < creatures.Count; i++)
            {
                Creature candidate = creatures[i];
                if (candidate.Monster?.Id == target.Monster.Id)
                    return BuildCreatureKey(candidate, i);
            }
        }

        return null;
    }

    private static IReadOnlyList<UndoPlayerPileCardCostState> CaptureCardCostStates(RunState runState)
    {
        List<UndoPlayerPileCardCostState> states = [];
        foreach (Player player in runState.Players)
        {
            foreach (PileType pileType in CombatPileOrder)
            {
                CardPile? pile = CardPile.Get(pileType, player);
                if (pile == null)
                    continue;

                states.Add(new UndoPlayerPileCardCostState
                {
                    PlayerNetId = player.NetId,
                    PileType = pileType,
                    Cards = [.. pile.Cards.Select(CaptureCardCostState)]
                });
            }
        }

        return states;
    }

    private static IReadOnlyList<UndoPlayerPileCardRuntimeState> CaptureCardRuntimeStates(RunState runState, CombatState combatState)
    {
        UndoRuntimeCaptureContext context = new()
        {
            RunState = runState,
            CombatState = combatState
        };

        List<UndoPlayerPileCardRuntimeState> states = [];
        foreach (Player player in runState.Players)
        {
            foreach (PileType pileType in CombatPileOrder)
            {
                CardPile? pile = CardPile.Get(pileType, player);
                if (pile == null)
                    continue;

                states.Add(new UndoPlayerPileCardRuntimeState
                {
                    PlayerNetId = player.NetId,
                    PileType = pileType,
                    Cards = [.. pile.Cards.Select(card => CaptureCardRuntimeState(card, context))]
                });
            }
        }

        return states;
    }

    private static UndoCardRuntimeState CaptureCardRuntimeState(CardModel card, UndoRuntimeCaptureContext context)
    {
        return CreateCardRuntimeState(
            card,
            CaptureEnchantmentRuntimeState(card.Enchantment),
            UndoRuntimeStateCodecRegistry.CaptureCardStates(card, context));
    }

    private static UndoCardRuntimeState CaptureDefaultCardRuntimeState(CardModel card)
    {
        return CreateCardRuntimeState(card, CaptureEnchantmentRuntimeState(card.Enchantment), []);
    }

    private static UndoCardRuntimeState CreateCardRuntimeState(
        CardModel card,
        UndoEnchantmentRuntimeState? enchantmentState,
        IReadOnlyList<UndoComplexRuntimeState> complexStates)
    {
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        return new UndoCardRuntimeState
        {
            BaseReplayCount = card.BaseReplayCount,
            HasSingleTurnRetain = FindProperty(card.GetType(), "HasSingleTurnRetain")?.GetValue(card) is bool retain && retain,
            HasSingleTurnSly = FindProperty(card.GetType(), "HasSingleTurnSly")?.GetValue(card) is bool sly && sly,
            ExhaustOnNextPlay = card.ExhaustOnNextPlay,
            DeckVersionRef = runState != null && card.DeckVersion != null
                ? UndoStableRefs.CaptureCardRef(runState, card.DeckVersion)
                : null,
            EnchantmentState = enchantmentState,
            ComplexStates = complexStates
        };
    }
    private static UndoEnchantmentRuntimeState? CaptureEnchantmentRuntimeState(EnchantmentModel? enchantment)
    {
        if (enchantment == null)
            return null;

        return new UndoEnchantmentRuntimeState
        {
            Serializable = ClonePacketSerializable(enchantment.ToSerializable()),
            Status = enchantment.Status,
            BoolProperties = CaptureRuntimeBoolProperties(enchantment),
            IntProperties = CaptureRuntimeIntProperties(enchantment, "Amount"),
            EnumProperties = CaptureRuntimeEnumProperties(enchantment)
        };
    }

    private static IReadOnlyList<UndoPowerRuntimeState> CapturePowerRuntimeStates(RunState runState, CombatState combatState)
    {
        UndoRuntimeCaptureContext context = new()
        {
            RunState = runState,
            CombatState = combatState
        };

        List<UndoPowerRuntimeState> states = [];
        IReadOnlyList<Creature> creatures = combatState.Creatures;
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            string ownerCreatureKey = BuildCreatureKey(creature, i);
            Dictionary<ModelId, int> ordinalsByPowerId = [];
            foreach (PowerModel power in creature.Powers)
            {
                int ordinal = ordinalsByPowerId.TryGetValue(power.Id, out int existingOrdinal) ? existingOrdinal : 0;
                ordinalsByPowerId[power.Id] = ordinal + 1;
                states.Add(new UndoPowerRuntimeState
                {
                    OwnerCreatureKey = ownerCreatureKey,
                    PowerId = power.Id,
                    Ordinal = ordinal,
                    TargetCreatureKey = TryResolveCreatureKey(creatures, power.Target),
                    ApplierCreatureKey = TryResolveCreatureKey(creatures, power.Applier),
                    StolenCard = power is SwipePower swipe && swipe.StolenCard != null
                        ? ClonePacketSerializable(swipe.StolenCard.ToSerializable())
                        : null,
                    StolenCardDeckVersion = power is SwipePower swipeWithDeckVersion
                        && swipeWithDeckVersion.StolenCard?.DeckVersion != null
                            ? ClonePacketSerializable(swipeWithDeckVersion.StolenCard.DeckVersion.ToSerializable())
                            : null,
                    TriggeredPlayerNetIds = CaptureTriggeredPlayerNetIds(power),
                    BoolProperties = CapturePowerRuntimeBoolProperties(power),
                    IntProperties = CaptureRuntimeIntProperties(power, "Amount"),
                    EnumProperties = CaptureRuntimeEnumProperties(power),
                    ComplexStates = UndoRuntimeStateCodecRegistry.CapturePowerStates(power, context)
                });
            }
        }

        return states;
    }
    private static IReadOnlyList<UndoRelicRuntimeState> CaptureRelicRuntimeStates(RunState runState, CombatState combatState)
    {
        UndoRuntimeCaptureContext context = new()
        {
            RunState = runState,
            CombatState = combatState
        };

        List<UndoRelicRuntimeState> states = [];
        foreach (Player player in runState.Players)
        {
            Dictionary<ModelId, int> ordinalsByRelicId = [];
            foreach (RelicModel relic in player.Relics)
            {
                int ordinal = ordinalsByRelicId.TryGetValue(relic.Id, out int existingOrdinal) ? existingOrdinal : 0;
                ordinalsByRelicId[relic.Id] = ordinal + 1;
                states.Add(new UndoRelicRuntimeState
                {
                    PlayerNetId = player.NetId,
                    RelicId = relic.Id,
                    Ordinal = ordinal,
                    Status = relic.Status,
                    IsActivating = FindProperty(relic.GetType(), "IsActivating")?.GetValue(relic) is bool activating ? activating : null,
                    BoolProperties = CaptureRuntimeBoolProperties(relic, "IsActivating"),
                    IntProperties = CaptureRuntimeIntProperties(relic),
                    EnumProperties = CaptureRuntimeEnumProperties(relic),
                    ComplexStates = UndoRuntimeStateCodecRegistry.CaptureRelicStates(relic, context)
                });
            }
        }

        return states;
    }
    private static UndoSelectionSessionState CaptureSelectionSessionState(UndoChoiceSpec? choiceSpecOverride = null)
    {
        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        object? overlay = NOverlayStack.Instance?.Peek();
        return new UndoSelectionSessionState
        {
            HandSelectionActive = combatUi?.Hand?.IsInCardSelection == true,
            OverlaySelectionActive = overlay is NChooseACardSelectionScreen or NCardGridSelectionScreen,
            SupportedChoiceUiActive = combatUi != null && IsSupportedChoiceUiActive(combatUi),
            OverlayScreenType = overlay?.GetType().Name,
            ChoiceSpec = choiceSpecOverride ?? TryCaptureCurrentChoiceSpecFromUi()
        };
    }
    private IReadOnlyList<UndoFirstInSeriesPlayCountState> CaptureFirstInSeriesPlayCounts(CombatState combatState)
    {
        if (_hasFirstInSeriesPlayCountOverride
            && combatState.RoundNumber == _firstInSeriesPlayCountOverrideRound
            && combatState.CurrentSide == _firstInSeriesPlayCountOverrideSide)
        {
            return _firstInSeriesPlayCountOverrides
                .Select(static pair => new UndoFirstInSeriesPlayCountState
                {
                    CreatureKey = pair.Key,
                    Count = pair.Value
                })
                .ToList();
        }

        return CaptureFirstInSeriesPlayCountsFromHistory(combatState);
    }

    private static ActionKernelState CaptureActionKernelState()
    {
        RunState runState = RunManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Run state was not available while capturing action kernel state.");
        return UndoActionKernelService.Capture(runState, TryCaptureCurrentChoiceSpecFromUi());
    }

    private static IReadOnlyList<UndoFirstInSeriesPlayCountState> CaptureFirstInSeriesPlayCountsFromHistory(CombatState combatState)
    {
        List<UndoFirstInSeriesPlayCountState> states = [];
        IReadOnlyList<Creature> creatures = combatState.Creatures;
        foreach (CardPlayStartedEntry entry in CombatManager.Instance.History.CardPlaysStarted)
        {
            if (!entry.HappenedThisTurn(combatState) || !entry.CardPlay.IsFirstInSeries)
                continue;

            string? creatureKey = TryResolveCreatureKey(creatures, entry.Actor);
            if (string.IsNullOrWhiteSpace(creatureKey))
                continue;

            for (int i = 0; i < states.Count; i++)
            {
                if (states[i].CreatureKey != creatureKey)
                    continue;

                states[i] = new UndoFirstInSeriesPlayCountState
                {
                    CreatureKey = creatureKey,
                    Count = states[i].Count + 1
                };
                creatureKey = null;
                break;
            }

            if (!string.IsNullOrWhiteSpace(creatureKey))
            {
                states.Add(new UndoFirstInSeriesPlayCountState
                {
                    CreatureKey = creatureKey,
                    Count = 1
                });
            }
        }

        return states;
    }

    private static IReadOnlyList<ulong> CaptureTriggeredPlayerNetIds(PowerModel power)
    {
        if (power is not VitalSparkPower)
            return [];

        if (FindField(typeof(PowerModel), "_internalData")?.GetValue(power) is not { } internalData)
            return [];

        if (FindField(internalData.GetType(), "playersTriggeredThisTurn")?.GetValue(internalData) is not System.Collections.IEnumerable triggeredPlayers)
            return [];

        List<ulong> playerNetIds = [];
        foreach (object? entry in triggeredPlayers)
        {
            if (entry is Player player)
                playerNetIds.Add(player.NetId);
        }

        return playerNetIds;
    }

    private static IReadOnlyList<UndoNamedBoolState> CapturePowerRuntimeBoolProperties(PowerModel power)
    {
        List<UndoNamedBoolState> states = [.. CaptureRuntimeBoolProperties(power, "Target", "Applier")];
        PropertyInfo? isRevivingProperty = FindProperty(power.GetType(), "IsReviving");
        if (isRevivingProperty?.PropertyType == typeof(bool)
            && states.All(static state => state.Name != "IsReviving"))
        {
            states.Add(new UndoNamedBoolState
            {
                Name = "IsReviving",
                Value = isRevivingProperty.GetValue(power) is bool isReviving && isReviving
            });
        }

        return states;
    }

    private static IReadOnlyList<UndoNamedBoolState> CaptureRuntimeBoolProperties(object model, params string[] excludedNames)
    {
        HashSet<string> excluded = [.. excludedNames];
        return GetRuntimeStateProperties(model.GetType())
            .Where(property => property.PropertyType == typeof(bool) && !ShouldSkipRuntimeStateProperty(model.GetType(), property.Name, excluded))
            .Select(property => new UndoNamedBoolState
            {
                Name = property.Name,
                Value = property.GetValue(model) is bool value && value
            })
            .ToList();
    }

    private static IReadOnlyList<UndoNamedIntState> CaptureRuntimeIntProperties(object model, params string[] excludedNames)
    {
        HashSet<string> excluded = [.. excludedNames];
        return GetRuntimeStateProperties(model.GetType())
            .Where(property => property.PropertyType == typeof(int) && !ShouldSkipRuntimeStateProperty(model.GetType(), property.Name, excluded))
            .Select(property => new UndoNamedIntState
            {
                Name = property.Name,
                Value = property.GetValue(model) is int value ? value : 0
            })
            .ToList();
    }

    private static IReadOnlyList<UndoNamedEnumState> CaptureRuntimeEnumProperties(object model, params string[] excludedNames)
    {
        HashSet<string> excluded = [.. excludedNames];
        return GetRuntimeStateProperties(model.GetType())
            .Where(property => property.PropertyType.IsEnum && !ShouldSkipRuntimeStateProperty(model.GetType(), property.Name, excluded))
            .Select(property => new UndoNamedEnumState
            {
                Name = property.Name,
                EnumTypeName = property.PropertyType.AssemblyQualifiedName ?? property.PropertyType.FullName ?? property.PropertyType.Name,
                Value = Convert.ToInt32(property.GetValue(model))
            })
            .ToList();
    }

    private static bool ShouldSkipRuntimeStateProperty(Type type, string propertyName, IReadOnlySet<string>? excludedNames = null)
    {
        if (excludedNames?.Contains(propertyName) == true)
            return true;

        if (propertyName.StartsWith("Test", StringComparison.Ordinal))
            return true;

        return type == typeof(ConfusedPower) && propertyName == "TestEnergyCostOverride";
    }

    private static IEnumerable<PropertyInfo> GetRuntimeStateProperties(Type type)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return type
            .GetProperties(Flags)
            .Where(static property => property.GetIndexParameters().Length == 0)
            .Where(static property => property.CanRead)
            .Where(property => property.GetMethod != null)
            .Where(property => property.SetMethod != null || FindField(type, $"<{property.Name}>k__BackingField") != null)
            .Where(static property => property.Name != "Status");
    }
    private static UndoCardCostState CaptureCardCostState(CardModel card)
    {
        CardEnergyCost energyCost = card.EnergyCost;
        List<LocalCostModifier> localModifiers = GetPrivateFieldValue<List<LocalCostModifier>>(energyCost, "_localModifiers") ?? [];
        List<TemporaryCardCost> temporaryStarCosts = GetPrivateFieldValue<List<TemporaryCardCost>>(card, "_temporaryStarCosts") ?? [];

        return new UndoCardCostState
        {
            EnergyBaseCost = FindField(energyCost.GetType(), "_base")?.GetValue(energyCost) as int? ?? energyCost.Canonical,
            CapturedXValue = FindField(energyCost.GetType(), "_capturedXValue")?.GetValue(energyCost) as int? ?? 0,
            EnergyWasJustUpgraded = FindProperty(energyCost.GetType(), "WasJustUpgraded")?.GetValue(energyCost) as bool? ?? false,
            EnergyLocalModifiers =
            [
                .. localModifiers.Select(static modifier => new UndoLocalCostModifierState
                {
                    Amount = modifier.Amount,
                    Type = modifier.Type,
                    Expiration = modifier.Expiration,
                    IsReduceOnly = modifier.IsReduceOnly
                })
            ],
            StarCostSet = FindField(card.GetType(), "_starCostSet")?.GetValue(card) as bool? ?? false,
            BaseStarCost = FindField(card.GetType(), "_baseStarCost")?.GetValue(card) as int? ?? 0,
            StarWasJustUpgraded = FindField(card.GetType(), "_wasStarCostJustUpgraded")?.GetValue(card) as bool? ?? false,
            TemporaryStarCosts =
            [
                .. temporaryStarCosts.Select(static cost => new UndoTemporaryStarCostState
                {
                    Cost = cost.Cost,
                    ClearsWhenTurnEnds = cost.ClearsWhenTurnEnds,
                    ClearsWhenCardIsPlayed = cost.ClearsWhenCardIsPlayed
                })
            ]
        };
    }
}

