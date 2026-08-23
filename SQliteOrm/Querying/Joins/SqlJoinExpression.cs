using SQliteOrm.Mapping;

namespace SQliteOrm.Querying.Joins;

internal enum SqlJoinType { Inner, Left }

internal sealed record SqlJoinExpression(
    EntityMap LeftMap,
    string LeftAlias,
    EntityMap RightMap,
    string RightAlias,
    PropertyMap LeftKey,
    PropertyMap RightKey,
    SqlJoinType JoinType);

internal sealed record SqlProjectionColumn(
    string TableAlias, PropertyMap Source, string ResultColumnName);
