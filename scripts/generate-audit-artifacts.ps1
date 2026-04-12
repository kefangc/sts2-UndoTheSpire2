param(
    [string]$OfficialSourceRoot,
    [string]$RepoRoot,
    [string]$CacheRoot,
    [string]$ArtifactsRoot,
    [string]$ScenarioRoot,
    [string]$ReportsRoot
)

$ErrorActionPreference = 'Stop'

function Resolve-PathSetting([string]$ConfiguredValue, [string]$EnvVarName, [string]$Fallback) {
    if (-not [string]::IsNullOrWhiteSpace($ConfiguredValue)) {
        return $ConfiguredValue
    }

    $environmentValue = [System.Environment]::GetEnvironmentVariable($EnvVarName)
    if (-not [string]::IsNullOrWhiteSpace($environmentValue)) {
        return $environmentValue
    }

    return $Fallback
}

function Ensure-Dir([string]$Path) {
    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
}

function Read-Text([string]$Path) {
    return Get-Content -Raw -LiteralPath $Path
}

function Has-Pattern([string]$Text, [string]$Pattern) {
    return [regex]::IsMatch($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
}

function Get-StateSources([string]$Text) {
    $sources = [System.Collections.Generic.List[string]]::new()
    if (Has-Pattern $Text '\[SavedProperty\]') { $sources.Add('SavedProperty') }
    if (Has-Pattern $Text 'CombatManager\.Instance\.History|CombatHistory') { $sources.Add('History-derived') }
    if (Has-Pattern $Text 'InitInternalData\s*\(' -or Has-Pattern $Text '_internalData') { $sources.Add('Runtime object graph') }
    if (Has-Pattern $Text 'CardSelectCmd\.|ChooseACard|SimpleGridSelection|GatheringPlayerChoice|FromHand') { $sources.Add('Action continuation') }
    if (Has-Pattern $Text 'private bool|private int|private .* CardModel|private .* Creature|private .* HashSet|private .* Dictionary') { $sources.Add('Simple runtime property') }
    return $sources | Select-Object -Unique
}

function Get-RecommendedHandling([string[]]$Sources, [string]$Path) {
    if ($Sources -contains 'Action continuation') { return 'ActionKernelState' }
    if ($Sources -contains 'History-derived') { return 'CombatHistoryState' }
    if ($Sources -contains 'Runtime object graph') {
        if ($Path -match '\\Monsters\\') { return 'Topology codec + Runtime graph codec' }
        return 'Runtime graph codec'
    }
    if ($Sources -contains 'SavedProperty') { return 'Official SavedProperty + supplement when needed' }
    return 'Simple property capture'
}

function Get-Entries([string]$Category, [string]$Root) {
    $files = Get-ChildItem -LiteralPath $Root -Filter *.cs -File
    foreach ($file in $files) {
        $text = Read-Text $file.FullName
        $sources = Get-StateSources $text
        [pscustomobject]@{
            category = $Category
            name = $file.BaseName
            file = $file.FullName
            stateSources = $sources
            needsCodecType = Get-RecommendedHandling $sources $file.FullName
            needsTopologyCodec = ($file.FullName -match '\\Monsters\\' -and (Has-Pattern $text 'AfterAddedToRoom|CreateCreature|Revival|Reattach|Doormaker|FollowUpState'))
            usesHistory = $sources -contains 'History-derived'
            usesInternalData = $sources -contains 'Runtime object graph'
            usesChoiceContinuation = $sources -contains 'Action continuation'
            usesSavedProperty = $sources -contains 'SavedProperty'
        }
    }
}

$OfficialSourceRoot = Resolve-PathSetting $OfficialSourceRoot 'UNDO_THE_SPIRE2_OFFICIAL_SOURCE_ROOT' 'F:\projects\slay the spire2\sts2\MegaCrit\sts2\Core'
$RepoRoot = if (-not [string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot
} else {
    (Resolve-Path (Join-Path $PSScriptRoot '..\undo the spire2')).Path
}
$CacheRoot = Resolve-PathSetting $CacheRoot 'UNDO_THE_SPIRE2_CACHE_ROOT' 'F:\projects\undo-the-spire2-cache'
$auditDir = Join-Path $CacheRoot 'audit'
$artifactsDir = Resolve-PathSetting $ArtifactsRoot 'UNDO_THE_SPIRE2_ARTIFACTS_ROOT' (Join-Path $CacheRoot 'artifacts')
$scenarioDir = Resolve-PathSetting $ScenarioRoot 'UNDO_THE_SPIRE2_SCENARIO_ROOT' (Join-Path $artifactsDir 'scenario-definitions')
$reportsDir = Resolve-PathSetting $ReportsRoot 'UNDO_THE_SPIRE2_REPORTS_ROOT' (Join-Path $CacheRoot 'reports')
Ensure-Dir $auditDir
Ensure-Dir $artifactsDir
Ensure-Dir $scenarioDir
Ensure-Dir $reportsDir

$cards = @(Get-Entries 'card' (Join-Path $OfficialSourceRoot 'Models\Cards'))
$powers = @(Get-Entries 'power' (Join-Path $OfficialSourceRoot 'Models\Powers'))
$relics = @(Get-Entries 'relic' (Join-Path $OfficialSourceRoot 'Models\Relics'))
$monsters = @(Get-Entries 'monster' (Join-Path $OfficialSourceRoot 'Models\Monsters'))
$historyEntries = Get-ChildItem -LiteralPath (Join-Path $OfficialSourceRoot 'Combat\History\Entries') -Filter *.cs -File | ForEach-Object {
    [pscustomobject]@{
        category = 'history-entry'
        name = $_.BaseName
        file = $_.FullName
    }
}
$actions = Get-ChildItem -LiteralPath (Join-Path $OfficialSourceRoot 'GameActions') -Filter *.cs -File | ForEach-Object {
    $text = Read-Text $_.FullName
    [pscustomobject]@{
        category = 'action'
        name = $_.BaseName
        file = $_.FullName
        stateSources = Get-StateSources $text
        needsCodecType = if (Has-Pattern $text 'GatheringPlayerChoice|ResumeAfterGatheringPlayerChoice|PauseForPlayerChoice') { 'ActionKernelState' } else { 'Action metadata only' }
    }
}

$coverageMatrix = @($cards + $powers + $relics + $monsters + $actions)
$coverageMatrix | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $auditDir 'coverage-matrix.json')

$knownCases = @(
    [pscustomobject]@{ id='well-laid-plans'; title='Well Laid Plans'; category='power'; sourceFiles=@('WellLaidPlansPower.cs','CardSelectCmd.cs'); primaryRisk='Paused choice continuation'; scenarioTags=@('choice','retain','end-turn'); assertions=@('paused_choice_captured','primary_choice_supported','primary_restore_used','retain_selection_reopens','no_hidden_choice_anchor_skip') },
    [pscustomobject]@{ id='toolbox-combat-start'; title='Toolbox Combat Start'; category='relic'; sourceFiles=@('Toolbox.cs','CardSelectCmd.cs'); primaryRisk='combat-start choose-a-card anchor'; scenarioTags=@('relic','choice','choose-a-card','combat-start'); assertions=@('choice_anchor_visible','primary_choice_supported','primary_restore_used','choose_a_card_reopens','no_hidden_choice_anchor_skip') },
    [pscustomobject]@{ id='forgotten-ritual'; title='Forgotten Ritual'; category='card'; sourceFiles=@('ForgottenRitual.cs','CardExhaustedEntry.cs'); primaryRisk='CombatHistory missing'; scenarioTags=@('history','exhaust'); assertions=@('exhaust_history_survives_undo','gold_glow_matches_history') },
    [pscustomobject]@{ id='death-march'; title='Death March'; category='card'; sourceFiles=@('DeathMarch.cs','CardDrawnEntry.cs'); primaryRisk='Per-turn draw history'; scenarioTags=@('history','draw-count'); assertions=@('draw_count_damage_restores') },
    [pscustomobject]@{ id='automation-power'; title='Automation Power'; category='power'; sourceFiles=@('AutomationPower.cs'); primaryRisk='Internal counter cardsLeft'; scenarioTags=@('internal-data','counter'); assertions=@('cards_left_restores','display_counter_restores') },
    [pscustomobject]@{ id='infested-prism'; title='Infested Prism'; category='monster'; sourceFiles=@('InfestedPrism.cs','VitalSparkPower.cs'); primaryRisk='playersTriggeredThisTurn'; scenarioTags=@('monster','power','internal-data'); assertions=@('first_damage_only_triggers_once_after_undo') },
    [pscustomobject]@{ id='decimillipede'; title='Decimillipede'; category='monster'; sourceFiles=@('DecimillipedeSegment.cs','ReattachPower.cs'); primaryRisk='revive topology'; scenarioTags=@('monster','revive','topology'); assertions=@('reviving_state_restores','segment_rejoins_correctly') },
    [pscustomobject]@{ id='door-maker'; title='Doormaker'; category='monster'; sourceFiles=@('Door.cs','Doormaker.cs','DoorRevivalPower.cs'); primaryRisk='cross-creature topology'; scenarioTags=@('monster','boss','topology'); assertions=@('door_phase_restores','times_got_back_in_restores') },
    [pscustomobject]@{ id='paels-legion'; title='Pael''s Legion'; category='relic'; sourceFiles=@('PaelsLegion.cs'); primaryRisk='pet ownership topology'; scenarioTags=@('relic','pet','topology'); assertions=@('pet_role_restores','pet_owner_restores','no_duplicate_pet_after_undo','pet_visual_state_restores') },
    [pscustomobject]@{ id='tunneler'; title='Tunneler'; category='monster'; sourceFiles=@('Tunneler.cs','BurrowedPower.cs'); primaryRisk='burrow/stun reconciliation and visuals'; scenarioTags=@('monster','burrow','stun'); assertions=@('burrow_visual_restores','stun_intent_restores') },
    [pscustomobject]@{ id='owl-magistrate-flight'; title='Owl Magistrate'; category='monster'; sourceFiles=@('OwlMagistrate.cs','SoarPower.cs'); primaryRisk='flight state reconciliation and visuals'; scenarioTags=@('monster','flight','visual'); assertions=@('flight_visual_restores','flight_intent_restores') },
    [pscustomobject]@{ id='queen-soulbound'; title='Queen Soulbound'; category='power'; sourceFiles=@('Queen.cs','ChainsOfBindingPower.cs'); primaryRisk='bound-card turn flag runtime'; scenarioTags=@('monster','power','turn-state'); assertions=@('bound_card_play_flag_restores') },
    [pscustomobject]@{ id='queen-amalgam-branch'; title='Queen Amalgam Branch'; category='monster'; sourceFiles=@('Queen.cs','TorchHeadAmalgam.cs'); primaryRisk='linked minion death branch reconciliation'; scenarioTags=@('monster','linked-ref','branch'); assertions=@('queen_branch_intent_restores') },
    [pscustomobject]@{ id='osty-summon-roundtrip'; title='Osty Summon Roundtrip'; category='monster'; sourceFiles=@('Osty.cs','OstyCmd.cs','Necrobinder.cs'); primaryRisk='local pet summon position and scaling'; scenarioTags=@('monster','pet','summon','necrobinder'); assertions=@('pet_role_restores','osty_owner_restores','osty_position_restores','osty_scale_restores','osty_block_track_restores','no_restore_noop_potion_slot','no_post_restore_input_lock') },
    [pscustomobject]@{ id='osty-revive-roundtrip'; title='Osty Revive Roundtrip'; category='monster'; sourceFiles=@('Osty.cs','OstyCmd.cs','Necrobinder.cs'); primaryRisk='local pet revive position and scaling'; scenarioTags=@('monster','pet','revive','necrobinder'); assertions=@('pet_role_restores','osty_owner_restores','osty_position_restores','osty_scale_restores','osty_block_track_restores','no_restore_noop_potion_slot','no_post_restore_input_lock') },
    [pscustomobject]@{ id='osty-enemy-hit-roundtrip'; title='Osty Enemy Hit Roundtrip'; category='monster'; sourceFiles=@('Osty.cs','OstyCmd.cs','Necrobinder.cs'); primaryRisk='local pet block tracking survives enemy hit after undo'; scenarioTags=@('monster','pet','necrobinder','block-tracking'); assertions=@('pet_role_restores','osty_owner_restores','osty_position_restores','osty_scale_restores','osty_block_track_restores','osty_state_display_rebound','no_disposed_block_subscription','no_restore_noop_potion_slot','no_post_restore_input_lock') },
    [pscustomobject]@{ id='slumbering-beetle'; title='Slumbering Beetle'; category='monster'; sourceFiles=@('SlumberingBeetle.cs','SlumberPower.cs'); primaryRisk='sleeping runtime state'; scenarioTags=@('monster','sleep','stun'); assertions=@('status_runtime_restores','creature_visual_state_restores','creature_intent_state_restores') },
    [pscustomobject]@{ id='lagavulin-matriarch'; title='Lagavulin Matriarch'; category='monster'; sourceFiles=@('LagavulinMatriarch.cs','AsleepPower.cs'); primaryRisk='sleeping runtime state'; scenarioTags=@('monster','sleep','wake'); assertions=@('status_runtime_restores','creature_visual_state_restores','creature_intent_state_restores') },
    [pscustomobject]@{ id='thieving-hopper'; title='Thieving Hopper'; category='monster'; sourceFiles=@('ThievingHopper.cs'); primaryRisk='hover and stun runtime'; scenarioTags=@('monster','hover','stun'); assertions=@('status_runtime_restores','creature_visual_state_restores') },
    [pscustomobject]@{ id='fat-gremlin'; title='Fat Gremlin'; category='monster'; sourceFiles=@('FatGremlin.cs'); primaryRisk='spawned stunned runtime'; scenarioTags=@('monster','stun'); assertions=@('status_runtime_restores','creature_visual_state_restores') },
    [pscustomobject]@{ id='sneaky-gremlin'; title='Sneaky Gremlin'; category='monster'; sourceFiles=@('SneakyGremlin.cs'); primaryRisk='spawned stunned runtime'; scenarioTags=@('monster','stun'); assertions=@('status_runtime_restores','creature_visual_state_restores') },
    [pscustomobject]@{ id='ceremonial-beast'; title='Ceremonial Beast'; category='monster'; sourceFiles=@('CeremonialBeast.cs'); primaryRisk='plow stun runtime'; scenarioTags=@('monster','stun'); assertions=@('status_runtime_restores','creature_visual_state_restores') },
    [pscustomobject]@{ id='wriggler'; title='Wriggler'; category='monster'; sourceFiles=@('Wriggler.cs'); primaryRisk='start stunned branch'; scenarioTags=@('monster','spawn','stun'); assertions=@('status_runtime_restores') },
    [pscustomobject]@{ id='throwing-axe'; title='Throwing Axe'; category='relic'; sourceFiles=@('ThrowingAxe.cs'); primaryRisk='simple combat flag'; scenarioTags=@('relic','counter'); assertions=@('first_card_double_flag_restores') },
    [pscustomobject]@{ id='happy-flower'; title='Happy Flower'; category='relic'; sourceFiles=@('HappyFlower.cs'); primaryRisk='saved property plus runtime flag'; scenarioTags=@('relic','counter'); assertions=@('turn_counter_restores','activation_flag_restores') },
    [pscustomobject]@{ id='history-course'; title='History Course'; category='relic'; sourceFiles=@('HistoryCourse.cs'); primaryRisk='last turn card identity'; scenarioTags=@('relic','history'); assertions=@('last_turn_card_replay_restores') },
    [pscustomobject]@{ id='pen-nib'; title='Pen Nib'; category='relic'; sourceFiles=@('PenNib.cs'); primaryRisk='card object reference'; scenarioTags=@('relic','card-ref'); assertions=@('attack_to_double_restores') },
    [pscustomobject]@{ id='art-of-war'; title='Art of War'; category='relic'; sourceFiles=@('ArtOfWar.cs'); primaryRisk='turn flags'; scenarioTags=@('relic','turn-state'); assertions=@('attack_played_flags_restore') },
    [pscustomobject]@{ id='swipe-power'; title='Swipe Power'; category='power'; sourceFiles=@('SwipePower.cs'); primaryRisk='stolen card runtime'; scenarioTags=@('power','card-ref'); assertions=@('stolen_card_restores','stolen_card_ui_restores') }
)

$implementedCodecIds = @()
$runtimeCodecFile = Join-Path $RepoRoot 'Restore\UndoRuntimeCodecs.cs'
$actionCodecFile = Join-Path $RepoRoot 'Restore\UndoActionCodecs.cs'
$statusCodecFile = Join-Path $RepoRoot 'Restore\UndoCreatureStatusCodecs.cs'
$reconciliationCodecFile = Join-Path $RepoRoot 'Restore\UndoCreatureReconciliationCodecs.cs'
$codecTexts = @()
if (Test-Path $runtimeCodecFile) { $codecTexts += Read-Text $runtimeCodecFile }
if (Test-Path $actionCodecFile) { $codecTexts += Read-Text $actionCodecFile }
if (Test-Path $statusCodecFile) { $codecTexts += Read-Text $statusCodecFile }
if (Test-Path $reconciliationCodecFile) { $codecTexts += Read-Text $reconciliationCodecFile }
if ($codecTexts.Count -gt 0) {
    $implementedCodecIds = ($codecTexts | ForEach-Object {
        ([regex]::Matches($_, 'CodecId\s*=>\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
    }) | Sort-Object -Unique
}

$officialRuntimePatterns = @(
    [pscustomobject]@{ id='card:UpMySleeve.timesPlayedThisCombat'; category='card'; sourceFile='UpMySleeve.cs'; stateShape='int'; implemented=($implementedCodecIds -contains 'card:UpMySleeve.timesPlayedThisCombat') },
    [pscustomobject]@{ id='power:AutomationPower.cardsLeft'; category='power'; sourceFile='AutomationPower.cs'; stateShape='int'; implemented=($implementedCodecIds -contains 'power:AutomationPower.cardsLeft') },
    [pscustomobject]@{ id='power:VitalSparkPower.playersTriggeredThisTurn'; category='power'; sourceFile='VitalSparkPower.cs'; stateShape='HashSet<Player>'; implemented=($implementedCodecIds -contains 'power:VitalSparkPower.playersTriggeredThisTurn') },
    [pscustomobject]@{ id='power:AfterimagePower.amountsForPlayedCards'; category='power'; sourceFile='AfterimagePower.cs'; stateShape='Dictionary<CardModel,int>'; implemented=($implementedCodecIds -contains 'power:AfterimagePower.amountsForPlayedCards') },
    [pscustomobject]@{ id='power:NightmarePower.selectedCard'; category='power'; sourceFile='NightmarePower.cs'; stateShape='CardModel clone'; implemented=($implementedCodecIds -contains 'power:NightmarePower.selectedCard') },
    [pscustomobject]@{ id='power:DampenPower.data'; category='power'; sourceFile='DampenPower.cs'; stateShape='HashSet<Creature> + Dictionary<CardModel,int>'; implemented=($implementedCodecIds -contains 'power:DampenPower.data') },
    [pscustomobject]@{ id='power:DoorRevivalPower.isHalfDead'; category='power'; sourceFile='DoorRevivalPower.cs'; stateShape='bool'; implemented=($implementedCodecIds -contains 'power:DoorRevivalPower.isHalfDead') },
    [pscustomobject]@{ id='relic:PenNib.AttackToDouble'; category='relic'; sourceFile='PenNib.cs'; stateShape='CardModel ref'; implemented=($implementedCodecIds -contains 'relic:PenNib.AttackToDouble') },
    [pscustomobject]@{ id='relic:Pocketwatch.turnCounts'; category='relic'; sourceFile='Pocketwatch.cs'; stateShape='int + int'; implemented=($implementedCodecIds -contains 'relic:Pocketwatch.turnCounts') },
    [pscustomobject]@{ id='relic:VelvetChoker.cardsPlayedThisTurn'; category='relic'; sourceFile='VelvetChoker.cs'; stateShape='int'; implemented=($implementedCodecIds -contains 'relic:VelvetChoker.cardsPlayedThisTurn') },
    [pscustomobject]@{ id='relic:PaelsLegion.affectedCardPlay'; category='relic'; sourceFile='PaelsLegion.cs'; stateShape='CardPlay ref'; implemented=($implementedCodecIds -contains 'relic:PaelsLegion.affectedCardPlay') },
    [pscustomobject]@{ id='power:ChainsOfBindingPower.boundCardPlayed'; category='power'; sourceFile='ChainsOfBindingPower.cs'; stateShape='bool'; implemented=($implementedCodecIds -contains 'power:ChainsOfBindingPower.boundCardPlayed') },
    [pscustomobject]@{ id='reconcile:Tunneler.BurrowIntent'; category='reconciliation'; sourceFile='Tunneler.cs'; stateShape='burrowed power + transient stun reconciliation'; implemented=($implementedCodecIds -contains 'reconcile:Tunneler.BurrowIntent') },
    [pscustomobject]@{ id='reconcile:OwlMagistrate.FlightState'; category='reconciliation'; sourceFile='OwlMagistrate.cs'; stateShape='flight state reconciliation'; implemented=($implementedCodecIds -contains 'reconcile:OwlMagistrate.FlightState') },
    [pscustomobject]@{ id='reconcile:Queen.AmalgamBranch'; category='reconciliation'; sourceFile='Queen.cs'; stateShape='linked-creature branch reconciliation'; implemented=($implementedCodecIds -contains 'reconcile:Queen.AmalgamBranch') },
    [pscustomobject]@{ id='reconcile:Osty.LocalPetConsistency'; category='reconciliation'; sourceFile='Osty.cs'; stateShape='local pet owner/position consistency'; implemented=($implementedCodecIds -contains 'reconcile:Osty.LocalPetConsistency') },
    [pscustomobject]@{ id='status:SlumberingBeetle.IsAwake'; category='status'; sourceFile='SlumberingBeetle.cs'; stateShape='bool'; implemented=($implementedCodecIds -contains 'status:SlumberingBeetle.IsAwake') },
    [pscustomobject]@{ id='status:LagavulinMatriarch.IsAwake'; category='status'; sourceFile='LagavulinMatriarch.cs'; stateShape='bool'; implemented=($implementedCodecIds -contains 'status:LagavulinMatriarch.IsAwake') },
    [pscustomobject]@{ id='status:BowlbugRock.IsOffBalance'; category='status'; sourceFile='BowlbugRock.cs'; stateShape='bool'; implemented=($implementedCodecIds -contains 'status:BowlbugRock.IsOffBalance') },
    [pscustomobject]@{ id='status:ThievingHopper.IsHovering'; category='status'; sourceFile='ThievingHopper.cs'; stateShape='bool'; implemented=($implementedCodecIds -contains 'status:ThievingHopper.IsHovering') },
    [pscustomobject]@{ id='status:OwlMagistrate.IsFlying'; category='status'; sourceFile='OwlMagistrate.cs'; stateShape='bool'; implemented=($implementedCodecIds -contains 'status:OwlMagistrate.IsFlying') },
    [pscustomobject]@{ id='status:Queen.HasAmalgamDied'; category='status'; sourceFile='Queen.cs'; stateShape='bool'; implemented=($implementedCodecIds -contains 'status:Queen.HasAmalgamDied') },
    [pscustomobject]@{ id='status:FatGremlin.IsAwake'; category='status'; sourceFile='FatGremlin.cs'; stateShape='bool'; implemented=($implementedCodecIds -contains 'status:FatGremlin.IsAwake') },
    [pscustomobject]@{ id='status:SneakyGremlin.IsAwake'; category='status'; sourceFile='SneakyGremlin.cs'; stateShape='bool'; implemented=($implementedCodecIds -contains 'status:SneakyGremlin.IsAwake') },
    [pscustomobject]@{ id='status:CeremonialBeast.IsStunnedByPlowRemoval'; category='status'; sourceFile='CeremonialBeast.cs'; stateShape='bool'; implemented=($implementedCodecIds -contains 'status:CeremonialBeast.IsStunnedByPlowRemoval') },
    [pscustomobject]@{ id='status:CeremonialBeast.InMidCharge'; category='status'; sourceFile='CeremonialBeast.cs'; stateShape='bool'; implemented=($implementedCodecIds -contains 'status:CeremonialBeast.InMidCharge') },
    [pscustomobject]@{ id='status:Wriggler.StartStunned'; category='status'; sourceFile='Wriggler.cs'; stateShape='bool'; implemented=($implementedCodecIds -contains 'status:Wriggler.StartStunned') },
    [pscustomobject]@{ id='reconcile:SlumberingBeetle.MoveIntent'; category='reconciliation'; sourceFile='SlumberingBeetle.cs'; stateShape='power-driven move reconciliation'; implemented=($implementedCodecIds -contains 'reconcile:SlumberingBeetle.MoveIntent') },
    [pscustomobject]@{ id='reconcile:LagavulinMatriarch.MoveIntent'; category='reconciliation'; sourceFile='LagavulinMatriarch.cs'; stateShape='power-driven move reconciliation'; implemented=($implementedCodecIds -contains 'reconcile:LagavulinMatriarch.MoveIntent') },
    [pscustomobject]@{ id='reconcile:GenericTransientStun'; category='reconciliation'; sourceFile='Creature.cs'; stateShape='transient stunned move recreation'; implemented=($implementedCodecIds -contains 'reconcile:GenericTransientStun') },
    [pscustomobject]@{ id='reconcile:CeremonialBeast.TransientStun'; category='reconciliation'; sourceFile='CeremonialBeast.cs'; stateShape='custom stunned callback'; implemented=($implementedCodecIds -contains 'reconcile:CeremonialBeast.TransientStun') },
    [pscustomobject]@{ id='reconcile:Wriggler.StartStunned'; category='reconciliation'; sourceFile='Wriggler.cs'; stateShape='conditional initial move reconciliation'; implemented=($implementedCodecIds -contains 'reconcile:Wriggler.StartStunned') },
    [pscustomobject]@{ id='topology:DoorAndDoormaker'; category='topology'; sourceFile='Door.cs'; stateShape='linked creature refs + phase'; implemented=$true },
    [pscustomobject]@{ id='topology:Decimillipede'; category='topology'; sourceFile='DecimillipedeSegment.cs'; stateShape='segment graph + starter move'; implemented=$true },
    [pscustomobject]@{ id='topology:TestSubject'; category='topology'; sourceFile='TestSubject.cs'; stateShape='phase state'; implemented=$true },
    [pscustomobject]@{ id='topology:InfestedPrism'; category='topology'; sourceFile='InfestedPrism.cs'; stateShape='monster topology marker'; implemented=$true },
    [pscustomobject]@{ id='topology:QueenAmalgam'; category='topology'; sourceFile='Queen.cs'; stateShape='linked amalgam creature ref'; implemented=$true },
    [pscustomobject]@{ id='action:WellLaidPlans.choice'; category='action'; sourceFile='WellLaidPlansPower.cs'; stateShape='paused player choice'; implemented=(($implementedCodecIds -contains 'action:WellLaidPlans.choice') -or ($implementedCodecIds -contains 'action:from-hand')) },
    [pscustomobject]@{ id='action:Toolbox.choice'; category='action'; sourceFile='Toolbox.cs'; stateShape='combat-start choose-a-card anchor'; implemented=(($implementedCodecIds -contains 'action:Toolbox.choice') -or ($implementedCodecIds -contains 'action:choose-a-card')) },
    [pscustomobject]@{ id='history:CombatHistory.entries'; category='history'; sourceFile='CombatHistory.cs'; stateShape='17 official entry types'; implemented=$true }
)
$officialRuntimePatterns | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $artifactsDir 'official-runtime-patterns.json')

$codecRegistrySeed = [pscustomobject]@{
    generatedAt = (Get-Date).ToString('s')
    implementedCodecIds = $implementedCodecIds
    plannedCodecIds = @(
        'power:VigorPower.commandToModify',
        'power:ReattachPower.isReviving',
        'relic:PaelsLegion.affectedCardPlay',
        'action:WellLaidPlans.choice',
        'action:Toolbox.choice',
        'reconcile:SlumberingBeetle.MoveIntent',
        'reconcile:LagavulinMatriarch.MoveIntent',
        'reconcile:GenericTransientStun',
        'reconcile:CeremonialBeast.TransientStun',
        'reconcile:Wriggler.StartStunned',
        'reconcile:Tunneler.BurrowIntent',
        'status:OwlMagistrate.IsFlying',
        'status:Queen.HasAmalgamDied',
        'power:ChainsOfBindingPower.boundCardPlayed',
        'reconcile:OwlMagistrate.FlightState',
        'reconcile:Queen.AmalgamBranch'
    )
}
$codecRegistrySeed | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $artifactsDir 'codec-registry-seed.json')

$scenarioMatrix = $knownCases | ForEach-Object {
    [pscustomobject]@{
        id = $_.id
        title = $_.title
        category = $_.category
        primaryRisk = $_.primaryRisk
        scenarioTags = $_.scenarioTags
        assertions = $_.assertions
        sourceFiles = $_.sourceFiles
    }
}
$scenarioMatrix | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $auditDir 'scenario-matrix.json')

