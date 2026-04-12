using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace UndoTheSpire2;

internal static class UndoReflectionUtil
{
    private enum ReflectionMemberKind
    {
        Field,
        Property,
        Method
    }

    private readonly record struct ReflectionMemberKey(
        RuntimeTypeHandle TypeHandle,
        string Name,
        ReflectionMemberKind Kind,
        string SignatureKey);

    private readonly record struct CachedMember<T>(T? Member) where T : class;

    private static readonly ConcurrentDictionary<ReflectionMemberKey, CachedMember<FieldInfo>> FieldCache = new();
    private static readonly ConcurrentDictionary<ReflectionMemberKey, CachedMember<PropertyInfo>> PropertyCache = new();
    private static readonly ConcurrentDictionary<ReflectionMemberKey, CachedMember<MethodInfo>> MethodCache = new();

    public static FieldInfo? FindField(Type? type, string name)
    {
        if (type == null)
            return null;

        ReflectionMemberKey key = new(type.TypeHandle, name, ReflectionMemberKind.Field, string.Empty);
        return FieldCache.GetOrAdd(
            key,
            static memberKey => new CachedMember<FieldInfo>(
                FindFieldCore(ResolveType(memberKey.TypeHandle), memberKey.Name))).Member;
    }

    public static PropertyInfo? FindProperty(Type? type, string name)
    {
        if (type == null)
            return null;

        ReflectionMemberKey key = new(type.TypeHandle, name, ReflectionMemberKind.Property, string.Empty);
        return PropertyCache.GetOrAdd(
            key,
            static memberKey => new CachedMember<PropertyInfo>(
                FindPropertyCore(ResolveType(memberKey.TypeHandle), memberKey.Name))).Member;
    }

    public static MethodInfo? FindMethod(Type? type, string name)
    {
        if (type == null)
            return null;

        ReflectionMemberKey key = new(type.TypeHandle, name, ReflectionMemberKind.Method, string.Empty);
        return MethodCache.GetOrAdd(
            key,
            static memberKey => new CachedMember<MethodInfo>(
                FindMethodCore(ResolveType(memberKey.TypeHandle), memberKey.Name, null))).Member;
    }

    public static MethodInfo? FindMethod(Type? type, string name, Type[] parameterTypes)
    {
        if (type == null)
            return null;

        ReflectionMemberKey key = new(type.TypeHandle, name, ReflectionMemberKind.Method, BuildSignatureKey(parameterTypes));
        return MethodCache.GetOrAdd(
            key,
            static memberKey => new CachedMember<MethodInfo>(
                FindMethodCore(ResolveType(memberKey.TypeHandle), memberKey.Name, ParseSignatureKey(memberKey.SignatureKey)))).Member;
    }

    public static T? GetFieldValue<T>(object target, string fieldName)
    {
        return TryGetFieldValue(target, fieldName, out T? value) ? value : default;
    }

    public static T? GetStaticFieldValue<T>(Type type, string fieldName)
    {
        FieldInfo? field = FindField(type, fieldName);
        if (field == null)
            return default;

        return TryCoerceValue(field.GetValue(null), out T? value) ? value : default;
    }

    public static T? GetPropertyValue<T>(object target, string propertyName)
    {
        return TryGetPropertyValue(target, propertyName, out T? value) ? value : default;
    }

    public static bool TryGetFieldValue<T>(object target, string fieldName, [MaybeNull] out T value)
    {
        FieldInfo? field = FindField(target.GetType(), fieldName);
        if (field == null)
        {
            value = default;
            return false;
        }

        return TryCoerceValue(field.GetValue(target), out value);
    }

    public static bool TryGetPropertyValue<T>(object target, string propertyName, [MaybeNull] out T value)
    {
        PropertyInfo? property = FindProperty(target.GetType(), propertyName);
        if (property == null)
        {
            value = default;
            return false;
        }

        return TryCoerceValue(property.GetValue(target), out value);
    }

