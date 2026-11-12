namespace DbAiTest.Cli;

/// <summary>
/// The parsed command line: a command and its options.
/// </summary>
/// <remarks>
/// Parsing is strict on purpose. A typo such as <c>--shema</c> used to be ignored silently, so
/// the command ran without a schema and reported success; an option missing its value used to
/// surface as an index-out-of-range error. Both are now errors that name the problem.
/// </remarks>
internal sealed class CliArguments
{
    /// <summary>The options that take a value, and the setter for each.</summary>
    private static readonly Dictionary<string, Action<CliArguments, string>> ValueOptions =
        new(StringComparer.Ordinal)
        {
            ["--engine"] = (a, v) => a.Engine = v,
            ["--schema"] = (a, v) => a.Schema = v,
            ["--image"] = (a, v) => a.Image = v,
            ["--database"] = (a, v) => a.Database = v,
            ["--username"] = (a, v) => a.Username = v,
            ["--password"] = (a, v) => a.Password = v,
        };

    /// <summary>The command: <c>engines</c>, <c>up</c>, <c>schema</c> or <c>help</c>.</summary>
    public string Command { get; private set; } = "help";

    /// <summary>The engine name; required by <c>up</c> and <c>schema</c>.</summary>
    public string? Engine { get; private set; }

    /// <summary>The schema directory.</summary>
    public string? Schema { get; private set; }

    /// <summary>A container image override.</summary>
    public string? Image { get; private set; }

    /// <summary>The database name inside the container.</summary>
    public string Database { get; private set; } = "dbaitest";

    /// <summary>Null means the engine's own default login.</summary>
    public string? Username { get; private set; }

    /// <summary>Null means the engine's own default password.</summary>
    public string? Password { get; private set; }

    /// <summary>Parses the arguments.</summary>
    /// <exception cref="CliUsageException">The arguments are malformed.</exception>
    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var parsed = new CliArguments();
        if (args.Count == 0)
        {
            return parsed;
        }

        parsed.Command = args[0] is "-h" or "--help" ? "help" : args[0];

        for (var i = 1; i < args.Count; i++)
        {
            var name = args[i];

            if (name is "-h" or "--help")
            {
                parsed.Command = "help";
                continue;
            }

            if (!ValueOptions.TryGetValue(name, out var assign))
            {
                throw new CliUsageException($"unknown option '{name}'.");
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliUsageException($"option '{name}' needs a value.");
            }

            assign(parsed, args[++i]);
        }

        return parsed;
    }

    /// <summary>The engine name, or a usage error when it was not given.</summary>
    /// <exception cref="CliUsageException">No <c>--engine</c> was given.</exception>
    public string RequireEngine() =>
        Engine ?? throw new CliUsageException($"'{Command}' needs --engine <name>.");
}

/// <summary>The command line is malformed. The message is shown to the user with the usage text.</summary>
internal sealed class CliUsageException(string message) : Exception(message);
