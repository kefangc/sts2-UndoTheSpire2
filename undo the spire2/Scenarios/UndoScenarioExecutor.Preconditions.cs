using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace UndoTheSpire2;

internal static partial class UndoScenarioExecutor
{
    private static UndoScenarioPreconditionResult EvaluatePreconditions(string scenarioId, RunState runState, CombatState combatState)
    {
        Player? me = LocalContext.GetMe(combatState);
        return scenarioId switch
        {
            "well-laid-plans" => Require(me != null
                    && CreatureHasPower(me.Creature, "WellLaidPlansPower")
                    && TryCaptureActiveChoiceSpec(MainFile.Controller)?.Kind == UndoChoiceKind.HandSelection,
                "well_laid_plans_choice_boundary_required"),
            "toolbox-combat-start" => Require(me != null
                    && PlayerHasRelic(me, "Toolbox")
                    && GetLatestUndoSnapshot(MainFile.Controller)?.IsChoiceAnchor == true
                    && GetLatestUndoSnapshot(MainFile.Controller)?.ChoiceSpec?.Kind == UndoChoiceKind.ChooseACard,
                "toolbox_choice_anchor_required"),
            "forgotten-ritual" => Require(ContainsCombatCard(runState, "ForgottenRitual"), "forgotten_ritual_card_required"),
            "automation-power" => Require(me != null && CreatureHasPower(me.Creature, "AutomationPower"), "automation_power_required"),
            "infested-prism" => Require(AnyMonsterType(combatState, "InfestedPrism") || AnyCreatureHasPower(combatState, "VitalSparkPower"), "infested_prism_required"),
            "decimillipede" => Require(AnyMonsterType(combatState, "DecimillipedeSegment"), "decimillipede_required"),
            "door-maker" => Require(AnyMonsterType(combatState, "Door") || AnyMonsterType(combatState, "Doormaker"), "door_or_doormaker_required"),
            "paels-legion" => Require(me != null && PlayerHasRelic(me, "PaelsLegion") && combatState.Allies.Any(creature => creature.PetOwner == me && HasTypeName(creature.Monster, "PaelsLegion")), "paels_legion_pet_required"),
            "tunneler" => Require(AnyMonsterType(combatState, "Tunneler"), "tunneler_required"),
            "owl-magistrate-flight" => Require(AnyMonsterType(combatState, "OwlMagistrate"), "owl_magistrate_required"),
            "queen-soulbound" => Require(me != null && AnyMonsterType(combatState, "Queen") && CreatureHasPower(me.Creature, "ChainsOfBindingPower"), "queen_soulbound_required"),
            "queen-amalgam-branch" => Require(AnyMonsterType(combatState, "Queen") && AnyMonsterType(combatState, "TorchHeadAmalgam"), "queen_and_amalgam_required"),
            "osty-summon-roundtrip" => Require(me != null && IsLocalNecrobinder(me) && combatState.Allies.Any(creature => creature.PetOwner == me && HasTypeName(creature.Monster, "Osty")), "local_osty_pet_required"),
            "osty-revive-roundtrip" => Require(me != null && IsLocalNecrobinder(me) && combatState.Allies.Any(creature => creature.PetOwner == me && HasTypeName(creature.Monster, "Osty")), "local_osty_pet_required"),
            "osty-enemy-hit-roundtrip" => Require(me != null && IsLocalNecrobinder(me) && combatState.Allies.Any(creature => creature.PetOwner == me && HasTypeName(creature.Monster, "Osty")), "local_osty_pet_required"),
            "slumbering-beetle" => Require(AnyMonsterType(combatState, "SlumberingBeetle"), "slumbering_beetle_required"),
            "lagavulin-matriarch" => Require(AnyMonsterType(combatState, "LagavulinMatriarch"), "lagavulin_matriarch_required"),
            "bowlbug-rock" => Require(AnyMonsterType(combatState, "BowlbugRock"), "bowlbug_rock_required"),
            "thieving-hopper" => Require(AnyMonsterType(combatState, "ThievingHopper"), "thieving_hopper_required"),
            "fat-gremlin" => Require(AnyMonsterType(combatState, "FatGremlin"), "fat_gremlin_required"),
            "sneaky-gremlin" => Require(AnyMonsterType(combatState, "SneakyGremlin"), "sneaky_gremlin_required"),
            "ceremonial-beast" => Require(AnyMonsterType(combatState, "CeremonialBeast"), "ceremonial_beast_required"),
            "wriggler" => Require(AnyMonsterType(combatState, "Wriggler"), "wriggler_required"),
            "throwing-axe" => Require(me != null && PlayerHasRelic(me, "ThrowingAxe"), "throwing_axe_required"),
            "happy-flower" => Require(me != null && PlayerHasRelic(me, "HappyFlower"), "happy_flower_required"),
            "history-course" => Require(me != null && PlayerHasRelic(me, "HistoryCourse"), "history_course_required"),
            "pen-nib" => Require(me != null && PlayerHasRelic(me, "PenNib"), "pen_nib_required"),
            "art-of-war" => Require(me != null && PlayerHasRelic(me, "ArtOfWar"), "art_of_war_required"),
            "swipe-power" => Require(AnyCreatureHasPower(combatState, "SwipePower"), "swipe_power_required"),
            "death-march" => Require(ContainsCombatCard(runState, "DeathMarch"), "death_march_card_required"),
            _ => Require(true, "matched")
        };
    }

