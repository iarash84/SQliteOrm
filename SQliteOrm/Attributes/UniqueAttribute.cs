namespace SQliteOrm;

/// <summary>Adds a SQLite <c>UNIQUE</c> constraint to a mapped property.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class UniqueAttribute : Attribute;