    public static bool TryInvokeMethod(object target, string methodName, [MaybeNull] out object? result, params object?[]? args)
    {
        MethodInfo? method = FindMethod(target.GetType(), methodName);
        if (method == null)
        {
            result = null;
            return false;
        }

        result = method.Invoke(target, args);
        return true;
    }

    public static bool TryInvokeMethod(object target, string methodName, Type[] parameterTypes, [MaybeNull] out object? result, params object?[]? args)
    {
        MethodInfo? method = FindMethod(target.GetType(), methodName, parameterTypes);
        if (method == null)
        {
            result = null;
            return false;
        }

        result = method.Invoke(target, args);
        return true;
    }

    public static bool TryReadMember<T>(object instance, string memberName, [MaybeNull] out T value)
    {
        if (TryGetPropertyValue(instance, memberName, out value))
            return true;

        return TryGetFieldValue(instance, memberName, out value);
    }

    public static bool TrySetFieldValue(object target, string fieldName, object? value)
    {
        FieldInfo? field = FindField(target.GetType(), fieldName);
        if (field == null)
            return false;

        field.SetValue(target, value);
        return true;
    }

    public static bool TrySetPropertyValue(object target, string propertyName, object? value)
    {
        PropertyInfo? property = FindProperty(target.GetType(), propertyName);
        if (property?.SetMethod != null)
        {
            property.SetValue(target, value);
            return true;
        }

        return TrySetFieldValue(target, $"<{propertyName}>k__BackingField", value);
    }

    public static object? ReadMember(object instance, string memberName)
    {
        return TryReadMember(instance, memberName, out object? value) ? value : null;
    }

    public static string? ReadStringMember(object instance, string memberName)
    {
        return TryReadMember(instance, memberName, out string? value) ? value : null;
    }

    private static FieldInfo? FindFieldCore(Type type, string name)
    {
        while (type != null)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
                return field;

            type = type.BaseType!;
        }

        return null;
    }

    private static PropertyInfo? FindPropertyCore(Type type, string name)
    {
        while (type != null)
        {
            PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
                return property;

            type = type.BaseType!;
        }

        return null;
    }

    private static MethodInfo? FindMethodCore(Type type, string name, Type[]? parameterTypes)
    {
        while (type != null)
        {
            MethodInfo? method = parameterTypes == null
                ? type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                : type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, parameterTypes, null);
            if (method != null)
                return method;

            type = type.BaseType!;
        }

        return null;
    }

    private static string BuildSignatureKey(Type[] parameterTypes)
    {
        if (parameterTypes.Length == 0)
            return string.Empty;

        return string.Join("|", parameterTypes.Select(static parameterType => parameterType.AssemblyQualifiedName ?? parameterType.FullName ?? parameterType.Name));
    }

    private static Type[]? ParseSignatureKey(string signatureKey)
    {
        if (string.IsNullOrEmpty(signatureKey))
            return null;

        string[] parts = signatureKey.Split('|', StringSplitOptions.RemoveEmptyEntries);
        Type[] parameterTypes = new Type[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            parameterTypes[i] = Type.GetType(parts[i], throwOnError: true)
                ?? throw new InvalidOperationException($"Could not resolve reflection signature type '{parts[i]}'.");
        }

        return parameterTypes;
    }

    private static bool TryCoerceValue<T>(object? rawValue, [MaybeNull] out T value)
    {
        if (rawValue is T typedValue)
        {
            value = typedValue;
            return true;
        }

        if (rawValue == null && default(T) is null)
        {
            value = default;
            return true;
        }

        value = default;
        return false;
    }

    private static Type ResolveType(RuntimeTypeHandle typeHandle)
    {
        return Type.GetTypeFromHandle(typeHandle)
            ?? throw new InvalidOperationException("Could not resolve cached reflection target type.");
    }
}
