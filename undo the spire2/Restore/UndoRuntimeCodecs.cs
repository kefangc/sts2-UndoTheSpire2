// 文件说明：恢复卡牌、能力、遗物等通用运行时属性�?
using System.Reflection;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace UndoTheSpire2;
internal static partial class UndoRuntimeStateCodecRegistry
{
    private static readonly IReadOnlyList<IUndoCardRuntimeCodec> CardCodecs =
    [
        new UpMySleeveCardCodec(),
        new DamageGrowthCardCodec(),
        new SovereignBladeCardCodec()
    ];

    private static readonly IReadOnlyList<IUndoPowerRuntimeCodec> PowerCodecs =
    [
        new AutomationCardsLeftPowerCodec(),
        new CardsPlayedThisTurnPowerCodec(),
        new JugglingAttacksPlayedPowerCodec(),
        new PowerIntFieldsCodec<DarkEmbracePower>("power:DarkEmbracePower.etherealCount", true, "etherealCount"),
        new PowerIntFieldsCodec<FeralPower>("power:FeralPower.zeroCostAttacksPlayed", true, "zeroCostAttacksPlayed"),
        new PowerDecimalFieldsCodec<HardenedShellPower>("power:HardenedShellPower.damageReceivedThisTurn", true, "damageReceivedThisTurn"),
        new PowerIntFieldsCodec<OrbitPower>("power:OrbitPower.progress", true, "energySpent", "triggerCount"),
        new PowerIntFieldsCodec<OutbreakPower>("power:OutbreakPower.timesPoisoned", true, "timesPoisoned"),
        new VitalSparkTriggeredPlayersPowerCodec(),
        new AfterimagePlayedCardsPowerCodec(),
        new NightmareSelectedCardPowerCodec(),
        new DampenPowerCodec(),
        new DoorRevivalHalfDeadPowerCodec(),
        new RevivePendingPowerCodec(),
        new PrivateBoolFieldPowerCodec<BeaconOfHopePower>("power:BeaconOfHopePower.hasAlreadyBeenGivenBlock", "_hasAlreadyBeenGivenBlock"),
        new PrivateBoolFieldPowerCodec<NemesisPower>("power:NemesisPower.shouldApplyIntangible", "_shouldApplyIntangible"),
        new PrivateBoolFieldPowerCodec<RitualPower>("power:RitualPower.wasJustAppliedByEnemy", "_wasJustAppliedByEnemy"),
        new PrivateBoolFieldPowerCodec<TemporaryDexterityPower>("power:TemporaryDexterityPower.shouldIgnoreNextInstance", "_shouldIgnoreNextInstance"),
        new PrivateBoolFieldPowerCodec<TemporaryFocusPower>("power:TemporaryFocusPower.shouldIgnoreNextInstance", "_shouldIgnoreNextInstance"),
        new PrivateBoolFieldPowerCodec<TemporaryStrengthPower>("power:TemporaryStrengthPower.shouldIgnoreNextInstance", "_shouldIgnoreNextInstance"),
        new ChainsOfBindingBoundCardPlayedPowerCodec()
    ];

    private static readonly IReadOnlyList<IUndoRelicRuntimeCodec> RelicCodecs =
    [
        new GenericRelicScalarFieldsCodec(),
        new GenericRelicCardRefFieldsCodec(),
        new GenericRelicCardPlayFieldsCodec(),
        new GenericRelicCardRefCollectionsCodec(),
        new GenericRelicPowerRefCollectionsCodec(),
        new PaelsLegionAffectedCardPlayRelicCodec(),
        new PenNibAttackToDoubleRelicCodec(),
        new CardsPlayedTurnCounterRelicCodec()
    ];

    public static HashSet<string> GetImplementedCodecIds()
    {
        return CardCodecs.Select(static codec => codec.CodecId)
            .Concat(PowerCodecs.Select(static codec => codec.CodecId))
            .Concat(RelicCodecs.Select(static codec => codec.CodecId))
            .ToHashSet(StringComparer.Ordinal);
    }

