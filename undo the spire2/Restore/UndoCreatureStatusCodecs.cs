using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;

namespace UndoTheSpire2;

// Creature status codecs persist monster runtime booleans that drive sleep,
// stun, hover, and awake logic outside topology and move-state metadata.
internal interface IUndoCreatureStatusCodec
{
    string CodecId { get; }

    bool CanHandle(MonsterModel monster);

    UndoCreatureStatusRuntimePayload? Capture(MonsterModel monster);

    bool Restore(MonsterModel monster, UndoCreatureStatusRuntimePayload state);
}

internal interface IUndoCreatureStatusCodec<TState> : IUndoCreatureStatusCodec
    where TState : UndoCreatureStatusRuntimePayload
{
    TState? CaptureTyped(MonsterModel monster);

    bool RestoreTyped(MonsterModel monster, TState state);
}

internal abstract class UndoCreatureStatusBoolCodec<TMonster> : IUndoCreatureStatusCodec<UndoBoolCreatureStatusRuntimePayload>
    where TMonster : MonsterModel
{
    public abstract string CodecId { get; }

    protected abstract string PropertyName { get; }

    public bool CanHandle(MonsterModel monster)
    {
        return monster is TMonster;
    }

    public UndoBoolCreatureStatusRuntimePayload? CaptureTyped(MonsterModel monster)
    {
        return TryReadBool(monster, PropertyName, out bool value)
            ? new UndoBoolCreatureStatusRuntimePayload
            {
                CodecId = CodecId,
                Value = value
            }
            : null;
    }

    public bool RestoreTyped(MonsterModel monster, UndoBoolCreatureStatusRuntimePayload state)
    {
        return TryWriteBool(monster, PropertyName, state.Value);
    }

    UndoCreatureStatusRuntimePayload? IUndoCreatureStatusCodec.Capture(MonsterModel monster)
    {
        return CaptureTyped(monster);
    }

    bool IUndoCreatureStatusCodec.Restore(MonsterModel monster, UndoCreatureStatusRuntimePayload state)
    {
        return state is UndoBoolCreatureStatusRuntimePayload typed && RestoreTyped(monster, typed);
    }

    private static bool TryReadBool(MonsterModel monster, string propertyName, out bool value)
    {
        value = false;
        if (UndoReflectionUtil.FindProperty(monster.GetType(), propertyName)?.GetValue(monster) is bool propertyValue)
        {
            value = propertyValue;
            return true;
        }

        string fieldName = '_' + char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        if (UndoReflectionUtil.FindField(monster.GetType(), fieldName)?.GetValue(monster) is bool fieldValue)
        {
            value = fieldValue;
            return true;
        }

        return false;
    }

    private static bool TryWriteBool(MonsterModel monster, string propertyName, bool value)
    {
        if (UndoReflectionUtil.TrySetPropertyValue(monster, propertyName, value))
            return true;

        string fieldName = '_' + char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        return UndoReflectionUtil.TrySetFieldValue(monster, fieldName, value);
    }
}

internal static class UndoCreatureStatusCodecRegistry
{
    private static readonly IReadOnlyList<IUndoCreatureStatusCodec> Codecs =
    [
        new BowlbugRockOffBalanceCodec(),
        new SlumberingBeetleAwakeCodec(),
        new LagavulinMatriarchAwakeCodec(),
        new FatGremlinAwakeCodec(),
        new SneakyGremlinAwakeCodec(),
        new CeremonialBeastStunnedCodec(),
        new CeremonialBeastInMidChargeCodec(),
        new WrigglerStartStunnedCodec(),
        new ThievingHopperHoveringCodec()
    ];

    public static HashSet<string> GetImplementedCodecIds()
    {
        return Codecs.Select(static codec => codec.CodecId).ToHashSet(StringComparer.Ordinal);
    }

