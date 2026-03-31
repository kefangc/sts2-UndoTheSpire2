// 文件说明：集中放置反射、克隆和内部工具函数。
// Coordinates undo/redo history and restore transactions.
// Capture/restore details should live in dedicated services; this type is the orchestrator.
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Cards;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoTheSpire2;

// Shared reflection, cloning, and restore helper utilities.
public sealed partial class UndoController
{
    private const string UndoQueuedForFreeMetaKey = "__undo_queued_for_free";

    private static T GetStaticFieldValue<T>(Type type, string fieldName)
    {
        return UndoReflectionUtil.GetStaticFieldValue<T>(type, fieldName)!;
    }
    private static T GetPrivatePropertyValue<T>(object instance, string propertyName)
    {
        return UndoReflectionUtil.GetPropertyValue<T>(instance, propertyName)!;
    }
    private static NCard CreateCardNode(CardModel card, PileType pileType)
    {
        NCard? cardNode = NCard.Create(card, ModelVisibility.Visible);
        if (cardNode == null)
            throw new InvalidOperationException("Failed to create NCard.");

        cardNode.UpdateVisuals(pileType, CardPreviewMode.Normal);
        return cardNode;
    }

    private static void ClearNodeChildren(Node node)
    {
        foreach (Node child in node.GetChildren().Cast<Node>().ToList())
        {
            child.GetParent()?.RemoveChildSafely(child);
            QueueFreeNodeSafelyOnce(child);
        }
    }

    private static void QueueFreeNodeSafelyOnce(Node? node)
    {
        if (node == null || !GodotObject.IsInstanceValid(node))
            return;

        if (node.HasMeta(UndoQueuedForFreeMetaKey))
            return;

        node.SetMeta(UndoQueuedForFreeMetaKey, true);
        node.QueueFreeSafely();
    }

    private static void QueueFreeNodeSafelyNoPoolOnce(Node? node)
    {
        if (node == null || !GodotObject.IsInstanceValid(node))
            return;

        if (node.HasMeta(UndoQueuedForFreeMetaKey))
            return;

        node.SetMeta(UndoQueuedForFreeMetaKey, true);
        node.QueueFreeSafelyNoPool();
    }

    private static void ClearOptionalNodeChildren(Node node, string path)
    {
        Node? child = node.GetNodeOrNull<Node>(path);
        if (child != null)
            ClearNodeChildren(child);
    }

    private static void HideControl(Node node, string path, Control.MouseFilterEnum mouseFilter = Control.MouseFilterEnum.Ignore)
    {
        Control? control = node.GetNodeOrNull<Control>(path);
        if (control == null)
            return;

        control.Visible = false;
        control.MouseFilter = mouseFilter;
    }

    private static void ShowControl(Node node, string path, Control.MouseFilterEnum mouseFilter = Control.MouseFilterEnum.Stop)
    {
        Control? control = node.GetNodeOrNull<Control>(path);
        if (control == null)
            return;

        control.Visible = true;
        control.MouseFilter = mouseFilter;
    }

    private static void DisableControl(Node node, string path)
    {
        Node? control = node.GetNodeOrNull<Node>(path);
        if (control == null)
            return;

        control.GetType()
            .GetMethod("Disable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .Invoke(control, null);
    }

    private static void ClearCombatManagerCollection(string fieldName)
    {
        object? collection = FindField(typeof(CombatManager), fieldName)?.GetValue(CombatManager.Instance);
        collection?.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(collection, null);
    }

    private static void SetCombatManagerProperty(string propertyName, object? value)
    {
        FindProperty(typeof(CombatManager), propertyName)?.SetValue(CombatManager.Instance, value);
    }

    private static T? GetPrivateFieldValue<T>(object instance, string fieldName) where T : class
    {
        return FindField(instance.GetType(), fieldName)?.GetValue(instance) as T;
    }

    private static void SetPrivateFieldValue(object instance, string fieldName, object? value)
    {
        FindField(instance.GetType(), fieldName)?.SetValue(instance, value);
    }

    private static void SetPrivatePropertyValue(object instance, string propertyName, object? value)
    {
        PropertyInfo? property = FindProperty(instance.GetType(), propertyName);
        MethodInfo? setter = property?.GetSetMethod(true);
        if (setter != null)
        {
            setter.Invoke(instance, [value]);
            return;
        }

                property?.SetValue(instance, value);
    }

    private static bool TrySetRuntimePropertyValue(object instance, PropertyInfo property, string propertyName, object? value)
    {
        if (ShouldSkipRuntimeStateProperty(instance.GetType(), propertyName))
            return false;

        if (TrySetPrivateAutoPropertyBackingField(instance, propertyName, value))
            return true;

        MethodInfo? setter = property.GetSetMethod(true);
        if (setter == null)
            return false;

        setter.Invoke(instance, [value]);
        return true;
    }

    private static bool TrySetPrivateAutoPropertyBackingField(object instance, string propertyName, object? value)
    {
        FieldInfo? backingField = FindField(instance.GetType(), $"<{propertyName}>k__BackingField");
        if (backingField == null)
            return false;

        backingField.SetValue(instance, value);
        return true;
    }

    private static object? InvokePrivateMethod(object instance, string methodName, params object?[]? args)
    {
        return FindMethod(instance.GetType(), methodName)?.Invoke(instance, args);
    }

    private static object? InvokePrivateMethodExact(object instance, string methodName, Type[] parameterTypes, params object?[]? args)
    {
        return FindMethod(instance.GetType(), methodName, parameterTypes)?.Invoke(instance, args);
    }

    private static string DescribeException(Exception ex)
    {
        List<string> parts = [];
        Exception? current = ex;
        while (current != null)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
            current = current.InnerException;
        }

        return string.Join(" | ", parts);
    }

