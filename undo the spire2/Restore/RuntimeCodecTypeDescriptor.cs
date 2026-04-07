using System.Collections.Concurrent;
using System.Reflection;

namespace UndoTheSpire2;

internal sealed class RuntimeCodecTypeDescriptor
{
    private const BindingFlags ScalarFieldFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private const BindingFlags PropertyFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private readonly Type _type;
    private readonly PropertyInfo[] _declaredProperties;
    private readonly ConcurrentDictionary<Type, PropertyInfo[]> _referenceProperties = new();
    private readonly ConcurrentDictionary<Type, FieldInfo[]> _referenceFields = new();
    private readonly ConcurrentDictionary<Type, PropertyInfo[]> _collectionProperties = new();
    private readonly ConcurrentDictionary<Type, FieldInfo[]> _collectionFields = new();

    public RuntimeCodecTypeDescriptor(Type type)
    {
        _type = type;
        _declaredProperties = type.GetProperties(PropertyFlags);
        ScalarFields = type.GetFields(ScalarFieldFlags)
            .Where(static field => !field.IsStatic && !field.IsInitOnly && !field.IsLiteral)
            .Where(static field => !field.Name.StartsWith("<", StringComparison.Ordinal))
            .Where(static field =>
            {
                Type fieldType = field.FieldType;
                return fieldType == typeof(bool) || fieldType == typeof(int) || fieldType == typeof(decimal) || fieldType.IsEnum;
            })
            .Where(field => !HasComparableRuntimeProperty(field.Name, field.FieldType))
            .ToArray();
    }

    public IReadOnlyList<FieldInfo> ScalarFields { get; }

    public IReadOnlyList<PropertyInfo> GetReferenceProperties(Type valueType)
    {
        return _referenceProperties.GetOrAdd(valueType, static (candidateValueType, descriptor) =>
            descriptor._declaredProperties
                .Where(static property => property.GetIndexParameters().Length == 0)
                .Where(static property => property.CanRead)
                .Where(property => property.PropertyType == candidateValueType)
                .Where(static property => property.Name != "Status")
                .ToArray(), this);
    }

    public IReadOnlyList<FieldInfo> GetReferenceFields(Type valueType)
    {
        return _referenceFields.GetOrAdd(valueType, static (candidateValueType, descriptor) =>
            descriptor._type.GetFields(ScalarFieldFlags)
                .Where(static field => !field.IsStatic && !field.IsInitOnly && !field.IsLiteral)
                .Where(static field => !field.Name.StartsWith("<", StringComparison.Ordinal))
                .Where(field => field.FieldType == candidateValueType)
                .Where(field => !descriptor.HasComparableRuntimeProperty(field.Name, candidateValueType))
                .ToArray(), this);
    }

    public IReadOnlyList<PropertyInfo> GetCollectionProperties(Type elementType)
    {
        return _collectionProperties.GetOrAdd(elementType, static (candidateElementType, descriptor) =>
            descriptor._declaredProperties
                .Where(static property => property.GetIndexParameters().Length == 0)
                .Where(static property => property.CanRead)
                .Where(property => property.SetMethod != null || UndoReflectionUtil.FindField(descriptor._type, $"<{property.Name}>k__BackingField") != null)
                .Where(property => TryGetCollectionElementType(property.PropertyType, out Type? candidate) && candidate == candidateElementType)
                .ToArray(), this);
    }

    public IReadOnlyList<FieldInfo> GetCollectionFields(Type elementType)
    {
        return _collectionFields.GetOrAdd(elementType, static (candidateElementType, descriptor) =>
            descriptor._type.GetFields(ScalarFieldFlags)
                .Where(static field => !field.IsStatic && !field.IsInitOnly && !field.IsLiteral)
                .Where(static field => !field.Name.StartsWith("<", StringComparison.Ordinal))
                .Where(field => TryGetCollectionElementType(field.FieldType, out Type? candidate) && candidate == candidateElementType)
                .ToArray(), this);
    }

    private bool HasComparableRuntimeProperty(string memberName, Type memberType)
    {
        string normalizedFieldName = NormalizeRuntimeFieldName(memberName);
        return _declaredProperties.Any(property =>
            string.Equals(property.Name, normalizedFieldName, StringComparison.OrdinalIgnoreCase)
            && (property.PropertyType == memberType
                || (TryGetCollectionElementType(property.PropertyType, out Type? propertyElementType)
                    && TryGetCollectionElementType(memberType, out Type? fieldElementType)
                    && propertyElementType == fieldElementType)));
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

    private static string NormalizeRuntimeFieldName(string fieldName)
    {
        return fieldName.StartsWith("_", StringComparison.Ordinal) && fieldName.Length > 1
            ? char.ToUpperInvariant(fieldName[1]) + fieldName[2..]
            : fieldName;
    }
}