    public static IReadOnlyList<CreatureStatusRuntimeState> Capture(IReadOnlyList<Creature> creatures)
    {
        List<CreatureStatusRuntimeState> states = [];
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            MonsterModel? monster = creature.Monster;
            if (monster == null)
                continue;

            string creatureKey = UndoStableRefs.BuildCreatureKey(creature, i);
            foreach (IUndoCreatureStatusCodec codec in Codecs)
            {
                if (!codec.CanHandle(monster))
                    continue;

                UndoCreatureStatusRuntimePayload? payload = codec.Capture(monster);
                if (payload == null)
                    continue;

                states.Add(new CreatureStatusRuntimeState
                {
                    CreatureKey = creatureKey,
                    CodecId = codec.CodecId,
                    Payload = payload
                });
            }
        }

        return states;
    }

    public static RestoreCapabilityReport Restore(IReadOnlyList<CreatureStatusRuntimeState> states, IReadOnlyList<Creature> creatures)
    {
        if (states.Count == 0)
            return RestoreCapabilityReport.SupportedReport();

        Dictionary<string, Creature> creaturesByKey = UndoStableRefs.BuildCreatureKeyMap(creatures);
        foreach (CreatureStatusRuntimeState state in states)
        {
            if (!creaturesByKey.TryGetValue(state.CreatureKey, out Creature? creature) || creature.Monster == null)
            {
                return new RestoreCapabilityReport
                {
                    Result = RestoreCapabilityResult.TopologyMismatch,
                    Detail = $"missing_status_creature:{state.CreatureKey}"
                };
            }

            IUndoCreatureStatusCodec? codec = Codecs.FirstOrDefault(candidate => candidate.CodecId == state.CodecId && candidate.CanHandle(creature.Monster));
            if (codec == null || state.Payload == null || !codec.Restore(creature.Monster, state.Payload))
            {
                return new RestoreCapabilityReport
                {
                    Result = RestoreCapabilityResult.TopologyMismatch,
                    Detail = $"status_codec_failed:{state.CodecId}:{state.CreatureKey}"
                };
            }
        }

        return RestoreCapabilityReport.SupportedReport();
    }

    private sealed class BowlbugRockOffBalanceCodec : UndoCreatureStatusBoolCodec<BowlbugRock>
    {
        public override string CodecId => "status:BowlbugRock.IsOffBalance";

        protected override string PropertyName => "IsOffBalance";
    }

    private sealed class SlumberingBeetleAwakeCodec : UndoCreatureStatusBoolCodec<SlumberingBeetle>
    {
        public override string CodecId => "status:SlumberingBeetle.IsAwake";

        protected override string PropertyName => "IsAwake";
    }

    private sealed class LagavulinMatriarchAwakeCodec : UndoCreatureStatusBoolCodec<LagavulinMatriarch>
    {
        public override string CodecId => "status:LagavulinMatriarch.IsAwake";

        protected override string PropertyName => "IsAwake";
    }

    private sealed class FatGremlinAwakeCodec : UndoCreatureStatusBoolCodec<FatGremlin>
    {
        public override string CodecId => "status:FatGremlin.IsAwake";

        protected override string PropertyName => "IsAwake";
    }

    private sealed class SneakyGremlinAwakeCodec : UndoCreatureStatusBoolCodec<SneakyGremlin>
    {
        public override string CodecId => "status:SneakyGremlin.IsAwake";

        protected override string PropertyName => "IsAwake";
    }

    private sealed class CeremonialBeastStunnedCodec : UndoCreatureStatusBoolCodec<CeremonialBeast>
    {
        public override string CodecId => "status:CeremonialBeast.IsStunnedByPlowRemoval";

        protected override string PropertyName => "IsStunnedByPlowRemoval";
    }

    private sealed class CeremonialBeastInMidChargeCodec : UndoCreatureStatusBoolCodec<CeremonialBeast>
    {
        public override string CodecId => "status:CeremonialBeast.InMidCharge";

        protected override string PropertyName => "InMidCharge";
    }

    private sealed class WrigglerStartStunnedCodec : UndoCreatureStatusBoolCodec<Wriggler>
    {
        public override string CodecId => "status:Wriggler.StartStunned";

        protected override string PropertyName => "StartStunned";
    }

    private sealed class ThievingHopperHoveringCodec : UndoCreatureStatusBoolCodec<ThievingHopper>
    {
        public override string CodecId => "status:ThievingHopper.IsHovering";

        protected override string PropertyName => "IsHovering";
    }
}