foreach ($scenario in $knownCases) {
    $definition = [pscustomobject]@{
        id = $scenario.id
        title = $scenario.title
        summary = $scenario.primaryRisk
        tags = $scenario.scenarioTags
        sourceFiles = $scenario.sourceFiles
        assertions = $scenario.assertions
        setup = @('load_official_scenario')
        steps = @('capture_baseline','perform_repro_action','capture_target','undo_redo_cycle')
        capturePoints = @('baseline','target')
        expectedUnsupportedCapabilities = @()
    }
    $definition | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $scenarioDir ($scenario.id + '.json'))
}

$stateSourceCounts = [ordered]@{
    SavedProperty = ($coverageMatrix | Where-Object { $_.stateSources -contains 'SavedProperty' }).Count
    HistoryDerived = ($coverageMatrix | Where-Object { $_.stateSources -contains 'History-derived' }).Count
    SimpleRuntimeProperty = ($coverageMatrix | Where-Object { $_.stateSources -contains 'Simple runtime property' }).Count
    RuntimeObjectGraph = ($coverageMatrix | Where-Object { $_.stateSources -contains 'Runtime object graph' }).Count
    ActionContinuation = ($coverageMatrix | Where-Object { $_.stateSources -contains 'Action continuation' }).Count
}

$stateSourceAudit = @"
# State Source Audit

