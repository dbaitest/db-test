using System.Text;

namespace DbTest.Core.Schema;

/// <summary>
/// Splits a SQL script file into individual statements.
/// </summary>
/// <remarks>
/// <para>
/// The three engines disagree on what a statement is, and getting this wrong is silent:
/// SQL Server rejects a batch containing a <c>GO</c>, Postgres rejects a dollar-quoted
/// function body that was split on its inner semicolons, and MySQL needs its custom
/// <c>DELIMITER</c> directive honored. Splitting in the core keeps each engine's rules in
/// one place instead of scattered through test code.
/// </para>
/// <para>
/// The splitter is deliberately conservative: it tracks string literals, line comments,
/// block comments and dollar-quoted bodies, and only splits on a semicolon that is at
/// nesting depth zero and not inside any of those.
/// </para>
/// </remarks>
public static class SqlStatementSplitter
{
    /// <summary>
    /// Splits a script into statements.
    /// </summary>
    /// <param name="script">The script text.</param>
    /// <param name="options">Engine-specific splitting rules.</param>
    /// <returns>The statements, in order, with empty statements removed.</returns>
    public static IReadOnlyList<string> Split(string script, SqlSplitOptions options)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        var inLineComment = false;
        var inBlockComment = false;
        var inString = false;
        var dollarQuoteTag = (string?)null;
        var line = 0;

        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            var next = i + 1 < script.Length ? script[i + 1] : '\0';

            if (c == '\n')
            {
                line++;
            }

            if (inLineComment)
            {
                current.Append(c);
                if (c == '\n')
                {
                    inLineComment = false;
                }
                continue;
            }

            if (inBlockComment)
            {
                current.Append(c);
                if (c == '*' && next == '/')
                {
                    current.Append(next);
                    i++;
                    inBlockComment = false;
                }
                continue;
            }

            if (dollarQuoteTag is not null)
            {
                current.Append(c);
                if (c == '$' && MatchesDollarTag(script, i, dollarQuoteTag))
                {
                    current.Append(script.AsSpan(i + 1, dollarQuoteTag.Length - 1));
                    i += dollarQuoteTag.Length - 1;
                    dollarQuoteTag = null;
                }
                continue;
            }

            if (inString)
            {
                current.Append(c);
                if (c == '\'' && next == '\'')
                {
                    current.Append(next);
                    i++;
                }
                else if (c == '\'')
                {
                    inString = false;
                }
                continue;
            }