    private static FieldInfo? FindField(Type? type, string name)
    {
        return UndoReflectionUtil.FindField(type, name);
    }

    private static PropertyInfo? FindProperty(Type? type, string name)
    {
        return UndoReflectionUtil.FindProperty(type, name);
    }
    private static MethodInfo? FindMethod(Type? type, string name)
    {
        return UndoReflectionUtil.FindMethod(type, name);
    }

    private static MethodInfo? FindMethod(Type? type, string name, Type[] parameterTypes)
    {
        return UndoReflectionUtil.FindMethod(type, name, parameterTypes);
    }

    private static UndoCombatFullState CreateDerivedCombatState(
        UndoCombatFullState source,
        NetFullCombatState fullState,
        IReadOnlyList<UndoPlayerPileCardCostState>? cardCostStates = null,
        IReadOnlyList<UndoPlayerPileCardRuntimeState>? cardRuntimeStates = null)
    {
        return CreateDerivedCombatState(source, null, fullState, cardCostStates, cardRuntimeStates);
    }

    private static UndoCombatFullState CreateDerivedCombatState(
        UndoCombatFullState source,
        UndoCombatFullState? fallbackSource,
        NetFullCombatState fullState,
        IReadOnlyList<UndoPlayerPileCardCostState>? cardCostStates = null,
        IReadOnlyList<UndoPlayerPileCardRuntimeState>? cardRuntimeStates = null)
    {
        IReadOnlyList<UndoPlayerPileCardCostState> effectiveCardCostStates = cardCostStates ?? source.CardCostStates;
        IReadOnlyList<UndoPlayerPileCardRuntimeState> effectiveCardRuntimeStates = cardRuntimeStates ?? source.CardRuntimeStates;
        UndoSelectionSessionState? derivedSelectionSessionState = CreateDerivedSelectionSessionState(source);
        UndoCombatCardDbState derivedCombatCardDbState = RebuildDerivedCombatCardDbState(source, fallbackSource, fullState);
        IReadOnlyList<UndoCreatureVisualState> derivedCreatureVisualStates = SanitizeDerivedCreatureVisualStates(source.CreatureVisualStates);
        return new UndoCombatFullState(
            fullState,
            source.RoundNumber,
            source.CurrentSide,
            source.SynchronizerCombatState,
            source.NextActionId,
            source.NextHookId,
            source.NextChecksumId,
            source.CombatHistoryState,
            source.ActionKernelState,
            source.MonsterStates,
            effectiveCardCostStates,
            effectiveCardRuntimeStates,
            source.PowerRuntimeStates,
            source.RelicRuntimeStates,
            derivedSelectionSessionState,
            source.FirstInSeriesPlayCounts,
            new RuntimeGraphState
            {
                CardRuntimeStates = effectiveCardRuntimeStates,
                PowerRuntimeStates = source.PowerRuntimeStates,
                RelicRuntimeStates = source.RelicRuntimeStates
            },
            new PresentationHints
            {
                SelectionSessionState = derivedSelectionSessionState
            },
            source.CreatureTopologyStates,
            source.CreatureStatusRuntimeStates,
            derivedCreatureVisualStates,
            derivedCombatCardDbState,
            source.PlayerOrbStates,
            source.PlayerDeckStates,
            source.PlayerPotionStates,
            source.AudioLoopStates,
            source.SchemaVersion, source.ChoiceBranchStates);
    }

