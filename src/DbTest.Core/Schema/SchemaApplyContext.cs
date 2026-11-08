using DbTest.Core.Engine;

namespace DbTest.Core.Schema;

/// <summary>What a <see cref="SchemaSource"/> needs to apply a schema.</summary>
/// <remarks>
/// A class rather than a positional record on purpose. A record's generated
/// <c>ToString</c> prints every member, and <see cref="ConnectionString"/> carries the
/// password. A positional record also cannot gain a member without breaking its constructor
/// and <c>Deconstruct</c>, and new members are expected.
/// </remarks>
public sealed class SchemaApplyContext
{
    /// <summary>The engine of the running container.</summary>
    public required DbEngine Engine { get; init; }

    /// <summary>
    /// Connection string to the target database. It contains the password: never log it or put
    /// it in an exception message.
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>Name of the target database, which the engine has already created.</summary>
    public required string Database { get; init; }

    /// <summary>Names the engine and the database. Never includes the connection string.</summary>
    public override string ToString() => $"{Engine.Name}/{Database}";
}
