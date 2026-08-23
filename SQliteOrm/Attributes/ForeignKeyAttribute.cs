namespace SQliteOrm;

/// <summary>Defines a SQLite foreign-key constraint for a mapped property.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ForeignKeyAttribute : Attribute
{
    /// <summary>Gets the related CLR type or table name.</summary>
    public string Name { get; }

    /// <summary>Gets or sets the SQLite <c>ON DELETE</c> action.</summary>
    public string OnDelete { get; set; } = "NO ACTION";

    /// <summary>Gets or sets the SQLite <c>ON UPDATE</c> action.</summary>
    public string OnUpdate { get; set; } = "NO ACTION";

    /// <summary>Creates a foreign-key mapping to the specified type or table name.</summary>
    public ForeignKeyAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Foreign-key target cannot be empty.", nameof(name));
        Name = name;
    }
}
