using DbTest.Core;
using DbTest.Core.Diagnostics;
using DbTest.Core.Engine;
using DbTest.Core.Schema;

namespace DbTest.MSTest;

/// <summary>
/// The harnesses shared by a test run: one container per engine and schema, started on first
/// use and kept until the run ends.
/// </summary>
/// <remarks>
/// <para>
/// A container per test class would multiply the startup cost for no benefit: the schema is
/// the same, the tests clean what they seed, and the shape guard catches the tests that do
/// not leave the shared schema as they found it.
/// </para>
/// <para>
/// A harness that failed to start is remembered as failed, so when Docker is not running the
/// first test fails with the real error and every later test fails fast with the same one,
/// instead of each one waiting for its own container to time out.
/// </para>
/// <para>
/// Call <see cref="DisposeAllAsync"/> from an <c>[AssemblyCleanup]</c> method to remove the
/// containers as soon as the run ends. Without it, Testcontainers' resource reaper removes
/// them when the test host exits, and the timing file is flushed on process exit.
/// </para>
/// </remarks>
public static class DbTestRun
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, Task<DbTestHarness>> Harnesses = new(StringComparer.Ordinal);

    static DbTestRun()
    {
        Diagnostics = TestDiagnostics.TryCreate(AppContext.BaseDirectory);
        if (Diagnostics is not null)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Diagnostics.Flush();
        }
    }

    /// <summary>
    /// The per-test timing collector, or null unless <c>DBTEST_TIMING=1</c>. The file goes to
    /// <c>dbtest-timing.tsv</c> next to the test binaries, or to <c>DBTEST_TIMING_FILE</c>.
    /// </summary>
    public static TestDiagnostics? Diagnostics { get; }

    /// <summary>
    /// Returns the shared harness for an engine and schema, starting it on first use.
    /// </summary>
    /// <param name="engine">The engine. Only its type, name and the options identify the harness.</param>
    /// <param name="schemaRoot">
    /// The schema directory, or null for none. A relative path is resolved against the test
    /// binaries' directory, so <c>Schema/postgres</c> works when the files are copied to output.
    /// </param>
    /// <param name="containerOptions">Container options, or null for the engine defaults.</param>
    /// <param name="loadOptions">Schema load options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the wait; a start already under way keeps going for the next caller.</param>
    public static Task<DbTestHarness> GetHarnessAsync(
        DbEngine engine,
        string? schemaRoot = null,
        DbContainerOptions? containerOptions = null,
        SchemaLoadOptions? loadOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var source = schemaRoot is null
            ? null
            : new DirectorySchemaSource(Path.GetFullPath(schemaRoot, AppContext.BaseDirectory), loadOptions);

        return GetCoreAsync(engine, source, containerOptions, cancellationToken);
    }

    /// <summary>
    /// Returns the shared harness for an engine and schema source, starting it on first use.
    /// Harnesses are shared by <see cref="SchemaSource.Key"/>.
    /// </summary>
    /// <param name="engine">The engine. Only its type, name and the options identify the harness.</param>
    /// <param name="schemaSource">
    /// The schema to apply. Relative paths inside a source resolve against the current
    /// directory; pass <c>Path.Combine(AppContext.BaseDirectory, …)</c> for files copied to output.
    /// </param>
    /// <param name="containerOptions">Container options, or null for the engine defaults.</param>
    /// <param name="cancellationToken">Cancels the wait; a start already under way keeps going for the next caller.</param>
    public static Task<DbTestHarness> GetHarnessAsync(
        DbEngine engine,
        SchemaSource schemaSource,
        DbContainerOptions? containerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(schemaSource);

        return GetCoreAsync(engine, schemaSource, containerOptions, cancellationToken);
    }

    private static async Task<DbTestHarness> GetCoreAsync(
        DbEngine engine,
        SchemaSource? source,
        DbContainerOptions? containerOptions,
        CancellationToken cancellationToken)
    {
        var options = containerOptions ?? new DbContainerOptions();
        // The source type is part of the key: two kinds of source that happen to produce the same
        // Key string still deploy different things. "none" marks a harness with no schema.
        var schemaKey = source is null ? "none" : source.GetType().FullName + ":" + source.Key;
        var key = BuildKey(engine, schemaKey, options);

        Task<DbTestHarness>? harness;
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Harnesses.TryGetValue(key, out harness))
            {
                harness = source is null
                    ? DbTestHarness.StartAsync(engine, schemaRoot: null, options, cancellationToken: CancellationToken.None)
                    : DbTestHarness.StartAsync(engine, source, options, CancellationToken.None);
                Harnesses[key] = harness;
            }
        }
        finally
        {
            Gate.Release();
        }

        return await harness.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes every container this run started and flushes the timing file.</summary>
    /// <remarks>
    /// A start still in progress is awaited and then disposed, and one container that fails to
    /// stop does not keep the others running: every failure is collected and rethrown at the end.
    /// </remarks>
    /// <exception cref="AggregateException">One or more containers failed to stop.</exception>
    public static async Task DisposeAllAsync()
    {
        var failures = new List<Exception>();

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var pending in Harnesses.Values)
            {
                // Wait for a start still in progress, whatever its outcome. One that failed
                // never produced a container; the tests that needed it already reported why.
                // Task.WhenAny completes with the task and never throws its exception.
                await Task.WhenAny(pending).ConfigureAwait(false);
                if (!pending.IsCompletedSuccessfully)
                {
                    continue;
                }

                var disposal = pending.Result.DisposeAsync().AsTask();
                await disposal.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (disposal.Exception is { } failure)
                {
                    failures.AddRange(failure.InnerExceptions);
                }
            }

            Harnesses.Clear();
        }
        finally
        {
            Gate.Release();
            Diagnostics?.Flush();
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("One or more DbTest containers failed to stop.", failures);
        }
    }

    /// <summary>
    /// Identifies a harness by everything that changes the container or the schema baseline:
    /// two test classes share a container only when every one of these is equal.
    /// </summary>
    /// <remarks>
    /// The schema part is <see cref="SchemaSource.Key"/>, which covers the directory and its
    /// load options, or a dacpac and its deploy options.
    /// </remarks>
    internal static string BuildKey(
        DbEngine engine,
        string? schemaKey,
        DbContainerOptions options)
    {
        var environment = options.Environment
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}");

        return string.Join(
            '\u001f',
            engine.GetType().FullName,
            engine.Name,
            options.Image,
            options.Database,
            options.Username,
            options.Password,
            string.Join(',', environment),
            schemaKey);
    }
}
