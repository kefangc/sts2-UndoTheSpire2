using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Nodes.Audio;

namespace UndoTheSpire2;

internal static class UndoAudioLoopTracker
{
    private sealed class TrackedLoopState
    {
        public required string EventPath { get; init; }

        public required bool UsesLoopParam { get; set; }

        public Dictionary<string, float> Parameters { get; } = new(StringComparer.Ordinal);
    }

    private static readonly Dictionary<string, TrackedLoopState> ActiveLoops = new(StringComparer.Ordinal);

    public static bool ShouldPlayLoop(string eventPath, bool usesLoopParam)
    {
        if (string.IsNullOrWhiteSpace(eventPath))
            return true;

        if (ActiveLoops.TryGetValue(eventPath, out TrackedLoopState? existingLoop))
        {
            bool shouldPlay = existingLoop.UsesLoopParam != usesLoopParam;
            existingLoop.UsesLoopParam = usesLoopParam;
            return shouldPlay;
        }

        ActiveLoops[eventPath] = new TrackedLoopState
        {
            EventPath = eventPath,
            UsesLoopParam = usesLoopParam
        };
        return true;
    }

    public static void OnStopLoop(string eventPath)
    {
        if (string.IsNullOrWhiteSpace(eventPath))
            return;

        ActiveLoops.Remove(eventPath);
    }

    public static void OnSetLoopParam(string eventPath, string paramName, float value)
    {
        if (string.IsNullOrWhiteSpace(eventPath)
            || string.IsNullOrWhiteSpace(paramName)
            || !ActiveLoops.TryGetValue(eventPath, out TrackedLoopState? loopState))
        {
            return;
        }

        loopState.Parameters[paramName] = value;
    }

    public static void Clear()
    {
        ActiveLoops.Clear();
    }

    public static IReadOnlyList<UndoAudioLoopState> CaptureSnapshot()
    {
        return
        [
            .. ActiveLoops.Values
                .OrderBy(static state => state.EventPath, StringComparer.Ordinal)
                .Select(static state => new UndoAudioLoopState
                {
                    EventPath = state.EventPath,
                    UsesLoopParam = state.UsesLoopParam,
                    Parameters =
                    [
                        .. state.Parameters
                            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                            .Select(static pair => new UndoAudioLoopParamState
                            {
                                Name = pair.Key,
                                Value = pair.Value
                            })
                    ]
                })
        ];
    }

    public static void ApplySnapshot(IReadOnlyList<UndoAudioLoopState> snapshotStates)
    {
        NAudioManager? audioManager = NAudioManager.Instance;
        if (audioManager == null)
            return;

        Dictionary<string, UndoAudioLoopState> targetStates = snapshotStates
            .Where(static state => !string.IsNullOrWhiteSpace(state.EventPath))
            .ToDictionary(static state => state.EventPath, StringComparer.Ordinal);

        foreach (string activePath in ActiveLoops.Keys.Except(targetStates.Keys, StringComparer.Ordinal).ToList())
        {
            audioManager.StopLoop(activePath);
        }

        foreach ((string eventPath, UndoAudioLoopState targetState) in targetStates)
        {
            if (!ActiveLoops.TryGetValue(eventPath, out TrackedLoopState? activeState)
                || activeState.UsesLoopParam != targetState.UsesLoopParam)
            {
                if (activeState != null)
                    audioManager.StopLoop(eventPath);

                audioManager.PlayLoop(eventPath, targetState.UsesLoopParam);
            }

            foreach (UndoAudioLoopParamState parameterState in targetState.Parameters)
                audioManager.SetParam(eventPath, parameterState.Name, parameterState.Value);
        }
    }
}
