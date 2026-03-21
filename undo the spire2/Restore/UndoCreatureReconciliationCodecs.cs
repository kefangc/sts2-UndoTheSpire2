// 文件说明：恢复战斗实体后对 creature 引用做对齐修正。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace UndoTheSpire2;

// Reconciles monsters whose final intent depends on restored powers or
// transient stun moves that are not part of the canonical move-state machine.
internal interface IUndoCreatureReconciliationCodec
{
    string CodecId { get; }

    bool CanHandle(MonsterModel monster);

    void Reconcile(MonsterModel monster, UndoMonsterState? state);
}

internal static class UndoCreatureReconciliationCodecRegistry
{
    private static readonly IReadOnlyList<IUndoCreatureReconciliationCodec> Codecs =
    [
        // Recreate vanilla no-op stunned moves for any monster first, then let
        // monster-specific codecs overwrite the callback when the stun carries
        // custom wake-up / follow-up behavior.
        new GenericTransientStunReconciliationCodec(),
        new SlumberingBeetleReconciliationCodec(),
        new LagavulinMatriarchReconciliationCodec(),
        new OwlMagistrateReconciliationCodec(),
        new QueenReconciliationCodec(),
        new OstyReconciliationCodec(),
        new TunnelerReconciliationCodec(),
        new CeremonialBeastReconciliationCodec(),
        new WrigglerReconciliationCodec(),
        new CorpseSlugReconciliationCodec()
    ];

    public static HashSet<string> GetImplementedCodecIds()
    {
        return [.. Codecs.Select(static codec => codec.CodecId)];
    }

    public static RestoreCapabilityReport Restore(IReadOnlyList<UndoMonsterState> monsterStates, IReadOnlyList<Creature> creatures)
    {
        if (creatures.Count == 0)
            return RestoreCapabilityReport.SupportedReport();

        Dictionary<string, UndoMonsterState> statesByKey = monsterStates.ToDictionary(static state => state.CreatureKey);
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            MonsterModel? monster = creature.Monster;
            if (monster?.MoveStateMachine == null)
                continue;

            string creatureKey = UndoStableRefs.BuildCreatureKey(creature, i);
            statesByKey.TryGetValue(creatureKey, out UndoMonsterState? state);
            foreach (IUndoCreatureReconciliationCodec codec in Codecs)
            {
                if (!codec.CanHandle(monster))
                    continue;

                codec.Reconcile(monster, state);
            }
        }

