using DbTest.Core.Schema;

namespace DbTest.Core.Tests;

[TestClass]
public sealed class GoBatchSplitterTests
{
    [TestMethod]
    public void Should_Separate_Batches()
    {
        const string script = """
            CREATE TABLE a (id INT);
            GO
            CREATE TABLE b (id INT);
            GO
            """;

        var batches = SqlStatementSplitter.SplitOnGo(script);

        Assert.HasCount(2, batches);
        Assert.AreEqual("CREATE TABLE a (id INT);", batches[0]);
        Assert.AreEqual("CREATE TABLE b (id INT);", batches[1]);
    }

    [TestMethod]
    public void Should_Not_Treat_Go_Inside_A_Statement_As_A_Separator()
    {
        var batches = SqlStatementSplitter.SplitOnGo("SELECT * FROM going_here; GO SELECT 1;");

        Assert.HasCount(1, batches, "A GO that is not alone on its line is not a separator.");
    }

    [TestMethod]
    public void Should_Accept_Indented_Lowercase_Go_With_Crlf_And_A_Trailing_Comment()
    {
        const string script = "SELECT 1\r\n    go   -- end of batch\r\nSELECT 2\r\n";

        var batches = SqlStatementSplitter.SplitOnGo(script);

        CollectionAssert.AreEqual(new[] { "SELECT 1", "SELECT 2" }, batches.ToArray());
    }

    [TestMethod]
    public void Should_Accept_Carriage_Return_Only_Line_Endings()
    {
        var batches = SqlStatementSplitter.SplitOnGo("SELECT 1\rGO\rSELECT 2");

        CollectionAssert.AreEqual(new[] { "SELECT 1", "SELECT 2" }, batches.ToArray());
    }

    [TestMethod]
    public void Should_Repeat_A_Batch_By_The_Go_Count()
    {
        var batches = SqlStatementSplitter.SplitOnGo("INSERT INTO t DEFAULT VALUES\nGO 3\n");

        Assert.HasCount(3, batches);
        Assert.IsTrue(batches.All(b => b == "INSERT INTO t DEFAULT VALUES"));
    }

    [TestMethod]
    [DataRow("GOTO done")]
    [DataRow("GO3")]
    [DataRow("GO 0")]
    [DataRow("GO;")]
    public void Should_Not_Treat_Lookalikes_As_Separators(string line)
    {
        var batches = SqlStatementSplitter.SplitOnGo($"SELECT 1\n{line}\nSELECT 2");

        Assert.HasCount(1, batches, $"'{line}' is not a batch separator.");
    }

    [TestMethod]
    public void Should_Not_Split_On_Go_Inside_Strings_Identifiers_Or_Comments()
    {
        const string script = """
            SELECT '
            GO
            ' AS text_with_go, [
            GO
            ] AS bracketed, "
            GO
            " AS quoted
            /* outer /* nested
            GO
            */ still a comment
            GO
            */
            SELECT 'it''s'
            """;

        var batches = SqlStatementSplitter.SplitOnGo(script);

        Assert.HasCount(1, batches, string.Join(Environment.NewLine + "----" + Environment.NewLine, batches));
    }

    [TestMethod]
    public void Should_Drop_Batches_That_Hold_Only_Comments()
    {
        const string script = """
            -- header
            CREATE TABLE a (id INT);
            GO
            /* nothing here */
            GO
            -- trailing comment
            """;

        var batches = SqlStatementSplitter.SplitOnGo(script);

        Assert.HasCount(1, batches);
        StringAssert.Contains(batches[0], "-- header", "Comments stay with the batch they belong to.");
    }

    [TestMethod]
    public void Should_Keep_A_Procedure_Body_With_Semicolons_In_One_Batch()
    {
        const string script = """
            CREATE PROCEDURE dbo.p AS
            BEGIN
                SELECT 1;
                SELECT 2;
            END;
            GO
            """;

        var batches = SqlStatementSplitter.SplitOnGo(script);

        Assert.HasCount(1, batches);
        StringAssert.Contains(batches[0], "SELECT 2;");
    }
}
