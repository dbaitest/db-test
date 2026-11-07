using DbTest.Core;
using DbTest.Core.Engine;

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

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0];
        var options = ParseOptions(args.Skip(1));

        try
        {
            return command switch
            {
                "engines" => ListEngines(),
                "up" => await UpAsync(options).ConfigureAwait(false),
                "schema" => await ShowSchemaAsync(options).ConfigureAwait(false),
                _ => UnknownCommand(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
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

    private static async Task<int> UpAsync(CommandOptions options)
    {
        var engine = EngineRegistry.Create(options.Engine);
        var schemaRoot = options.Schema;

        Console.WriteLine($"Starting {engine.Name} ({options.Image ?? engine.DefaultImage})...");

        await using var harness = await DbTestHarness.StartAsync(
            engine,
            schemaRoot,
            new DbContainerOptions
            {
                Image = options.Image,
                Database = options.Database,
                Username = options.Username,
                Password = options.Password,
            });

        Console.WriteLine($"Container is up: {harness.Image}");
        Console.WriteLine($"Connection string: {harness.ConnectionString}");

        if (schemaRoot is not null)
        {
            Console.WriteLine($"Schema applied from {schemaRoot}.");
        }

        Console.WriteLine("The container is removed when this command exits.");
        return 0;
    }

    private static async Task<int> ShowSchemaAsync(CommandOptions options)
    {
        var engine = EngineRegistry.Create(options.Engine);
        var schemaRoot = options.Schema;

        if (schemaRoot is null)
        {
            Console.Error.WriteLine("error: --schema <dir> is required.");
            return 1;
        }

        var load = Core.Schema.SchemaLoader.Load(schemaRoot);

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

        await using var harness = await DbTestHarness.StartAsync(engine, schemaRoot);
        var objects = await harness.ExecuteOnConnectionAsync(connection => engine.ListObjectsAsync(connection));

        Console.WriteLine($"{objects.Count} object(s) created:");
        foreach (var obj in objects)
        {
            Console.WriteLine($"  {obj.Kind,-7} {obj.QualifiedName} ({obj.Columns.Count} column(s))");
        }

        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'.");
        PrintUsage();
        return 1;
    }

    private static CommandOptions ParseOptions(IEnumerable<string> args)
    {
        var options = new CommandOptions();
        var list = args.ToList();

        for (var i = 0; i < list.Count; i++)
        {
            switch (list[i])
            {
                case "--engine":
                    options.Engine = list[++i];
                    break;
                case "--schema":
                    options.Schema = list[++i];
                    break;
                case "--image":
                    options.Image = list[++i];
                    break;
                case "--database":
                    options.Database = list[++i];
                    break;
                case "--username":
                    options.Username = list[++i];
                    break;
                case "--password":
                    options.Password = list[++i];
                    break;
            }
        }

        return options;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            dbtest - database integration test harness

            Usage:
              dbtest engines                          List the registered engines.
              dbtest up    --engine <name> [options]  Start a container and apply a schema.
              dbtest schema --engine <name> --schema <dir>
                                                    Apply a schema and list the objects it created.

            Options:
              --engine <name>     mssql | postgres | mysql
              --schema <dir>      Directory holding the schema files.
              --image <image>     Override the container image.
              --database <name>   Database name inside the container (default: dbtest).
              --username <name>   Login inside the container (default: the engine's own).
              --password <pass>   Password inside the container (default: the engine's own).
            """);
    }

    /// <summary>Parsed command line options.</summary>
    private sealed class CommandOptions
    {
        public string Engine { get; set; } = "postgres";

        public string? Schema { get; set; }

        public string? Image { get; set; }

        public string Database { get; set; } = "dbtest";

        /// <summary>Null means the engine's own default login.</summary>
        public string? Username { get; set; }

        /// <summary>Null means the engine's own default password.</summary>
        public string? Password { get; set; }
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
