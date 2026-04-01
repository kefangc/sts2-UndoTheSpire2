using MegaCrit.Sts2.Core.Entities.Cards;

namespace UndoTheSpire2;

internal static class UndoSharedConstants
{
    public static readonly IReadOnlyList<PileType> CombatPileOrder =
    [
        PileType.Hand,
        PileType.Draw,
        PileType.Discard,
        PileType.Exhaust,
        PileType.Play
    ];
}
