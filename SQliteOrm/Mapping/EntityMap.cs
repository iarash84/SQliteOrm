namespace SQliteOrm.Mapping;

internal abstract class EntityMap
{
    internal abstract Type EntityType { get; }
    internal abstract string TableName { get; }
    internal abstract PropertyMap? Key { get; }
    internal abstract IReadOnlyList<PropertyMap> Properties { get; }
    internal PropertyMap GetProperty(string name) => Properties.FirstOrDefault(p =>
        p.PropertyName.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
        throw new ArgumentException($"Property '{name}' is not mapped for type '{EntityType.Name}'.", nameof(name));
}

internal sealed class EntityMap<T> : EntityMap
{
    private readonly IReadOnlyList<PropertyMap> _properties;
    internal EntityMap(string tableName, IReadOnlyList<PropertyMap> properties)
    { TableName = tableName; _properties = properties; Key = properties.FirstOrDefault(p => p.IsPrimaryKey); }
    internal override Type EntityType => typeof(T);
    internal override string TableName { get; }
    internal override PropertyMap? Key { get; }
    internal override IReadOnlyList<PropertyMap> Properties => _properties;
}
