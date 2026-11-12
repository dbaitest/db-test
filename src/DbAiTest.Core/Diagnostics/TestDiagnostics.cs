using System.Globalization;
using System.Text;

namespace DbAiTest.Core.Diagnostics;

/// <summary>
/// Collects per-test timings and writes them as a tab-separated file.
/// </summary>
/// <remarks>
/// <para>
/// A shared-container suite is usually slow, and without numbers nobody knows whether a slow
/// test is slow because of the code under test or because of the harness. Writing one TSV row
/// per test makes the answer a sort away.
/// </para>
/// <para>
/// Off unless explicitly enabled: the file is written only when
/// <c>DBAITEST_TIMING</c> is <c>1</c>. Diagnostics must never fail a run, so every write is
/// guarded and a failure is reported on the console and otherwise ignored.
/// </para>
/// <para>
/// The file is appended to, so several test assemblies can share one. The header is written
/// only when the file is new or empty, and each row is written exactly once however many
/// times <see cref="Flush"/> is called.
/// </para>
/// </remarks>
public sealed class TestDiagnostics : IDisposable
{
    /// <summary>Environment variable that turns diagnostics on.</summary>
    public const string EnableVariable = "DBAITEST_TIMING";

    /// <summary>Environment variable that overrides the output path.</summary>
    public const string PathVariable = "DBAITEST_TIMING_FILE";

    private readonly object _gate = new();
    private readonly List<string> _rows = [];
    private readonly string? _path;

    private TestDiagnostics(string? path)
    {
        _path = path;
    }

    /// <summary>
    /// Creates a collector when diagnostics are enabled, otherwise returns null.
    /// </summary>
    /// <param name="defaultDirectory">Directory the default file is written to.</param>
    public static TestDiagnostics? TryCreate(string defaultDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultDirectory);

        if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal))
        {
            return null;
        }

        var path = Environment.GetEnvironmentVariable(PathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Join(defaultDirectory, "dbaitest-timing.tsv");
        }

        return new TestDiagnostics(path);
    }

    /// <summary>Records the duration of one test.</summary>
    public void Record(string testName, TimeSpan duration)
    {
        lock (_gate)
        {
            _rows.Add(string.Join(
                '\t',
                "test",
                duration.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                testName));
        }
    }

    /// <summary>Records a named metric.</summary>
    public void RecordMetric(string metric, long value, string detail = "")
    {
        lock (_gate)
        {
            _rows.Add(string.Join('\t', metric, value.ToString(CultureInfo.InvariantCulture), detail));
        }
    }

    /// <summary>The file the rows are written to.</summary>
    public string? FilePath => _path;

    /// <summary>Appends the rows collected since the last flush to the configured file.</summary>
    public void Flush()
    {
        if (_path is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_rows.Count == 0)
            {
                return;
            }

            try
            {
                var builder = new StringBuilder();
                var file = new FileInfo(_path);
                if (!file.Exists || file.Length == 0)
                {
                    builder.Append("kind\tvalue\tdetail\n");
                }

                foreach (var row in _rows)
                {
                    builder.Append(row).Append('\n');
                }

                File.AppendAllText(_path, builder.ToString());
                _rows.Clear();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Diagnostics rows dropped ({_path}): {ex.Message}");
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => Flush();
}