    private static UndoCombatFullState CreateDerivedCombatState(
        UndoCombatFullState source,
        UndoCombatFullState? fallbackSource,
        NetFullCombatState fullState)
    {
        RebuildDerivedCardSupplementalStates(source, fallbackSource, fullState, out IReadOnlyList<UndoPlayerPileCardCostState> cardCostStates, out IReadOnlyList<UndoPlayerPileCardRuntimeState> cardRuntimeStates);
        return CreateDerivedCombatState(source, fallbackSource, fullState, cardCostStates, cardRuntimeStates);
    }

    private static void RebuildDerivedCardSupplementalStates(
        UndoCombatFullState primarySource,
        UndoCombatFullState? fallbackSource,
        NetFullCombatState fullState,
        out IReadOnlyList<UndoPlayerPileCardCostState> cardCostStates,
        out IReadOnlyList<UndoPlayerPileCardRuntimeState> cardRuntimeStates)
    {
        List<UndoPlayerPileCardCostState> rebuiltCostStates = [];
        List<UndoPlayerPileCardRuntimeState> rebuiltRuntimeStates = [];

        foreach (NetFullCombatState.PlayerState playerState in fullState.Players)
        {
            List<DerivedCardSupplementalCandidate> primaryCandidates = BuildDerivedCardSupplementalCandidates(primarySource, playerState.playerId);
            List<DerivedCardSupplementalCandidate> fallbackCandidates = fallbackSource == null
                ? []
                : BuildDerivedCardSupplementalCandidates(fallbackSource, playerState.playerId);
            Dictionary<PileType, NetFullCombatState.CombatPileState> pilesByType = playerState.piles.ToDictionary(static pile => pile.pileType);

            foreach (PileType pileType in CombatPileOrder)
            {
                List<UndoCardCostState> pileCostStates = [];
                List<UndoCardRuntimeState> pileRuntimeStates = [];
                if (pilesByType.TryGetValue(pileType, out NetFullCombatState.CombatPileState pileState))
                {
                    for (int cardIndex = 0; cardIndex < pileState.cards.Count; cardIndex++)
                    {
                        NetFullCombatState.CardState cardState = pileState.cards[cardIndex];
                        if (!TryTakeMatchingCardSupplementalState(primaryCandidates, pileType, cardIndex, cardState.card, out UndoCardCostState? costState, out UndoCardRuntimeState? runtimeState)
                            && !TryTakeMatchingCardSupplementalState(fallbackCandidates, pileType, cardIndex, cardState.card, out costState, out runtimeState))
                        {
                            CardModel defaultCard = CardModel.FromSerializable(ClonePacketSerializable(cardState.card));
                            costState = CaptureCardCostState(defaultCard);
                            runtimeState = CaptureDefaultCardRuntimeState(defaultCard);
                        }

                        pileCostStates.Add(costState ?? CaptureCardCostState(CardModel.FromSerializable(ClonePacketSerializable(cardState.card))));
                        pileRuntimeStates.Add(runtimeState ?? CaptureDefaultCardRuntimeState(CardModel.FromSerializable(ClonePacketSerializable(cardState.card))));
                    }
                }

                rebuiltCostStates.Add(new UndoPlayerPileCardCostState
                {
                    PlayerNetId = playerState.playerId,
                    PileType = pileType,
                    Cards = pileCostStates
                });
                rebuiltRuntimeStates.Add(new UndoPlayerPileCardRuntimeState
                {
                    PlayerNetId = playerState.playerId,
                    PileType = pileType,
                    Cards = pileRuntimeStates
                });
            }
        }

        cardCostStates = rebuiltCostStates;
        cardRuntimeStates = rebuiltRuntimeStates;
    }

    private static UndoSelectionSessionState? CreateDerivedSelectionSessionState(UndoCombatFullState source)
    {
        if (source.SelectionSessionState == null && source.PresentationHints.SelectionSessionState == null)
            return null;

        // Derived/synthetic branches represent a resolved post-choice state. Carrying
        // the template branch's active hand/overlay selection session back into the new
        // branch leaves stale choice UI, dangling selected holders, and incorrect redo
        // boundaries. Keep the branch explicitly stable.
        return new UndoSelectionSessionState
        {
            HandSelectionActive = false,
            OverlaySelectionActive = false,
            SupportedChoiceUiActive = false,
            OverlayScreenType = null,
            ChoiceSpec = null
        };
    }

