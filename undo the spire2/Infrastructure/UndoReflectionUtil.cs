// 文件说明：提供反射辅助工具，统一访问官方私有成员。
using System.Reflection;

namespace UndoTheSpire2;

internal static class UndoReflectionUtil
{
    public static FieldInfo? FindField(Type? type, string name)
    {
        while (type != null)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
                return field;

            type = type.BaseType;
        }

        return null;
    }

    public static PropertyInfo? FindProperty(Type? type, string name)
    {
        while (type != null)
        {
            PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
                return property;

            type = type.BaseType;
        }

        return null;
    }

    public static MethodInfo? FindMethod(Type? type, string name)
    {
        while (type != null)
        {
            MethodInfo? method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null)
                return method;

            type = type.BaseType;
        }

        return null;
    }

    public static MethodInfo? FindMethod(Type? type, string name, Type[] parameterTypes)
    {
        while (type != null)
        {
            MethodInfo? method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, parameterTypes, null);
            if (method != null)
                return method;

            type = type.BaseType;
        }

        return null;
    }

    public static T? GetFieldValue<T>(object target, string fieldName)
    {
        object? value = FindField(target.GetType(), fieldName)?.GetValue(target);
        return value is T typed ? typed : default;
    }

    public static T? GetStaticFieldValue<T>(Type type, string fieldName)
    {
        object? value = FindField(type, fieldName)?.GetValue(null);
        return value is T typed ? typed : default;
    }

    public static T? GetPropertyValue<T>(object target, string propertyName)
    {
        object? value = FindProperty(target.GetType(), propertyName)?.GetValue(target);
        return value is T typed ? typed : default;
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
        Type type = instance.GetType();
        return FindProperty(type, memberName)?.GetValue(instance) ?? FindField(type, memberName)?.GetValue(instance);
    }

    public static string? ReadStringMember(object instance, string memberName)
    {
        return ReadMember(instance, memberName) as string;
    }
}
