using System.Text;

namespace DbAiTest.Core.Schema;

/// <summary>
/// Splits a SQL script into the commands a server accepts, for the dialects that need it.
/// </summary>
/// <remarks>
/// <para>
/// The engines disagree on what a command is, and getting this wrong is silent: SQL Server
/// rejects a batch containing a <c>GO</c>, and MySQL needs its client-side <c>DELIMITER</c>
/// directive honoured. PostgreSQL needs no splitting at all: a file is sent whole, which keeps
/// every dollar-quoted body intact.
/// </para>
/// <para>
/// Both splitters are real scanners, not line matchers: a separator inside a string literal,
/// a quoted identifier or a comment is never a separator. Comments are kept in the command
/// they belong to; a trailing piece that holds nothing but comments and whitespace is dropped,
/// because MySQL rejects an empty query.
/// </para>
/// </remarks>
public static partial class SqlStatementSplitter
{
    /// <summary>
    /// Accumulates one command and remembers whether it holds anything but comments and whitespace.
    /// </summary>
    private sealed class CommandBuffer
    {
        private readonly StringBuilder _text = new();

        /// <summary>True once a character outside a comment that is not whitespace was appended.</summary>
        public bool HasCode { get; private set; }

        /// <summary>True when nothing but whitespace was appended since the last line break.</summary>
        public bool AtLineStart { get; private set; } = true;

        public void Append(char c, bool isCode)
        {
            _text.Append(c);

            // CR alone ends a line too (classic Mac line endings).
            if (IsLineBreak(c))
            {
                AtLineStart = true;
                return;
            }

            if (!char.IsWhiteSpace(c))
            {
                AtLineStart = false;
                HasCode |= isCode;
            }
        }

        /// <summary>Appends <c>script[start..end)</c>, one character at a time.</summary>
        public void AppendRange(string script, int start, int end, bool isCode)
        {
            for (var k = start; k < end; k++)
            {
                Append(script[k], isCode);
            }
        }

        public void MarkLineStart() => AtLineStart = true;

        /// <summary>Adds the command to <paramref name="commands"/> when it holds code, then resets.</summary>
        public void FlushTo(List<string> commands, int repeat = 1)
        {
            var text = _text.ToString().Trim();
            if (HasCode && text.Length > 0)
            {
                for (var r = 0; r < repeat; r++)
                {
                    commands.Add(text);
                }
            }

            _text.Clear();
            HasCode = false;
        }
    }

    private static bool IsLineBreak(char c) => c is '\n' or '\r';

    /// <summary>Index of the first <c>\r</c> or <c>\n</c> at or after <paramref name="index"/>, or the script length.</summary>
    private static int EndOfLine(string script, int index)
    {
        var end = script.IndexOfAny(['\r', '\n'], index);
        return end < 0 ? script.Length : end;
    }
}
