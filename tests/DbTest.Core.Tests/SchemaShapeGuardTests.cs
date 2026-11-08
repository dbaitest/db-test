using System.Data.Common;

using DbTest.Core.Diagnostics;
using DbTest.Core.Engine;
using DbTest.Core.Schema;

namespace DbTest.Core.Tests;

[TestClass]
public sealed class SchemaShapeGuardTests
{
    private static readonly SchemaObject Customers = new("dbo", "customers", SchemaObjectKind.Table, ["id", "email"]);
    private static readonly SchemaObject Orders = new("dbo", "orders", SchemaObjectKind.Table, ["id", "customer_id"]);

    private readonly FakeEngine _engine = new();
    private SchemaShapeGuard _guard = null!;

    [TestInitialize]
    public void CreateGuard()
    {
        _engine.Objects = [Customers, Orders];
        _guard = new SchemaShapeGuard(_engine, _engine.Objects.ToDictionary(o => o.QualifiedName));
    }

    [TestMethod]
    public async Task Reports_Nothing_When_The_Schema_Is_Intact()
    {
        var leaks = await CheckAsync("A.Test");

        Assert.IsEmpty(leaks);
    }

    [TestMethod]
    public async Task Reports_A_Dropped_Object()
    {
        _engine.Objects = [Customers];

        var leak = (await CheckAsync("A.Drops")).Single();

        Assert.AreEqual("dbo.orders", leak.ObjectName);
        Assert.AreEqual("dropped", leak.Change);
        Assert.AreEqual("A.Drops", leak.Responsible);
    }

    [TestMethod]
    public async Task Reports_A_Retyped_Object_And_Changed_Columns()
    {
        _engine.Objects =
        [
            Customers with { Columns = ["id", "email", "probe"] },
            Orders with { Kind = SchemaObjectKind.View },
        ];

        var leaks = await CheckAsync("A.Reshapes");

        Assert.HasCount(2, leaks);
        Assert.AreEqual("[ShapeGuard] A.Reshapes left dbo.customers columns changed (id,email) -> (id,email,probe)", leaks[0].ToString());
        StringAssert.Contains(leaks[1].Change, "re-typed (Table -> View)");
    }

    [TestMethod]
    public async Task Ignores_Objects_Created_After_The_Baseline()
    {
        _engine.Objects = [Customers, Orders, new SchemaObject("dbo", "scratch", SchemaObjectKind.Table, ["x"])];

        Assert.IsEmpty(await CheckAsync("A.CreatesScratch"));
    }

    [TestMethod]
    public async Task Blames_Only_The_First_Test_That_Saw_The_Damage()
    {
        _engine.Objects = [Customers];

        Assert.HasCount(1, await CheckAsync("A.Culprit"));
        Assert.IsEmpty(await CheckAsync("B.Victim"), "The victim inherited the damage; it is not blamed again.");
        Assert.HasCount(1, _guard.Leaks);
    }

    [TestMethod]
    public async Task Blames_Again_When_A_Repaired_Object_Is_Broken_Again()
    {
        _engine.Objects = [Customers];
        await CheckAsync("A.Breaks");

        _engine.Objects = [Customers, Orders];
        Assert.IsEmpty(await CheckAsync("B.Repairs"));

        _engine.Objects = [Customers];
        var leak = (await CheckAsync("C.BreaksAgain")).Single();

        Assert.AreEqual("C.BreaksAgain", leak.Responsible);
        Assert.HasCount(2, _guard.Leaks);
    }

    private Task<IReadOnlyList<SchemaShapeLeak>> CheckAsync(string responsible) =>
        _guard.CheckAsync(connection: null!, responsible);

    /// <summary>An engine whose catalog is a list the test controls. It never touches a connection.</summary>
    private sealed class FakeEngine : DbEngine
    {
        public IReadOnlyList<SchemaObject> Objects { get; set; } = [];

        public override string Name => "fake";

        public override string DefaultImage => "none";

        public override Task<IReadOnlyList<SchemaObject>> ListObjectsAsync(DbConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(Objects);

        public override DbConnection CreateConnection(string connectionString) => throw new NotSupportedException();

        public override Task<IDbContainerHandle> StartContainerAsync(DbContainerOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override Task CleanAsync(DbConnection connection, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override IReadOnlyList<string> SplitScript(SchemaScript script) => throw new NotSupportedException();
    }
}
