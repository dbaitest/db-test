namespace DbAiTest.Core.Engine;

/// <summary>
/// A named parameter value, in a provider-neutral form.
/// </summary>
/// <param name="Name">
/// The parameter name as it appears in the SQL, for example <c>@email</c>. All three drivers
/// also accept the name without the <c>@</c> prefix.
/// </param>
/// <param name="Value">The value, or null for SQL NULL.</param>
/// <remarks>
/// Each engine turns this into its own parameter type, so a caller never has to reference
/// <c>SqlParameter</c>, <c>NpgsqlParameter</c> or <c>MySqlParameter</c>.
/// </remarks>
public sealed record DbParameterValue(string Name, object? Value);