    private static UndoCombatCardDbState RebuildDerivedCombatCardDbState(
        UndoCombatFullState primarySource,
        UndoCombatFullState? fallbackSource,
        NetFullCombatState fullState)
    {
        List<DerivedCombatCardDbCandidate> primaryCandidates = BuildDerivedCombatCardDbCandidates(primarySource);
        List<DerivedCombatCardDbCandidate> fallbackCandidates = fallbackSource == null
            ? []
            : BuildDerivedCombatCardDbCandidates(fallbackSource);
        HashSet<uint> usedCombatCardIds = [];
        List<UndoCombatCardDbEntryState> entries = [];

        foreach (NetFullCombatState.PlayerState playerState in fullState.Players)
        {
            Dictionary<PileType, NetFullCombatState.CombatPileState> pilesByType = playerState.piles.ToDictionary(static pile => pile.pileType);
            foreach (PileType pileType in CombatPileOrder)
            {
                if (!pilesByType.TryGetValue(pileType, out NetFullCombatState.CombatPileState pileState))
                    continue;

                for (int cardIndex = 0; cardIndex < pileState.cards.Count; cardIndex++)
                {
                    SerializableCard card = ClonePacketSerializable(pileState.cards[cardIndex].card);
                    if (!TryTakeMatchingCombatCardDbCandidate(primaryCandidates, usedCombatCardIds, playerState.playerId, pileType, cardIndex, card, out DerivedCombatCardDbCandidate? candidate)
                        && !TryTakeMatchingCombatCardDbCandidate(fallbackCandidates, usedCombatCardIds, playerState.playerId, pileType, cardIndex, card, out candidate))
                    {
                        continue;
                    }

                    usedCombatCardIds.Add(candidate.CombatCardId);
                    entries.Add(new UndoCombatCardDbEntryState
                    {
                        CombatCardId = candidate.CombatCardId,
                        Card = CreateDerivedCardRef(playerState.playerId, pileType, cardIndex, card)
                    });
                }
            }
        }

        uint nextId = Math.Max(primarySource.CombatCardDbState.NextId, fallbackSource?.CombatCardDbState.NextId ?? 0U);
        if (entries.Count > 0)
            nextId = Math.Max(nextId, entries.Max(static entry => entry.CombatCardId) + 1U);

        return new UndoCombatCardDbState
        {
            Entries = [.. entries.OrderBy(static entry => entry.CombatCardId)],
            NextId = nextId
        };
    }

    private static List<DerivedCombatCardDbCandidate> BuildDerivedCombatCardDbCandidates(UndoCombatFullState source)
    {
        List<DerivedCombatCardDbCandidate> candidates = [];
        foreach (UndoCombatCardDbEntryState entry in source.CombatCardDbState.Entries)
        {
            candidates.Add(new DerivedCombatCardDbCandidate
            {
                CombatCardId = entry.CombatCardId,
                PlayerNetId = entry.Card.PlayerNetId,
                PileType = entry.Card.PileType,
                PileIndex = entry.Card.PileIndex,
                Card = ClonePacketSerializable(entry.Card.Card)
            });
        }

        return candidates;
    }

    private static bool TryTakeMatchingCombatCardDbCandidate(
        List<DerivedCombatCardDbCandidate> candidates,
        ISet<uint> usedCombatCardIds,
        ulong playerNetId,
        PileType pileType,
        int cardIndex,
        SerializableCard card,
        out DerivedCombatCardDbCandidate? matchedCandidate)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            DerivedCombatCardDbCandidate candidate = candidates[i];
            if (candidate.Used
                || usedCombatCardIds.Contains(candidate.CombatCardId)
                || candidate.PlayerNetId != playerNetId
                || candidate.PileType != pileType
                || candidate.PileIndex != cardIndex
                || !PacketDataEquals(candidate.Card, card))
            {
                continue;
            }

            candidate.Used = true;
            matchedCandidate = candidate;
            return true;
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            DerivedCombatCardDbCandidate candidate = candidates[i];
            if (candidate.Used || usedCombatCardIds.Contains(candidate.CombatCardId) || !PacketDataEquals(candidate.Card, card))
                continue;

