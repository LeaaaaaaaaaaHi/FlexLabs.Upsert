using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using FlexLabs.EntityFrameworkCore.Upsert.Internal.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FlexLabs.EntityFrameworkCore.Upsert.Internal;

internal sealed record RelationalColumn(
    IProperty Property,
    string ColumnName,
    OwnershipType Owned,
    string? Path = null,
    Func<object, object?>? EntityGetter = null
) : IColumnBase
{
    public string Name => Property.Name;

    // Complex properties containing this column, ordered root to leaf.
    // With Parent -> Child -> SubChild you need to check Parent first, then Child, then SubChild.
    // Computed once at construction (columns are cached per table).
    // Caveats: record equality includes this field and ImmutableArray compares by reference, so separately built
    // columns never compare equal; and `with` copies it as-is, so a `with { Property = ... }` copy keeps a stale chain.
    private readonly ImmutableArray<IComplexProperty> _complexChain = GetComplexChain(Property);

    private static ImmutableArray<IComplexProperty> GetComplexChain(IProperty property)
    {
        // Walk the chain twice (count, then fill from the end) to allocate a single exact-size array.
        // The metadata walk is a few field reads, which is much cheaper than a temporary list.
        var depth = 0;
        for (var declaringType = property.DeclaringType; declaringType is IComplexType complexType; declaringType = complexType.ComplexProperty.DeclaringType)
            depth++;

        if (depth == 0)
            return [];

        var chain = new IComplexProperty[depth];
        for (var declaringType = property.DeclaringType; declaringType is IComplexType complexType; declaringType = complexType.ComplexProperty.DeclaringType)
            chain[--depth] = complexType.ComplexProperty;

        return ImmutableCollectionsMarshal.AsImmutableArray(chain);
    }

    // EF's getter silently returns CLR defaults (e.g. 0) for value types behind a null complex property,
    // so we need to walk the chain ourselves to tell "null parent" apart from "zero value".
    private bool IsNull([NotNullWhen(false)] object? entity)
    {
        if (entity == null) return true;

        foreach (var complexProperty in _complexChain)
        {
            entity = complexProperty.GetGetter().GetClrValue(entity);
            if (entity == null)
                return true;
        }

        return false;
    }

    public (string ColumnName, ConstantValue Value, string? DefaultSql, bool AllowInserts) GetValue(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var obj = entity;
        if (EntityGetter is not null)
        {
            obj = EntityGetter(entity);
        }

        string? defaultSql = null;
        object? rawValue = null;

        // A null owner / complex property means the column is NULL - configured defaults don't apply
        if (!IsNull(obj))
        {
            rawValue = Property.GetGetter().GetClrValueUsingContainingEntity(obj);
            var hasDefaultValue = Property.TryGetUsableDefaultValue(out var defaultValue);
            var defaultValueSql = Property.GetDefaultValueSql();

            var hasConfiguredDefault = hasDefaultValue || defaultValueSql != null;
            if (rawValue == null || (hasConfiguredDefault && Property.IsSentinel(rawValue)))
            {
                if (hasDefaultValue)
                    rawValue = defaultValue;
                else
                    defaultSql = defaultValueSql;
            }
        }

        var value = new ConstantValue(rawValue, this);
        var allowInserts = Property.ValueGenerated == ValueGenerated.Never || Property.GetAfterSaveBehavior() == PropertySaveBehavior.Save;

        return (ColumnName, value, defaultSql, allowInserts);
    }
}
