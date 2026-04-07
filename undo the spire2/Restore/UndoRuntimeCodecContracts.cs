using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace UndoTheSpire2;

internal sealed class UndoRuntimeCaptureContext
{
    public required RunState RunState { get; init; }

    public required CombatState CombatState { get; init; }
}

internal sealed class UndoRuntimeRestoreContext
{
    public required RunState RunState { get; init; }

    public required CombatState CombatState { get; init; }

    public CardResolutionIndex? CardResolutionIndex { get; init; }
}

internal interface IUndoRuntimeCodec<TLive, TState>
{
    string CodecId { get; }

    bool CanHandle(TLive live);

    TState? Capture(TLive live, UndoRuntimeCaptureContext context);

    void Restore(TLive live, TState state, UndoRuntimeRestoreContext context);
}

internal interface IUndoCardRuntimeCodec
{
    string CodecId { get; }

    bool CanHandle(CardModel card);

    UndoComplexRuntimeState? Capture(CardModel card, UndoRuntimeCaptureContext context);

    void Restore(CardModel card, UndoComplexRuntimeState state, UndoRuntimeRestoreContext context);
}

internal interface IUndoPowerRuntimeCodec
{
    string CodecId { get; }

    bool CanHandle(PowerModel power);

    UndoComplexRuntimeState? Capture(PowerModel power, UndoRuntimeCaptureContext context);

    void Restore(PowerModel power, UndoComplexRuntimeState state, UndoRuntimeRestoreContext context);
}

internal interface IUndoRelicRuntimeCodec
{
    string CodecId { get; }

    bool CanHandle(RelicModel relic);

    UndoComplexRuntimeState? Capture(RelicModel relic, UndoRuntimeCaptureContext context);

    void Restore(RelicModel relic, UndoComplexRuntimeState state, UndoRuntimeRestoreContext context);
}

internal abstract class UndoCardRuntimeCodec<TState> : IUndoCardRuntimeCodec, IUndoRuntimeCodec<CardModel, TState>
    where TState : UndoComplexRuntimeState
{
    public abstract string CodecId { get; }

    public abstract bool CanHandle(CardModel card);

    public abstract TState? Capture(CardModel card, UndoRuntimeCaptureContext context);

    public abstract void Restore(CardModel card, TState state, UndoRuntimeRestoreContext context);

    UndoComplexRuntimeState? IUndoCardRuntimeCodec.Capture(CardModel card, UndoRuntimeCaptureContext context)
    {
        return Capture(card, context);
    }

    void IUndoCardRuntimeCodec.Restore(CardModel card, UndoComplexRuntimeState state, UndoRuntimeRestoreContext context)
    {
        if (state is TState typed)
            Restore(card, typed, context);
    }
}

internal abstract class UndoPowerRuntimeCodec<TState> : IUndoPowerRuntimeCodec, IUndoRuntimeCodec<PowerModel, TState>
    where TState : UndoComplexRuntimeState
{
    public abstract string CodecId { get; }

    public abstract bool CanHandle(PowerModel power);

    public abstract TState? Capture(PowerModel power, UndoRuntimeCaptureContext context);

    public abstract void Restore(PowerModel power, TState state, UndoRuntimeRestoreContext context);

    UndoComplexRuntimeState? IUndoPowerRuntimeCodec.Capture(PowerModel power, UndoRuntimeCaptureContext context)
    {
        return Capture(power, context);
    }

    void IUndoPowerRuntimeCodec.Restore(PowerModel power, UndoComplexRuntimeState state, UndoRuntimeRestoreContext context)
    {
        if (state is TState typed)
            Restore(power, typed, context);
    }
}

internal abstract class UndoRelicRuntimeCodec<TState> : IUndoRelicRuntimeCodec, IUndoRuntimeCodec<RelicModel, TState>
    where TState : UndoComplexRuntimeState
{
    public abstract string CodecId { get; }

    public abstract bool CanHandle(RelicModel relic);

    public abstract TState? Capture(RelicModel relic, UndoRuntimeCaptureContext context);

    public abstract void Restore(RelicModel relic, TState state, UndoRuntimeRestoreContext context);

    UndoComplexRuntimeState? IUndoRelicRuntimeCodec.Capture(RelicModel relic, UndoRuntimeCaptureContext context)
    {
        return Capture(relic, context);
    }

    void IUndoRelicRuntimeCodec.Restore(RelicModel relic, UndoComplexRuntimeState state, UndoRuntimeRestoreContext context)
    {
        if (state is TState typed)
            Restore(relic, typed, context);
    }
}
