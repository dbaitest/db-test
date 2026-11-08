using DbTest.Core;
using DbTest.Core.Engine;
using DbTest.Core.Schema;
using DbTest.MsSql;
using DbTest.MySql;
using DbTest.PostgreSql;

namespace DbTest.Cli;

/// <summary>
/// The command line front end. It exposes the harness operations that are useful outside a
/// test run: starting a container with a schema applied, and inspecting the result.
/// </summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static async Task<int> Main(string[] args)
    {
        EngineRegistration.RegisterBuiltins();

        // Ctrl+C cancels the running command instead of killing the process, so the container
        // is disposed on the way out.
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var arguments = CliArguments.Parse(args);
            return arguments.Command switch
            {
                "help" => PrintUsage(exitCode: args.Length == 0 ? 1 : 0),
                "engines" => ListEngines(),
                "up" => await UpAsync(arguments, cancellation.Token).ConfigureAwait(false),
                "schema" => await ShowSchemaAsync(arguments, cancellation.Token).ConfigureAwait(false),
                _ => throw new CliUsageException($"unknown command '{arguments.Command}'."),
            };
        }
        catch (CliUsageException ex)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            await Console.Error.WriteLineAsync().ConfigureAwait(false);
            PrintUsage(exitCode: 2);
            return 2;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
            return 130;
        }
        catch (Exception ex)
        {
            // A CLI reports a failure — Docker not running, a broken schema, a bad image — as
            // one line, not as a stack trace.
            await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static int ListEngines()
    {
        Console.WriteLine("Registered engines:");
        foreach (var name in EngineRegistry.Names)
        {
            Console.WriteLine($"  {name}");
        }

        return 0;
    }

    /// <summary>
    /// Starts a container, applies the schema, prints the connection string and keeps the
    /// container running until Ctrl+C — a container that disappears as soon as its connection
    /// string is printed is of no use to anyone.
    /// </summary>
    private static async Task<int> UpAsync(CliArguments arguments, CancellationToken cancellationToken)
    {
        var engine = EngineRegistry.Create(arguments.RequireEngine());

        Console.WriteLine($"Starting {engine.Name} ({arguments.Image ?? engine.DefaultImage})...");

        await using var harness = await DbTestHarness.StartAsync(
            engine,
            arguments.Schema,
            new DbContainerOptions
            {
                Image = arguments.Image,
                Database = arguments.Database,
                Username = arguments.Username,
                Password = arguments.Password,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"Container is up: {harness.Image}");
        Console.WriteLine($"Connection string: {harness.ConnectionString}");

        if (arguments.Schema is not null)
        {
            Console.WriteLine($"Schema applied from {arguments.Schema}.");
        }

        Console.WriteLine("Press Ctrl+C to stop and remove the container.");

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Stopping the container...");
        }

        return 0;
    }

    private static async Task<int> ShowSchemaAsync(CliArguments arguments, CancellationToken cancellationToken)
    {
        var engine = EngineRegistry.Create(arguments.RequireEngine());
        var schemaRoot = arguments.Schema ?? throw new CliUsageException("'schema' needs --schema <dir>.");

        var load = SchemaLoader.Load(schemaRoot);

        Console.WriteLine($"{load.Scripts.Count} script(s) will be applied:");
        foreach (var script in load.Scripts)
        {
            Console.WriteLine($"  {script.RelativePath}");
        }

        if (load.Excluded.Count > 0)
        {
            Console.WriteLine($"{load.Excluded.Count} file(s) excluded:");
            foreach (var exclusion in load.Excluded)
            {
                Console.WriteLine($"  {exclusion.RelativePath} ({exclusion.Reason})");
            }
        }

        await using var harness = await DbTestHarness.StartAsync(
            engine,
            schemaRoot,
            new DbContainerOptions
            {
                Image = arguments.Image,
                Database = arguments.Database,
                Username = arguments.Username,
                Password = arguments.Password,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var objects = await harness.ListObjectsAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"{objects.Count} object(s) created:");
        foreach (var obj in objects)
        {
            Console.WriteLine($"  {obj.Kind,-7} {obj.QualifiedName} ({obj.Columns.Count} column(s))");
        }

        return 0;
    }

    private static int PrintUsage(int exitCode)
    {
        var writer = exitCode == 0 ? Console.Out : Console.Error;
        writer.WriteLine("""
            dbtest - database integration test harness

            Usage:
              dbtest engines                           List the registered engines.
              dbtest up     --engine <name> [options]  Start a container, apply a schema, and keep
                                                       it running until Ctrl+C.
              dbtest schema --engine <name> --schema <dir> [options]
                                                       Apply a schema, list the objects it
                                                       created, and remove the container.

            Options:
              --engine <name>     mssql | postgres | mysql
              --schema <dir>      Directory holding the schema files.
              --image <image>     Override the container image.
              --database <name>   Database name inside the container (default: dbtest).
              --username <name>   Login inside the container (default: the engine's own).
              --password <pass>   Password inside the container (default: the engine's own).
            """);
        return exitCode;
    }
}

/// <summary>Registers every engine the CLI knows about.</summary>
public static class EngineRegistration
{
    /// <summary>Registers the built-in engines. Called once at startup.</summary>
    public static void RegisterBuiltins()
    {
        EngineRegistry.Register("mssql", () => new MsSqlEngine());
        EngineRegistry.Register("postgres", () => new PostgreSqlEngine());
        EngineRegistry.Register("mysql", () => new MySqlEngine());
    }
}
