using DbTest.Core.Schema;

namespace DbTest.Core.Tests;

[TestClass]
public sealed class MySqlDelimiterSplitterTests
{
    [TestMethod]
    public void Should_Keep_A_Routine_Body_Intact()
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

        Assert.HasCount(2, statements, "The routine body is one statement, then the select.");
        StringAssert.StartsWith(statements[0], "CREATE PROCEDURE p()");
        StringAssert.Contains(statements[0], "SELECT 1;");
        StringAssert.EndsWith(statements[0], "END");
        Assert.AreEqual("SELECT 2", statements[1]);
    }

    [TestMethod]
    public void Should_Not_Mistake_A_Column_Named_Delimiter_For_A_Directive()
    {
        const string script = """
            CREATE TABLE settings (
                delimiter_char CHAR(1) NOT NULL,
                delimiter VARCHAR(5) NOT NULL
            );
            SELECT 1;
            """;

        var statements = SqlStatementSplitter.SplitRespectingDelimiter(script);

        Assert.HasCount(2, statements, string.Join(" | ", statements));
        StringAssert.Contains(statements[0], "delimiter VARCHAR(5) NOT NULL");
    }

    [TestMethod]
    public void Should_Not_Split_On_A_Semicolon_In_A_Comment_At_The_End_Of_A_Line()
    {
        const string script = """
            CREATE TABLE t (
                id INT, -- the key; never null
                # hash comment;
                /* block;
                   comment; */
                name VARCHAR(10)
            );
            """;

        var statements = SqlStatementSplitter.SplitRespectingDelimiter(script);

        Assert.HasCount(1, statements, string.Join(" | ", statements));
    }

    [TestMethod]
    public void Should_Not_Split_Inside_Strings_And_Identifiers()
    {
        const string script = """
            INSERT INTO t (v) VALUES ('a;
            b'), ("c;d"), ('it\'s;'), ('doubled'';');
            SELECT `odd;name` FROM t;
            """;

        var statements = SqlStatementSplitter.SplitRespectingDelimiter(script);

        Assert.HasCount(2, statements, string.Join(" | ", statements));
        StringAssert.Contains(statements[0], "('doubled'';')");
        Assert.AreEqual("SELECT `odd;name` FROM t", statements[1]);
    }

    [TestMethod]
    public void Should_Treat_Double_Dash_Without_A_Space_As_Code()
    {
        // MySQL only starts a comment at "-- " (with whitespace); "1--1" is arithmetic.
        var statements = SqlStatementSplitter.SplitRespectingDelimiter("SELECT 1--1; SELECT 2;");

        CollectionAssert.AreEqual(new[] { "SELECT 1--1", "SELECT 2" }, statements.ToArray());
    }

    [TestMethod]
    public void Should_Accept_A_Lowercase_Directive_After_A_Comment_With_Crlf()
    {
        const string script = "-- routines\r\ndelimiter $$\r\nCREATE FUNCTION f() RETURNS INT RETURN 1$$\r\ndelimiter ;\r\n";

        var statements = SqlStatementSplitter.SplitRespectingDelimiter(script);

        Assert.HasCount(1, statements);
        StringAssert.Contains(statements[0], "CREATE FUNCTION f() RETURNS INT RETURN 1");
        Assert.IsFalse(statements[0].Contains("$$", StringComparison.Ordinal), "The delimiter is not sent to the server.");
    }

    [TestMethod]
    public void Should_Keep_Executable_Comments_As_Statements()
    {
        // mysqldump wraps SET statements and triggers in /*! … */, which the server executes.
        const string script = """
            /*!40101 SET NAMES utf8mb4 */;
            DELIMITER ;;
            /*!50003 CREATE*/ /*!50017 DEFINER=`root`@`%`*/ /*!50003 TRIGGER t_bi BEFORE INSERT ON t FOR EACH ROW BEGIN
              SET NEW.x = 1;
            END */;;
            DELIMITER ;
            SELECT /*+ MAX_EXECUTION_TIME(1000) */ 1;
            """;

        var statements = SqlStatementSplitter.SplitRespectingDelimiter(script);

        Assert.HasCount(3, statements, string.Join(" | ", statements));
        Assert.AreEqual("/*!40101 SET NAMES utf8mb4 */", statements[0]);
        StringAssert.Contains(statements[1], "TRIGGER t_bi");
        StringAssert.EndsWith(statements[1], "END */");
    }

    [TestMethod]
    public void Should_Drop_A_Trailing_Comment_Only_Statement()
    {
        // MySQL rejects a query that holds nothing but a comment ("Query was empty").
        var statements = SqlStatementSplitter.SplitRespectingDelimiter("SELECT 1;\n-- the end\n");

        CollectionAssert.AreEqual(new[] { "SELECT 1" }, statements.ToArray());
    }
}
