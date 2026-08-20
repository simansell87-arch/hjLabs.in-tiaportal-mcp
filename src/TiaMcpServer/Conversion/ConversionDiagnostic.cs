namespace TiaMcpServer.Conversion;

public enum ConversionSeverity
{
    /// <summary>Conversion succeeded but the result is worth checking.</summary>
    Info,

    /// <summary>Conversion produced ladder, but an assumption was made that may be wrong.</summary>
    Warning,

    /// <summary>The construct could not be converted; the SCL was preserved as a comment instead.</summary>
    Error
}

/// <summary>
/// A single finding raised while converting SCL to ladder.
/// </summary>
public sealed class ConversionDiagnostic
{
    public ConversionDiagnostic(ConversionSeverity severity, string code, string message, int line = 0, string? snippet = null)
    {
        Severity = severity;
        Code = code;
        Message = message;
        Line = line;
        Snippet = snippet;
    }

    public ConversionSeverity Severity { get; }

    /// <summary>Stable identifier such as <c>SCL2LAD001</c>, so callers can filter or suppress.</summary>
    public string Code { get; }

    public string Message { get; }

    /// <summary>One-based source line, or 0 when the finding is not tied to a line.</summary>
    public int Line { get; }

    /// <summary>The offending SCL, trimmed to a single readable line.</summary>
    public string? Snippet { get; }

    public override string ToString()
    {
        var location = Line > 0 ? $"line {Line}: " : string.Empty;
        return $"{Severity} {Code} - {location}{Message}";
    }
}