            // Not inside any literal or comment: this is where structure is recognized.
            if (c == '-' && next == '-')
            {
                inLineComment = true;
                current.Append(c);
                continue;
            }

            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                current.Append(c);
                current.Append(next);
                i++;
                continue;
            }

            if (c == '\'')
            {
                inString = true;
                current.Append(c);
                continue;
            }

            if (options.SupportsDollarQuoting && c == '$')
            {
                var tag = ReadDollarTag(script, i);
                if (tag is not null)
                {
                    dollarQuoteTag = tag;
                    current.Append(script.AsSpan(i, tag.Length));
                    i += tag.Length - 1;
                    continue;
                }
            }

            if (c == ';' && !options.DelimiterIsCustom)
            {
                AddStatement(statements, current);
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        AddStatement(statements, current);
        return statements;
    }

    /// <summary>
    /// Removes SQL Server <c>GO</c> batch separators, returning the batches they delimited.
    /// </summary>
    /// <remarks>
    /// <c>GO</c> is a client-side directive, not SQL: the server rejects it. A script that
    /// contains it must be split before it reaches the wire.
    /// </remarks>
    public static IReadOnlyList<string> SplitOnGo(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        var inLineComment = false;
        var inBlockComment = false;
        var inString = false;

        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            var next = i + 1 < script.Length ? script[i + 1] : '\0';

            if (inLineComment)
            {
                current.Append(c);
                if (c == '\n')
                {
                    inLineComment = false;
                }
                continue;
            }

            if (inBlockComment)
            {
                current.Append(c);
                if (c == '*' && next == '/')
                {
                    current.Append(next);
                    i++;
                    inBlockComment = false;
                }
                continue;
            }

            if (inString)
            {
                current.Append(c);
                if (c == '\'' && next == '\'')
                {
                    current.Append(next);
                    i++;
                }
                else if (c == '\'')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '-' && next == '-')
            {
                inLineComment = true;
                current.Append(c);
                continue;
            }

            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                current.Append(c);
                current.Append(next);
                i++;
                continue;
            }

            if (c == '\'')
            {
                inString = true;
                current.Append(c);
                continue;
            }

            if (c is 'G' or 'g' && IsGoLine(script, i))
            {
                AddStatement(batches, current);
                current.Clear();

                // Skip to the end of the GO line.
                var end = i;
                while (end < script.Length && script[end] != '\n')
                {
                    end++;
                }
                i = end - 1;
                continue;
            }

            current.Append(c);
        }

        AddStatement(batches, current);
        return batches;
    }

    /// <summary>
    /// Rewrites MySQL <c>DELIMITER</c> directives into plain statements.
    /// </summary>
    /// <remarks>
    /// <c>DELIMITER</c> is a mysql-client directive that lets a stored-routine body contain
    /// semicolons. The server does not understand it. The body is kept intact here and sent
    /// as one statement.
    /// </remarks>
    public static IReadOnlyList<string> SplitRespectingDelimiter(string script)
    {
        var statements = new List<string>();
        var lines = script.Split('\n');
        var delimiter = ";";
        var buffer = new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            var delimiterDirective = TryReadDelimiterDirective(line);
            if (delimiterDirective is not null)
            {
                // Flush whatever was accumulated under the previous delimiter.
                FlushByDelimiter(statements, buffer, delimiter);
                delimiter = delimiterDirective;
                continue;
            }

            buffer.AppendLine(line);

            if (EndsWithDelimiter(line, delimiter))
            {
                FlushByDelimiter(statements, buffer, delimiter);
            }
        }

        FlushByDelimiter(statements, buffer, delimiter);
        return statements;
    }

    private static void FlushByDelimiter(List<string> statements, StringBuilder buffer, string delimiter)
    {
        var text = buffer.ToString();
        buffer.Clear();

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        // Strip the trailing delimiter; the server rejects it as a trailing token.
        if (trimmed.EndsWith(delimiter, StringComparison.Ordinal))
        {
            trimmed = trimmed[..^delimiter.Length];
        }

        if (trimmed.Trim().Length > 0)
        {
            statements.Add(trimmed);
        }
    }

    private static string? TryReadDelimiterDirective(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("DELIMITER", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = trimmed["DELIMITER".Length..].Trim();
        return rest.Length == 0 ? null : rest;
    }

    private static bool EndsWithDelimiter(string line, string delimiter)
    {
        return line.TrimEnd().EndsWith(delimiter, StringComparison.Ordinal);
    }

    private static bool IsGoLine(string script, int index)
    {
        // GO must be the whole line: the token at the start of a line, optionally followed by
        // a repeat count. A GO in the middle of a statement is not a separator.
        if (index + 1 >= script.Length)
        {
            return false;
        }

        if (index > 0 && script[index - 1] is not ('\n' or '\r'))
        {
            return false;
        }

        if (script[index] is not ('G' or 'g') || script[index + 1] is not ('O' or 'o'))
        {
            return false;
        }

        // Consume the rest of the line; only whitespace or digits may follow.
        var j = index + 2;
        while (j < script.Length && script[j] != '\n')
        {
            if (script[j] is not (' ' or '\t' or '\r') && !char.IsDigit(script[j]))
            {
                return false;
            }
            j++;
        }

        return true;
    }

    private static string? ReadDollarTag(string script, int index)
    {
        // A dollar tag is $ followed by an optional identifier and a closing $.
        var j = index + 1;
        while (j < script.Length && (char.IsLetterOrDigit(script[j]) || script[j] == '_'))
        {
            j++;
        }

        if (j >= script.Length || script[j] != '$')
        {
            return null;
        }

        return script.Substring(index, j - index + 1);
    }

    private static bool MatchesDollarTag(string script, int index, string tag)
    {
        if (index + tag.Length > script.Length)
        {
            return false;
        }

        for (var k = 0; k < tag.Length; k++)
        {
            if (script[index + k] != tag[k])
            {
                return false;
            }
        }

        return true;
    }

    private static void AddStatement(List<string> statements, StringBuilder current)
    {
        var text = current.ToString().Trim();
        current.Clear();

        if (text.Length == 0)
        {
            return;
        }

        statements.Add(text);
    }
}

/// <summary>Engine-specific rules for splitting a script into statements.</summary>
public sealed class SqlSplitOptions
{
    /// <summary>True for engines that use dollar-quoted strings (PostgreSQL).</summary>
    public bool SupportsDollarQuoting { get; init; }

    /// <summary>
    /// True when the engine uses a custom delimiter directive (MySQL). A custom-delimiter
    /// engine is split by <see cref="SqlStatementSplitter.SplitRespectingDelimiter"/>, not here.
    /// </summary>
    public bool DelimiterIsCustom { get; init; }
}