Generated: $(Get-Date -Format s)
Official source: `$OfficialSourceRoot`
Repo: `$RepoRoot`

## Counts
- SavedProperty-backed files: $($stateSourceCounts.SavedProperty)
- History-derived files: $($stateSourceCounts.HistoryDerived)
- Simple runtime property files: $($stateSourceCounts.SimpleRuntimeProperty)
- Runtime object graph files: $($stateSourceCounts.RuntimeObjectGraph)
- Action continuation files: $($stateSourceCounts.ActionContinuation)

## Coverage focus
- `WellLaidPlans`: paused hand selection anchor and branch-state restore.
- `PaelsLegion`: pet ownership topology and special visual normalization.
- `SlumberingBeetle` / `LagavulinMatriarch`: power-driven sleep/wake move reconciliation.
- `BowlbugRock`, `ThievingHopper`, `FatGremlin`, `SneakyGremlin`, `CeremonialBeast`, `Wriggler`, `TerrorEel`, and generic stun effects such as `Whistle`/`ShriekPower`/`ImbalancedPower`: transient stunned move reconciliation.
- `AutomationPower`: private combat counter.
- `VitalSparkPower`: `HashSet<Player>` runtime graph.
- `PenNib`: private `AttackToDouble` card reference.
- `Door + Doormaker`: cross-creature topology and revive phase.

