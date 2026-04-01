namespace UndoTheSpire2;

internal static partial class UndoScenarioExecutor
{
    private static List<string> GetCapabilityGaps(UndoScenarioDefinition scenario)
    {
        HashSet<string> implemented = UndoRuntimeStateCodecRegistry.GetImplementedCodecIds();
        implemented.UnionWith(UndoCreatureTopologyCodecRegistry.GetImplementedCodecIds());
        implemented.UnionWith(UndoCreatureStatusCodecRegistry.GetImplementedCodecIds());
        implemented.UnionWith(UndoCreatureReconciliationCodecRegistry.GetImplementedCodecIds());
        implemented.UnionWith(UndoActionCodecRegistry.GetImplementedCodecIds());

        List<string> required = scenario.Id switch
        {
            "well-laid-plans" => ["action:WellLaidPlans.choice"],
            "toolbox-combat-start" => ["action:Toolbox.choice"],
            "forgotten-ritual" => ["history:CombatHistory.entries"],
            "automation-power" => ["power:AutomationPower.cardsLeft"],
            "infested-prism" => ["power:VitalSparkPower.playersTriggeredThisTurn", "topology:InfestedPrism"],
            "decimillipede" => ["topology:Decimillipede"],
            "door-maker" => ["topology:DoorAndDoormaker", "power:DoorRevivalPower.isHalfDead"],
            "paels-legion" => ["relic:PaelsLegion.affectedCardPlay"],
            "tunneler" => ["reconcile:Tunneler.BurrowIntent"],
            "owl-magistrate-flight" => ["status:OwlMagistrate.IsFlying", "reconcile:OwlMagistrate.FlightState"],
            "queen-soulbound" => ["power:ChainsOfBindingPower.boundCardPlayed"],
            "queen-amalgam-branch" => ["status:Queen.HasAmalgamDied", "topology:QueenAmalgam", "reconcile:Queen.AmalgamBranch"],
            "osty-summon-roundtrip" => ["reconcile:Osty.LocalPetConsistency"],
            "osty-revive-roundtrip" => ["reconcile:Osty.LocalPetConsistency"],
            "osty-enemy-hit-roundtrip" => ["reconcile:Osty.LocalPetConsistency"],
            "slumbering-beetle" => ["status:SlumberingBeetle.IsAwake", "reconcile:SlumberingBeetle.MoveIntent"],
            "lagavulin-matriarch" => ["status:LagavulinMatriarch.IsAwake", "reconcile:LagavulinMatriarch.MoveIntent"],
            "bowlbug-rock" => ["status:BowlbugRock.IsOffBalance", "reconcile:GenericTransientStun"],
            "thieving-hopper" => ["status:ThievingHopper.IsHovering", "reconcile:GenericTransientStun"],
            "fat-gremlin" => ["status:FatGremlin.IsAwake", "reconcile:GenericTransientStun"],
            "sneaky-gremlin" => ["status:SneakyGremlin.IsAwake", "reconcile:GenericTransientStun"],
            "ceremonial-beast" => ["status:CeremonialBeast.IsStunnedByPlowRemoval", "status:CeremonialBeast.InMidCharge", "reconcile:CeremonialBeast.TransientStun"],
            "wriggler" => ["status:Wriggler.StartStunned", "reconcile:Wriggler.StartStunned"],
            "throwing-axe" => [],
            "happy-flower" => [],
            "history-course" => ["history:CombatHistory.entries"],
            "pen-nib" => ["relic:PenNib.AttackToDouble"],
            "art-of-war" => [],
            "swipe-power" => [],
            "death-march" => ["history:CombatHistory.entries"],
            _ => []
        };

        return required.Where(requiredId => !implemented.Contains(requiredId)).ToList();
    }
}
