namespace DbTest.Core.Engine;

/// <summary>Options for starting a throwaway database container.</summary>
public sealed class DbContainerOptions
{
    /// <summary>Container image. Defaults to the engine's <see cref="IDbEngine.DefaultImage"/>.</summary>
    public string? Image { get; init; }

    /// <summary>Database name created inside the container.</summary>
    public string Database { get; init; } = "dbtest";

    /// <summary>
    /// Login used inside the container. Null means the engine's own default, which differs:
    /// SQL Server always uses <c>sa</c> and exposes no way to change it.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Password used inside the container. Null means the engine's own default, which differs:
    /// SQL Server enforces a password policy and rejects a simple password outright, failing
    /// the container at startup rather than at first login.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>How long to wait for the container to accept connections.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Extra environment variables passed to the container.</summary>
    public IDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A running throwaway database. Disposing it removes the container and its data.
/// </summary>
public interface IDbContainerHandle : IAsyncDisposable
{
    /// <summary>Connection string pointing at the containerized database.</summary>
    string ConnectionString { get; }

    /// <summary>The image the container is running.</summary>
    string Image { get; }
}

/// <summary>Thrown when the harness cannot complete an engine-level operation.</summary>
public sealed class DbTestException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DbTestException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public DbTestException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
