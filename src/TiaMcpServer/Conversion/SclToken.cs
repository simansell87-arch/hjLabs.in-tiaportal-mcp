using System;

namespace TiaMcpServer.Conversion;

/// <summary>
/// Lexical categories produced by <see cref="SclLexer"/>.
/// </summary>
public enum SclTokenKind
{
    EndOfFile,

    /// <summary>Bare identifier or keyword, e.g. <c>IF</c>, <c>Motor</c>.</summary>
    Identifier,

    /// <summary>Quoted symbol, e.g. <c>"Motor Run"</c>. Text excludes the quotes.</summary>
    QuotedSymbol,

    /// <summary>Block-local symbol, e.g. <c>#command</c>. Text excludes the leading '#'.</summary>
    LocalSymbol,

    /// <summary>Absolute address, e.g. <c>%I0.0</c>. Text includes the leading '%'.</summary>
    AbsoluteAddress,

    /// <summary>Numeric or typed literal, e.g. <c>12</c>, <c>16#FF</c>, <c>T#5s</c>, <c>INT#3</c>.</summary>
    Number,

    /// <summary>Character string literal, e.g. <c>'text'</c>. Text excludes the quotes.</summary>
    StringLiteral,

    /// <summary>Operator or punctuation, e.g. <c>:=</c>, <c>&gt;=</c>, <c>(</c>.</summary>
    Operator,

    /// <summary>Line or block comment. Text excludes the comment markers.</summary>
    Comment
}

/// <summary>
/// A single SCL token together with its position in the source text.
/// </summary>
public sealed class SclToken
{
    public SclToken(SclTokenKind kind, string text, int position, int length, int line, int column)
    {
        Kind = kind;
        Text = text ?? string.Empty;
        Position = position;
        Length = length;
        Line = line;
        Column = column;
    }

    public SclTokenKind Kind { get; }

    /// <summary>Token value with delimiters (quotes, '#', comment markers) already removed.</summary>
    public string Text { get; }

    /// <summary>Zero-based character offset of the first character of the token.</summary>
    public int Position { get; }

    /// <summary>Number of source characters the token spans, including delimiters.</summary>
    public int Length { get; }

    /// <summary>Zero-based character offset just past the last character of the token.</summary>
    public int EndPosition
    {
        get { return Position + Length; }
    }

    /// <summary>One-based line number of the first character of the token.</summary>
    public int Line { get; }

    /// <summary>One-based column number of the first character of the token.</summary>
    public int Column { get; }

    /// <summary>Returns true when the token is an identifier matching <paramref name="keyword"/> case-insensitively.</summary>
    public bool IsKeyword(string keyword)
    {
        return Kind == SclTokenKind.Identifier
            && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns true when the token is the given operator or punctuation.</summary>
    public bool IsOperator(string op)
    {
        return Kind == SclTokenKind.Operator
            && string.Equals(Text, op, StringComparison.Ordinal);
    }

    public override string ToString()
    {
        return $"{Kind}:'{Text}' ({Line},{Column})";
    }
}
