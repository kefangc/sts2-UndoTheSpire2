using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace UndoTheSpire2;

public sealed class UndoCombatFullState
{
    public UndoCombatFullState(
        NetFullCombatState fullState,
        int roundNumber,
        CombatSide currentSide,
        ActionSynchronizerCombatState synchronizerCombatState,
        uint nextActionId,
        uint nextHookId,
        uint nextChecksumId)
    {
        FullState = fullState;
        RoundNumber = roundNumber;
        CurrentSide = currentSide;
        SynchronizerCombatState = synchronizerCombatState;
        NextActionId = nextActionId;
        NextHookId = nextHookId;
        NextChecksumId = nextChecksumId;
    }

    public NetFullCombatState FullState { get; }

    public int RoundNumber { get; }

    public CombatSide CurrentSide { get; }

    public ActionSynchronizerCombatState SynchronizerCombatState { get; }

    public uint NextActionId { get; }

    public uint NextHookId { get; }

    public uint NextChecksumId { get; }
}
