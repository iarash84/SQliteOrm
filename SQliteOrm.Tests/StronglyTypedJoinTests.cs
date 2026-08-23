using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.SQLite;

namespace SQliteOrm.Tests;

public sealed class StronglyTypedJoinTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sqlite-orm-join-{Guid.NewGuid():N}.db");
    private readonly SqliteOrm _db;

    public StronglyTypedJoinTests()
    {
        _db = new SqliteOrm($"Data Source={_path}");
        _db.CreateTable<JoinCustomer>();
        _db.CreateTable<JoinOrder>();
    }

    [Fact]
    public void Inner_join_uses_mapped_tables_keys_columns_and_projection()
    {
        var customerKey = Guid.NewGuid();
        _db.Insert(new JoinCustomer { CustomerKey = customerKey, Name = "Ada" });
        _db.Insert(new JoinOrder { CustomerReference = customerKey, Total = 120m, IsOpen = true });

        var rows = _db.Table<JoinOrder>()
            .Join<JoinCustomer>(order => order.CustomerReference, customer => customer.CustomerKey)
            .Select((order, customer) => new JoinResult
            {
                OrderKey = order.OrderKey,
                CustomerName = customer.Name,
                Total = order.Total
            })
            .ToList();

        var row = Assert.Single(rows);
        Assert.Equal("Ada", row.CustomerName);
        Assert.Equal(120m, row.Total);
    }

    [Fact]
    public void Joined_filters_are_parameterized_and_combine_with_left_filters()
    {
        var firstKey = Guid.NewGuid();
        var secondKey = Guid.NewGuid();
        _db.Insert(new JoinCustomer { CustomerKey = firstKey, Name = "Ada%" });
        _db.Insert(new JoinCustomer { CustomerKey = secondKey, Name = "Grace" });
        _db.Insert(new JoinOrder { CustomerReference = firstKey, Total = 150m, IsOpen = true });
        _db.Insert(new JoinOrder { CustomerReference = secondKey, Total = 50m, IsOpen = true });

        var search = "Ada%' OR 1=1 --";
        var minimum = 100m;
        var projection = _db.Table<JoinOrder>()
            .Where(order => order.IsOpen)
            .Join<JoinCustomer>(order => order.CustomerReference, customer => customer.CustomerKey)
            .Where((order, customer) => customer.Name.Contains(search) || order.Total >= minimum)
            .Select((order, customer) => new JoinResult
            {
                OrderKey = order.OrderKey,
                CustomerName = customer.Name,
                Total = order.Total
            });
        var command = projection.BuildCommand();

        Assert.DoesNotContain(search, command.Sql);
        Assert.Contains("FROM \"join_orders\" AS \"t0\" INNER JOIN \"join_customers\" AS \"t1\"", command.Sql);
        Assert.Contains("\"t0\".\"customer_ref\" = \"t1\".\"customer_key\"", command.Sql);
        Assert.Contains("\"t1\".\"display_name\"", command.Sql);
        Assert.Equal(3, command.Parameters!.Count);
        Assert.Single(projection.ToList());
    }

    [Fact]
    public void Join_rejects_computed_keys_and_computed_projections()
    {
        Assert.Throws<NotSupportedException>(() => _db.Table<JoinOrder>()
            .Join<JoinCustomer>(order => order.Total + 1, customer => customer.CustomerKey));
        Assert.Throws<NotSupportedException>(() => _db.Table<JoinOrder>()
            .Join<JoinCustomer>(order => order.CustomerReference, customer => customer.CustomerKey)
            .Select((order, customer) => new JoinResult { Total = order.Total + 1 }));
    }

    public void Dispose()
    {
        _db.Dispose();
        SQLiteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Table("join_customers")]
    private sealed class JoinCustomer
    {
        [Key, Column("customer_key")] public Guid CustomerKey { get; set; }
        [Column("display_name")] public string Name { get; set; } = string.Empty;
    }

    [Table("join_orders")]
    private sealed class JoinOrder
    {
        [Key, AutoIncrement, Column("order_key")] public long OrderKey { get; set; }
        [Column("customer_ref")] public Guid CustomerReference { get; set; }
        public decimal Total { get; set; }
        public bool IsOpen { get; set; }
    }

    private sealed class JoinResult
    {
        public long OrderKey { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public decimal Total { get; set; }
    }
}
