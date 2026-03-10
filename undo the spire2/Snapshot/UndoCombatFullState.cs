// Represents the mod-owned combat save-state kernel layered above NetFullCombatState.
// It aggregates official state, history, runtime graph, action kernel, and topology supplements.
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace UndoTheSpire2;

internal sealed class UndoCombatFullState
{
    public const int CurrentSchemaVersion = 3;

    public UndoCombatFullState(
        NetFullCombatState fullState,
        int roundNumber,
        CombatSide currentSide,
        ActionSynchronizerCombatState synchronizerCombatState,
        uint nextActionId,
        uint nextHookId,
        uint nextChecksumId,
        UndoCombatHistoryState? combatHistoryState,
        ActionKernelState? actionKernelState,
        IReadOnlyList<UndoMonsterState> monsterStates,
        IReadOnlyList<UndoPlayerPileCardCostState> cardCostStates,
        IReadOnlyList<UndoPlayerPileCardRuntimeState>? cardRuntimeStates = null,
        IReadOnlyList<UndoPowerRuntimeState>? powerRuntimeStates = null,
        IReadOnlyList<UndoRelicRuntimeState>? relicRuntimeStates = null,
        UndoSelectionSessionState? selectionSessionState = null,
        IReadOnlyList<UndoFirstInSeriesPlayCountState>? firstInSeriesPlayCounts = null,
        RuntimeGraphState? runtimeGraphState = null,
        PresentationHints? presentationHints = null,
        IReadOnlyList<CreatureTopologyState>? creatureTopologyStates = null,
        IReadOnlyList<CreatureStatusRuntimeState>? creatureStatusRuntimeStates = null,
        int schemaVersion = CurrentSchemaVersion)
    {
        SchemaVersion = schemaVersion;
        FullState = fullState;
        RoundNumber = roundNumber;
        CurrentSide = currentSide;
        SynchronizerCombatState = synchronizerCombatState;
        NextActionId = nextActionId;
        NextHookId = nextHookId;
        NextChecksumId = nextChecksumId;
        CombatHistoryState = combatHistoryState ?? UndoCombatHistoryState.Empty;
        ActionKernelState = actionKernelState ?? ActionKernelState.Empty;
        MonsterStates = monsterStates;
        CreatureTopologyStates = creatureTopologyStates ?? [];
        CreatureStatusRuntimeStates = creatureStatusRuntimeStates ?? [];
        CardCostStates = cardCostStates;
        CardRuntimeStates = cardRuntimeStates ?? [];
        PowerRuntimeStates = powerRuntimeStates ?? [];
        RelicRuntimeStates = relicRuntimeStates ?? [];
        SelectionSessionState = selectionSessionState;
        FirstInSeriesPlayCounts = firstInSeriesPlayCounts ?? [];
        RuntimeGraphState = runtimeGraphState ?? new RuntimeGraphState
        {
            CardRuntimeStates = CardRuntimeStates,
            PowerRuntimeStates = PowerRuntimeStates,
            RelicRuntimeStates = RelicRuntimeStates
        };
        PresentationHints = presentationHints ?? new PresentationHints
        {
            SelectionSessionState = selectionSessionState
        };
    }

    public int SchemaVersion { get; }

    public NetFullCombatState FullState { get; }

    public int RoundNumber { get; }

    public CombatSide CurrentSide { get; }

    public ActionSynchronizerCombatState SynchronizerCombatState { get; }

    public uint NextActionId { get; }

    public uint NextHookId { get; }

    public uint NextChecksumId { get; }

    public UndoCombatHistoryState CombatHistoryState { get; }

    public ActionKernelState ActionKernelState { get; }

    public IReadOnlyList<UndoMonsterState> MonsterStates { get; }

    public IReadOnlyList<CreatureTopologyState> CreatureTopologyStates { get; }

    public IReadOnlyList<CreatureStatusRuntimeState> CreatureStatusRuntimeStates { get; }

    public IReadOnlyList<UndoPlayerPileCardCostState> CardCostStates { get; }

    public IReadOnlyList<UndoPlayerPileCardRuntimeState> CardRuntimeStates { get; }

    public IReadOnlyList<UndoPowerRuntimeState> PowerRuntimeStates { get; }

    public IReadOnlyList<UndoRelicRuntimeState> RelicRuntimeStates { get; }

    public UndoSelectionSessionState? SelectionSessionState { get; }

    public IReadOnlyList<UndoFirstInSeriesPlayCountState> FirstInSeriesPlayCounts { get; }

    public RuntimeGraphState RuntimeGraphState { get; }

    public PresentationHints PresentationHints { get; }
}

internal sealed class UndoPlayerPileCardCostState
{
    public required ulong PlayerNetId { get; init; }

    public required PileType PileType { get; init; }

    public required IReadOnlyList<UndoCardCostState> Cards { get; init; }
}