## Current Implementation Seed
- Implemented runtime/topology codec ids:
$((@($implementedCodecIds) + @('topology:DoorAndDoormaker','topology:Decimillipede','topology:TestSubject','topology:InfestedPrism')) | Sort-Object -Unique | ForEach-Object { "- $_" } | Out-String)
"@
$stateSourceAudit | Set-Content (Join-Path $auditDir 'state-source-audit.md')

$riskRegistry = @"
# Risk Registry

Generated: $(Get-Date -Format s)

## Cross-object topology
- `Door.cs` + `Doormaker.cs` + `DoorRevivalPower.cs`: door/doormaker references, follow-up state, phase counters.
- `DecimillipedeSegment.cs` + `ReattachPower.cs`: revive lifecycle spans creature state machine and power internal data.
- `PaelsLegion.cs`: pet owner, ally list membership, special visual state.

## Runtime object graph
- `VitalSparkPower.cs`: `HashSet<Player>`.
- `AfterimagePower.cs`: `Dictionary<CardModel,int>`.
- `NightmarePower.cs`: detached selected card clone.
- `DampenPower.cs`: `HashSet<Creature>` plus `Dictionary<CardModel,int>`.
- `PenNib.cs`: card reference bound to next attack.

## Power-driven reconciliation
- `SlumberingBeetle.cs` + `SlumberPower.cs`: sleep/stun/rollout branch depends on restored power and transient stunned move.
- `LagavulinMatriarch.cs` + `AsleepPower.cs`: asleep/stunned/slash branch depends on restored power and transient stunned move.
- `CeremonialBeast.cs`: plow charge and plow-removal stun must not cross wires after restore.

