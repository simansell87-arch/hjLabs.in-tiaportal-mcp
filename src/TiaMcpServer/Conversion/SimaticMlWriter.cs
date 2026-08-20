using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace TiaMcpServer.Conversion;

/// <summary>
/// Settings for the SimaticML document that TIA Portal imports.
/// </summary>
public sealed class SimaticMlOptions
{
    public string BlockName { get; set; } = "LAD_Block";

    /// <summary>FC, FB or OB.</summary>
    public string BlockType { get; set; } = "FC";

    /// <summary>Block number; null lets TIA Portal assign one automatically.</summary>
    public int? BlockNumber { get; set; }

    /// <summary>Optimized or Standard.</summary>
    public string MemoryLayout { get; set; } = "Optimized";

    /// <summary>Start event of an organization block, e.g. ProgramCycle.</summary>
    public string SecondaryType { get; set; } = "ProgramCycle";

    /// <summary>Culture of the generated titles and comments.</summary>
    public string Culture { get; set; } = "en-US";

    public int TiaMajorVersion { get; set; } = 20;

    public string? HeaderVersion { get; set; } = "0.1";
    public string? HeaderAuthor { get; set; }
    public string? HeaderFamily { get; set; }
    public string? HeaderName { get; set; }

    /// <summary>Block title shown in TIA Portal.</summary>
    public string? Title { get; set; }

    /// <summary>Block comment shown in TIA Portal.</summary>
    public string? Comment { get; set; }
}

/// <summary>
/// Renders converted ladder networks as a SimaticML document that can be imported with
/// <c>ImportBlock</c>.
/// </summary>
/// <remarks>
/// <para>
/// The schema versions of the two nested namespaces move with TIA Portal. The mapping below covers
/// V15 and newer; anything older is written with the oldest known pair, and anything newer with the
/// newest, because TIA Portal accepts a document written for an earlier schema of the same major
/// generation.
/// </para>
/// <para>
/// Attributes inside <c>AttributeList</c> are written in alphabetical order to match what TIA Portal
/// itself exports.
/// </para>
/// </remarks>
public sealed class SimaticMlWriter
{
    /// <summary>Pin names of the ladder elements this writer emits, kept in one place per element.</summary>
    private const string PinIn = "in";
    private const string PinOut = "out";
    private const string PinOperand = "operand";
    private const string PinPre = "pre";
    private const string PinEn = "en";
    private const string PinEno = "eno";
    private const string PinIn1 = "in1";
    private const string PinIn2 = "in2";
    private const string PinOut1 = "out1";

    private readonly SimaticMlOptions _options;
    private readonly XNamespace _interfaceNamespace;
    private readonly XNamespace _flgNetNamespace;

    private int _nextId;

    public SimaticMlWriter(SimaticMlOptions? options = null)
    {
        _options = options ?? new SimaticMlOptions();
        _interfaceNamespace = InterfaceNamespaceFor(_options.TiaMajorVersion);
        _flgNetNamespace = FlgNetNamespaceFor(_options.TiaMajorVersion);
    }

    public static XNamespace InterfaceNamespaceFor(int tiaMajorVersion)
    {
        if (tiaMajorVersion <= 15)
        {
            return "http://www.siemens.com/automation/Openness/SW/Interface/v3";
        }

        if (tiaMajorVersion == 16)
        {
            return "http://www.siemens.com/automation/Openness/SW/Interface/v4";
        }

        return "http://www.siemens.com/automation/Openness/SW/Interface/v5";
    }

    public static XNamespace FlgNetNamespaceFor(int tiaMajorVersion)
    {
        if (tiaMajorVersion <= 15)
        {
            return "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v2";
        }

        if (tiaMajorVersion == 16)
        {
            return "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v3";
        }

        return "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4";
    }

    /// <summary>Renders the document as UTF-8 XML text, ready to be written to a .xml file.</summary>
    public string Write(LadConversionResult conversion)
    {
        _nextId = 0;

        var blockElementName = "SW.Blocks." + NormalizeBlockType(_options.BlockType);

        var block = new XElement(blockElementName,
            new XAttribute("ID", NextId()),
            BuildBlockAttributeList(conversion),
            BuildBlockObjectList(conversion));

        var document = new XElement("Document",
            new XElement("Engineering", new XAttribute("version", "V" + _options.TiaMajorVersion.ToString(CultureInfo.InvariantCulture))),
            block);

        // The declaration is written by hand: saving through a StringWriter would stamp the writer's
        // UTF-16 encoding into it, and the file this text ends up in is UTF-8.
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.Append(Environment.NewLine);
        builder.Append(document.ToString(SaveOptions.None));

        return builder.ToString();
    }

