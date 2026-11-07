using System.Globalization;
using System.Text;

namespace DbTest.Core.Diagnostics;

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
/// <c>DBTEST_TIMING</c> is <c>1</c>. Diagnostics must never fail a run, so every write is
/// guarded and a failure is reported on the console and otherwise ignored.
/// </para>
/// </remarks>
public sealed class TestDiagnostics : IDisposable
{
    /// <summary>Environment variable that turns diagnostics on.</summary>
    public const string EnableVariable = "DBTEST_TIMING";

    /// <summary>Environment variable that overrides the output path.</summary>
    public const string PathVariable = "DBTEST_TIMING_FILE";

    private readonly object _gate = new();
    private readonly List<string> _rows = [];
    private readonly string? _path;
    private bool _headerWritten;

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
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal))
        {
            return null;
        }

        var path = Environment.GetEnvironmentVariable(PathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(defaultDirectory, "dbtest-timing.tsv");
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

    /// <summary>Writes the collected rows to the configured file.</summary>
    public void Flush()
    {
        if (_path is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                var builder = new StringBuilder();
                if (!_headerWritten)
                {
                    builder.AppendLine("kind\tvalue\tdetail");
                    _headerWritten = true;
                }

                foreach (var row in _rows)
                {
                    builder.AppendLine(row);
                }

                File.AppendAllText(_path, builder.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Diagnostics row dropped ({_path}): {ex.Message}");
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => Flush();
}