## Action / choice
- `WellLaidPlansPower.cs` and other choice-bearing actions need paused continuation restore to avoid black-screen replay.
"@
$riskRegistry | Set-Content (Join-Path $auditDir 'risk-registry.md')

$auditSummary = @"
# Audit Summary

Generated: $(Get-Date -Format s)
Coverage entries: $($coverageMatrix.Count)
History entry types: $($historyEntries.Count)
Known regression scenarios: $($knownCases.Count)
Implemented runtime codecs: $($implementedCodecIds.Count)

## Notes
- This report is generated from official source scanning plus curated known regressions.
- The cache artifacts are intended to be implementation inputs for the undo kernel refactor.
- Action codec coverage and runtime closed-loop are tracked separately; static cache generation does not imply a live runtime pass.
- Runtime coverage now includes card, relic, power, topology, status, reconciliation, and action registry seeds.
"@
$auditSummary | Set-Content (Join-Path $reportsDir 'audit-summary.md')

$auditCoverageReport = @"
# Audit Coverage Report

- Implemented official runtime/topology patterns: $((@($officialRuntimePatterns | Where-Object { $_.implemented }).Count))
- Missing official runtime/action patterns: $((@($officialRuntimePatterns | Where-Object { -not $_.implemented }).Count))

$((@($officialRuntimePatterns | Where-Object { -not $_.implemented }) | ForEach-Object { "- $($_.id)" }) -join "`r`n")
"@
$auditCoverageReport | Set-Content (Join-Path $reportsDir 'audit-coverage-report.md')