    private static string NormalizeBlockType(string blockType)
    {
        switch ((blockType ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "FB": return "FB";
            case "OB": return "OB";
            default: return "FC";
        }
    }

    private string NextId()
    {
        return (_nextId++).ToString(CultureInfo.InvariantCulture);
    }

    #region block attributes and interface

    private XElement BuildBlockAttributeList(LadConversionResult conversion)
    {
        var blockType = NormalizeBlockType(_options.BlockType);
        var attributes = new XElement("AttributeList");

        attributes.Add(new XElement("AutoNumber", _options.BlockNumber.HasValue ? "false" : "true"));
        attributes.Add(new XElement("HeaderAuthor", _options.HeaderAuthor ?? string.Empty));
        attributes.Add(new XElement("HeaderFamily", _options.HeaderFamily ?? string.Empty));
        attributes.Add(new XElement("HeaderName", _options.HeaderName ?? string.Empty));
        attributes.Add(new XElement("HeaderVersion", _options.HeaderVersion ?? "0.1"));
        attributes.Add(new XElement("Interface", BuildInterface(conversion.Block, blockType)));
        attributes.Add(new XElement("MemoryLayout", _options.MemoryLayout));
        attributes.Add(new XElement("Name", _options.BlockName));

        if (_options.BlockNumber.HasValue)
        {
            attributes.Add(new XElement("Number", _options.BlockNumber.Value.ToString(CultureInfo.InvariantCulture)));
        }

        attributes.Add(new XElement("ProgrammingLanguage", "LAD"));

        if (string.Equals(blockType, "OB", StringComparison.Ordinal))
        {
            attributes.Add(new XElement("SecondaryType", _options.SecondaryType));
        }

        return attributes;
    }

    /// <summary>Sections TIA Portal expects for each block type, in export order.</summary>
    private static string[] SectionOrderFor(string blockType)
    {
        switch (blockType)
        {
            case "FB":
                return new[] { "Input", "Output", "InOut", "Static", "Temp", "Constant" };
            case "OB":
                return new[] { "Input", "Temp", "Constant" };
            default:
                return new[] { "Input", "Output", "InOut", "Temp", "Constant", "Return" };
        }
    }

    private XElement BuildInterface(SclBlockDefinition definition, string blockType)
    {
        var sections = new XElement(_interfaceNamespace + "Sections");

        foreach (var sectionName in SectionOrderFor(blockType))
        {
            var section = new XElement(_interfaceNamespace + "Section", new XAttribute("Name", sectionName));

            foreach (var declared in definition.Sections)
            {
                if (!string.Equals(declared.Name, sectionName, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var member in declared.Members)
                {
                    section.Add(BuildMember(member));
                }
            }

            if (string.Equals(sectionName, "Return", StringComparison.Ordinal) && !section.HasElements)
            {
                section.Add(new XElement(_interfaceNamespace + "Member",
                    new XAttribute("Name", "Ret_Val"),
                    new XAttribute("Datatype", SclDataTypes.Normalize(definition.ReturnType ?? "Void"))));
            }

            sections.Add(section);
        }

        return sections;
    }

    private XElement BuildMember(SclInterfaceMember member)
    {
        var element = new XElement(_interfaceNamespace + "Member",
            new XAttribute("Name", member.Name),
            new XAttribute("Datatype", SclDataTypes.Normalize(member.DataType)));

        if (!string.IsNullOrWhiteSpace(member.StartValue))
        {
            element.Add(new XElement(_interfaceNamespace + "StartValue", member.StartValue));
        }

        if (!string.IsNullOrWhiteSpace(member.Comment))
        {
            element.Add(new XElement(_interfaceNamespace + "Comment",
                new XElement(_interfaceNamespace + "MultiLanguageText",
                    new XAttribute("Lang", _options.Culture),
                    member.Comment)));
        }

        foreach (var child in member.Members)
        {
            element.Add(BuildMember(child));
        }

        return element;
    }

    #endregion

    #region networks

    private XElement BuildBlockObjectList(LadConversionResult conversion)
    {
        var objects = new XElement("ObjectList");

        objects.Add(BuildMultilingualText("Comment", _options.Comment));

        foreach (var network in conversion.Networks)
        {
            objects.Add(BuildCompileUnit(network));
        }

        objects.Add(BuildMultilingualText("Title", _options.Title));

        return objects;
    }

    private XElement BuildMultilingualText(string compositionName, string? text)
    {
        // IDs are taken parent first, as in a TIA Portal export.
        var textId = NextId();
        var itemId = NextId();

        var item = new XElement("MultilingualTextItem",
            new XAttribute("ID", itemId),
            new XAttribute("CompositionName", "Items"),
            new XElement("AttributeList",
                new XElement("Culture", _options.Culture),
                new XElement("Text", text ?? string.Empty)));

        return new XElement("MultilingualText",
            new XAttribute("ID", textId),
            new XAttribute("CompositionName", compositionName),
            new XElement("ObjectList", item));
    }

    private XElement BuildCompileUnit(LadNetwork network)
    {
        var flgNet = BuildFlgNet(network);

        return new XElement("SW.Blocks.CompileUnit",
            new XAttribute("ID", NextId()),
            new XAttribute("CompositionName", "CompileUnits"),
            new XElement("AttributeList",
                new XElement("NetworkSource", flgNet),
                new XElement("ProgrammingLanguage", "LAD")),
            new XElement("ObjectList",
                BuildMultilingualText("Comment", network.Comment),
                BuildMultilingualText("Title", network.Title)));
    }

    private XElement BuildFlgNet(LadNetwork network)
    {
        var builder = new FlgNetBuilder(_flgNetNamespace);

        if (network.IsPlaceholder || network.Outputs.Count == 0)
        {
            return builder.ToXml();
        }

        var node = builder.PowerRailNode();

        if (network.Condition != null)
        {
            node = EmitLogic(builder, network.Condition, node);
        }

        foreach (var output in network.Outputs)
        {
            node = EmitOutput(builder, output, node);
        }

        return builder.ToXml();
    }

    /// <summary>Emits power-flow logic and returns the node carrying its result.</summary>
    private int EmitLogic(FlgNetBuilder builder, LadLogic logic, int inNode)
    {
        switch (logic)
        {
            case LadContact contact:
                {
                    var content = new List<XObject>();

                    if (contact.Kind == LadContactKind.Negated)
                    {
                        content.Add(new XElement(_flgNetNamespace + "Negated", new XAttribute("Name", PinOperand)));
                    }

                    var uid = builder.AddPart("Contact", content.ToArray());

                    builder.AttachPin(inNode, uid, PinIn, false);
                    builder.ConnectOperand(builder.AddAccess(contact.Operand), uid, PinOperand, true);

                    var outNode = builder.NewNode();
                    builder.AttachPin(outNode, uid, PinOut, true);
                    return outNode;
                }

            case LadCompare compare:
                {
                    var uid = builder.AddPart(ComparePartName(compare.Operator),
                        new XElement(_flgNetNamespace + "TemplateValue",
                            new XAttribute("Name", "SrcType"),
                            new XAttribute("Type", "Type"),
                            compare.DataType));

                    builder.AttachPin(inNode, uid, PinPre, false);
                    builder.ConnectOperand(builder.AddAccess(compare.Left), uid, PinIn1, true);
                    builder.ConnectOperand(builder.AddAccess(compare.Right), uid, PinIn2, true);

                    var outNode = builder.NewNode();
                    builder.AttachPin(outNode, uid, PinOut, true);
                    return outNode;
                }

            case LadSeries series:
                {
                    var node = inNode;

                    foreach (var element in series.Elements)
                    {
                        node = EmitLogic(builder, element, node);
                    }

                    return node;
                }

            case LadParallel parallel:
                {
                    var outNode = builder.NewNode();

                    foreach (var branch in parallel.Branches)
                    {
                        builder.Merge(outNode, EmitLogic(builder, branch, inNode));
                    }

                    return outNode;
                }
        }

        // A constant rung state is folded away before this point; treat anything else as a pass-through.
        return inNode;
    }

    /// <summary>Emits an output element and returns the node that feeds the next one.</summary>
    private int EmitOutput(FlgNetBuilder builder, LadOutput output, int inNode)
    {
        switch (output)
        {
            case LadCoil coil:
                {
                    var uid = builder.AddPart(CoilPartName(coil.Kind));

                    builder.AttachPin(inNode, uid, PinIn, false);
                    builder.ConnectOperand(builder.AddAccess(coil.Operand), uid, PinOperand, true);

                    // Coils terminate the rung, so following outputs stay on the same node.
                    return inNode;
                }

            case LadMove move:
                {
                    var uid = builder.AddPart("Move");

                    builder.AttachPin(inNode, uid, PinEn, false);
                    builder.ConnectOperand(builder.AddAccess(move.Source), uid, PinIn, true);
                    builder.ConnectOperand(builder.AddAccess(move.Destination), uid, PinOut1, false);

                    var outNode = builder.NewNode();
                    builder.AttachPin(outNode, uid, PinEno, true);
                    return outNode;
                }

            case LadMath math:
                {
                    var uid = builder.AddPart(MathPartName(math.Operator),
                        new XElement(_flgNetNamespace + "TemplateValue",
                            new XAttribute("Name", "SrcType"),
                            new XAttribute("Type", "Type"),
                            math.DataType));

                    builder.AttachPin(inNode, uid, PinEn, false);
                    builder.ConnectOperand(builder.AddAccess(math.Left), uid, PinIn1, true);
                    builder.ConnectOperand(builder.AddAccess(math.Right), uid, PinIn2, true);
                    builder.ConnectOperand(builder.AddAccess(math.Destination), uid, PinOut, false);

                    var outNode = builder.NewNode();
                    builder.AttachPin(outNode, uid, PinEno, true);
                    return outNode;
                }

            case LadCall call:
                return EmitCall(builder, call, inNode);
        }

        return inNode;
    }

    private int EmitCall(FlgNetBuilder builder, LadCall call, int inNode)
    {
        var uid = builder.AddCall(() =>
        {
            var callInfo = new XElement(_flgNetNamespace + "CallInfo",
                new XAttribute("Name", call.BlockName),
                new XAttribute("BlockType", call.BlockType));

            if (call.BlockNumber.HasValue)
            {
                callInfo.Add(new XAttribute("BlockNumber", call.BlockNumber.Value.ToString(CultureInfo.InvariantCulture)));
            }

            if (call.Instance != null)
            {
                callInfo.Add(builder.BuildInstance(call.Instance));
            }

            foreach (var parameter in call.Parameters)
            {
                var element = new XElement(_flgNetNamespace + "Parameter",
                    new XAttribute("Name", parameter.Name),
                    new XAttribute("Section", parameter.Section));

                if (!string.IsNullOrWhiteSpace(parameter.DataType))
                {
                    element.Add(new XAttribute("Type", SclDataTypes.Normalize(parameter.DataType)));
                }

                callInfo.Add(element);
            }

            return callInfo;
        });

        builder.AttachPin(inNode, uid, PinEn, false);

        foreach (var parameter in call.Parameters)
        {
            // Inputs are driven by their operand; outputs and return values drive theirs.
            var operandIsSource = string.Equals(parameter.Section, "Input", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parameter.Section, "InOut", StringComparison.OrdinalIgnoreCase);

            builder.ConnectOperand(builder.AddAccess(parameter.Operand), uid, parameter.Name, operandIsSource);
        }

        var outNode = builder.NewNode();
        builder.AttachPin(outNode, uid, PinEno, true);
        return outNode;
    }

    private static string ComparePartName(LadCompareOperator op)
    {
        switch (op)
        {
            case LadCompareOperator.Equal: return "Eq";
            case LadCompareOperator.NotEqual: return "Ne";
            case LadCompareOperator.Less: return "Lt";
            case LadCompareOperator.LessEqual: return "Le";
            case LadCompareOperator.Greater: return "Gt";
            default: return "Ge";
        }
    }

    private static string CoilPartName(LadCoilKind kind)
    {
        switch (kind)
        {
            case LadCoilKind.Set: return "SCoil";
            case LadCoilKind.Reset: return "RCoil";
            default: return "Coil";
        }
    }

    private static string MathPartName(LadMathOperator op)
    {
        switch (op)
        {
            case LadMathOperator.Subtract: return "Sub";
            case LadMathOperator.Multiply: return "Mul";
            case LadMathOperator.Divide: return "Div";
            case LadMathOperator.Modulo: return "Mod";
            default: return "Add";
        }
    }

    #endregion
}