            candidate.Used = true;
            matchedCandidate = candidate;
            return true;
        }

        matchedCandidate = null;
        return false;
    }

    private static CardRef CreateDerivedCardRef(ulong playerNetId, PileType pileType, int pileIndex, SerializableCard card)
    {
        return new CardRef
        {
            Card = ClonePacketSerializable(card),
            PlayerNetId = playerNetId,
            PileType = pileType,
            PileIndex = pileIndex
        };
    }

    private static IReadOnlyList<UndoCreatureVisualState> SanitizeDerivedCreatureVisualStates(IReadOnlyList<UndoCreatureVisualState> creatureVisualStates)
    {
        if (creatureVisualStates.Count == 0)
            return creatureVisualStates;

        return
        [
            .. creatureVisualStates.Select(static state => new UndoCreatureVisualState
            {
                CreatureKey = state.CreatureKey,
                VisualDefaultScale = state.VisualDefaultScale,
                VisualHue = state.VisualHue,
                TempScale = state.TempScale,
                TrackStates =
                [
                    .. state.TrackStates.Where(static trackState => !string.IsNullOrWhiteSpace(trackState.AnimationName))
                        .Select(static trackState => new UndoCreatureTrackState
                        {
                            RelativePath = trackState.RelativePath,
                            TrackIndex = trackState.TrackIndex,
                            AnimationName = trackState.AnimationName,
                            Loop = trackState.Loop,
                            TrackTime = trackState.TrackTime
                        })
                ],
                CanvasStates = state.CanvasStates,
                ParticleStates = state.ParticleStates,
                ShaderParamStates = state.ShaderParamStates,
                StateDisplayState = state.StateDisplayState,
                AnimatorState = state.AnimatorState
            })
        ];
    }

    private static List<DerivedCardSupplementalCandidate> BuildDerivedCardSupplementalCandidates(UndoCombatFullState source, ulong playerNetId)
    {
        NetFullCombatState.PlayerState playerState = default;
        bool foundPlayer = false;
        foreach (NetFullCombatState.PlayerState candidate in source.FullState.Players)
        {
            if (candidate.playerId != playerNetId)
                continue;

            playerState = candidate;
            foundPlayer = true;
            break;
        }

        if (!foundPlayer)
            return [];

        IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardCostState>>? costStatesByPile = GetCardCostStatesForPlayer(source, playerNetId);
        IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardRuntimeState>>? runtimeStatesByPile = GetCardRuntimeStatesForPlayer(source, playerNetId);
        Dictionary<PileType, NetFullCombatState.CombatPileState> pilesByType = playerState.piles.ToDictionary(static pile => pile.pileType);
        List<DerivedCardSupplementalCandidate> candidates = [];

        foreach (PileType pileType in CombatPileOrder)
        {
            if (!pilesByType.TryGetValue(pileType, out NetFullCombatState.CombatPileState pileState))
                continue;

            IReadOnlyList<UndoCardCostState>? pileCostStates = null;
            IReadOnlyList<UndoCardRuntimeState>? pileRuntimeStates = null;
            costStatesByPile?.TryGetValue(pileType, out pileCostStates);
            runtimeStatesByPile?.TryGetValue(pileType, out pileRuntimeStates);
            for (int cardIndex = 0; cardIndex < pileState.cards.Count; cardIndex++)
            {
                candidates.Add(new DerivedCardSupplementalCandidate
                {
                    PileType = pileType,
                    CardIndex = cardIndex,
                    Card = ClonePacketSerializable(pileState.cards[cardIndex].card),
                    CostState = pileCostStates != null && cardIndex < pileCostStates.Count ? pileCostStates[cardIndex] : null,
                    RuntimeState = pileRuntimeStates != null && cardIndex < pileRuntimeStates.Count ? pileRuntimeStates[cardIndex] : null
                });
            }
        }

        return candidates;
    }

    private static bool TryTakeMatchingCardSupplementalState(
        List<DerivedCardSupplementalCandidate> candidates,
        PileType pileType,
        int cardIndex,
        SerializableCard card,
        out UndoCardCostState? costState,
        out UndoCardRuntimeState? runtimeState)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            DerivedCardSupplementalCandidate candidate = candidates[i];
            if (candidate.Used
                || candidate.PileType != pileType
                || candidate.CardIndex != cardIndex
                || !PacketDataEquals(candidate.Card, card))
            {
                continue;
            }

            candidate.Used = true;
            costState = candidate.CostState;
            runtimeState = candidate.RuntimeState;
            return true;
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            DerivedCardSupplementalCandidate candidate = candidates[i];
            if (candidate.Used || !PacketDataEquals(candidate.Card, card))
                continue;

            candidate.Used = true;
            costState = candidate.CostState;
            runtimeState = candidate.RuntimeState;
            return true;
        }

        costState = null;
        runtimeState = null;
        return false;
    }

    private static SerializableRun CloneRun(SerializableRun run)
    {
        string json = SaveManager.ToJson(run);
        ReadSaveResult<SerializableRun> result = SaveManager.FromJson<SerializableRun>(json);
        if (!result.Success || result.SaveData == null)
        {
            throw new InvalidOperationException(
                $"Failed to clone SerializableRun. Status={result.Status} Msg={result.ErrorMessage}");
        }

        return result.SaveData;
    }

    private static NetFullCombatState CloneFullState(NetFullCombatState state)
    {
        return ClonePacketSerializable(state);
    }

    private static T ClonePacketSerializable<T>(T value) where T : IPacketSerializable, new()
    {
        PacketReader reader = new();
        reader.Reset(SerializePacketSerializable(value));
        return reader.Read<T>();
    }

    private static bool PacketDataEquals<T>(T left, T right) where T : IPacketSerializable
    {
        return SerializePacketSerializable(left).AsSpan().SequenceEqual(SerializePacketSerializable(right));
    }

    private static byte[] SerializePacketSerializable<T>(T value) where T : IPacketSerializable
    {
        PacketWriter writer = new() { WarnOnGrow = false };
        writer.Write(value);
        writer.ZeroByteRemainder();

        byte[] buffer = new byte[writer.BytePosition];
        Array.Copy(writer.Buffer, buffer, writer.BytePosition);
        return buffer;
    }

    private static async Task WaitForExecutorToUnpauseAsync()
    {
        while (RunManager.Instance.ActionExecutor.IsPaused)
            await WaitOneFrameAsync();
    }

    private static async Task WaitForChoiceUiReadyAsync()
    {
        for (int i = 0; i < 240; i++)
        {
            if (IsChoiceUiReady())
                return;

            await WaitOneFrameAsync();
        }
    }

    private static bool IsChoiceUiReady()
    {
        if (NOverlayStack.Instance?.ScreenCount > 0)
            return true;

        if (NCombatRoom.Instance?.Ui?.Hand.IsInCardSelection == true)
            return true;

        if (NTargetManager.Instance?.IsInSelection == true)
            return true;

        return false;
    }

    private async Task WaitForReplayBranchAdvanceAsync(int baselineReplayEventCount)
    {
        for (int attempt = 0; attempt < 120; attempt++)
        {
            if (GetCurrentReplayEventCount() > baselineReplayEventCount)
                return;

            await WaitOneFrameAsync();
        }
    }
    private static async Task WaitForReplayToSettleAsync()
    {
        while (true)
        {
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

            if (RunManager.Instance.ActionQueueSet.IsEmpty)
                break;

            GameAction? action = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
            if (action?.State == GameActionState.GatheringPlayerChoice)
                break;

            await WaitOneFrameAsync();
        }

        await WaitOneFrameAsync();
    }

    private static async Task WaitOneFrameAsync()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
            throw new InvalidOperationException("Main loop is not a SceneTree.");

        TaskCompletionSource<bool> completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Callable callback = default;
        callback = Callable.From(() =>
        {
            if (tree.IsConnected(SceneTree.SignalName.ProcessFrame, callback))
                tree.Disconnect(SceneTree.SignalName.ProcessFrame, callback);

            completionSource.TrySetResult(true);
        });

        tree.Connect(SceneTree.SignalName.ProcessFrame, callback, (uint)GodotObject.ConnectFlags.OneShot);
        await completionSource.Task;
    }

    private static Task<T?> RunOnMainThreadAsync<T>(Func<T?> action)
    {
        if (NGame.IsMainThread())
            return Task.FromResult(action());

        TaskCompletionSource<T?> completionSource = new();
        Node? dispatcher = NGame.Instance ?? (Node?)NRun.Instance ?? NCombatRoom.Instance;
        if (dispatcher == null)
        {
            completionSource.SetException(new InvalidOperationException("No main-thread dispatcher was available."));
            return completionSource.Task;
        }

        Callable.From(() =>
        {
            try
            {
                completionSource.SetResult(action());
            }
            catch (Exception ex)
            {
                completionSource.SetException(ex);
            }
        }).CallDeferred();

        return completionSource.Task;
    }
}

