using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using SQliteOrm.Querying;

namespace SQliteOrm.Tests;

public sealed class ExpressionTranslatorTests
{
    [Fact]
    public void Comparison_operators_use_mapped_columns_and_parameters()
    {
        var minimum = 18;
        var result = Compile(x => x.Age >= minimum && x.Credit > 100m && x.Age < 65 && x.Credit <= 500m && x.Age != 21);
        Assert.Equal("(((((\"person_age\" >= @p0) AND (\"Credit\" > @p1)) AND (\"person_age\" < @p2)) AND (\"Credit\" <= @p3)) AND (\"person_age\" <> @p4))", result.Sql);
        Assert.Equal(new object[] { 18, 100m, 65, 500m, 21 }, result.Parameters.Values);
    }

    [Fact]
    public void Boolean_not_or_and_parentheses_are_preserved()
    {
        var result = Compile(x => x.IsActive && (x.Role == QueryRole.Admin || !(x.Credit > 100m)));
        Assert.Equal("((\"IsActive\" = @p0) AND ((\"Role\" = @p1) OR (NOT (\"Credit\" > @p2))))", result.Sql);
        Assert.Equal(new object[] { 1L, 1, 100m }, result.Parameters.Values);
    }

    [Fact]
    public void Null_comparisons_use_is_null_without_parameters()
    {
        string? value = null;
        var result = Compile(x => x.Name == null || x.Name != value);
        Assert.Equal("((\"Name\" IS NULL) OR (\"Name\" IS NOT NULL))", result.Sql);
        Assert.Empty(result.Parameters);
    }

    [Fact]
    public void String_operations_are_parameterized_and_escape_like_wildcards()
    {
        var unsafeValue = "x%' OR 1=1 --_";
        var result = Compile(x => x.Name!.Contains(unsafeValue) || x.Name.StartsWith("prefix") || x.Name.EndsWith("suffix"));
        Assert.DoesNotContain(unsafeValue, result.Sql);
        Assert.Equal("%x\\%' OR 1=1 --\\_%", result.Parameters["@p0"]);
        Assert.Equal("prefix%", result.Parameters["@p1"]);
        Assert.Equal("%suffix", result.Parameters["@p2"]);
        Assert.Contains("ESCAPE '\\'", result.Sql);
    }

    [Fact]
    public void Collection_contains_becomes_parameterized_in_predicate()
    {
        var ids = new[] { 3, 5, 8 };
        var result = Compile(x => ids.Contains(x.Age));
        Assert.Equal("(\"person_age\" IN (@p0, @p1, @p2))", result.Sql);
        Assert.Equal(new object[] { 3, 5, 8 }, result.Parameters.Values);
    }

    [Fact]
    public void Empty_collection_contains_is_always_false()
    {
        var ids = Array.Empty<int>();
        var result = Compile(x => ids.Contains(x.Age));
        Assert.Equal("(1 = 0)", result.Sql);
        Assert.Empty(result.Parameters);
    }

    [Fact]
    public void Captured_enum_and_nullable_values_are_parameterized()
    {
        var role = QueryRole.Admin;
        int? score = 42;
        var result = Compile(x => x.Role == role && x.Score == score);
        Assert.Equal(new object[] { 1, 42 }, result.Parameters.Values);
    }

    [Fact]
    public void Unsupported_method_calls_report_the_expression()
    {
        var exception = Assert.Throws<NotSupportedException>(() => Compile(x => IsAdult(x.Age)));
        Assert.Contains("Call", exception.Message);
        Assert.Contains(nameof(IsAdult), exception.Message);
    }

    private static bool IsAdult(int age) => age >= 18;
    private static SqlitePredicate Compile(Expression<Func<QueryEntity, bool>> expression) =>
        new SqliteQueryCompiler().Compile(new ExpressionTranslator<QueryEntity>().Translate(expression));

    private sealed class QueryEntity
    {
        [Column("person_age")] public int Age { get; set; }
        public decimal Credit { get; set; }
        public bool IsActive { get; set; }
        public QueryRole Role { get; set; }
        public string? Name { get; set; }
        public int? Score { get; set; }
    }
    private enum QueryRole { User, Admin }
}
