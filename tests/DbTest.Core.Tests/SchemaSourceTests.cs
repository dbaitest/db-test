using System.Data.Common;

using DbTest.Core.Engine;
using DbTest.Core.Schema;
using DbTest.MSTest;

namespace DbTest.Core.Tests;

[TestClass]
public sealed class SchemaSourceTests
{
    [TestMethod]
    public void Directory_Source_Copies_Its_Options()
    {
        var ordered = new List<string> { "tables" };
        var source = new DirectorySchemaSource("/schema", new SchemaLoadOptions { OrderedDirectories = ordered });
        var before = source.Key;

        ordered.Add("views");

        Assert.AreEqual(before, source.Key, "Changing the caller's list must not change what the source applies.");
    }

    [TestMethod]
    public void Directory_Source_Resolves_A_Relative_Root_Against_The_Current_Directory()
    {
        var source = new DirectorySchemaSource("Schema/mssql");

        Assert.AreEqual(Path.GetFullPath("Schema/mssql"), source.RootDirectory);
    }

    [TestMethod]
    public void Context_ToString_Never_Prints_The_Connection_String()
    {
        var context = new SchemaApplyContext
        {
            Engine = new RecordingEngine(),
            ConnectionString = "Server=x;Database=db;User Id=sa;Password=Sup3r-Secret!",
            Database = "db",
        };

        var text = context.ToString();

        Assert.DoesNotContain("Sup3r-Secret!", text);
        Assert.DoesNotContain("Password", text);
        Assert.AreEqual("recording/db", text);
    }

    [TestMethod]
    public void Schema_Alone_Is_Used()
    {
        var source = new DirectorySchemaSource("/schema");

        Assert.AreSame(source, DbTestBase.ResolveSchema(source, null, null));
        Assert.IsNull(DbTestBase.ResolveSchema(null, "Schema/mssql", new SchemaLoadOptions()));
    }

    [TestMethod]
    public void Schema_With_SchemaRoot_Is_Refused()
    {
        var source = new DirectorySchemaSource("/schema");

        Assert.ThrowsExactly<InvalidOperationException>(() => DbTestBase.ResolveSchema(source, "Schema/mssql", null));
    }

    [TestMethod]
    public void Schema_With_SchemaLoadOptions_Is_Refused()
    {
        var source = new DirectorySchemaSource("/schema");

        Assert.ThrowsExactly<InvalidOperationException>(
            () => DbTestBase.ResolveSchema(source, null, new SchemaLoadOptions()));
    }

    [TestMethod]
    public async Task A_Source_That_Rejects_The_Configuration_Fails_Before_A_Container_Starts()
    {
        var engine = new RecordingEngine();

        await Assert.ThrowsExactlyAsync<DbTestException>(
            () => DbTestHarness.StartAsync(engine, new RejectingSource()));

        Assert.IsFalse(engine.ContainerStarted);
    }

    /// <summary>A source whose configuration is always wrong.</summary>
    private sealed class RejectingSource : SchemaSource
    {
        public override string Key => "rejecting";

        public override void Validate(DbEngine engine) => throw new DbTestException("misconfigured");

        public override Task<SchemaApplyResult> ApplyAsync(
            SchemaApplyContext context,
            CancellationToken cancellationToken = default)
            => throw new AssertFailedException("ApplyAsync must not run when Validate fails.");
    }

    /// <summary>An engine that records whether a container was asked for, and never starts one.</summary>
    private sealed class RecordingEngine : DbEngine
    {
        public bool ContainerStarted { get; private set; }

        public override string Name => "recording";

        public override string DefaultImage => "none";

        public override DbConnection CreateConnection(string connectionString) => throw new NotSupportedException();

        public override Task<IDbContainerHandle> StartContainerAsync(
            DbContainerOptions options,
            CancellationToken cancellationToken = default)
        {
            ContainerStarted = true;
            throw new NotSupportedException();
        }

        public override Task<IReadOnlyList<SchemaObject>> ListObjectsAsync(
            DbConnection connection,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task CleanAsync(DbConnection connection, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override IReadOnlyList<string> SplitScript(SchemaScript script) => throw new NotSupportedException();
    }
}
