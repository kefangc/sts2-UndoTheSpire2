// 文件说明：保存怪物意图和相关战斗状态。
// Captures monster move-machine metadata that supplements official full state.
// Sleep/stun/hover runtime booleans now live in CreatureStatusRuntimeState.
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace UndoTheSpire2;

internal sealed class UndoMonsterState
{
    public required string CreatureKey { get; init; }

    public string? SlotName { get; init; }

    public float? VisualDefaultScale { get; init; }

    public float? VisualHue { get; init; }

    public string? NextMoveId { get; init; }

    public string? CurrentStateId { get; init; }

    public bool IsHovering { get; init; }

    public bool SpawnedThisTurn { get; init; }

    public bool PerformedFirstMove { get; init; }

    public bool NextMovePerformedAtLeastOnce { get; init; }

    public string? TransientNextMoveFollowUpId { get; init; }

    public string? SpecialNodeStateKey { get; init; }

    public int? StarterMoveIndex { get; init; }

    public int? TurnsUntilSummonable { get; init; }

    public int? CallForBackupCount { get; init; }

    public ModelId? FabricatorLastSpawnedMonsterId { get; init; }

    public int? LivingFogBloatAmount { get; init; }

    public bool? ToughEggIsHatched { get; init; }

    public bool? ToughEggVisualHatched { get; init; }

    public string? ToughEggAfterHatchedStateId { get; init; }

    public Vector2? ToughEggHatchPos { get; init; }

    public IReadOnlyList<string> StateLogIds { get; init; } = [];
}
