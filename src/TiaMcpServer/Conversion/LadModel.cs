using System.Collections.Generic;

namespace TiaMcpServer.Conversion;

public enum LadOperandKind
{
    /// <summary>A PLC tag or data block member: SimaticML scope <c>GlobalVariable</c>.</summary>
    Global,

    /// <summary>A block interface member: SimaticML scope <c>LocalVariable</c>.</summary>
    Local,

    /// <summary>A literal: SimaticML scope <c>LiteralConstant</c>.</summary>
    Constant,

    /// <summary>An absolute address such as <c>%I0.0</c>: SimaticML scope <c>Address</c>.</summary>
    Address
}

/// <summary>
/// An operand as it appears on a ladder element.
/// </summary>
public sealed class LadOperand
{
    public LadOperandKind Kind { get; set; } = LadOperandKind.Global;

    /// <summary>Display text, e.g. <c>"Motor".Run</c>. Never used for the XML itself.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Dotted operand components. Empty for constants and addresses.</summary>
    public List<LadOperandComponent> Components { get; } = new List<LadOperandComponent>();

    /// <summary>SimaticML type name of a constant, e.g. <c>Int</c>.</summary>
    public string? ConstantType { get; set; }

    /// <summary>Literal payload of a constant, without its type prefix.</summary>
    public string? ConstantValue { get; set; }

    /// <summary>Parsed absolute address; set only when <see cref="Kind"/> is <see cref="LadOperandKind.Address"/>.</summary>
    public LadAddress? Address { get; set; }

    /// <summary>Best-effort data type of the operand, used to pick a box <c>SrcType</c>.</summary>
    public string? DataType { get; set; }

    public static LadOperand FromConstant(string type, string value, string displayText)
    {
        return new LadOperand
        {
            Kind = LadOperandKind.Constant,
            ConstantType = type,
            ConstantValue = value,
            DataType = type,
            Text = displayText
        };
    }
}

/// <summary>
/// One dotted component of an operand, with optional array indices.
/// </summary>
public sealed class LadOperandComponent
{
    public LadOperandComponent(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>Array subscripts of this component; empty when it is not an array access.</summary>
    public List<LadOperand> Indices { get; } = new List<LadOperand>();
}

/// <summary>
/// A decoded absolute address such as <c>%IW10</c>.
/// </summary>
public sealed class LadAddress
{
    /// <summary>SimaticML memory area: Input, Output, Memory, PeripheryInput or PeripheryOutput.</summary>
    public string Area { get; set; } = string.Empty;

    /// <summary>Offset of the operand in bits from the start of the area.</summary>
    public int BitOffset { get; set; }

    /// <summary>Width of the access as a SimaticML type name: Bool, Byte, Word, DWord or LWord.</summary>
    public string Type { get; set; } = "Bool";
}

#region rung logic

/// <summary>
/// Boolean power-flow logic to the left of the outputs of a network.
/// </summary>
public abstract class LadLogic
{
}

public enum LadContactKind
{
    /// <summary>Normally open contact.</summary>
    Normal,

    /// <summary>Normally closed contact.</summary>
    Negated
}

public sealed class LadContact : LadLogic
{
    public LadOperand Operand { get; set; } = new LadOperand();
    public LadContactKind Kind { get; set; } = LadContactKind.Normal;
}

public enum LadCompareOperator
{
    Equal,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual
}

public sealed class LadCompare : LadLogic
{
    public LadCompareOperator Operator { get; set; }
    public LadOperand Left { get; set; } = new LadOperand();
    public LadOperand Right { get; set; } = new LadOperand();

    /// <summary>SimaticML <c>SrcType</c> template value of the compare box.</summary>
    public string DataType { get; set; } = "Int";
}

/// <summary>Elements wired in series - a logical AND.</summary>
public sealed class LadSeries : LadLogic
{
    public List<LadLogic> Elements { get; } = new List<LadLogic>();
}

/// <summary>Elements wired in parallel branches - a logical OR.</summary>
public sealed class LadParallel : LadLogic
{
    public List<LadLogic> Branches { get; } = new List<LadLogic>();
}

/// <summary>A constant rung state; <c>true</c> means 'wired straight to the power rail'.</summary>
public sealed class LadConstantLogic : LadLogic
{
    public bool Value { get; set; }
}

#endregion

#region rung outputs

/// <summary>
/// An element placed at the right-hand end of a network.
/// </summary>
public abstract class LadOutput
{
}

public enum LadCoilKind
{
    /// <summary>Assignment coil.</summary>
    Coil,

    /// <summary>Set coil.</summary>
    Set,

    /// <summary>Reset coil.</summary>
    Reset
}

public sealed class LadCoil : LadOutput
{
    public LadOperand Operand { get; set; } = new LadOperand();
    public LadCoilKind Kind { get; set; } = LadCoilKind.Coil;
}

/// <summary>A MOVE box.</summary>
public sealed class LadMove : LadOutput
{
    public LadOperand Source { get; set; } = new LadOperand();
    public LadOperand Destination { get; set; } = new LadOperand();
}

public enum LadMathOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo
}

/// <summary>An arithmetic box with EN/ENO.</summary>
public sealed class LadMath : LadOutput
{
    public LadMathOperator Operator { get; set; }
    public LadOperand Left { get; set; } = new LadOperand();
    public LadOperand Right { get; set; } = new LadOperand();
    public LadOperand Destination { get; set; } = new LadOperand();
    public string DataType { get; set; } = "Int";
}

public sealed class LadCallParameter
{
    public string Name { get; set; } = string.Empty;

    /// <summary>SimaticML parameter section: Input, Output, InOut or Return.</summary>
    public string Section { get; set; } = "Input";

    /// <summary>SimaticML parameter type, or null when it could not be resolved.</summary>
    public string? DataType { get; set; }

    public LadOperand Operand { get; set; } = new LadOperand();
}

/// <summary>A call of another block, drawn as a box with EN/ENO.</summary>
public sealed class LadCall : LadOutput
{
    public string BlockName { get; set; } = string.Empty;

    /// <summary>FC, FB or the name of a system block type.</summary>
    public string BlockType { get; set; } = "FC";

    public int? BlockNumber { get; set; }

    /// <summary>Instance operand for FB calls; null for FC calls.</summary>
    public LadOperand? Instance { get; set; }

    public List<LadCallParameter> Parameters { get; } = new List<LadCallParameter>();
}

#endregion

/// <summary>
/// One ladder network (rung group): a boolean condition feeding a chain of outputs.
/// </summary>
public sealed class LadNetwork
{
    public string? Title { get; set; }
    public string? Comment { get; set; }

    /// <summary>Power-flow condition; null means the outputs hang directly on the power rail.</summary>
    public LadLogic? Condition { get; set; }

    public List<LadOutput> Outputs { get; } = new List<LadOutput>();

    /// <summary>One-based line of the SCL statement this network came from.</summary>
    public int SourceLine { get; set; }

    /// <summary>True when the network carries no logic and only preserves unconvertible SCL as a comment.</summary>
    public bool IsPlaceholder { get; set; }
}