    public static IReadOnlyList<UndoComplexRuntimeState> CaptureCardStates(CardModel card, UndoRuntimeCaptureContext context)
    {
        List<UndoComplexRuntimeState> states = [];
        foreach (IUndoCardRuntimeCodec codec in CardCodecs)
        {
            if (!codec.CanHandle(card))
                continue;

            UndoComplexRuntimeState? state = codec.Capture(card, context);
            if (state != null)
                states.Add(state);
        }

        return states;
    }

    public static void RestoreCardStates(CardModel card, IReadOnlyList<UndoComplexRuntimeState> states, UndoRuntimeRestoreContext context)
    {
        foreach (UndoComplexRuntimeState state in states)
        {
            IUndoCardRuntimeCodec? codec = CardCodecs.FirstOrDefault(candidate => candidate.CodecId == state.CodecId && candidate.CanHandle(card));
            codec?.Restore(card, state, context);
        }
    }

    public static IReadOnlyList<UndoComplexRuntimeState> CapturePowerStates(PowerModel power, UndoRuntimeCaptureContext context)
    {
        List<UndoComplexRuntimeState> states = [];
        foreach (IUndoPowerRuntimeCodec codec in PowerCodecs)
        {
            if (!codec.CanHandle(power))
                continue;

            UndoComplexRuntimeState? state = codec.Capture(power, context);
            if (state != null)
                states.Add(state);
        }

        return states;
    }

    public static void RestorePowerStates(PowerModel power, IReadOnlyList<UndoComplexRuntimeState> states, UndoRuntimeRestoreContext context)
    {
        foreach (UndoComplexRuntimeState state in states)
        {
            IUndoPowerRuntimeCodec? codec = PowerCodecs.FirstOrDefault(candidate => candidate.CodecId == state.CodecId && candidate.CanHandle(power));
            codec?.Restore(power, state, context);
        }
    }

    public static IReadOnlyList<UndoComplexRuntimeState> CaptureRelicStates(RelicModel relic, UndoRuntimeCaptureContext context)
    {
        List<UndoComplexRuntimeState> states = [];
        foreach (IUndoRelicRuntimeCodec codec in RelicCodecs)
        {
            if (!codec.CanHandle(relic))
                continue;

            UndoComplexRuntimeState? state = codec.Capture(relic, context);
            if (state != null)
                states.Add(state);
        }

        return states;
    }

    public static bool? CaptureRelicIsActivatingForSavestate(RelicModel relic)
    {
        if (!TryGetRelicIsActivating(relic, out bool isActivating))
            return null;

        return HasTransientActivationDisplayState(relic) ? false : isActivating;
    }

    public static void RestoreRelicStates(RelicModel relic, IReadOnlyList<UndoComplexRuntimeState> states, UndoRuntimeRestoreContext context)
    {
        foreach (UndoComplexRuntimeState state in states)
        {
            IUndoRelicRuntimeCodec? codec = RelicCodecs.FirstOrDefault(candidate => candidate.CodecId == state.CodecId && candidate.CanHandle(relic));
            codec?.Restore(relic, state, context);
        }
    }

    public static bool ShouldNormalizeRelicActivationForSavestate(RelicModel relic)
    {
        return HasTransientActivationDisplayState(relic);
    }

    public static void NormalizeRelicDisplayForSavestateRestore(RelicModel relic)
    {
        if (HasTransientActivationDisplayState(relic))
            TrySetRelicIsActivating(relic, false);

        RefreshRelicCounterDisplay(relic);
    }

    public static void RefreshPowerDisplays(CombatState combatState)
    {
        foreach (Creature creature in combatState.Creatures)
        {
            foreach (PowerModel power in creature.Powers)
                InvokeDisplayAmountChanged(power);
        }
    }

    private static void InvokeDisplayAmountChanged(PowerModel power)
    {
        UndoReflectionUtil.FindMethod(power.GetType(), "InvokeDisplayAmountChanged")?.Invoke(power, []);
    }

    private static object? GetPowerInternalData(PowerModel power)
    {
        return UndoReflectionUtil.FindField(typeof(PowerModel), "_internalData")?.GetValue(power);
    }

