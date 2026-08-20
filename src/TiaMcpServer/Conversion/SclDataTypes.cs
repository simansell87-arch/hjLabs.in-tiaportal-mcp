using System;
using System.Collections.Generic;
using System.Globalization;

namespace TiaMcpServer.Conversion;

/// <summary>
/// Data type helpers shared by the parser, the converter and the SimaticML writer.
/// </summary>
/// <remarks>
/// SimaticML spells elementary types in a fixed casing (<c>Bool</c>, <c>DInt</c>, <c>Time_Of_Day</c>, ...)
/// while SCL sources are written in any casing, so every type name is funnelled through
/// <see cref="Normalize"/> before it reaches the XML.
/// </remarks>
public static class SclDataTypes
{
    /// <summary>Canonical SimaticML spelling keyed by the upper-case SCL spelling.</summary>
    private static readonly Dictionary<string, string> CanonicalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "BOOL", "Bool" },
        { "BYTE", "Byte" },
        { "WORD", "Word" },
        { "DWORD", "DWord" },
        { "LWORD", "LWord" },
        { "SINT", "SInt" },
        { "USINT", "USInt" },
        { "INT", "Int" },
        { "UINT", "UInt" },
        { "DINT", "DInt" },
        { "UDINT", "UDInt" },
        { "LINT", "LInt" },
        { "ULINT", "ULInt" },
        { "REAL", "Real" },
        { "LREAL", "LReal" },
        { "TIME", "Time" },
        { "T", "Time" },
        { "LTIME", "LTime" },
        { "LT", "LTime" },
        { "S5TIME", "S5Time" },
        { "S5T", "S5Time" },
        { "DATE", "Date" },
        { "D", "Date" },
        { "TIME_OF_DAY", "Time_Of_Day" },
        { "TOD", "Time_Of_Day" },
        { "LTIME_OF_DAY", "LTime_Of_Day" },
        { "LTOD", "LTime_Of_Day" },
        { "DATE_AND_TIME", "Date_And_Time" },
        { "DT", "Date_And_Time" },
        { "LDT", "LDT" },
        { "DTL", "DTL" },
        { "CHAR", "Char" },
        { "WCHAR", "WChar" },
        { "STRING", "String" },
        { "WSTRING", "WString" },
        { "VOID", "Void" }
    };

    private static readonly HashSet<string> TimeTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "T", "TIME", "LT", "LTIME", "S5T", "S5TIME", "D", "DATE", "TOD", "TIME_OF_DAY",
        "LTOD", "LTIME_OF_DAY", "DT", "DATE_AND_TIME", "LDT", "DTL"
    };

    private static readonly HashSet<string> BooleanTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "BOOL"
    };

    /// <summary>Returns the SimaticML spelling of an elementary type, or the input unchanged for UDTs and arrays.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name!.Trim();
        return CanonicalNames.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }

    public static bool IsTimeTypeName(string? name)
    {
        return !string.IsNullOrEmpty(name) && TimeTypeNames.Contains(name!.Trim());
    }

    public static bool IsBooleanTypeName(string? name)
    {
        return !string.IsNullOrEmpty(name) && BooleanTypeNames.Contains(name!.Trim());
    }

    /// <summary>
    /// Infers the SimaticML <c>ConstantType</c> of a literal as written in SCL.
    /// Returns <c>Int</c> for a plain whole number, which is what TIA Portal itself assumes.
    /// </summary>
    public static string InferConstantType(SclLiteralExpression literal)
    {
        switch (literal.Kind)
        {
            case SclLiteralKind.Boolean:
                return "Bool";

            case SclLiteralKind.Text:
                return literal.Text.Length == 1 ? "Char" : "String";

            case SclLiteralKind.Time:
                return Normalize(literal.TypePrefix ?? "Time");
        }

        if (!string.IsNullOrEmpty(literal.TypePrefix))
        {
            return Normalize(literal.TypePrefix);
        }

        var text = literal.Text.Replace("_", string.Empty);
        var hashIndex = text.IndexOf('#');

        if (hashIndex >= 0)
        {
            var digits = text.Substring(hashIndex + 1);

            if (digits.Length <= 2)
            {
                return "Byte";
            }

            if (digits.Length <= 4)
            {
                return "Word";
            }

            return digits.Length <= 8 ? "DWord" : "LWord";
        }

        if (text.IndexOf('.') >= 0 || text.IndexOf('e') >= 0 || text.IndexOf('E') >= 0)
        {
            return "Real";
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            if (value >= short.MinValue && value <= short.MaxValue)
            {
                return "Int";
            }

            return value >= int.MinValue && value <= int.MaxValue ? "DInt" : "LInt";
        }

        return "Int";
    }

    /// <summary>
    /// Returns the literal payload without its type or base prefix, which is what
    /// SimaticML expects inside <c>ConstantValue</c>.
    /// </summary>
    public static string ConstantValueText(SclLiteralExpression literal)
    {
        if (literal.Kind == SclLiteralKind.Boolean)
        {
            var text = literal.Text;
            var hash = text.IndexOf('#');
            var payload = hash >= 0 ? text.Substring(hash + 1) : text;
            return payload.ToUpperInvariant() == "TRUE" ? "true" : "false";
        }

        if (literal.Kind == SclLiteralKind.Text)
        {
            return literal.Text;
        }

        if (!string.IsNullOrEmpty(literal.TypePrefix))
        {
            // Time literals keep their prefix, because 'T#5s' is the value TIA Portal shows.
            if (literal.Kind == SclLiteralKind.Time)
            {
                return literal.Text;
            }

            var hash = literal.Text.IndexOf('#');
            if (hash >= 0)
            {
                return literal.Text.Substring(hash + 1);
            }
        }

        return literal.Text;
    }

    /// <summary>
    /// Picks the wider of two inferred operand types so that a compare or math box gets a single
    /// <c>SrcType</c>. Unknown or conflicting types fall back to <paramref name="fallback"/>.
    /// </summary>
    public static string Widen(string? left, string? right, string fallback)
    {
        var l = Normalize(left);
        var r = Normalize(right);

        if (l.Length == 0 && r.Length == 0)
        {
            return fallback;
        }

        if (l.Length == 0)
        {
            return r;
        }

        if (r.Length == 0)
        {
            return l;
        }

        if (string.Equals(l, r, StringComparison.Ordinal))
        {
            return l;
        }

        var rank = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "Bool", 0 }, { "Byte", 1 }, { "SInt", 1 }, { "USInt", 1 },
            { "Word", 2 }, { "Int", 2 }, { "UInt", 2 },
            { "DWord", 3 }, { "DInt", 3 }, { "UDInt", 3 },
            { "Real", 4 }, { "LWord", 5 }, { "LInt", 5 }, { "ULInt", 5 }, { "LReal", 6 }
        };

        var hasLeft = rank.TryGetValue(l, out var leftRank);
        var hasRight = rank.TryGetValue(r, out var rightRank);

        if (!hasLeft || !hasRight)
        {
            // A UDT, array or time type on either side wins over an inferred numeric default.
            return hasLeft ? r : l;
        }

        return leftRank >= rightRank ? l : r;
    }
}
