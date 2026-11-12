using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using DbAiTest.Core;
using DbAiTest.Core.Engine;

namespace Sample.Database.Tests.Fixtures;

/// <summary>
/// Loads test data from the files in this folder, which the .csproj copies next to the test
/// binaries. A missing file fails the test: a fixture that silently loads nothing turns a test
/// into one that passes against an empty database.
/// </summary>
public static partial class ShopFixtures
{
    private const string ProvenanceKey = "_provenance";

    /// <summary>
    /// Runs a SQL fixture script, such as <c>customers-with-orders.sql</c>, as one batch: no
    /// <c>GO</c> separators.
    /// </summary>
    public static async Task LoadSqlAsync(DbAiTestHarness harness, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);

        var sql = await File.ReadAllTextAsync(PathOf(fileName), cancellationToken);
        await harness.ExecuteAsync(sql, cancellationToken);
    }

    /// <summary>
    /// Inserts the rows of a JSON data file: <c>{ "schema.Table": [ { "Column": value } ] }</c>.
    /// Tables load in file order, so parents come before the rows that reference them. A row that
    /// carries an <c>Id</c> keeps it (IDENTITY_INSERT), and SQL Server moves the table's identity
    /// past it. Computed columns are not data: leave them out. A top-level <c>_provenance</c>
    /// object says where the data came from and is not loaded.
    /// </summary>
    public static async Task LoadJsonAsync(DbAiTestHarness harness, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);

        await using var stream = File.OpenRead(PathOf(fileName));
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        Expect(document.RootElement, JsonValueKind.Object, fileName, "the file must hold an object of tables");
        foreach (var table in document.RootElement.EnumerateObject())
        {
            if (table.Name == ProvenanceKey)
            {
                continue;
            }

            var tableName = Identifier(table.Name, fileName);
            Expect(table.Value, JsonValueKind.Array, $"{fileName}: {table.Name}", "a table must hold an array of rows");

            var rowIndex = 0;
            foreach (var row in table.Value.EnumerateArray())
            {
                var location = $"{fileName}: {table.Name}[{rowIndex}]";
                var (sql, parameters) = InsertFor(tableName, row, location);
                try
                {
                    await harness.ExecuteAsync(sql, parameters, cancellationToken);
                }
                catch (DbException ex)
                {
                    // A foreign key, a type or a computed column the server rejects: say which row.
                    throw new InvalidDataException($"{location}: {ex.Message}", ex);
                }

                rowIndex++;
            }
        }
    }

    private static (string Sql, IReadOnlyList<DbParameterValue> Parameters) InsertFor(
        string tableName,
        JsonElement row,
        string location)
    {
        Expect(row, JsonValueKind.Object, location, "a row must be an object of columns");

        var columns = new List<string>();
        var parameters = new List<DbParameterValue>();
        var hasId = false;

        foreach (var column in row.EnumerateObject())
        {
            hasId |= column.Name.Equals("Id", StringComparison.OrdinalIgnoreCase);
            columns.Add(Identifier(column.Name, location));
            parameters.Add(new DbParameterValue($"@p{parameters.Count}", ValueOf(column.Value, $"{location}.{column.Name}")));
        }

        if (columns.Count == 0)
        {
            throw new InvalidDataException($"{location}: a row needs at least one column.");
        }

        var insert = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"INSERT INTO {tableName} ({string.Join(", ", columns)}) ")
            .Append(CultureInfo.InvariantCulture, $"VALUES ({string.Join(", ", parameters.Select(p => p.Name))});")
            .ToString();

        // IDENTITY_INSERT belongs to the session, so ON, INSERT and OFF go in one batch.
        var sql = hasId
            ? $"SET IDENTITY_INSERT {tableName} ON; {insert} SET IDENTITY_INSERT {tableName} OFF;"
            : insert;

        return (sql, parameters);
    }

    private static object? ValueOf(JsonElement value, string location) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt32(out var whole) => whole,
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw new InvalidDataException($"{location}: a fixture value must be a string, number, boolean or null."),
    };

    private static void Expect(JsonElement element, JsonValueKind kind, string location, string rule)
    {
        if (element.ValueKind != kind)
        {
            throw new InvalidDataException($"{location}: {rule}.");
        }
    }

    // Names come from a file, not from code, so they are checked and bracketed before they reach SQL.
    private static string Identifier(string name, string location)
    {
        if (!IdentifierPattern().IsMatch(name))
        {
            throw new InvalidDataException($"{location}: '{name}' is not a plain table or column name.");
        }

        return string.Join('.', name.Split('.').Select(part => $"[{part}]"));
    }

    private static string PathOf(string fileName)
    {
        var path = Path.Join(AppContext.BaseDirectory, "Fixtures", fileName);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"Fixture '{fileName}' was not found in the test output's Fixtures folder.", path);
    }

    [GeneratedRegex(@"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)?\z")]
    private static partial Regex IdentifierPattern();
}