function Get-ScenarioDeclaredSupport([string]$ScenarioId) {
    switch ($ScenarioId) {
        'well-laid-plans' { return $officialRuntimePatterns.Where({ $_.id -eq 'action:WellLaidPlans.choice' }).implemented -contains $true }
        'toolbox-combat-start' { return $officialRuntimePatterns.Where({ $_.id -eq 'action:Toolbox.choice' }).implemented -contains $true }
        'forgotten-ritual' { return $officialRuntimePatterns.Where({ $_.id -eq 'history:CombatHistory.entries' }).implemented -contains $true }
        'automation-power' { return $officialRuntimePatterns.Where({ $_.id -eq 'power:AutomationPower.cardsLeft' }).implemented -contains $true }
        'infested-prism' { return $officialRuntimePatterns.Where({ $_.id -eq 'power:VitalSparkPower.playersTriggeredThisTurn' }).implemented -contains $true }
        'decimillipede' { return $officialRuntimePatterns.Where({ $_.id -eq 'topology:Decimillipede' }).implemented -contains $true }
        'door-maker' { return $officialRuntimePatterns.Where({ $_.id -eq 'topology:DoorAndDoormaker' }).implemented -contains $true }
        'paels-legion' { return $officialRuntimePatterns.Where({ $_.id -eq 'relic:PaelsLegion.affectedCardPlay' }).implemented -contains $true }
        'tunneler' { return $officialRuntimePatterns.Where({ $_.id -eq 'reconcile:Tunneler.BurrowIntent' }).implemented -contains $true }
        'owl-magistrate-flight' { return ($officialRuntimePatterns.Where({ $_.id -eq 'status:OwlMagistrate.IsFlying' }).implemented -contains $true) -and ($officialRuntimePatterns.Where({ $_.id -eq 'reconcile:OwlMagistrate.FlightState' }).implemented -contains $true) }
        'queen-soulbound' { return $officialRuntimePatterns.Where({ $_.id -eq 'power:ChainsOfBindingPower.boundCardPlayed' }).implemented -contains $true }
        'queen-amalgam-branch' { return ($officialRuntimePatterns.Where({ $_.id -eq 'status:Queen.HasAmalgamDied' }).implemented -contains $true) -and ($officialRuntimePatterns.Where({ $_.id -eq 'topology:QueenAmalgam' }).implemented -contains $true) -and ($officialRuntimePatterns.Where({ $_.id -eq 'reconcile:Queen.AmalgamBranch' }).implemented -contains $true) }
        'osty-summon-roundtrip' { return $officialRuntimePatterns.Where({ $_.id -eq 'reconcile:Osty.LocalPetConsistency' }).implemented -contains $true }
        'osty-revive-roundtrip' { return $officialRuntimePatterns.Where({ $_.id -eq 'reconcile:Osty.LocalPetConsistency' }).implemented -contains $true }
        'osty-enemy-hit-roundtrip' { return $officialRuntimePatterns.Where({ $_.id -eq 'reconcile:Osty.LocalPetConsistency' }).implemented -contains $true }
        'slumbering-beetle' { return ($officialRuntimePatterns.Where({ $_.id -eq 'status:SlumberingBeetle.IsAwake' }).implemented -contains $true) -and ($officialRuntimePatterns.Where({ $_.id -eq 'reconcile:SlumberingBeetle.MoveIntent' }).implemented -contains $true) }
        'lagavulin-matriarch' { return ($officialRuntimePatterns.Where({ $_.id -eq 'status:LagavulinMatriarch.IsAwake' }).implemented -contains $true) -and ($officialRuntimePatterns.Where({ $_.id -eq 'reconcile:LagavulinMatriarch.MoveIntent' }).implemented -contains $true) }
        'bowlbug-rock' { return ($officialRuntimePatterns.Where({ $_.id -eq 'status:BowlbugRock.IsOffBalance' }).implemented -contains $true) -and ($officialRuntimePatterns.Where({ $_.id -eq 'reconcile:GenericTransientStun' }).implemented -contains $true) }
        'thieving-hopper' { return ($officialRuntimePatterns.Where({ $_.id -eq 'status:ThievingHopper.IsHovering' }).implemented -contains $true) -and ($officialRuntimePatterns.Where({ $_.id -eq 'reconcile:GenericTransientStun' }).implemented -contains $true) }
        'fat-gremlin' { return ($officialRuntimePatterns.Where({ $_.id -eq 'status:FatGremlin.IsAwake' }).implemented -contains $true) -and ($officialRuntimePatterns.Where({ $_.id -eq 'reconcile:GenericTransientStun' }).implemented -contains $true) }
        'sneaky-gremlin' { return ($officialRuntimePatterns.Where({ $_.id -eq 'status:SneakyGremlin.IsAwake' }).implemented -contains $true) -and ($officialRuntimePatterns.Where({ $_.id -eq 'reconcile:GenericTransientStun' }).implemented -contains $true) }
        'ceremonial-beast' { return ($officialRuntimePatterns.Where({ $_.id -eq 'status:CeremonialBeast.IsStunnedByPlowRemoval' }).implemented -contains $true) -and ($officialRuntimePatterns.Where({ $_.id -eq 'status:CeremonialBeast.InMidCharge' }).implemented -contains $true) -and ($officialRuntimePatterns.Where({ $_.id -eq 'reconcile:CeremonialBeast.TransientStun' }).implemented -contains $true) }
        'wriggler' { return ($officialRuntimePatterns.Where({ $_.id -eq 'status:Wriggler.StartStunned' }).implemented -contains $true) -and ($officialRuntimePatterns.Where({ $_.id -eq 'reconcile:Wriggler.StartStunned' }).implemented -contains $true) }
        'pen-nib' { return $officialRuntimePatterns.Where({ $_.id -eq 'relic:PenNib.AttackToDouble' }).implemented -contains $true }
        'death-march' { return $officialRuntimePatterns.Where({ $_.id -eq 'history:CombatHistory.entries' }).implemented -contains $true }
        default { return $true }
    }
}

