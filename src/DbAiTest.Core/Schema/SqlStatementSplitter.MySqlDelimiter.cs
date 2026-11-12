namespace DbAiTest.Core.Schema;

public static partial class SqlStatementSplitter
{
    private const string DelimiterKeyword = "DELIMITER";

    /// <summary>
    /// Splits a MySQL script into statements, honouring <c>DELIMITER</c> directives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DELIMITER</c> is a mysql-client directive that lets a stored-routine body contain
    /// semicolons. The server does not understand it, so the directive is consumed here and each
    /// statement is sent without its delimiter.
    /// </para>
    /// <para>
    /// The scanner follows MySQL's lexical rules: <c>'…'</c> and <c>"…"</c> strings with
    /// backslash escapes and doubled quotes, <c>`…`</c> identifiers, <c>#</c> and <c>-- </c>
    /// line comments (MySQL requires the space) and <c>/* … */</c> block comments. A delimiter
    /// inside any of them is not a delimiter. An executable comment (<c>/*!40101 SET … */</c>,
    /// as mysqldump writes them) or an optimizer hint (<c>/*+ … */</c>) is code to the server,
    /// so a statement made only of one is kept. A <c>DELIMITER</c> directive is recognised only at
    /// the start of a line and only between statements, so a column named
    /// <c>delimiter_char</c> in the middle of a <c>CREATE TABLE</c> is left alone.
    /// </para>
    /// </remarks>
    /// <param name="script">The script text.</param>
    /// <returns>The statements, in order, without delimiters and without empty statements.</returns>
    public static IReadOnlyList<string> SplitRespectingDelimiter(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var statements = new List<string>();
        var buffer = new CommandBuffer();
        var delimiter = ";";
        var inBlockComment = false;
        var blockIsCode = false;
        char? closingQuote = null;

        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            var next = i + 1 < script.Length ? script[i + 1] : '\0';

            if (inBlockComment)
            {
                buffer.Append(c, blockIsCode);
                if (c == '*' && next == '/')
                {
                    buffer.Append(next, blockIsCode);
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (closingQuote is { } quote)
            {
                buffer.Append(c, isCode: true);
                if (c == '\\' && quote != '`' && next != '\0')
                {
                    // A backslash escapes the next character inside a string.
                    buffer.Append(next, isCode: true);
                    i++;
                }
                else if (c == quote)
                {
                    if (next == quote)
                    {
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

            if (buffer.AtLineStart && !buffer.HasCode && TryReadDelimiterDirective(script, i, out var newDelimiter, out var lineEnd))
            {
                buffer.FlushTo(statements);
                buffer.MarkLineStart();
                delimiter = newDelimiter;
                i = lineEnd;
                continue;
            }

            if (string.CompareOrdinal(script, i, delimiter, 0, delimiter.Length) == 0)
            {
                buffer.FlushTo(statements);
                i += delimiter.Length - 1;
                continue;
            }

            if (c == '#' || (c == '-' && next == '-' && (i + 2 >= script.Length || char.IsWhiteSpace(script[i + 2]))))
            {
                // A line comment runs to the end of the line; the line break itself is
                // processed by the next iteration.
                var end = EndOfLine(script, i);
                buffer.AppendRange(script, i, end, isCode: false);
                i = end - 1;
                continue;
            }

            if (c == '/' && next == '*')
            {
                blockIsCode = i + 2 < script.Length && script[i + 2] is '!' or '+';
                buffer.Append(c, blockIsCode);
                buffer.Append(next, blockIsCode);
                inBlockComment = true;
                i++;
                continue;
            }

            closingQuote = c is '\'' or '"' or '`' ? c : null;
            buffer.Append(c, isCode: true);
        }

        buffer.FlushTo(statements);
        return statements;
    }

    /// <summary>
    /// Reads a <c>DELIMITER &lt;token&gt;</c> line. The keyword is case-insensitive and must be
    /// followed by whitespace; the new delimiter is the first whitespace-separated token after it.
    /// </summary>
    private static bool TryReadDelimiterDirective(string script, int index, out string delimiter, out int lineEnd)
    {
        delimiter = string.Empty;
        lineEnd = EndOfLine(script, index);

        var keywordEnd = index + DelimiterKeyword.Length;
        if (keywordEnd >= lineEnd
            || string.Compare(script, index, DelimiterKeyword, 0, DelimiterKeyword.Length, StringComparison.OrdinalIgnoreCase) != 0
            || script[keywordEnd] is not (' ' or '\t'))
        {
            return false;
        }

        var token = script[keywordEnd..lineEnd].Trim();
        var space = token.IndexOfAny([' ', '\t']);
        delimiter = space < 0 ? token : token[..space];
        return delimiter.Length > 0;
    }
}