    private static UndoScenarioPreconditionResult Require(bool matched, string detail)
    {
        return new UndoScenarioPreconditionResult
        {
            Matched = matched,
            Detail = detail
        };
    }

    private static UndoCombatFullState? TryCaptureCurrentCombatFullState(UndoController controller)
    {
        return controller.DebugCaptureCurrentCombatFullState();
    }

    private static UndoSnapshot? GetLatestUndoSnapshot(UndoController controller)
    {
        return controller.DebugGetLatestUndoSnapshot();
    }

    private static UndoChoiceSpec? TryCaptureActiveChoiceSpec(UndoController controller)
    {
        return controller.DebugCaptureActiveChoiceSpec();
    }

    private static RestoreCapabilityReport GetLastRestoreCapabilityReport(UndoController controller)
    {
        return controller.DebugGetLastRestoreCapabilityReport();
    }

    private static string? GetLastRestoreFailureReason(UndoController controller)
    {
        return controller.DebugGetLastRestoreFailureReason();
    }

    private static bool HasSyntheticChoiceSession(UndoController controller)
    {
        return controller.DebugHasSyntheticChoiceSession();
    }

    private static bool IsSupportedChoiceUiActive()
    {
        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        if (combatUi?.Hand?.IsInCardSelection == true)
            return true;

        return NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen or NCardGridSelectionScreen;
    }

    private static bool ContainsCombatCard(RunState runState, string typeName)
    {
        foreach (Player player in runState.Players)
        {
            foreach (PileType pileType in CombatPileOrder)
            {
                CardPile? pile = CardPile.Get(pileType, player);
                if (pile?.Cards.Any(card => HasTypeName(card, typeName)) == true)
                    return true;
            }
        }

        return false;
    }

    private static bool PlayerHasRelic(Player player, string typeName)
    {
        return player.Relics.Any(relic => HasTypeName(relic, typeName));
    }

    private static bool CreatureHasPower(Creature creature, string typeName)
    {
        return creature.Powers.Any(power => HasTypeName(power, typeName));
    }

    private static bool AnyCreatureHasPower(CombatState combatState, string typeName)
    {
        return combatState.Creatures.Any(creature => CreatureHasPower(creature, typeName));
    }

    private static bool AnyMonsterType(CombatState combatState, string typeName)
    {
        return combatState.Creatures.Any(creature => HasTypeName(creature.Monster, typeName));
    }

    private static bool HasTypeName(object? value, string typeName)
    {
        return value?.GetType().Name.Equals(typeName, StringComparison.Ordinal) == true;
    }
}
