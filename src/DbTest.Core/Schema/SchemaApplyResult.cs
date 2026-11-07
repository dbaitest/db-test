namespace DbTest.Core.Schema;

/// <summary>The outcome of applying a schema.</summary>
/// <param name="Applied">Scripts that applied cleanly, in order.</param>
/// <param name="Failures">Scripts that failed, with the error each one raised.</param>
/// <param name="Duration">Total time spent applying.</param>
/// <remarks>
/// Failures are collected rather than thrown so the caller can report every broken file at
/// once. A schema that only partly applied is the hard case: the next test fails on a
/// missing object and the real cause is one file back in the log. Collecting makes the
/// whole breakage visible in one run.
/// </remarks>
public sealed record SchemaApplyResult(
    IReadOnlyList<string> Applied,
    IReadOnlyList<SchemaApplyFailure> Failures,
    TimeSpan Duration)
{
    /// <summary>True when every script applied cleanly.</summary>
    public bool Succeeded => Failures.Count == 0;

    /// <summary>
    /// Throws when the apply failed. Call this at the end of schema setup so a broken schema
    /// is reported as a schema problem instead of surfacing later as unrelated test failures.
    /// </summary>
    /// <exception cref="SchemaApplyException">At least one script failed.</exception>
    public void ThrowIfFailed()
    {
        if (Succeeded)
        {
            return;
        }

        var detail = string.Join(
            Environment.NewLine,
            Failures.Select(f => $"  {f.RelativePath}: {f.Error}"));

        throw new SchemaApplyException(
            $"{Failures.Count} schema script(s) failed to apply:" + Environment.NewLine + detail,
            Failures);
    }
}

/// <summary>A schema script that failed to apply.</summary>
/// <param name="RelativePath">Path of the failing script, relative to the schema root.</param>
/// <param name="Error">The error the engine raised.</param>
public sealed record SchemaApplyFailure(string RelativePath, string Error);

/// <summary>Thrown when one or more schema scripts fail to apply.</summary>
public sealed class SchemaApplyException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SchemaApplyException(string message, IReadOnlyList<SchemaApplyFailure> failures) : base(message)
    {
        Failures = failures;
    }

    /// <summary>The individual failures.</summary>
    public IReadOnlyList<SchemaApplyFailure> Failures { get; }
}
