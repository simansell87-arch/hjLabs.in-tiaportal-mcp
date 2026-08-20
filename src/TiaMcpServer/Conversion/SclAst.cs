using System.Collections.Generic;

namespace TiaMcpServer.Conversion;

public enum SclBinaryOperator
{
    And,
    Or,
    Xor,
    Equal,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Power
}

public enum SclUnaryOperator
{
    Not,
    Negate,
    Plus
}

public enum SclLiteralKind
{
    Boolean,
    Number,
    Time,
    Text
}

public enum SclArgumentDirection
{
    /// <summary>Passed with ':=' - an input or in/out parameter.</summary>
    Input,

    /// <summary>Passed with '=&gt;' - an output parameter.</summary>
    Output,

    /// <summary>Passed by position, without a parameter name.</summary>
    Positional
}

public enum SclBlockKind
{
    Function,
    FunctionBlock,
    OrganizationBlock,

    /// <summary>Body-only source without a block header.</summary>
    Fragment
}

#region expressions

public abstract class SclExpression
{
    public int Line { get; set; }
    public int Column { get; set; }
}

public sealed class SclLiteralExpression : SclExpression
{
    public string Text { get; set; } = string.Empty;
    public SclLiteralKind Kind { get; set; }

    /// <summary>Explicit type prefix of a typed literal (the 'INT' of <c>INT#3</c>), otherwise null.</summary>
    public string? TypePrefix { get; set; }
}

public sealed class SclSymbolExpression : SclExpression
{
    /// <summary>The operand exactly as it should be shown to the user, e.g. <c>"Motor".Run</c>.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Dotted operand components, e.g. <c>Motor</c>, <c>Run</c>.</summary>
    public List<string> Components { get; } = new List<string>();

    /// <summary>Index expressions per component; null when the component is not an array access.</summary>
    public List<List<SclExpression>?> ComponentIndices { get; } = new List<List<SclExpression>?>();

    /// <summary>True for <c>#local</c> operands that resolve against the block interface.</summary>
    public bool IsLocal { get; set; }

    /// <summary>True for absolute addresses such as <c>%I0.0</c>.</summary>
    public bool IsAbsolute { get; set; }
}

public sealed class SclUnaryExpression : SclExpression
{
    public SclUnaryOperator Operator { get; set; }
    public SclExpression Operand { get; set; } = null!;
}

public sealed class SclBinaryExpression : SclExpression
{
    public SclBinaryOperator Operator { get; set; }
    public SclExpression Left { get; set; } = null!;
    public SclExpression Right { get; set; } = null!;
}

public sealed class SclFunctionCallExpression : SclExpression
{
    /// <summary>Called function or instance name, without quotes.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>True when the callee was written as a block-local symbol (multi-instance call).</summary>
    public bool IsLocal { get; set; }

    public List<SclArgument> Arguments { get; } = new List<SclArgument>();
}

public sealed class SclArgument
{
    public string? Name { get; set; }
    public SclArgumentDirection Direction { get; set; } = SclArgumentDirection.Positional;
    public SclExpression Value { get; set; } = null!;
}

#endregion

#region statements

public abstract class SclStatement
{
    public int Line { get; set; }

    /// <summary>Comments that directly precede the statement in the source.</summary>
    public List<string> LeadingComments { get; } = new List<string>();

    /// <summary>Comment on the same line as the end of the statement, if any.</summary>
    public string? TrailingComment { get; set; }

    /// <summary>Verbatim source of the statement, used for diagnostics and for preserving unconvertible code.</summary>
    public string RawText { get; set; } = string.Empty;
}

public sealed class SclAssignmentStatement : SclStatement
{
    public SclExpression Target { get; set; } = null!;
    public SclExpression Value { get; set; } = null!;
}

public sealed class SclIfBranch
{
    public SclExpression Condition { get; set; } = null!;
    public List<SclStatement> Body { get; } = new List<SclStatement>();
}

public sealed class SclIfStatement : SclStatement
{
    /// <summary>The IF branch followed by any ELSIF branches, in source order.</summary>
    public List<SclIfBranch> Branches { get; } = new List<SclIfBranch>();

    public List<SclStatement>? ElseBody { get; set; }
}

public sealed class SclCaseLabel
{
    public SclExpression From { get; set; } = null!;

    /// <summary>Upper bound of a <c>1..5</c> range label, otherwise null.</summary>
    public SclExpression? To { get; set; }
}

public sealed class SclCaseBranch
{
    public List<SclCaseLabel> Labels { get; } = new List<SclCaseLabel>();
    public List<SclStatement> Body { get; } = new List<SclStatement>();
}

public sealed class SclCaseStatement : SclStatement
{
    public SclExpression Selector { get; set; } = null!;
    public List<SclCaseBranch> Branches { get; } = new List<SclCaseBranch>();
    public List<SclStatement>? ElseBody { get; set; }
}

public sealed class SclCallStatement : SclStatement
{
    public string Name { get; set; } = string.Empty;
    public bool IsLocal { get; set; }
    public List<SclArgument> Arguments { get; } = new List<SclArgument>();
}

public sealed class SclRegionStatement : SclStatement
{
    public string Title { get; set; } = string.Empty;
    public List<SclStatement> Body { get; } = new List<SclStatement>();
}

/// <summary>
/// A statement that has no ladder equivalent. The raw SCL is kept so the converter can preserve it
/// as a network comment instead of silently dropping logic.
/// </summary>
public sealed class SclUnsupportedStatement : SclStatement
{
    public string Construct { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

#endregion

#region block definition

public sealed class SclInterfaceMember
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Data type exactly as declared, e.g. <c>Bool</c>, <c>Array[0..9] of Int</c>, <c>"UDT_Motor"</c>.</summary>
    public string DataType { get; set; } = string.Empty;

    public string? StartValue { get; set; }
    public string? Comment { get; set; }

    /// <summary>Child members of an inline <c>STRUCT</c> declaration; empty for scalar members.</summary>
    public List<SclInterfaceMember> Members { get; } = new List<SclInterfaceMember>();
}

public sealed class SclInterfaceSection
{
    public SclInterfaceSection(string name)
    {
        Name = name;
    }

    /// <summary>SimaticML section name: Input, Output, InOut, Static, Temp, Constant or Return.</summary>
    public string Name { get; }

    public List<SclInterfaceMember> Members { get; } = new List<SclInterfaceMember>();
}

public sealed class SclBlockDefinition
{
    public SclBlockKind Kind { get; set; } = SclBlockKind.Fragment;
    public string Name { get; set; } = string.Empty;
    public string? ReturnType { get; set; }
    public string? Title { get; set; }
    public string? Comment { get; set; }
    public string? Version { get; set; }
    public string? Family { get; set; }
    public string? Author { get; set; }
    public int? Number { get; set; }

    public List<SclInterfaceSection> Sections { get; } = new List<SclInterfaceSection>();
    public List<SclStatement> Body { get; } = new List<SclStatement>();
}

#endregion
