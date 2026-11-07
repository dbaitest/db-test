using DbTest.Core.Schema;

namespace DbTest.Integration.Tests.Unit;

/// <summary>
/// Unit tests for the statement splitter. These need no container, so they run first and
/// fail fast when the splitting rules regress.
/// </summary>
[TestClass]
public sealed class SqlStatementSplitterTests
{
    [TestMethod]
    public void Split_Should_Keep_Dollar_Quoted_Bodies_Intact()
    {
        const string script = """
            CREATE FUNCTION f() RETURNS int AS $$
            BEGIN
                RETURN 1;
            END;
            $$;
            SELECT 2;
            """;

        var statements = SqlStatementSplitter.Split(script, new SqlSplitOptions { SupportsDollarQuoting = true });

        Assert.AreEqual(2, statements.Count, "The function body and the trailing select are two statements.");
        StringAssert.Contains(statements[0], "RETURN 1;", "The body must survive intact.");
        Assert.AreEqual("SELECT 2", statements[1]);
    }

    [TestMethod]
    public void Split_Should_Not_Split_Inside_A_String_Literal()
    {
        const string script = "SELECT 'a;b' FROM t; SELECT 2;";

        var statements = SqlStatementSplitter.Split(script, new SqlSplitOptions());

        Assert.AreEqual(2, statements.Count);
        StringAssert.Contains(statements[0], "'a;b'");
    }

    [TestMethod]
    public void Split_Should_Not_Split_Inside_A_Comment()
    {
        const string script = """
            -- a comment; with a semicolon
            SELECT 1;
            /* block; comment */
            SELECT 2;
            """;

        var statements = SqlStatementSplitter.Split(script, new SqlSplitOptions());

        Assert.AreEqual(2, statements.Count);
    }

    [TestMethod]
    public void SplitOnGo_Should_Separate_Batches()
    {
        const string script = """
            CREATE TABLE a (id INT);
            GO
            CREATE TABLE b (id INT);
            GO
            """;

        var batches = SqlStatementSplitter.SplitOnGo(script);

        Assert.AreEqual(2, batches.Count);
        StringAssert.Contains(batches[0], "CREATE TABLE a");
        StringAssert.Contains(batches[1], "CREATE TABLE b");
    }

    [TestMethod]
    public void SplitOnGo_Should_Not_Treat_Go_Inside_A_Statement_As_A_Separator()
    {
        const string script = "SELECT * FROM going_here; GO SELECT 1;";

        var batches = SqlStatementSplitter.SplitOnGo(script);

        Assert.AreEqual(1, batches.Count, "A GO that is not the whole line is not a separator.");
        StringAssert.Contains(batches[0], "going_here");
        StringAssert.Contains(batches[0], "SELECT 1");
    }

    [TestMethod]
    public void SplitRespectingDelimiter_Should_Keep_A_Routine_Body_Intact()
    {
        const string script = """
            DELIMITER //
            CREATE PROCEDURE p()
            BEGIN
                SELECT 1;
            END //
            DELIMITER ;
            SELECT 2;
            """;

        var statements = SqlStatementSplitter.SplitRespectingDelimiter(script);

        Assert.AreEqual(2, statements.Count, "The routine body is one statement, then the select.");
        StringAssert.Contains(statements[0], "CREATE PROCEDURE p()");
        StringAssert.Contains(statements[0], "SELECT 1;");
        Assert.AreEqual("SELECT 2", statements[1]);
    }
}