    private static bool TryGetPowerCardsPlayedThisTurn(PowerModel power, out int value)
    {
        object? internalData = GetPowerInternalData(power);
        if (internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), "cardsPlayedThisTurn")?.GetValue(internalData) is int internalValue)
        {
            value = internalValue;
            return true;
        }

        if (UndoReflectionUtil.FindProperty(power.GetType(), "CardsPlayedThisTurn")?.GetValue(power) is int propertyValue)
        {
            value = propertyValue;
            return true;
        }

        if (UndoReflectionUtil.FindField(power.GetType(), "_cardsPlayedThisTurn")?.GetValue(power) is int fieldValue)
        {
            value = fieldValue;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TrySetPowerCardsPlayedThisTurn(PowerModel power, int value)
    {
        object? internalData = GetPowerInternalData(power);
        if (internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), "cardsPlayedThisTurn") != null)
            return UndoReflectionUtil.TrySetFieldValue(internalData, "cardsPlayedThisTurn", value);

        if (UndoReflectionUtil.FindProperty(power.GetType(), "CardsPlayedThisTurn") != null)
            return UndoReflectionUtil.TrySetPropertyValue(power, "CardsPlayedThisTurn", value);

        return UndoReflectionUtil.TrySetFieldValue(power, "_cardsPlayedThisTurn", value);
    }

    private static bool TryGetPowerIntField(PowerModel power, string fieldName, bool preferInternalData, out int value)
    {
        object? internalData = GetPowerInternalData(power);
        if (preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName)?.GetValue(internalData) is int internalValue)
        {
            value = internalValue;
            return true;
        }

        if (UndoReflectionUtil.FindField(power.GetType(), fieldName)?.GetValue(power) is int fieldValue)
        {
            value = fieldValue;
            return true;
        }

        if (!preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName)?.GetValue(internalData) is int fallbackInternalValue)
        {
            value = fallbackInternalValue;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TrySetPowerIntField(PowerModel power, string fieldName, int value, bool preferInternalData)
    {
        object? internalData = GetPowerInternalData(power);
        if (preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName) != null)
            return UndoReflectionUtil.TrySetFieldValue(internalData, fieldName, value);

        if (UndoReflectionUtil.FindField(power.GetType(), fieldName) != null)
            return UndoReflectionUtil.TrySetFieldValue(power, fieldName, value);

        if (!preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName) != null)
            return UndoReflectionUtil.TrySetFieldValue(internalData, fieldName, value);

        return false;
    }

    private static bool TryGetPowerDecimalField(PowerModel power, string fieldName, bool preferInternalData, out decimal value)
    {
        object? internalData = GetPowerInternalData(power);
        if (preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName)?.GetValue(internalData) is decimal internalValue)
        {
            value = internalValue;
            return true;
        }

        if (UndoReflectionUtil.FindField(power.GetType(), fieldName)?.GetValue(power) is decimal fieldValue)
        {
            value = fieldValue;
            return true;
        }

        if (!preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName)?.GetValue(internalData) is decimal fallbackInternalValue)
        {
            value = fallbackInternalValue;
            return true;
        }

        value = 0m;
        return false;
    }

    private static bool TrySetPowerDecimalField(PowerModel power, string fieldName, decimal value, bool preferInternalData)
    {
        object? internalData = GetPowerInternalData(power);
        if (preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName) != null)
            return UndoReflectionUtil.TrySetFieldValue(internalData, fieldName, value);

        if (UndoReflectionUtil.FindField(power.GetType(), fieldName) != null)
            return UndoReflectionUtil.TrySetFieldValue(power, fieldName, value);

        if (!preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName) != null)
            return UndoReflectionUtil.TrySetFieldValue(internalData, fieldName, value);

        return false;
    }

    private static bool TryGetPowerBoolField(PowerModel power, string fieldName, bool preferInternalData, out bool value)
    {
        object? internalData = GetPowerInternalData(power);
        if (preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName)?.GetValue(internalData) is bool internalBool)
        {
            value = internalBool;
            return true;
        }

        if (UndoReflectionUtil.FindField(power.GetType(), fieldName)?.GetValue(power) is bool fieldBool)
        {
            value = fieldBool;
            return true;
        }

        if (!preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName)?.GetValue(internalData) is bool fallbackInternalBool)
        {
            value = fallbackInternalBool;
            return true;
        }

        value = false;
        return false;
    }

    private static bool TrySetPowerBoolField(PowerModel power, string fieldName, bool value, bool preferInternalData)
    {
        object? internalData = GetPowerInternalData(power);
        if (preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName) != null)
            return UndoReflectionUtil.TrySetFieldValue(internalData, fieldName, value);

        if (UndoReflectionUtil.FindField(power.GetType(), fieldName) != null)
            return UndoReflectionUtil.TrySetFieldValue(power, fieldName, value);

        if (!preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName) != null)
            return UndoReflectionUtil.TrySetFieldValue(internalData, fieldName, value);

        return false;
    }

    private static bool IsCardsPlayedTurnCounterRelic(RelicModel relic)
    {
        return relic is BrilliantScarf or DiamondDiadem or Pocketwatch or VelvetChoker;
    }

    private static IEnumerable<FieldInfo> GetDeclaredScalarRuntimeFields(Type type)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return type.GetFields(Flags)
            .Where(static field => !field.IsStatic && !field.IsInitOnly && !field.IsLiteral)
            .Where(static field => !field.Name.StartsWith("<", StringComparison.Ordinal))
            .Where(field =>
            {
                Type fieldType = field.FieldType;
                return fieldType == typeof(bool) || fieldType == typeof(int) || fieldType.IsEnum;
            })
            .Where(field => !HasComparableRuntimeProperty(type, field.Name, field.FieldType));
    }

    private static IEnumerable<PropertyInfo> GetDeclaredReferenceRuntimeProperties(Type type, Type valueType)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return type.GetProperties(Flags)
            .Where(static property => property.GetIndexParameters().Length == 0)
            .Where(static property => property.CanRead)
            .Where(property => property.PropertyType == valueType)
            .Where(static property => property.Name != "Status");
    }

    private static IEnumerable<FieldInfo> GetDeclaredReferenceRuntimeFields(Type type, Type valueType)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return type.GetFields(Flags)
            .Where(static field => !field.IsStatic && !field.IsInitOnly && !field.IsLiteral)
            .Where(static field => !field.Name.StartsWith("<", StringComparison.Ordinal))
            .Where(field => field.FieldType == valueType)
            .Where(field => !HasComparableRuntimeProperty(type, field.Name, valueType));
    }

    private static IEnumerable<PropertyInfo> GetDeclaredCollectionRuntimeProperties(Type type, Type elementType)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return type.GetProperties(Flags)
            .Where(static property => property.GetIndexParameters().Length == 0)
            .Where(static property => property.CanRead)
            .Where(property => property.SetMethod != null || UndoReflectionUtil.FindField(type, $"<{property.Name}>k__BackingField") != null)
            .Where(property => TryGetCollectionElementType(property.PropertyType, out Type? candidate) && candidate == elementType);
    }

    private static IEnumerable<FieldInfo> GetDeclaredCollectionRuntimeFields(Type type, Type elementType)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return type.GetFields(Flags)
            .Where(static field => !field.IsStatic && !field.IsInitOnly && !field.IsLiteral)
            .Where(static field => !field.Name.StartsWith("<", StringComparison.Ordinal))
            .Where(field => TryGetCollectionElementType(field.FieldType, out Type? candidate) && candidate == elementType);
    }

    private static bool TryGetCollectionElementType(Type type, out Type? elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return elementType != null;
        }

        Type? collectionInterface = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ICollection<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(interfaceType =>
                interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == typeof(ICollection<>));
        if (collectionInterface != null)
        {
            elementType = collectionInterface.GetGenericArguments()[0];
            return true;
        }

        elementType = null;
        return false;
    }

    private static bool HasComparableRuntimeProperty(Type type, string memberName, Type memberType)
    {
        string normalizedFieldName = NormalizeRuntimeFieldName(memberName);
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Any(property =>
                string.Equals(property.Name, normalizedFieldName, StringComparison.OrdinalIgnoreCase)
                && (property.PropertyType == memberType
                    || (TryGetCollectionElementType(property.PropertyType, out Type? propertyElementType)
                        && TryGetCollectionElementType(memberType, out Type? fieldElementType)
                        && propertyElementType == fieldElementType)));
    }

    private static string NormalizeRuntimeFieldName(string fieldName)
    {
        return fieldName.StartsWith("_", StringComparison.Ordinal) && fieldName.Length > 1
            ? char.ToUpperInvariant(fieldName[1]) + fieldName[2..]
            : fieldName;
    }

    private static PowerModel? ResolvePowerRef(IReadOnlyDictionary<string, Creature> creaturesByKey, PowerRef powerRef)
    {
        Creature? owner = UndoStableRefs.ResolveCreature(creaturesByKey, powerRef.OwnerCreatureKey);
        if (owner == null)
            return null;

        int ordinal = 0;
        foreach (PowerModel power in owner.Powers)
        {
            if (power.Id != powerRef.PowerId)
                continue;

            if (ordinal == powerRef.Ordinal)
                return power;

            ordinal++;
        }

        return null;
    }

    private static bool TryReplaceCollectionItems<TItem>(object collection, IReadOnlyList<TItem> items)
    {
        MethodInfo? clearMethod = collection.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo? addMethod = collection.GetType().GetMethod("Add", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [typeof(TItem)], null);
        if (clearMethod == null || addMethod == null)
            return false;

        clearMethod.Invoke(collection, null);
        foreach (TItem item in items)
            addMethod.Invoke(collection, [item]);

        return true;
    }

    private static bool TryGetRelicCardsPlayedThisTurn(RelicModel relic, out int value)
    {
        if (UndoReflectionUtil.FindProperty(relic.GetType(), "CardsPlayedThisTurn")?.GetValue(relic) is int propertyValue)
        {
            value = propertyValue;
            return true;
        }

        if (UndoReflectionUtil.FindField(relic.GetType(), "_cardsPlayedThisTurn")?.GetValue(relic) is int fieldValue)
        {
            value = fieldValue;
            return true;
        }

        value = 0;
        return false;
    }

    private static void SetRelicCardsPlayedThisTurn(RelicModel relic, int value)
    {
        if (UndoReflectionUtil.FindProperty(relic.GetType(), "CardsPlayedThisTurn") != null)
        {
            UndoReflectionUtil.TrySetPropertyValue(relic, "CardsPlayedThisTurn", value);
            return;
        }

        UndoReflectionUtil.TrySetFieldValue(relic, "_cardsPlayedThisTurn", value);
    }

    private static bool HasTransientActivationDisplayState(RelicModel relic)
    {
        return UndoReflectionUtil.FindProperty(relic.GetType(), "IsActivating")?.PropertyType == typeof(bool)
            && UndoReflectionUtil.FindMethod(relic.GetType(), "DoActivateVisuals") != null;
    }

    private static bool TryGetRelicIsActivating(RelicModel relic, out bool value)
    {
        if (UndoReflectionUtil.FindProperty(relic.GetType(), "IsActivating")?.GetValue(relic) is bool propertyValue)
        {
            value = propertyValue;
            return true;
        }

        if (UndoReflectionUtil.FindField(relic.GetType(), "_isActivating")?.GetValue(relic) is bool fieldValue)
        {
            value = fieldValue;
            return true;
        }

        value = false;
        return false;
    }

    private static bool TrySetRelicIsActivating(RelicModel relic, bool value)
    {
        if (UndoReflectionUtil.FindProperty(relic.GetType(), "IsActivating")?.PropertyType == typeof(bool)
            && UndoReflectionUtil.TrySetPropertyValue(relic, "IsActivating", value))
        {
            return true;
        }

        return UndoReflectionUtil.TrySetFieldValue(relic, "_isActivating", value);
    }

    private static void RefreshRelicCounterDisplay(RelicModel relic)
    {
        if (UndoReflectionUtil.FindMethod(relic.GetType(), "RefreshCounter") is MethodInfo refreshCounter)
        {
            refreshCounter.Invoke(relic, []);
            return;
        }

        if (UndoReflectionUtil.FindMethod(relic.GetType(), "UpdateDisplay") is MethodInfo updateDisplay)
        {
            updateDisplay.Invoke(relic, []);
            return;
        }

        UndoReflectionUtil.FindMethod(relic.GetType(), "InvokeDisplayAmountChanged")?.Invoke(relic, []);
    }

}





