using System;
using System.Collections.Generic;
using System.Text;

namespace TiaMcpServer.Conversion;

/// <summary>
/// Converts SCL source text into a flat token list.
/// </summary>
/// <remarks>
/// The lexer is deliberately forgiving: unknown characters are skipped rather than reported, so that a
/// partially understood source still yields a usable statement stream for the ladder conversion.
/// Pragma/attribute blocks (<c>{ ... }</c>) are discarded because they carry no ladder semantics.
/// </remarks>
public sealed class SclLexer
{
    /// <summary>Type prefixes whose literal payload may contain '-' and ':' (date/time literals).</summary>
    private static readonly HashSet<string> DateTimeLiteralPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "T", "TIME", "LT", "LTIME", "S5T", "S5TIME", "D", "DATE", "TOD", "TIME_OF_DAY",
        "LTOD", "LTIME_OF_DAY", "DT", "DATE_AND_TIME", "LDT", "DTL"
    };

    private static readonly string[] TwoCharOperators = { ":=", "=>", "<=", ">=", "<>", "**", ".." };

    private static readonly char[] SingleCharOperators = { '+', '-', '*', '/', '<', '>', '=', '(', ')', '[', ']', ',', ';', ':', '.', '&' };

    private readonly string _text;
    private int _index;
    private int _line = 1;
    private int _column = 1;

    public SclLexer(string text)
    {
        _text = text ?? string.Empty;
    }

    /// <summary>
    /// Tokenizes the whole source. The returned list always ends with an <see cref="SclTokenKind.EndOfFile"/> token.
    /// </summary>
    public List<SclToken> Tokenize()
    {
        var tokens = new List<SclToken>();

        while (true)
        {
            SkipWhitespace();

            if (_index >= _text.Length)
            {
                tokens.Add(new SclToken(SclTokenKind.EndOfFile, string.Empty, _text.Length, 0, _line, _column));
                return tokens;
            }

            var start = _index;
            var line = _line;
            var column = _column;
            var c = _text[_index];

            if (c == '/' && Peek(1) == '/')
            {
                Advance(2);
                var textStart = _index;
                while (_index < _text.Length && _text[_index] != '\n' && _text[_index] != '\r')
                {
                    Advance(1);
                }
                var comment = _text.Substring(textStart, _index - textStart).Trim();
                tokens.Add(new SclToken(SclTokenKind.Comment, comment, start, _index - start, line, column));
                continue;
            }

            if (c == '(' && Peek(1) == '*')
            {
                tokens.Add(ReadBlockComment(start, line, column));
                continue;
            }

            if (c == '{')
            {
                // Attribute/pragma block - no ladder meaning, drop it.
                while (_index < _text.Length && _text[_index] != '}')
                {
                    Advance(1);
                }
                if (_index < _text.Length)
                {
                    Advance(1);
                }
                continue;
            }

            if (c == '"')
            {
                var value = ReadDelimited('"');
                tokens.Add(new SclToken(SclTokenKind.QuotedSymbol, value, start, _index - start, line, column));
                continue;
            }

            if (c == '\'')
            {
                var value = ReadDelimited('\'');
                tokens.Add(new SclToken(SclTokenKind.StringLiteral, value, start, _index - start, line, column));
                continue;
            }

            if (c == '#')
            {
                Advance(1);
                string name;
                if (_index < _text.Length && _text[_index] == '"')
                {
                    name = ReadDelimited('"');
                }
                else
                {
                    var nameStart = _index;
                    while (_index < _text.Length && IsIdentifierChar(_text[_index]))
                    {
                        Advance(1);
                    }
                    name = _text.Substring(nameStart, _index - nameStart);
                }
                tokens.Add(new SclToken(SclTokenKind.LocalSymbol, name, start, _index - start, line, column));
                continue;
            }

            if (c == '%')
            {
                Advance(1);
                while (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '.' || _text[_index] == '_'))
                {
                    Advance(1);
                }
                tokens.Add(new SclToken(SclTokenKind.AbsoluteAddress, _text.Substring(start, _index - start), start, _index - start, line, column));
                continue;
            }

            if (char.IsDigit(c))
            {
                ReadNumber();
                tokens.Add(new SclToken(SclTokenKind.Number, _text.Substring(start, _index - start), start, _index - start, line, column));
                continue;
            }

            if (IsIdentifierStart(c))
            {
                while (_index < _text.Length && IsIdentifierChar(_text[_index]))
                {
                    Advance(1);
                }

                var word = _text.Substring(start, _index - start);

                if (_index < _text.Length && _text[_index] == '#')
                {
                    // Typed literal, e.g. INT#3, T#5s, 16#FF is handled by ReadNumber instead.
                    Advance(1);
                    var allowDateChars = DateTimeLiteralPrefixes.Contains(word);
                    while (_index < _text.Length && IsTypedLiteralChar(_text[_index], allowDateChars))
                    {
                        Advance(1);
                    }
                    tokens.Add(new SclToken(SclTokenKind.Number, _text.Substring(start, _index - start), start, _index - start, line, column));
                    continue;
                }

                tokens.Add(new SclToken(SclTokenKind.Identifier, word, start, _index - start, line, column));
                continue;
            }

            var op = ReadOperator();
            if (op != null)
            {
                tokens.Add(new SclToken(SclTokenKind.Operator, op, start, _index - start, line, column));
                continue;
            }

            // Unrecognized character - skip it so a single typo cannot stall the conversion.
            Advance(1);
        }
    }

    private SclToken ReadBlockComment(int start, int line, int column)
    {
        Advance(2);
        var textStart = _index;
        var depth = 1;
        var end = _index;

        while (_index < _text.Length && depth > 0)
        {
            if (_text[_index] == '(' && Peek(1) == '*')
            {
                depth++;
                Advance(2);
                continue;
            }

            if (_text[_index] == '*' && Peek(1) == ')')
            {
                depth--;
                end = _index;
                Advance(2);
                if (depth == 0)
                {
                    break;
                }
                continue;
            }

            Advance(1);
            end = _index;
        }

        var comment = end > textStart ? _text.Substring(textStart, end - textStart).Trim() : string.Empty;
        return new SclToken(SclTokenKind.Comment, comment, start, _index - start, line, column);
    }

    /// <summary>
    /// Reads a delimited run starting at the opening delimiter and returns the inner text.
    /// Honours the SCL '$' escape inside character strings.
    /// </summary>
    private string ReadDelimited(char delimiter)
    {
        Advance(1);
        var sb = new StringBuilder();

        while (_index < _text.Length)
        {
            var ch = _text[_index];

            if (ch == '$' && delimiter == '\'' && _index + 1 < _text.Length)
            {
                sb.Append(ch);
                sb.Append(_text[_index + 1]);
                Advance(2);
                continue;
            }

            if (ch == delimiter)
            {
                break;
            }

            sb.Append(ch);
            Advance(1);
        }

        if (_index < _text.Length)
        {
            Advance(1);
        }

        return sb.ToString();
    }

    private void ReadNumber()
    {
        while (_index < _text.Length && (char.IsDigit(_text[_index]) || _text[_index] == '_'))
        {
            Advance(1);
        }

        // Based literal: 2#1010, 8#777, 16#FF
        if (_index < _text.Length && _text[_index] == '#')
        {
            Advance(1);
            while (_index < _text.Length && (IsHexDigit(_text[_index]) || _text[_index] == '_'))
            {
                Advance(1);
            }
            return;
        }

        // Fractional part - '..' of a CASE range must not be consumed.
        if (_index < _text.Length && _text[_index] == '.' && char.IsDigit(Peek(1)))
        {
            Advance(1);
            while (_index < _text.Length && (char.IsDigit(_text[_index]) || _text[_index] == '_'))
            {
                Advance(1);
            }
        }

        // Exponent - only consumed when it is well formed.
        if (_index < _text.Length && (_text[_index] == 'e' || _text[_index] == 'E'))
        {
            var savedIndex = _index;
            var savedLine = _line;
            var savedColumn = _column;

            Advance(1);
            if (_index < _text.Length && (_text[_index] == '+' || _text[_index] == '-'))
            {
                Advance(1);
            }

            if (_index < _text.Length && char.IsDigit(_text[_index]))
            {
                while (_index < _text.Length && char.IsDigit(_text[_index]))
                {
                    Advance(1);
                }
            }
            else
            {
                _index = savedIndex;
                _line = savedLine;
                _column = savedColumn;
            }
        }
    }

    private string? ReadOperator()
    {
        if (_index + 1 < _text.Length)
        {
            var candidate = _text.Substring(_index, 2);
            foreach (var op in TwoCharOperators)
            {
                if (string.Equals(candidate, op, StringComparison.Ordinal))
                {
                    Advance(2);
                    return op;
                }
            }
        }

        var single = _text[_index];
        foreach (var op in SingleCharOperators)
        {
            if (single == op)
            {
                Advance(1);
                return single.ToString();
            }
        }

        return null;
    }

    private void SkipWhitespace()
    {
        while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
        {
            Advance(1);
        }
    }

    private void Advance(int count)
    {
        for (var i = 0; i < count && _index < _text.Length; i++)
        {
            if (_text[_index] == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }

            _index++;
        }
    }

    private char Peek(int offset)
    {
        var position = _index + offset;
        return position < _text.Length ? _text[position] : '\0';
    }

    private static bool IsTypedLiteralChar(char c, bool allowDateChars)
    {
        if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '#')
        {
            return true;
        }

        return allowDateChars && (c == '-' || c == ':' || c == '+');
    }

    private static bool IsIdentifierStart(char c)
    {
        return char.IsLetter(c) || c == '_';
    }

    private static bool IsIdentifierChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_';
    }

    private static bool IsHexDigit(char c)
    {
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }
}