        return RestoreCapabilityReport.SupportedReport();
    }

    private static bool TryRestoreTransientStunnedMove(MonsterModel monster, UndoMonsterState? state, Func<IReadOnlyList<Creature>, Task> stunMove, string? fallbackFollowUpId = null)
    {
        if (state?.NextMoveId != MonsterModel.stunnedMoveId)
            return false;

        monster.Creature.StunInternal(stunMove, state.TransientNextMoveFollowUpId ?? fallbackFollowUpId);
        return true;
    }

    private static bool TryRestoreTransientStunnedMove(MonsterModel monster, UndoMonsterState? state)
    {
        if (state?.NextMoveId != MonsterModel.stunnedMoveId)
            return false;

        monster.Creature.StunInternal(static _ => Task.CompletedTask, state.TransientNextMoveFollowUpId);
        return true;
    }

    private static Task InvokePrivateTaskMethod(object instance, string methodName, IReadOnlyList<Creature> targets)
    {
        return UndoReflectionUtil.FindMethod(instance.GetType(), methodName)?.Invoke(instance, [targets]) as Task
            ?? Task.CompletedTask;
    }

    private static bool TrySetMove(MonsterModel monster, string? moveId)
    {
        if (string.IsNullOrWhiteSpace(moveId) || monster.MoveStateMachine == null)
            return false;

        if (!monster.MoveStateMachine.States.TryGetValue(moveId, out MonsterState? nextState) || nextState is not MoveState moveState)
            return false;

        monster.SetMoveImmediate(moveState, true);
        return true;
    }

    private static void TrySetBoolProperty(MonsterModel monster, string propertyName, bool value)
    {
        if (UndoReflectionUtil.TrySetPropertyValue(monster, propertyName, value))
            return;

        string fieldName = '_' + char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        UndoReflectionUtil.TrySetFieldValue(monster, fieldName, value);
    }

    private static bool TryGetBoolProperty(MonsterModel monster, string propertyName, out bool value)
    {
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

        value = false;
        return false;
    }

    private static TReference? TryGetReferenceProperty<TReference>(MonsterModel monster, string propertyName) where TReference : class
    {
        if (UndoReflectionUtil.FindProperty(monster.GetType(), propertyName)?.GetValue(monster) is TReference propertyValue)
            return propertyValue;

        string fieldName = '_' + char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        return UndoReflectionUtil.FindField(monster.GetType(), fieldName)?.GetValue(monster) as TReference;
    }

    private static void TrySetReferenceProperty(MonsterModel monster, string propertyName, object? value)
    {
        if (UndoReflectionUtil.TrySetPropertyValue(monster, propertyName, value))
            return;

        string fieldName = '_' + char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        UndoReflectionUtil.TrySetFieldValue(monster, fieldName, value);
    }

    private sealed class SlumberingBeetleReconciliationCodec : IUndoCreatureReconciliationCodec
    {
        public string CodecId => "reconcile:SlumberingBeetle.MoveIntent";

        public bool CanHandle(MonsterModel monster)
        {
            return monster is SlumberingBeetle;
        }

        public void Reconcile(MonsterModel monster, UndoMonsterState? state)
        {
            SlumberingBeetle beetle = (SlumberingBeetle)monster;
            if (TryRestoreTransientStunnedMove(beetle, state, beetle.WakeUpMove, "ROLL_OUT_MOVE"))
                return;

            if (beetle.Creature.HasPower<SlumberPower>())
            {
                TrySetBoolProperty(beetle, "IsAwake", false);
                TrySetMove(beetle, "SNORE_MOVE");
                return;
            }

            if (!beetle.IsAwake)
            {
                beetle.Creature.StunInternal(beetle.WakeUpMove, state?.TransientNextMoveFollowUpId ?? "ROLL_OUT_MOVE");
                return;
            }

            if (beetle.NextMove?.Id == "SNORE_MOVE" || state?.NextMoveId == "ROLL_OUT_MOVE")
            {
                TrySetBoolProperty(beetle, "IsAwake", true);
                TrySetMove(beetle, "ROLL_OUT_MOVE");
                return;
            }

            if (beetle.IntendsToAttack || beetle.NextMove?.Id == "ROLL_OUT_MOVE")
                TrySetBoolProperty(beetle, "IsAwake", true);
        }
    }

    private sealed class LagavulinMatriarchReconciliationCodec : IUndoCreatureReconciliationCodec
    {
        public string CodecId => "reconcile:LagavulinMatriarch.MoveIntent";

        public bool CanHandle(MonsterModel monster)
        {
            return monster is LagavulinMatriarch;
        }

        public void Reconcile(MonsterModel monster, UndoMonsterState? state)
        {
            LagavulinMatriarch lagavulin = (LagavulinMatriarch)monster;
            if (TryRestoreTransientStunnedMove(lagavulin, state, lagavulin.WakeUpMove, "SLASH_MOVE"))
                return;

            if (lagavulin.Creature.HasPower<AsleepPower>())
            {
                TrySetBoolProperty(lagavulin, "IsAwake", false);
                TrySetMove(lagavulin, "SLEEP_MOVE");
                return;
            }

            if (!lagavulin.IsAwake)
            {
                lagavulin.Creature.StunInternal(lagavulin.WakeUpMove, state?.TransientNextMoveFollowUpId ?? "SLASH_MOVE");
                return;
            }

            if (lagavulin.NextMove?.Id == "SLEEP_MOVE")
            {
                TrySetBoolProperty(lagavulin, "IsAwake", true);
                TrySetMove(lagavulin, "SLASH_MOVE");
                return;
            }

            if (lagavulin.IntendsToAttack)
                TrySetBoolProperty(lagavulin, "IsAwake", true);
        }
    }

    private sealed class OwlMagistrateReconciliationCodec : IUndoCreatureReconciliationCodec
    {
        public string CodecId => "reconcile:OwlMagistrate.FlightState";

        public bool CanHandle(MonsterModel monster)
        {
            return monster is OwlMagistrate;
        }

        public void Reconcile(MonsterModel monster, UndoMonsterState? state)
        {
            OwlMagistrate owl = (OwlMagistrate)monster;
            string? targetMoveId = state?.NextMoveId ?? owl.NextMove?.Id;
            bool hasSoar = owl.Creature.HasPower<SoarPower>();

            if (!string.IsNullOrWhiteSpace(targetMoveId))
                TrySetMove(owl, targetMoveId);

            // Flying is only true after Judicial Flight has actually resolved and Soar is active.
            // Undoing back to the pre-flight intent should leave the owl grounded even if the current
            // move chain still points at JUDICIAL_FLIGHT.
            TrySetBoolProperty(owl, "IsFlying", hasSoar);
        }
    }
    private sealed class QueenReconciliationCodec : IUndoCreatureReconciliationCodec
    {
        private static readonly HashSet<string> EnragedPathMoveIds =
        [
            "OFF_WITH_YOUR_HEAD_MOVE",
            "EXECUTION_MOVE",
            "ENRAGE_MOVE"
        ];

        public string CodecId => "reconcile:Queen.AmalgamBranch";

        public bool CanHandle(MonsterModel monster)
        {
            return monster is Queen;
        }

        public void Reconcile(MonsterModel monster, UndoMonsterState? state)
        {
            Queen queen = (Queen)monster;
            Creature? amalgam = TryGetReferenceProperty<Creature>(queen, "Amalgam");
            bool amalgamAlive = amalgam != null && !amalgam.IsDead;
            string? targetMoveId = state?.NextMoveId ?? queen.NextMove?.Id;

            if (amalgamAlive)
            {
                TrySetBoolProperty(queen, "HasAmalgamDied", false);
                TrySetReferenceProperty(queen, "Amalgam", amalgam);
                if (string.IsNullOrWhiteSpace(targetMoveId) || EnragedPathMoveIds.Contains(targetMoveId))
                    targetMoveId = "BURN_BRIGHT_FOR_ME_MOVE";
            }
            else
            {
                TrySetBoolProperty(queen, "HasAmalgamDied", true);
                TrySetReferenceProperty(queen, "Amalgam", null);
                if (string.IsNullOrWhiteSpace(targetMoveId) || string.Equals(targetMoveId, "BURN_BRIGHT_FOR_ME_MOVE", StringComparison.Ordinal))
                    targetMoveId = "ENRAGE_MOVE";
            }

            if (!string.IsNullOrWhiteSpace(targetMoveId))
                TrySetMove(queen, targetMoveId);
        }
    }

    private sealed class OstyReconciliationCodec : IUndoCreatureReconciliationCodec
    {
        public string CodecId => "reconcile:Osty.LocalPetConsistency";

        public bool CanHandle(MonsterModel monster)
        {
            return monster is Osty;
        }

        public void Reconcile(MonsterModel monster, UndoMonsterState? state)
        {
            Osty osty = (Osty)monster;
            Player? owner = osty.Creature.PetOwner;
            if (owner?.PlayerCombatState == null)
                return;

            if (!owner.PlayerCombatState.Pets.Contains(osty.Creature))
                owner.PlayerCombatState.AddPetInternal(osty.Creature);

            if (!ReferenceEquals(owner.Osty, osty.Creature))
            {
                Creature? existingOsty = owner.PlayerCombatState.GetPet<Osty>();
                if (existingOsty != null && !ReferenceEquals(existingOsty, osty.Creature))
                    owner.PlayerCombatState.AddPetInternal(osty.Creature);
            }

            if (osty.Creature.IsAlive)
            {
                string? targetMoveId = state?.NextMoveId ?? osty.NextMove?.Id;
                if (string.IsNullOrWhiteSpace(targetMoveId) || !string.Equals(targetMoveId, "NOTHING_MOVE", StringComparison.Ordinal))
                    TrySetMove(osty, "NOTHING_MOVE");
            }
        }
    }

    private sealed class CeremonialBeastReconciliationCodec : IUndoCreatureReconciliationCodec
    {
        public string CodecId => "reconcile:CeremonialBeast.TransientStun";

        public bool CanHandle(MonsterModel monster)
        {
            return monster is CeremonialBeast;
        }

        public void Reconcile(MonsterModel monster, UndoMonsterState? state)
        {
            CeremonialBeast beast = (CeremonialBeast)monster;
            TryRestoreTransientStunnedMove(beast, state, beast.StunnedMove, beast.BeastCryState?.StateId);
        }
    }

    private sealed class TunnelerReconciliationCodec : IUndoCreatureReconciliationCodec
    {
        private const string BiteMoveId = "BITE_MOVE";
        private const string BurrowMoveId = "BURROW_MOVE";
        private const string BelowMoveId = "BELOW_MOVE_1";
        private const string DizzyMoveId = "DIZZY_MOVE";

        public string CodecId => "reconcile:Tunneler.BurrowIntent";

        public bool CanHandle(MonsterModel monster)
        {
            return monster is Tunneler;
        }

        public void Reconcile(MonsterModel monster, UndoMonsterState? state)
        {
            Tunneler tunneler = (Tunneler)monster;
            if (TryRestoreTransientStunnedMove(tunneler, state, static _ => Task.CompletedTask, BiteMoveId))
            {
                RemovePowers<BurrowedPower>(tunneler.Creature);
                return;
            }

            string? targetMoveId = state?.NextMoveId ?? tunneler.NextMove?.Id;
            bool burrowed = tunneler.Creature.HasPower<BurrowedPower>();

            if (burrowed)
            {
                if (string.Equals(targetMoveId, BurrowMoveId, StringComparison.Ordinal) ||
                    string.Equals(targetMoveId, BelowMoveId, StringComparison.Ordinal))
                {
                    TrySetMove(tunneler, targetMoveId);
                    return;
                }

                if (string.Equals(targetMoveId, BiteMoveId, StringComparison.Ordinal) ||
                    string.Equals(targetMoveId, DizzyMoveId, StringComparison.Ordinal))
                {
                    RemovePowers<BurrowedPower>(tunneler.Creature);
                    TrySetMove(tunneler, targetMoveId);
                    return;
                }

                if (string.Equals(state?.CurrentStateId, BelowMoveId, StringComparison.Ordinal))
                {
                    TrySetMove(tunneler, BelowMoveId);
                    return;
                }
            }
            else
            {
                if (string.Equals(targetMoveId, BelowMoveId, StringComparison.Ordinal) ||
                    string.Equals(state?.CurrentStateId, BelowMoveId, StringComparison.Ordinal))
                {
                    TrySetMove(tunneler, BiteMoveId);
                    return;
                }

                if (string.Equals(targetMoveId, BurrowMoveId, StringComparison.Ordinal) ||
                    string.Equals(targetMoveId, BiteMoveId, StringComparison.Ordinal) ||
                    string.Equals(targetMoveId, DizzyMoveId, StringComparison.Ordinal))
                {
                    TrySetMove(tunneler, targetMoveId);
                }
            }
        }
    }

    private sealed class WrigglerReconciliationCodec : IUndoCreatureReconciliationCodec
    {
        public string CodecId => "reconcile:Wriggler.StartStunned";

        public bool CanHandle(MonsterModel monster)
        {
            return monster is Wriggler;
        }

        public void Reconcile(MonsterModel monster, UndoMonsterState? state)
        {
            Wriggler wriggler = (Wriggler)monster;
            if (wriggler.StartStunned && state?.NextMoveId == "SPAWNED_MOVE")
                TrySetMove(wriggler, "SPAWNED_MOVE");
        }
    }

    private sealed class CorpseSlugReconciliationCodec : IUndoCreatureReconciliationCodec
    {
        public string CodecId => "reconcile:CorpseSlug.RavenousStun";

        public bool CanHandle(MonsterModel monster)
        {
            return monster is CorpseSlug;
        }

        public void Reconcile(MonsterModel monster, UndoMonsterState? state)
        {
            CorpseSlug corpseSlug = (CorpseSlug)monster;
            if (state?.NextMoveId != MonsterModel.stunnedMoveId)
                return;

            if (corpseSlug.Creature.GetPower<RavenousPower>() is not RavenousPower ravenousPower)
            {
                TryRestoreTransientStunnedMove(corpseSlug, state);
                return;
            }

            corpseSlug.Creature.StunInternal(
                targets => InvokePrivateTaskMethod(ravenousPower, "StunnedMove", targets),
                state.TransientNextMoveFollowUpId);
        }
    }

    private sealed class GenericTransientStunReconciliationCodec : IUndoCreatureReconciliationCodec
    {
        public string CodecId => "reconcile:GenericTransientStun";

        public bool CanHandle(MonsterModel monster)
        {
            return true;
        }

        public void Reconcile(MonsterModel monster, UndoMonsterState? state)
        {
            TryRestoreTransientStunnedMove(monster, state);
        }
    }

    private static void RemovePowers<TPower>(Creature creature)
        where TPower : PowerModel
    {
        foreach (TPower power in creature.Powers.OfType<TPower>().ToList())
            power.RemoveInternal();
    }
}
