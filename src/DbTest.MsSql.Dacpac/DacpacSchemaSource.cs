using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;

using DbTest.Core.Engine;
using DbTest.Core.Schema;

using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace DbTest.MsSql.Dacpac;

/// <summary>
/// A SQL Server schema built as a <c>.dacpac</c> — the output of an SSDT or
/// <c>Microsoft.Build.Sql</c> database project — deployed into the container with DacFx.
/// </summary>
/// <remarks>
/// <para>
/// A database project keeps one object per file, and its objects depend on each other across
/// folders in both directions: a table's computed column calls a function that reads another
/// table. No file order applies that; DacFx deploys from the model, in dependency order.
/// </para>
/// <para>
/// The dacpac is opened read-only and deployed into the database the engine created, once per
/// test run when the source is shared through <c>DbTestRun</c>.
/// </para>
/// </remarks>
public sealed class DacpacSchemaSource : SchemaSource
{
    private readonly DacDeployOptions? _deployOptions;
    private readonly string _fingerprint;

    /// <summary>Creates the source.</summary>
    /// <param name="dacpacPath">
    /// Path of the <c>.dacpac</c>. A relative path is resolved against the current directory
    /// now; for a dacpac copied next to the tests pass
    /// <c>Path.Combine(AppContext.BaseDirectory, "MyDatabase.dacpac")</c>.
    /// </param>
    /// <param name="deployOptions">
    /// DacFx deploy options, or null for the defaults described on <see cref="CreateDefaultOptions"/>.
    /// When given, they are used exactly as they are — a bare <c>new DacDeployOptions()</c> has no
    /// command timeout and applies database-level settings — so start from
    /// <see cref="CreateDefaultOptions"/> and change what you need:
    /// <code>
    /// var options = DacpacSchemaSource.CreateDefaultOptions(new MsSqlEngine());
    /// options.SqlCommandVariableValues["Environment"] = "Test";
    /// </code>
    /// Do not change them after creating the source.
    /// </param>
    public DacpacSchemaSource(string dacpacPath, DacDeployOptions? deployOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dacpacPath);

        DacpacPath = Path.GetFullPath(dacpacPath);
        _deployOptions = deployOptions;
        _fingerprint = DeployOptionsFingerprint.Of(deployOptions);
        Key = string.Join('\u001f', "dacpac", DacpacPath, _fingerprint);
    }

    /// <summary>The absolute path of the dacpac.</summary>
    public string DacpacPath { get; }

    /// <inheritdoc />
    public override string Key { get; }

    /// <summary>
    /// The options used when none are given: no data-loss block, no new database (the engine has
    /// created it), nothing dropped, no database-level settings (recovery model, filegroups and
    /// the like mean nothing in a throwaway container and are the likeliest deploy failure there),
    /// and the engine's command timeout for every command, because DacFx's long-running commands
    /// otherwise wait forever.
    /// </summary>
    /// <param name="engine">The engine whose command timeout applies.</param>
    public static DacDeployOptions CreateDefaultOptions(DbEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var timeoutSeconds = (int)Math.Ceiling(engine.CommandTimeout.TotalSeconds);
        return new DacDeployOptions
        {
            BlockOnPossibleDataLoss = false,
            CreateNewDatabase = false,
            DropObjectsNotInSource = false,
            ScriptDatabaseOptions = false,
            CommandTimeout = timeoutSeconds,
            LongRunningCommandTimeout = timeoutSeconds,
        };
    }

    /// <inheritdoc />
    /// <exception cref="DbTestException">The engine is not SQL Server, or the dacpac does not exist.</exception>
    public override void Validate(DbEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        if (engine is not MsSqlEngine)
        {
            throw new DbTestException(
                $"{nameof(DacpacSchemaSource)} deploys to SQL Server only; the engine is '{engine.Name}'.");
        }

        if (!File.Exists(DacpacPath))
        {
            throw new DbTestException(
                $"Dacpac not found at '{DacpacPath}'. Build the .sqlproj and copy its .dacpac next to the "
                + "tests (see the README section \"SQL Server database projects\").");
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The deploy options were changed after the source was created.</exception>
    public override async Task<SchemaApplyResult> ApplyAsync(
        SchemaApplyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The key was computed from the options at construction; deploying anything else would
        // let two different schemas share one container without anyone noticing.
        if (DeployOptionsFingerprint.Of(_deployOptions) != _fingerprint)
        {
            throw new InvalidOperationException(
                "The DacDeployOptions given to this DacpacSchemaSource were changed after it was created. "
                + "Create a new source with the changed options instead.");
        }

        var options = _deployOptions ?? CreateDefaultOptions(context.Engine);
        var fileName = Path.GetFileName(DacpacPath);
        // DacFx raises Message from its own worker; a concurrent queue keeps the collection safe.
        var errors = new ConcurrentQueue<string>();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // DacFx is synchronous; keep it off the caller's thread.
            await Task.Run(
                () =>
                {
                    using var package = DacPackage.Load(DacpacPath, DacSchemaModelStorageType.Memory, FileAccess.Read);

                    var services = new DacServices(context.ConnectionString);
                    services.Message += (_, e) =>
                    {
                        if (e.Message.MessageType == DacMessageType.Error)
                        {
                            errors.Enqueue(e.Message.ToString());
                        }
                    };

                    services.Deploy(package, context.Database, upgradeExisting: true, options, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsDeployFailure(ex))
        {
            stopwatch.Stop();

            // The outer DacFx message is often generic; the server's own error is further in.
            var messages = new List<string>();
            for (var current = ex; current is not null; current = current.InnerException)
            {
                messages.Add(current.Message);
            }

            var detail = string.Join(Environment.NewLine, messages.Concat(errors).Distinct());
            return new SchemaApplyResult(
                [],
                [new SchemaApplyFailure(fileName, Redact(detail, context.ConnectionString))],
                stopwatch.Elapsed);
        }

        stopwatch.Stop();
        return new SchemaApplyResult([fileName], [], stopwatch.Elapsed);
    }

    /// <summary>
    /// Failures of the dacpac or the deploy, which are schema problems to report. Cancellation
    /// and programming errors are not, and propagate.
    /// </summary>
    private static bool IsDeployFailure(Exception ex) =>
        ex is DacServicesException
            or DbException
            or IOException
            or InvalidDataException
            or FormatException
            or UnauthorizedAccessException;

    /// <summary>Removes the password from text that may end up in a test log.</summary>
    internal static string Redact(string text, string connectionString)
    {
        string password;
        try
        {
            password = new SqlConnectionStringBuilder(connectionString).Password;
        }
        catch (ArgumentException)
        {
            return text;
        }

        return string.IsNullOrEmpty(password) ? text : text.Replace(password, "[redacted]", StringComparison.Ordinal);
    }
}
