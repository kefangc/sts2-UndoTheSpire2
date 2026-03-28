// 鏂囦欢璇存槑锛氫繚瀛樻墍鏈夋垬鏂?creature 鐨勫彲鎭㈠瑙嗚缂╂斁/鑹茬浉鐘舵€併€?
using System.Collections.Generic;
using Godot;

namespace UndoTheSpire2;

internal sealed class UndoCreatureVisualState
{
    public required string CreatureKey { get; init; }

    public float? VisualDefaultScale { get; init; }

    public float? VisualHue { get; init; }

    public float? TempScale { get; init; }

    public IReadOnlyList<UndoCreatureTrackState> TrackStates { get; init; } = [];

    public IReadOnlyList<UndoCreatureCanvasState> CanvasStates { get; init; } = [];

    public IReadOnlyList<UndoCreatureParticleState> ParticleStates { get; init; } = [];

    public IReadOnlyList<UndoCreatureShaderParamState> ShaderParamStates { get; init; } = [];

    public UndoCreatureStateDisplayState? StateDisplayState { get; init; }

    public UndoCreatureAnimatorState? AnimatorState { get; init; }
}

internal sealed class UndoCreatureAnimatorState
{
    public required string StateId { get; init; }

    public bool? HasLooped { get; init; }
}

internal sealed class UndoCreatureStateDisplayState
{
    public Vector2? OriginalPosition { get; init; }

    public Vector2? CurrentPosition { get; init; }

    public bool? Visible { get; init; }

    public Color? Modulate { get; init; }

    public UndoCreatureHealthBarState? HealthBarState { get; init; }
}

internal sealed class UndoCreatureHealthBarState
{
    public Vector2? OriginalBlockPosition { get; init; }

    public Vector2? CurrentBlockPosition { get; init; }

    public bool? BlockVisible { get; init; }

    public bool? BlockOutlineVisible { get; init; }

    public Color? BlockModulate { get; init; }
}

internal sealed class UndoCreatureTrackState
{
    public required string RelativePath { get; init; }

    public int TrackIndex { get; init; }

    public required string AnimationName { get; init; }

    public bool? Loop { get; init; }

    public float? TrackTime { get; init; }
}

internal sealed class UndoCreatureCanvasState
{
    public required string RelativePath { get; init; }

    public bool Visible { get; init; }
}

internal sealed class UndoCreatureParticleState
{
    public required string RelativePath { get; init; }

    public bool Emitting { get; init; }
}

internal enum UndoCreatureShaderMaterialBindingKind
{
    CanvasItemMaterial,
    SpineNormalMaterial,
    SpineSlotNormalMaterial
}

internal enum UndoCreatureShaderParamValueKind
{
    Bool,
    Int,
    Float,
    Vector2,
    Color
}

internal sealed class UndoCreatureShaderParamState
{
    public required string RelativePath { get; init; }

    public required string ParamName { get; init; }

    public UndoCreatureShaderMaterialBindingKind BindingKind { get; init; }

    public UndoCreatureShaderParamValueKind ValueKind { get; init; }

    public bool BoolValue { get; init; }

    public int IntValue { get; init; }

    public float FloatValue { get; init; }

    public Vector2 Vector2Value { get; init; }

    public Color ColorValue { get; init; }
}
