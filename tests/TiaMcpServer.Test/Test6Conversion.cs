using System.Linq;
using System.Xml.Linq;
using TiaMcpServer.Conversion;

namespace TiaMcpServer.Test;

/// <summary>
/// Tests for the SCL to LAD converter. Nothing here needs TIA Portal, a license or a project:
/// the parser, the ladder lowering and the SimaticML writer are pure code.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class Test6Conversion
{
    private const string MotorSource = @"
FUNCTION_BLOCK ""FB_Motor""
VAR_INPUT
    Start : Bool;
    Stop : Bool;
    Level : Int;
END_VAR
VAR_OUTPUT
    Running : Bool;
    Alarm : Bool;
END_VAR
BEGIN
    // Motor run logic
    #Running := (#Start OR #Running) AND NOT #Stop;

    IF #Level > 80 THEN
        #Alarm := TRUE;
    ELSE
        #Alarm := FALSE;
    END_IF;
END_FUNCTION_BLOCK
";

    private static LadConversionResult Convert(string source)
    {
        var parse = new SclParser(source).Parse();
        var definition = parse.Blocks.FirstOrDefault() ?? new SclBlockDefinition();
        return new SclToLadConverter().Convert(definition);
    }

    private static XDocument WriteAndParse(LadConversionResult result, string blockType)
    {
        return XDocument.Parse(new SimaticMlWriter(new SimaticMlOptions { BlockType = blockType }).Write(result));
    }

    #region lexer and parser

    [TestMethod]
    public void Test_601_Lexer_ClassifiesOperandsAndLiterals()
    {
        var tokens = new SclLexer("#local := \"Global\".Member AND 16#FF; // trailing").Tokenize();

        Assert.AreEqual(SclTokenKind.LocalSymbol, tokens[0].Kind);
        Assert.AreEqual("local", tokens[0].Text);
        Assert.AreEqual(SclTokenKind.Operator, tokens[1].Kind);
        Assert.AreEqual(":=", tokens[1].Text);
        Assert.AreEqual(SclTokenKind.QuotedSymbol, tokens[2].Kind);
        Assert.AreEqual("Global", tokens[2].Text);

        Assert.IsTrue(tokens.Any(t => t.Kind == SclTokenKind.Number && t.Text == "16#FF"),
            "The based literal 16#FF should be one number token.");
        Assert.IsTrue(tokens.Any(t => t.Kind == SclTokenKind.Comment && t.Text == "trailing"),
            "The line comment should be captured without its marker.");
    }

    [TestMethod]
    public void Test_602_Lexer_KeepsCaseRangeSeparate()
    {
        var tokens = new SclLexer("1..5").Tokenize();

        Assert.AreEqual("1", tokens[0].Text, "'1..5' must not be lexed as the real number 1.");
        Assert.AreEqual("..", tokens[1].Text);
        Assert.AreEqual("5", tokens[2].Text);
    }

    [TestMethod]
    public void Test_603_Parser_ReadsBlockHeaderAndInterface()
    {
        var parse = new SclParser(MotorSource).Parse();

        Assert.AreEqual(1, parse.Blocks.Count);

        var block = parse.Blocks[0];
        Assert.AreEqual(SclBlockKind.FunctionBlock, block.Kind);
        Assert.AreEqual("FB_Motor", block.Name);

        var input = block.Sections.Single(s => s.Name == "Input");
        CollectionAssert.AreEqual(
            new[] { "Start", "Stop", "Level" },
            input.Members.Select(m => m.Name).ToArray());
        Assert.AreEqual("Int", input.Members[2].DataType);

        Assert.AreEqual(2, block.Sections.Single(s => s.Name == "Output").Members.Count);
    }

    [TestMethod]
    public void Test_604_Parser_SplitsSharedDeclarations()
    {
        var parse = new SclParser(@"
FUNCTION ""FC_Test"" : Void
VAR_TEMP
    a, b : Bool;
END_VAR
BEGIN
END_FUNCTION
").Parse();

        Assert.AreEqual("Void", parse.Blocks[0].ReturnType,
            "The return type must stop at the end of the header line.");

        var temp = parse.Blocks[0].Sections.Single(s => s.Name == "Temp");

        CollectionAssert.AreEqual(new[] { "a", "b" }, temp.Members.Select(m => m.Name).ToArray());
        Assert.IsTrue(temp.Members.All(m => m.DataType == "Bool"));
    }

    #endregion

    #region logic lowering

    [TestMethod]
    public void Test_610_Converter_BuildsSeriesParallelAndNegatedContacts()
    {
        var result = Convert(MotorSource);
        var network = result.Networks[0];

        Assert.AreEqual("Motor run logic", network.Title, "The leading comment becomes the network title.");

        var series = network.Condition as LadSeries;
        Assert.IsNotNull(series, "'a AND b' must become a series connection.");
        Assert.AreEqual(2, series!.Elements.Count);

        var parallel = series.Elements[0] as LadParallel;
        Assert.IsNotNull(parallel, "'a OR b' must become parallel branches.");
        Assert.AreEqual(2, parallel!.Branches.Count);

        var stop = series.Elements[1] as LadContact;
        Assert.IsNotNull(stop);
        Assert.AreEqual(LadContactKind.Negated, stop!.Kind, "NOT must be pushed into the contact.");
        Assert.AreEqual("#Stop", stop.Operand.Text);

        var coil = network.Outputs.Single() as LadCoil;
        Assert.IsNotNull(coil);
        Assert.AreEqual(LadCoilKind.Coil, coil!.Kind);
        Assert.AreEqual("#Running", coil.Operand.Text);
    }

    [TestMethod]
    public void Test_611_Converter_AppliesDeMorganToNegatedGroups()
    {
        var result = Convert(@"
FUNCTION_BLOCK ""FB""
VAR_INPUT
    a : Bool;
    b : Bool;
END_VAR
VAR_OUTPUT
    q : Bool;
END_VAR
BEGIN
    #q := NOT (#a AND #b);
END_FUNCTION_BLOCK
");

        var parallel = result.Networks.Single().Condition as LadParallel;
        Assert.IsNotNull(parallel, "NOT (a AND b) must become (NOT a) OR (NOT b).");

        Assert.IsTrue(parallel!.Branches.OfType<LadContact>().All(c => c.Kind == LadContactKind.Negated),
            "Both contacts of the De Morgan expansion must be normally closed.");
    }

    [TestMethod]
    public void Test_612_Converter_UsesSetAndResetCoilsInsideConditions()
    {
        var result = Convert(MotorSource);

        var setNetwork = result.Networks[1];
        var setCoil = setNetwork.Outputs.Single() as LadCoil;
        Assert.IsNotNull(setCoil);
        Assert.AreEqual(LadCoilKind.Set, setCoil!.Kind, "'x := TRUE' inside an IF is a set coil.");

        var compare = setNetwork.Condition as LadCompare;
        Assert.IsNotNull(compare, "The IF condition must become a compare box.");
        Assert.AreEqual(LadCompareOperator.Greater, compare!.Operator);
        Assert.AreEqual("Int", compare.DataType, "The compare type comes from the declared type of #Level.");

        var elseNetwork = result.Networks[2];
        var resetCoil = elseNetwork.Outputs.Single() as LadCoil;
        Assert.IsNotNull(resetCoil);
        Assert.AreEqual(LadCoilKind.Reset, resetCoil!.Kind, "'x := FALSE' is a reset coil.");

        var elseCompare = elseNetwork.Condition as LadCompare;
        Assert.IsNotNull(elseCompare);
        Assert.AreEqual(LadCompareOperator.LessEqual, elseCompare!.Operator,
            "The ELSE arm must invert the IF condition rather than add a NOT element.");
    }

    [TestMethod]
    public void Test_613_Converter_CarriesElsifConditionsIntoLaterBranches()
    {
        var result = Convert(@"
FUNCTION_BLOCK ""FB""
VAR_INPUT
    a : Bool;
    b : Bool;
END_VAR
VAR_OUTPUT
    q : Bool;
END_VAR
BEGIN
    IF #a THEN
        #q := TRUE;
    ELSIF #b THEN
        #q := FALSE;
    END_IF;
END_FUNCTION_BLOCK
");

        var elsif = result.Networks[1].Condition as LadSeries;
        Assert.IsNotNull(elsif, "An ELSIF arm is 'NOT previous AND own condition'.");

        var contacts = elsif!.Elements.OfType<LadContact>().ToArray();
        Assert.AreEqual(2, contacts.Length);
        Assert.AreEqual(LadContactKind.Negated, contacts[0].Kind);
        Assert.AreEqual("#a", contacts[0].Operand.Text);
        Assert.AreEqual(LadContactKind.Normal, contacts[1].Kind);
        Assert.AreEqual("#b", contacts[1].Operand.Text);
    }

    [TestMethod]
    public void Test_614_Converter_TurnsCaseLabelsIntoCompares()
    {
        var result = Convert(@"
FUNCTION_BLOCK ""FB""
VAR_INPUT
    step : Int;
END_VAR
VAR_OUTPUT
    q : Bool;
END_VAR
BEGIN
    CASE #step OF
        1:
            #q := TRUE;
        2..4:
            #q := FALSE;
    END_CASE;
END_FUNCTION_BLOCK
");

        Assert.AreEqual(2, result.Networks.Count);

        var single = result.Networks[0].Condition as LadCompare;
        Assert.IsNotNull(single);
        Assert.AreEqual(LadCompareOperator.Equal, single!.Operator);
        Assert.AreEqual("1", single.Right.ConstantValue);

        var range = result.Networks[1].Condition as LadSeries;
        Assert.IsNotNull(range, "A range label becomes '>= from AND <= to'.");
        Assert.AreEqual(2, range!.Elements.Count);
    }

    [TestMethod]
    public void Test_615_Converter_EmitsMoveAndMathBoxes()
    {
        var result = Convert(@"
FUNCTION_BLOCK ""FB""
VAR_INPUT
    setpoint : Int;
    offset : Int;
END_VAR
VAR_OUTPUT
    actual : Int;
    total : Int;
END_VAR
BEGIN
    #actual := #setpoint;
    #total := #setpoint + #offset;
END_FUNCTION_BLOCK
");

        var move = result.Networks[0].Outputs.Single() as LadMove;
        Assert.IsNotNull(move, "An assignment between non-boolean operands is a MOVE box.");
        Assert.AreEqual("#setpoint", move!.Source.Text);
        Assert.AreEqual("#actual", move.Destination.Text);

        var math = result.Networks[1].Outputs.Single() as LadMath;
        Assert.IsNotNull(math);
        Assert.AreEqual(LadMathOperator.Add, math!.Operator);
        Assert.AreEqual("Int", math.DataType);
        Assert.AreEqual("#total", math.Destination.Text);
    }

    [TestMethod]
    public void Test_616_Converter_ReportsNestedArithmeticInsteadOfGuessing()
    {
        var result = Convert(@"
FUNCTION_BLOCK ""FB""
VAR_INPUT
    a : Int;
    b : Int;
    c : Int;
END_VAR
VAR_OUTPUT
    q : Int;
END_VAR
BEGIN
    #q := #a + #b * #c;
END_FUNCTION_BLOCK
");

        Assert.AreEqual(0, result.ConvertedStatements);
        Assert.AreEqual(1, result.UnsupportedStatements);
        Assert.IsTrue(result.HasErrors);

        var placeholder = result.Networks.Single();
        Assert.IsTrue(placeholder.IsPlaceholder);
        StringAssert.Contains(placeholder.Comment, "#q := #a + #b * #c;",
            "The original SCL must be preserved in the network comment.");
    }

    [TestMethod]
    public void Test_617_Converter_PreservesLoopsAsComments()
    {
        var result = Convert(@"
FUNCTION_BLOCK ""FB""
VAR_OUTPUT
    q : Bool;
END_VAR
VAR_TEMP
    i : Int;
END_VAR
BEGIN
    FOR #i := 0 TO 9 DO
        #q := TRUE;
    END_FOR;
    #q := FALSE;
END_FUNCTION_BLOCK
");

        Assert.AreEqual(1, result.UnsupportedStatements, "The FOR loop is one unconvertible statement.");
        Assert.AreEqual(1, result.ConvertedStatements, "The statement after the loop must still convert.");

        Assert.IsTrue(result.Networks[0].IsPlaceholder);
        StringAssert.Contains(result.Networks[0].Comment, "FOR #i := 0 TO 9 DO");

        Assert.IsTrue(result.Diagnostics.Any(d => d.Severity == ConversionSeverity.Error && d.Code == "SCL2LAD001"));
    }

    [TestMethod]
    public void Test_618_Converter_BuildsCallBoxesFromNamedArguments()
    {
        var result = Convert(@"
FUNCTION_BLOCK ""FB""
VAR_INPUT
    trigger : Bool;
END_VAR
VAR
    delay : TON_TIME;
END_VAR
BEGIN
    #delay(IN := #trigger, PT := T#5s);
END_FUNCTION_BLOCK
");

        var call = result.Networks.Single().Outputs.Single() as LadCall;
        Assert.IsNotNull(call);
        Assert.AreEqual("FB", call!.BlockType, "'#instance(...)' is always a multi-instance FB call.");
        Assert.AreEqual("#delay", call.Instance!.Text);
        Assert.AreEqual(2, call.Parameters.Count);
        Assert.AreEqual("IN", call.Parameters[0].Name);
        Assert.AreEqual("Input", call.Parameters[0].Section);
        Assert.AreEqual("T#5s", call.Parameters[1].Operand.ConstantValue);
    }

    [TestMethod]
    public void Test_619_Converter_DropsUnreachableBranches()
    {
        var result = Convert(@"
FUNCTION_BLOCK ""FB""
VAR_OUTPUT
    q : Bool;
END_VAR
BEGIN
    IF FALSE THEN
        #q := TRUE;
    END_IF;
END_FUNCTION_BLOCK
");

        Assert.AreEqual(0, result.Networks.Count, "A branch that can never run produces no network.");
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "SCL2LAD012"));
    }

    #endregion

    #region SimaticML output

    [TestMethod]
    public void Test_620_Writer_ProducesImportableDocumentShape()
    {
        var result = Convert(MotorSource);

        var xml = new SimaticMlWriter(new SimaticMlOptions
        {
            BlockName = "FB_Motor_LAD",
            BlockType = "FB",
            TiaMajorVersion = 20
        }).Write(result);

        StringAssert.StartsWith(xml, "<?xml version=\"1.0\" encoding=\"utf-8\"?>");

        var root = XDocument.Parse(xml).Root;
        Assert.IsNotNull(root);
        Assert.AreEqual("Document", root!.Name.LocalName);

        var block = root.Element("SW.Blocks.FB");
        Assert.IsNotNull(block, "A FUNCTION_BLOCK source must produce an SW.Blocks.FB element.");

        var attributes = block!.Element("AttributeList");
        Assert.IsNotNull(attributes);
        Assert.AreEqual("LAD", attributes!.Element("ProgrammingLanguage")!.Value);
        Assert.AreEqual("FB_Motor_LAD", attributes.Element("Name")!.Value);

        Assert.AreEqual(result.Networks.Count,
            block.Element("ObjectList")!.Elements("SW.Blocks.CompileUnit").Count(),
            "Every network becomes one compile unit.");

        var ids = block.DescendantsAndSelf()
            .Select(e => (string?)e.Attribute("ID"))
            .Where(id => id != null)
            .ToList();

        Assert.AreEqual(ids.Count, ids.Distinct().Count(), "Object IDs must be unique inside the document.");
    }

    [TestMethod]
    public void Test_621_Writer_UsesVersionedSchemaNamespaces()
    {
        var result = Convert(MotorSource);

        var current = new SimaticMlWriter(new SimaticMlOptions { BlockType = "FB", TiaMajorVersion = 20 }).Write(result);
        StringAssert.Contains(current, "http://www.siemens.com/automation/Openness/SW/Interface/v5");
        StringAssert.Contains(current, "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4");

        var older = new SimaticMlWriter(new SimaticMlOptions { BlockType = "FB", TiaMajorVersion = 16 }).Write(result);
        StringAssert.Contains(older, "http://www.siemens.com/automation/Openness/SW/Interface/v4");
        StringAssert.Contains(older, "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v3");
    }

    [TestMethod]
    public void Test_622_Writer_WiresContactsCoilsAndOperands()
    {
        var document = WriteAndParse(Convert(@"
FUNCTION_BLOCK ""FB""
VAR_INPUT
    a : Bool;
END_VAR
VAR_OUTPUT
    q : Bool;
END_VAR
BEGIN
    #q := #a;
END_FUNCTION_BLOCK
"), "FB");

        var parts = document.Descendants().Where(e => e.Name.LocalName == "Part").ToList();
        CollectionAssert.AreEquivalent(
            new[] { "Contact", "Coil" },
            parts.Select(p => (string)p.Attribute("Name")!).ToArray());

        var accesses = document.Descendants().Where(e => e.Name.LocalName == "Access").ToList();
        Assert.AreEqual(2, accesses.Count, "Both operands need an access element.");
        Assert.IsTrue(accesses.All(a => (string)a.Attribute("Scope")! == "LocalVariable"));

        var wires = document.Descendants().Where(e => e.Name.LocalName == "Wire").ToList();
        Assert.AreEqual(4, wires.Count,
            "Power rail to contact, contact operand, contact to coil, and coil operand.");

        Assert.AreEqual(1, wires.Count(w => w.Elements().Any(e => e.Name.LocalName == "Powerrail")),
            "Exactly one wire starts at the power rail.");

        var uids = document.Descendants()
            .Where(e => e.Name.LocalName == "Part" || e.Name.LocalName == "Access"
                || e.Name.LocalName == "Call" || e.Name.LocalName == "Wire")
            .Select(e => (string)e.Attribute("UId")!)
            .ToList();

        Assert.AreEqual(uids.Count, uids.Distinct().Count(), "UIds must be unique inside a network.");
    }

    [TestMethod]
    public void Test_623_Writer_MergesParallelBranchesIntoOneWire()
    {
        var document = WriteAndParse(Convert(@"
FUNCTION_BLOCK ""FB""
VAR_INPUT
    a : Bool;
    b : Bool;
END_VAR
VAR_OUTPUT
    q : Bool;
END_VAR
BEGIN
    #q := #a OR #b;
END_FUNCTION_BLOCK
"), "FB");

        var railWire = document.Descendants()
            .Single(e => e.Name.LocalName == "Wire" && e.Elements().Any(c => c.Name.LocalName == "Powerrail"));

        Assert.AreEqual(2, railWire.Elements().Count(e => e.Name.LocalName == "NameCon"),
            "Both branches of an OR hang on the power rail.");

        var joinWire = document.Descendants()
            .Single(e => e.Name.LocalName == "Wire"
                && e.Elements().Count(c => c.Name.LocalName == "NameCon"
                    && (string)c.Attribute("Name")! == "out") == 2);

        Assert.AreEqual(3, joinWire.Elements().Count(),
            "The two branch outputs and the coil input share one electrical node.");
    }

    [TestMethod]
    public void Test_624_Writer_EmitsRequiredInterfaceSections()
    {
        var document = WriteAndParse(Convert(MotorSource), "FB");

        var sections = document.Descendants()
            .Where(e => e.Name.LocalName == "Section")
            .Select(e => (string)e.Attribute("Name")!)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "Input", "Output", "InOut", "Static", "Temp", "Constant" },
            sections);

        var level = document.Descendants()
            .Single(e => e.Name.LocalName == "Member" && (string)e.Attribute("Name")! == "Level");

        Assert.AreEqual("Int", (string)level.Attribute("Datatype")!);
    }

    [TestMethod]
    public void Test_625_Writer_AddsReturnMemberForFunctions()
    {
        var document = WriteAndParse(Convert(@"
FUNCTION ""FC_Test"" : Bool
VAR_INPUT
    a : Bool;
END_VAR
BEGIN
    #FC_Test := #a;
END_FUNCTION
"), "FC");

        var returnSection = document.Descendants()
            .Single(e => e.Name.LocalName == "Section" && (string)e.Attribute("Name")! == "Return");

        var member = returnSection.Elements().Single();
        Assert.AreEqual("Ret_Val", (string)member.Attribute("Name")!);
        Assert.AreEqual("Bool", (string)member.Attribute("Datatype")!);
    }

    #endregion

    #region preview

    [TestMethod]
    public void Test_630_Renderer_DrawsSeriesParallelAndCoils()
    {
        var preview = LadTextRenderer.Render(Convert(MotorSource));

        StringAssert.Contains(preview, "Network 1: Motor run logic");
        StringAssert.Contains(preview, "#Start");
        StringAssert.Contains(preview, "--|/|", "A negated contact is drawn as a normally closed contact.");
        StringAssert.Contains(preview, "--( S )", "The set coil must be visible in the preview.");
        StringAssert.Contains(preview, "--( R )");

        Assert.IsFalse(preview.Contains("\t"), "The preview must not contain tabs, which break alignment.");
    }

    [TestMethod]
    public void Test_631_Renderer_MarksPlaceholderNetworks()
    {
        var preview = LadTextRenderer.Render(Convert(@"
FUNCTION_BLOCK ""FB""
VAR_TEMP
    i : Int;
END_VAR
BEGIN
    WHILE #i < 10 DO
        #i := #i + 1;
    END_WHILE;
END_FUNCTION_BLOCK
"));

        StringAssert.Contains(preview, "(empty network - nothing was converted)");
        StringAssert.Contains(preview, "WHILE #i < 10 DO");
    }

    #endregion
}
