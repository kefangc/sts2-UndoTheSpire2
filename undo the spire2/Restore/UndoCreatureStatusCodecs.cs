// 文件说明：捕获和恢复 creature 的状态型运行时数据。
using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;

namespace UndoTheSpire2;

// Creature status codecs persist monster runtime fields that drive branching,
// form progression, and visuals outside topology and move-state metadata.
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

internal abstract class UndoCreatureStatusIntCodec<TMonster> : IUndoCreatureStatusCodec<UndoIntCreatureStatusRuntimePayload>
    where TMonster : MonsterModel
{
    public abstract string CodecId { get; }

    protected abstract string PropertyName { get; }

    public bool CanHandle(MonsterModel monster)
    {
        return monster is TMonster;
    }

    public UndoIntCreatureStatusRuntimePayload? CaptureTyped(MonsterModel monster)
    {
        return TryReadInt(monster, PropertyName, out int value)
            ? new UndoIntCreatureStatusRuntimePayload
            {
                CodecId = CodecId,
                Value = value
            }
            : null;
    }

    public bool RestoreTyped(MonsterModel monster, UndoIntCreatureStatusRuntimePayload state)
    {
        return TryWriteInt(monster, PropertyName, state.Value);
    }

    UndoCreatureStatusRuntimePayload? IUndoCreatureStatusCodec.Capture(MonsterModel monster)
    {
        return CaptureTyped(monster);
    }

    bool IUndoCreatureStatusCodec.Restore(MonsterModel monster, UndoCreatureStatusRuntimePayload state)
    {
        return state is UndoIntCreatureStatusRuntimePayload typed && RestoreTyped(monster, typed);
    }

    private static bool TryReadInt(MonsterModel monster, string propertyName, out int value)
    {
        value = 0;
        if (UndoReflectionUtil.FindProperty(monster.GetType(), propertyName)?.GetValue(monster) is int propertyValue)
        {
            value = propertyValue;
            return true;
        }

        string fieldName = '_' + char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        if (UndoReflectionUtil.FindField(monster.GetType(), fieldName)?.GetValue(monster) is int fieldValue)
        {
            value = fieldValue;
            return true;
        }

        return false;
    }

    private static bool TryWriteInt(MonsterModel monster, string propertyName, int value)
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
        new CorpseSlugRavenousCodec(),
        new KnowledgeDemonCurseCounterCodec(),
        new KnowledgeDemonBurntCodec(),
        new ThievingHopperHoveringCodec(),
        new OwlMagistrateFlyingCodec(),
        new QueenHasAmalgamDiedCodec(),
        new CrossbowRubyRaiderReloadedCodec(),
        new FabricatorCanFabricateCodec(),
        new FrogKnightHasBeetleChargedCodec(),
        new FuzzyWurmCrawlerPuffedCodec(),
        new GasBombHasExplodedCodec(),
        new KinPriestSpeechUsedCodec(),
        new MechaKnightWoundUpCodec(),
        new OvicopterCanLayCodec(),
        new SnappingJaxfruitChargedCodec(),
        new TheInsatiableHasLiquifiedCodec(),
        new TheObscuraHasSummonedCodec(),
        new TestSubjectExtraMultiClawCountCodec(),
        new TestSubjectRespawnsCodec(),
        new TwoTailedRatTurnsUntilSummonableCodec(),
        new TwoTailedRatCallForBackupCountCodec(),
        new CreatureShowsInfiniteHpCodec(),
        new WaterfallGiantCurrentPressureGunDamageCodec(),
        new WaterfallGiantSteamEruptionDamageCodec(),
        new WaterfallGiantAboutToBlowCodec(),
        new WaterfallGiantPressureBuildupIdxCodec()
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

    private sealed class CorpseSlugRavenousCodec : UndoCreatureStatusBoolCodec<CorpseSlug>
    {
        public override string CodecId => "status:CorpseSlug.IsRavenous";

        protected override string PropertyName => "IsRavenous";
    }

    private sealed class KnowledgeDemonCurseCounterCodec : UndoCreatureStatusIntCodec<KnowledgeDemon>
    {
        public override string CodecId => "status:KnowledgeDemon.CurseOfKnowledgeCounter";

        protected override string PropertyName => "CurseOfKnowledgeCounter";
    }

    private sealed class KnowledgeDemonBurntCodec : UndoCreatureStatusBoolCodec<KnowledgeDemon>
    {
        public override string CodecId => "status:KnowledgeDemon.IsBurnt";

        protected override string PropertyName => "IsBurnt";
    }

    private sealed class ThievingHopperHoveringCodec : UndoCreatureStatusBoolCodec<ThievingHopper>
    {
        public override string CodecId => "status:ThievingHopper.IsHovering";

        protected override string PropertyName => "IsHovering";
    }

    private sealed class OwlMagistrateFlyingCodec : UndoCreatureStatusBoolCodec<OwlMagistrate>
    {
        public override string CodecId => "status:OwlMagistrate.IsFlying";

        protected override string PropertyName => "IsFlying";
    }

    private sealed class QueenHasAmalgamDiedCodec : UndoCreatureStatusBoolCodec<Queen>
    {
        public override string CodecId => "status:Queen.HasAmalgamDied";

        protected override string PropertyName => "HasAmalgamDied";
    }

    private sealed class CrossbowRubyRaiderReloadedCodec : UndoCreatureStatusBoolCodec<CrossbowRubyRaider>
    {
        public override string CodecId => "status:CrossbowRubyRaider.IsCrossbowReloaded";

        protected override string PropertyName => "IsCrossbowReloaded";
    }

    private sealed class FabricatorCanFabricateCodec : UndoCreatureStatusBoolCodec<Fabricator>
    {
        public override string CodecId => "status:Fabricator.CanFabricate";

        protected override string PropertyName => "CanFabricate";
    }

    private sealed class FrogKnightHasBeetleChargedCodec : UndoCreatureStatusBoolCodec<FrogKnight>
    {
        public override string CodecId => "status:FrogKnight.HasBeetleCharged";

        protected override string PropertyName => "HasBeetleCharged";
    }

    private sealed class FuzzyWurmCrawlerPuffedCodec : UndoCreatureStatusBoolCodec<FuzzyWurmCrawler>
    {
        public override string CodecId => "status:FuzzyWurmCrawler.IsPuffed";

        protected override string PropertyName => "IsPuffed";
    }

    private sealed class GasBombHasExplodedCodec : UndoCreatureStatusBoolCodec<GasBomb>
    {
        public override string CodecId => "status:GasBomb.HasExploded";

        protected override string PropertyName => "HasExploded";
    }

    private sealed class KinPriestSpeechUsedCodec : UndoCreatureStatusBoolCodec<KinPriest>
    {
        public override string CodecId => "status:KinPriest.SpeechUsed";

        protected override string PropertyName => "SpeechUsed";
    }

    private sealed class MechaKnightWoundUpCodec : UndoCreatureStatusBoolCodec<MechaKnight>
    {
        public override string CodecId => "status:MechaKnight.IsWoundUp";

        protected override string PropertyName => "IsWoundUp";
    }

    private sealed class OvicopterCanLayCodec : UndoCreatureStatusBoolCodec<Ovicopter>
    {
        public override string CodecId => "status:Ovicopter.CanLay";

        protected override string PropertyName => "CanLay";
    }

    private sealed class SnappingJaxfruitChargedCodec : UndoCreatureStatusBoolCodec<SnappingJaxfruit>
    {
        public override string CodecId => "status:SnappingJaxfruit.IsCharged";

        protected override string PropertyName => "IsCharged";
    }

    private sealed class TheInsatiableHasLiquifiedCodec : UndoCreatureStatusBoolCodec<TheInsatiable>
    {
        public override string CodecId => "status:TheInsatiable.HasLiquified";

        protected override string PropertyName => "HasLiquified";
    }

    private sealed class TheObscuraHasSummonedCodec : UndoCreatureStatusBoolCodec<TheObscura>
    {
        public override string CodecId => "status:TheObscura.HasSummoned";

        protected override string PropertyName => "HasSummoned";
    }

    private sealed class TestSubjectExtraMultiClawCountCodec : UndoCreatureStatusIntCodec<TestSubject>
    {
        public override string CodecId => "status:TestSubject.ExtraMultiClawCount";

        protected override string PropertyName => "ExtraMultiClawCount";
    }

    private sealed class TestSubjectRespawnsCodec : UndoCreatureStatusIntCodec<TestSubject>
    {
        public override string CodecId => "status:TestSubject.Respawns";

        protected override string PropertyName => "Respawns";
    }

    private sealed class TwoTailedRatTurnsUntilSummonableCodec : UndoCreatureStatusIntCodec<TwoTailedRat>
    {
        public override string CodecId => "status:TwoTailedRat.TurnsUntilSummonable";

        protected override string PropertyName => "TurnsUntilSummonable";
    }

    private sealed class TwoTailedRatCallForBackupCountCodec : UndoCreatureStatusIntCodec<TwoTailedRat>
    {
        public override string CodecId => "status:TwoTailedRat.CallForBackupCount";

        protected override string PropertyName => "CallForBackupCount";
    }

    private sealed class CreatureShowsInfiniteHpCodec : IUndoCreatureStatusCodec<UndoBoolCreatureStatusRuntimePayload>
    {
        public string CodecId => "status:Creature.ShowsInfiniteHp";

        public bool CanHandle(MonsterModel monster)
        {
            return monster.Creature != null;
        }

        public UndoBoolCreatureStatusRuntimePayload? CaptureTyped(MonsterModel monster)
        {
            return new UndoBoolCreatureStatusRuntimePayload
            {
                CodecId = CodecId,
                Value = monster.Creature.ShowsInfiniteHp
            };
        }

        public bool RestoreTyped(MonsterModel monster, UndoBoolCreatureStatusRuntimePayload state)
        {
            monster.Creature.ShowsInfiniteHp = state.Value;
            return true;
        }

        UndoCreatureStatusRuntimePayload? IUndoCreatureStatusCodec.Capture(MonsterModel monster)
        {
            return CaptureTyped(monster);
        }

        bool IUndoCreatureStatusCodec.Restore(MonsterModel monster, UndoCreatureStatusRuntimePayload state)
        {
            return state is UndoBoolCreatureStatusRuntimePayload typed && RestoreTyped(monster, typed);
        }
    }

    private sealed class WaterfallGiantCurrentPressureGunDamageCodec : UndoCreatureStatusIntCodec<WaterfallGiant>
    {
        public override string CodecId => "status:WaterfallGiant.CurrentPressureGunDamage";

        protected override string PropertyName => "CurrentPressureGunDamage";
    }

    private sealed class WaterfallGiantSteamEruptionDamageCodec : UndoCreatureStatusIntCodec<WaterfallGiant>
    {
        public override string CodecId => "status:WaterfallGiant.SteamEruptionDamage";

        protected override string PropertyName => "SteamEruptionDamage";
    }

    private sealed class WaterfallGiantAboutToBlowCodec : UndoCreatureStatusBoolCodec<WaterfallGiant>
    {
        public override string CodecId => "status:WaterfallGiant.IsAboutToBlow";

        protected override string PropertyName => "IsAboutToBlow";
    }

    private sealed class WaterfallGiantPressureBuildupIdxCodec : UndoCreatureStatusIntCodec<WaterfallGiant>
    {
        public override string CodecId => "status:WaterfallGiant.PressureBuildupIdx";

        protected override string PropertyName => "PressureBuildupIdx";
    }
}
