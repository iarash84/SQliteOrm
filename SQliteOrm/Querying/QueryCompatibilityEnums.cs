namespace SQliteOrm;

/// <summary>Specifies ascending or descending ordering for compatibility query APIs.</summary>
public enum SortOrder
{
    /// <summary>Ascending order.</summary>
    ASC,
    /// <summary>Descending order.</summary>
    DESC
}

/// <summary>Specifies how compatibility query conditions are combined.</summary>
public enum LogicalOperator
{
    /// <summary>All conditions must match.</summary>
    And,
    /// <summary>At least one condition must match.</summary>
    Or
}