$scenarioRunReport = @"
# Scenario Run Report

- Scenario definitions generated: $($knownCases.Count)
- This file is generated from static definitions and curated audit data.
- Declared support comes from registry coverage.
- Runtime closed loop must be validated in-game via UndoScenarioRunner.

$((@($knownCases) | ForEach-Object {
    "## $($_.title)`r`n- id: $($_.id)`r`n- declared_supported: $(Get-ScenarioDeclaredSupport $_.id)`r`n- runtime_closed_loop: pending_live_validation`r`n- assertions: $([string]::Join(', ', $_.assertions))"
}) -join "`r`n`r`n")
"@
$scenarioRunReport | Set-Content (Join-Path $reportsDir 'scenario-run-report.md')

$unsupportedCapabilityIds = @($officialRuntimePatterns | Where-Object { -not $_.implemented } | ForEach-Object { $_.id })
$runtimePendingIds = @(
    'well-laid-plans:runtime_closed_loop_pending',
    'toolbox-combat-start:runtime_closed_loop_pending',
    'paels-legion:runtime_closed_loop_pending',
    'tunneler:runtime_closed_loop_pending',
    'owl-magistrate-flight:runtime_closed_loop_pending',
    'queen-soulbound:runtime_closed_loop_pending',
    'queen-amalgam-branch:runtime_closed_loop_pending',
    'osty-summon-roundtrip:runtime_closed_loop_pending',
    'osty-revive-roundtrip:runtime_closed_loop_pending',
    'osty-enemy-hit-roundtrip:runtime_closed_loop_pending',
    'slumbering-beetle:runtime_closed_loop_pending',
    'lagavulin-matriarch:runtime_closed_loop_pending',
    'bowlbug-rock:runtime_closed_loop_pending',
    'thieving-hopper:runtime_closed_loop_pending',
    'fat-gremlin:runtime_closed_loop_pending',
    'sneaky-gremlin:runtime_closed_loop_pending',
    'ceremonial-beast:runtime_closed_loop_pending',
    'wriggler:runtime_closed_loop_pending'
)
$unsupportedCapabilityLines = @()
if ($unsupportedCapabilityIds.Count -gt 0) {
    $unsupportedCapabilityLines += $unsupportedCapabilityIds | ForEach-Object { "- $_" }
}
$unsupportedCapabilityLines += $runtimePendingIds | ForEach-Object { "- $_" }
if ($unsupportedCapabilityLines.Count -eq 0) {
    $unsupportedCapabilityLines = @('- none')
}
$unsupportedCapabilitiesReport = @"
# Unsupported Capabilities Report

$($unsupportedCapabilityLines -join "`r`n")
"@
$unsupportedCapabilitiesReport | Set-Content (Join-Path $reportsDir 'unsupported-capabilities-report.md')

Write-Host "Generated audit artifacts under $CacheRoot"



