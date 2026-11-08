namespace DbTest.Core.Schema;

public static partial class SqlStatementSplitter
{
    /// <summary>
    /// Splits a SQL Server script on its <c>GO</c> batch separators.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GO</c> is a client-side directive (sqlcmd, SSMS), not T-SQL: the server rejects it, so
    /// a script must be split before it reaches the wire. The rules follow sqlcmd: <c>GO</c> is
    /// the only token on its line, case-insensitive, optionally indented, optionally followed by
    /// a repeat count (<c>GO 3</c> sends the batch three times) and a <c>--</c> comment.
    /// </para>
    /// <para>
    /// A <c>GO</c> inside a string, a <c>[bracketed]</c> or <c>"quoted"</c> identifier, or a
    /// comment — including a nested <c>/* /* */ */</c> block comment, which T-SQL allows — is
    /// not a separator, and neither is <c>GOTO</c>.
    /// </para>
    /// </remarks>
    /// <param name="script">The script text.</param>
    /// <returns>The batches, in order, without the separators and without empty batches.</returns>
    public static IReadOnlyList<string> SplitOnGo(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var batches = new List<string>();
        var buffer = new CommandBuffer();
        var blockDepth = 0;
        char? closingQuote = null;

        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            var next = i + 1 < script.Length ? script[i + 1] : '\0';

            if (blockDepth > 0)
            {
                buffer.Append(c, isCode: false);
                if (c == '/' && next == '*')
                {
                    buffer.Append(next, isCode: false);
                    blockDepth++;
                    i++;
                }
                else if (c == '*' && next == '/')
                {
                    buffer.Append(next, isCode: false);
                    blockDepth--;
                    i++;
                }

                continue;
            }

            if (closingQuote is { } quote)
            {
                buffer.Append(c, isCode: true);
                if (c == quote)
                {
                    if (next == quote)
                    {
                        // A doubled closing character is an escaped one: '' ]] "".
                        buffer.Append(next, isCode: true);
                        i++;
                    }
                    else
                    {
                        closingQuote = null;
                    }
                }

                continue;
            }

            if (c is 'G' or 'g' && buffer.AtLineStart && TryReadGoLine(script, i, out var repeat, out var lineEnd))
            {
                buffer.FlushTo(batches, repeat);
                buffer.MarkLineStart();
                i = lineEnd;
                continue;
            }

            if (c == '-' && next == '-')
            {
                var end = EndOfLine(script, i);
                for (; i < end; i++)
                {
                    buffer.Append(script[i], isCode: false);
                }

                i--;
                continue;
            }

            if (c == '/' && next == '*')
            {
                buffer.Append(c, isCode: false);
                buffer.Append(next, isCode: false);
                blockDepth = 1;
                i++;
                continue;
            }

            closingQuote = c switch
            {
                '\'' => '\'',
                '"' => '"',
                '[' => ']',
                _ => null,
            };

            buffer.Append(c, isCode: true);
        }

        buffer.FlushTo(batches);
        return batches;
    }

    /// <summary>
    /// Reads a <c>GO</c> line starting at <paramref name="index"/>: <c>GO</c>, optional
    /// whitespace, an optional positive repeat count, optional whitespace, an optional
    /// <c>--</c> comment, then the end of the line.
    /// </summary>
    /// <param name="script">The script.</param>
    /// <param name="index">Index of the <c>G</c>.</param>
    /// <param name="repeat">How many times the batch runs.</param>
    /// <param name="lineEnd">Index of the line's <c>\n</c>, or the script length.</param>
    private static bool TryReadGoLine(string script, int index, out int repeat, out int lineEnd)
    {
        repeat = 1;
        lineEnd = EndOfLine(script, index);

        if (index + 1 >= script.Length || script[index + 1] is not ('O' or 'o'))
        {
            return false;
        }

        var j = index + 2;
        SkipBlanks(script, ref j, lineEnd);

        var digitsStart = j;
        while (j < lineEnd && char.IsAsciiDigit(script[j]))
        {
            j++;
        }

        if (j > digitsStart)
        {
            // "GO3" is not sqlcmd syntax; the count must be separated from GO.
            if (digitsStart == index + 2
                || !int.TryParse(script.AsSpan(digitsStart, j - digitsStart), out repeat)
                || repeat < 1)
            {
                return false;
            }
        }

        SkipBlanks(script, ref j, lineEnd);

        if (j + 1 < lineEnd && script[j] == '-' && script[j + 1] == '-')
        {
            return true;
        }

        return j == lineEnd;
    }

    private static void SkipBlanks(string script, ref int j, int lineEnd)
    {
        while (j < lineEnd && (script[j] is ' ' or '\t' || IsLineBreak(script[j])))
        {
            j++;
        }
    }
}
