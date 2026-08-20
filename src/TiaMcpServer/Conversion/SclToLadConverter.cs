using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcpServer.Conversion;

/// <summary>
/// Tuning knobs for <see cref="SclToLadConverter"/>.
/// </summary>
public sealed class SclToLadOptions
{
    /// <summary>Type used for compare and math boxes when neither operand type can be resolved.</summary>
    public string DefaultNumericType { get; set; } = "Int";

    /// <summary>Emit a set/reset coil for <c>x := TRUE;</c> / <c>x := FALSE;</c> inside a condition.</summary>
    public bool UseSetResetCoils { get; set; } = true;

    /// <summary>Keep statements that have no ladder form as empty networks carrying the original SCL.</summary>
    public bool PreserveUnsupportedAsComments { get; set; } = true;

    /// <summary>Append the originating SCL statement to each network comment.</summary>
    public bool IncludeSourceInComments { get; set; } = true;

    /// <summary>Resolves interfaces of called blocks; null makes call boxes best-effort.</summary>
    public IBlockInterfaceProvider? InterfaceProvider { get; set; }
}

/// <summary>
/// Ladder networks produced from one SCL block, together with everything the caller must review.
/// </summary>
public sealed class LadConversionResult
{
    public SclBlockDefinition Block { get; set; } = new SclBlockDefinition();
    public List<LadNetwork> Networks { get; } = new List<LadNetwork>();
    public List<ConversionDiagnostic> Diagnostics { get; } = new List<ConversionDiagnostic>();

    /// <summary>Statements that produced ladder logic.</summary>
    public int ConvertedStatements { get; set; }

    /// <summary>Statements that had to be preserved as comments.</summary>
    public int UnsupportedStatements { get; set; }

    public bool HasErrors
    {
        get
        {
            foreach (var diagnostic in Diagnostics)
            {
                if (diagnostic.Severity == ConversionSeverity.Error)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
/// Lowers a parsed SCL block into ladder networks.
/// </summary>
/// <remarks>
/// <para>
/// One SCL statement becomes one network. Conditions from enclosing IF/ELSIF/ELSE/CASE branches are
/// carried down and wired in series ahead of the statement's own logic, so the ladder evaluates
/// exactly the same condition the SCL did.
/// </para>
/// <para>
/// Negation is pushed down to the contacts and compare boxes with De Morgan's laws, which is what an
/// engineer would draw by hand: NOT becomes a normally closed contact or an inverted comparison, and
/// NOT over a group swaps series for parallel.
/// </para>
/// <para>
/// Anything ladder cannot express - loops, jumps, nested arithmetic that would need a temporary -
/// is reported as an error diagnostic and, unless disabled, preserved verbatim as a network comment
/// rather than dropped.
/// </para>
/// </remarks>
public sealed class SclToLadConverter
{
    private static readonly Regex AddressPattern = new Regex(
        @"^%(?<area>PI|PQ|I|E|Q|A|M)(?<size>X|B|W|D|L)?(?<byte>\d+)(?:\.(?<bit>\d+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ArrayElementPattern = new Regex(
        @"^\s*array\s*\[.*\]\s*of\s+(?<element>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private readonly SclToLadOptions _options;
    private LadConversionResult _result = new LadConversionResult();
    private string? _regionTitle;

    public SclToLadConverter(SclToLadOptions? options = null)
    {
        _options = options ?? new SclToLadOptions();
    }

    public LadConversionResult Convert(SclBlockDefinition block)
    {
        _result = new LadConversionResult { Block = block };
        _regionTitle = null;

        EmitStatements(block.Body, new List<ConditionTerm>());

        return _result;
    }

    #region condition context

    /// <summary>One enclosing branch condition, optionally negated (ELSIF/ELSE arms).</summary>
    private sealed class ConditionTerm
    {
        public ConditionTerm(SclExpression expression, bool negate)
        {
            Expression = expression;
            Negate = negate;
        }

        public SclExpression Expression { get; }
        public bool Negate { get; }
    }

    #endregion

    #region statement emission

    private void EmitStatements(List<SclStatement> statements, List<ConditionTerm> context)
    {
        foreach (var statement in statements)
        {
            EmitStatement(statement, context);
        }
    }

    private void EmitStatement(SclStatement statement, List<ConditionTerm> context)
    {
        switch (statement)
        {
            case SclAssignmentStatement assignment:
                EmitAssignment(assignment, context);
                return;

            case SclCallStatement call:
                EmitCall(call, context);
                return;

            case SclIfStatement ifStatement:
                EmitIf(ifStatement, context);
                return;

            case SclCaseStatement caseStatement:
                EmitCase(caseStatement, context);
                return;

            case SclRegionStatement region:
                {
                    var previous = _regionTitle;
                    _regionTitle = string.IsNullOrWhiteSpace(region.Title) ? previous : region.Title;
                    EmitStatements(region.Body, context);
                    _regionTitle = previous;
                    return;
                }

            case SclUnsupportedStatement unsupported:
                EmitPlaceholder(unsupported, unsupported.Reason);
                return;
        }
    }

    private void EmitIf(SclIfStatement statement, List<ConditionTerm> context)
    {
        var negatedSoFar = new List<ConditionTerm>();

        foreach (var branch in statement.Branches)
        {
            var branchContext = new List<ConditionTerm>(context);
            branchContext.AddRange(negatedSoFar);
            branchContext.Add(new ConditionTerm(branch.Condition, false));

            EmitStatements(branch.Body, branchContext);

            negatedSoFar.Add(new ConditionTerm(branch.Condition, true));
        }

        if (statement.ElseBody != null)
        {
            var elseContext = new List<ConditionTerm>(context);
            elseContext.AddRange(negatedSoFar);

            EmitStatements(statement.ElseBody, elseContext);
        }
    }

    private void EmitCase(SclCaseStatement statement, List<ConditionTerm> context)
    {
        var branchConditions = new List<SclExpression>();

        foreach (var branch in statement.Branches)
        {
            var condition = BuildCaseCondition(statement.Selector, branch);

            if (condition == null)
            {
                foreach (var inner in branch.Body)
                {
                    EmitPlaceholder(inner, "A CASE branch without a usable label was skipped.");
                }

                continue;
            }

            branchConditions.Add(condition);

            var branchContext = new List<ConditionTerm>(context) { new ConditionTerm(condition, false) };
            EmitStatements(branch.Body, branchContext);
        }

        if (statement.ElseBody == null)
        {
            return;
        }

        var elseContext = new List<ConditionTerm>(context);
        foreach (var condition in branchConditions)
        {
            elseContext.Add(new ConditionTerm(condition, true));
        }

        EmitStatements(statement.ElseBody, elseContext);
    }

    /// <summary>Turns <c>CASE x OF 1, 3..5:</c> into <c>x = 1 OR (x &gt;= 3 AND x &lt;= 5)</c>.</summary>
    private static SclExpression? BuildCaseCondition(SclExpression selector, SclCaseBranch branch)
    {
        SclExpression? condition = null;

        foreach (var label in branch.Labels)
        {
            SclExpression term;

            if (label.To != null)
            {
                term = new SclBinaryExpression
                {
                    Operator = SclBinaryOperator.And,
                    Left = new SclBinaryExpression { Operator = SclBinaryOperator.GreaterEqual, Left = selector, Right = label.From, Line = selector.Line },
                    Right = new SclBinaryExpression { Operator = SclBinaryOperator.LessEqual, Left = selector, Right = label.To, Line = selector.Line },
                    Line = selector.Line
                };
            }
            else
            {
                term = new SclBinaryExpression
                {
                    Operator = SclBinaryOperator.Equal,
                    Left = selector,
                    Right = label.From,
                    Line = selector.Line
                };
            }

            condition = condition == null
                ? term
                : new SclBinaryExpression { Operator = SclBinaryOperator.Or, Left = condition, Right = term, Line = selector.Line };
        }

        return condition;
    }

    private void EmitAssignment(SclAssignmentStatement statement, List<ConditionTerm> context)
    {
        if (!(statement.Target is SclSymbolExpression targetSymbol))
        {
            EmitPlaceholder(statement, "The assignment target is not a single operand, which ladder cannot express.");
            return;
        }

        var target = BuildOperand(targetSymbol);
        if (target == null)
        {
            EmitPlaceholder(statement, "The assignment target could not be turned into a ladder operand.");
            return;
        }

        if (IsBooleanAssignment(statement, target))
        {
            EmitBooleanAssignment(statement, target, context);
        }
        else
        {
            EmitValueAssignment(statement, target, context);
        }
    }

    private void EmitBooleanAssignment(SclAssignmentStatement statement, LadOperand target, List<ConditionTerm> context)
    {
        var contextLogic = BuildContextLogic(context, statement);
        if (contextLogic.Failed)
        {
            return;
        }

        var constant = AsBooleanConstant(statement.Value);

        if (constant.HasValue)
        {
            // 'x := TRUE;' with no enclosing condition is simply a coil on the power rail. Everything
            // else becomes a set or reset coil: no ladder element is permanently off, so a reset coil
            // is the only faithful form of a FALSE assignment, conditional or not.
            var kind = LadCoilKind.Coil;

            if (!constant.Value)
            {
                kind = LadCoilKind.Reset;
            }
            else if (context.Count > 0 && _options.UseSetResetCoils)
            {
                kind = LadCoilKind.Set;
            }

            AddNetwork(statement, contextLogic.Logic, new LadCoil { Operand = target, Kind = kind });
            return;
        }

        var valueLogic = BuildLogic(statement.Value, false, statement);
        if (valueLogic == null)
        {
            EmitPlaceholder(statement, "The assigned expression has no ladder equivalent.");
            return;
        }

        AddNetwork(statement, Combine(contextLogic.Logic, valueLogic), new LadCoil { Operand = target, Kind = LadCoilKind.Coil });
    }

    private void EmitValueAssignment(SclAssignmentStatement statement, LadOperand target, List<ConditionTerm> context)
    {
        var contextLogic = BuildContextLogic(context, statement);
        if (contextLogic.Failed)
        {
            return;
        }

        if (statement.Value is SclFunctionCallExpression functionCall)
        {
            var call = BuildCall(functionCall.Name, functionCall.IsLocal, functionCall.Arguments, statement, target);
            if (call == null)
            {
                EmitPlaceholder(statement, "The called block could not be turned into a ladder call box.");
                return;
            }

            AddNetwork(statement, contextLogic.Logic, call);
            return;
        }

        if (statement.Value is SclBinaryExpression binary && TryMapMathOperator(binary.Operator, out var mathOperator))
        {
            var left = BuildOperand(binary.Left);
            var right = BuildOperand(binary.Right);

            if (left == null || right == null)
            {
                EmitPlaceholder(statement,
                    "Nested arithmetic needs a temporary variable, which the converter does not invent; keep this statement in SCL or split it up.");
                return;
            }

            AddNetwork(statement, contextLogic.Logic, new LadMath
            {
                Operator = mathOperator,
                Left = left,
                Right = right,
                Destination = target,
                DataType = SclDataTypes.Widen(SclDataTypes.Widen(left.DataType, right.DataType, string.Empty), target.DataType, _options.DefaultNumericType)
            });

            return;
        }

        var source = BuildOperand(statement.Value);
        if (source == null)
        {
            EmitPlaceholder(statement, "The assigned expression is not a single operand and has no MOVE equivalent.");
            return;
        }

        AddNetwork(statement, contextLogic.Logic, new LadMove { Source = source, Destination = target });
    }

    private void EmitCall(SclCallStatement statement, List<ConditionTerm> context)
    {
        var contextLogic = BuildContextLogic(context, statement);
        if (contextLogic.Failed)
        {
            return;
        }

        var call = BuildCall(statement.Name, statement.IsLocal, statement.Arguments, statement, null);
        if (call == null)
        {
            EmitPlaceholder(statement, "The called block could not be turned into a ladder call box.");
            return;
        }

        AddNetwork(statement, contextLogic.Logic, call);
    }

    private LadCall? BuildCall(string name, bool isLocal, List<SclArgument> arguments, SclStatement statement, LadOperand? returnTarget)
    {
        var call = new LadCall { BlockName = name };
        var info = _options.InterfaceProvider?.Find(name);

        if (info != null)
        {
            call.BlockNumber = info.Number;

            if (info.IsInstanceDb)
            {
                call.BlockType = "FB";
                call.BlockName = info.InstanceOfBlock ?? name;
                call.Instance = MakeSymbolOperand(name, isLocal);
            }
            else
            {
                call.BlockType = info.BlockType;
                call.BlockName = info.Name;

                if (string.Equals(info.BlockType, "FB", StringComparison.OrdinalIgnoreCase))
                {
                    call.Instance = MakeSymbolOperand(name, isLocal);
                }
            }
        }
        else if (isLocal)
        {
            // '#instance(...)' is always a multi-instance call of a function block.
            call.BlockType = "FB";
            call.Instance = MakeSymbolOperand(name, true);

            Report(ConversionSeverity.Warning, "SCL2LAD010",
                $"Call of multi-instance '#{name}' was drawn as an FB call, but the function block type is unknown; verify the call box.",
                statement.Line, FirstLine(statement.RawText));
        }
        else
        {
            Report(ConversionSeverity.Warning, "SCL2LAD010",
                $"Call of '{name}' was assumed to be an FC. Convert with an open project so the block interface can be resolved, then verify the call box.",
                statement.Line, FirstLine(statement.RawText));
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];

            if (string.IsNullOrEmpty(argument.Name))
            {
                Report(ConversionSeverity.Error, "SCL2LAD011",
                    "Positional call arguments cannot be mapped to a ladder call box; name every parameter.",
                    statement.Line, FirstLine(statement.RawText));

                return null;
            }

            var operand = BuildOperand(argument.Value);
            if (operand == null)
            {
                return null;
            }

            var parameter = new LadCallParameter
            {
                Name = argument.Name!,
                Section = argument.Direction == SclArgumentDirection.Output ? "Output" : "Input",
                Operand = operand
            };

            if (info != null && info.Parameters.TryGetValue(parameter.Name, out var declared))
            {
                parameter.Section = declared.Section;
                parameter.DataType = declared.DataType;
            }

            call.Parameters.Add(parameter);
        }

        if (returnTarget != null)
        {
            var returnName = info != null ? FindReturnParameterName(info) : "Ret_Val";

            call.Parameters.Add(new LadCallParameter
            {
                Name = returnName,
                Section = "Return",
                DataType = returnTarget.DataType,
                Operand = returnTarget
            });
        }

        return call;
    }

    private static string FindReturnParameterName(BlockInterfaceInfo info)
    {
        foreach (var parameter in info.Parameters.Values)
        {
            if (string.Equals(parameter.Section, "Return", StringComparison.OrdinalIgnoreCase))
            {
                return parameter.Name;
            }
        }

        return "Ret_Val";
    }

    private LadOperand MakeSymbolOperand(string name, bool isLocal)
    {
        var operand = new LadOperand
        {
            Kind = isLocal ? LadOperandKind.Local : LadOperandKind.Global,
            Text = isLocal ? "#" + name : "\"" + name + "\""
        };

        operand.Components.Add(new LadOperandComponent(name));
        return operand;
    }

    private void EmitPlaceholder(SclStatement statement, string reason)
    {
        _result.UnsupportedStatements++;

        Report(ConversionSeverity.Error, "SCL2LAD001", reason, statement.Line, FirstLine(statement.RawText));

        if (!_options.PreserveUnsupportedAsComments)
        {
            return;
        }

        var network = new LadNetwork
        {
            Title = BuildTitle(statement) ?? "Not converted",
            SourceLine = statement.Line,
            IsPlaceholder = true
        };

        var comment = new StringBuilder();
        comment.Append(reason);

        var raw = statement.RawText.Trim();
        if (raw.Length > 0)
        {
            comment.AppendLine();
            comment.AppendLine();
            comment.Append("Original SCL:");
            comment.AppendLine();
            comment.Append(raw);
        }

        network.Comment = comment.ToString();
        _result.Networks.Add(network);
    }

    private void AddNetwork(SclStatement statement, LadLogic? condition, LadOutput output)
    {
        var simplified = Simplify(condition);

        if (simplified is LadConstantLogic constant)
        {
            if (!constant.Value)
            {
                Report(ConversionSeverity.Warning, "SCL2LAD012",
                    "The condition of this statement is always false, so no network was generated.",
                    statement.Line, FirstLine(statement.RawText));

                return;
            }

            simplified = null;
        }

        var network = new LadNetwork
        {
            Title = BuildTitle(statement),
            Comment = BuildComment(statement),
            Condition = simplified,
            SourceLine = statement.Line
        };

        network.Outputs.Add(output);

        _result.Networks.Add(network);
        _result.ConvertedStatements++;
    }

    private string? BuildTitle(SclStatement statement)
    {
        if (statement.LeadingComments.Count > 0)
        {
            var first = statement.LeadingComments[0].Trim();
            if (first.Length > 0)
            {
                return first;
            }
        }

        return _regionTitle;
    }

    private string? BuildComment(SclStatement statement)
    {
        var parts = new List<string>();

        for (var i = 1; i < statement.LeadingComments.Count; i++)
        {
            parts.Add(statement.LeadingComments[i].Trim());
        }

        if (!string.IsNullOrWhiteSpace(statement.TrailingComment))
        {
            parts.Add(statement.TrailingComment!.Trim());
        }

        if (_options.IncludeSourceInComments)
        {
            var raw = statement.RawText.Trim();
            if (raw.Length > 0)
            {
                parts.Add("SCL: " + raw);
            }
        }

        return parts.Count > 0 ? string.Join(Environment.NewLine, parts.ToArray()) : null;
    }

    #endregion

    #region logic lowering

    private struct ContextLogic
    {
        public LadLogic? Logic;
        public bool Failed;
    }

    private ContextLogic BuildContextLogic(List<ConditionTerm> context, SclStatement statement)
    {
        var result = new ContextLogic();

        if (context.Count == 0)
        {
            return result;
        }

        var series = new LadSeries();

        foreach (var term in context)
        {
            var logic = BuildLogic(term.Expression, term.Negate, statement);

            if (logic == null)
            {
                EmitPlaceholder(statement, "An enclosing branch condition has no ladder equivalent.");
                result.Failed = true;
                return result;
            }

            AppendSeries(series, logic);
        }

        result.Logic = series.Elements.Count == 1 ? series.Elements[0] : series;
        return result;
    }

    private static LadLogic? Combine(LadLogic? left, LadLogic? right)
    {
        if (left == null)
        {
            return right;
        }

        if (right == null)
        {
            return left;
        }

        var series = new LadSeries();
        AppendSeries(series, left);
        AppendSeries(series, right);
        return series;
    }

    private static void AppendSeries(LadSeries series, LadLogic logic)
    {
        if (logic is LadSeries nested)
        {
            series.Elements.AddRange(nested.Elements);
            return;
        }

        series.Elements.Add(logic);
    }

    private static void AppendParallel(LadParallel parallel, LadLogic logic)
    {
        if (logic is LadParallel nested)
        {
            parallel.Branches.AddRange(nested.Branches);
            return;
        }

        parallel.Branches.Add(logic);
    }

    /// <summary>
    /// Lowers a boolean expression to ladder logic, pushing <paramref name="negate"/> down to the
    /// contacts with De Morgan's laws. Returns null when the expression has no ladder form.
    /// </summary>
    private LadLogic? BuildLogic(SclExpression expression, bool negate, SclStatement statement)
    {
        switch (expression)
        {
            case SclLiteralExpression literal when literal.Kind == SclLiteralKind.Boolean:
                {
                    var value = string.Equals(SclDataTypes.ConstantValueText(literal), "true", StringComparison.Ordinal);
                    return new LadConstantLogic { Value = negate ? !value : value };
                }

            case SclSymbolExpression symbol:
                {
                    var operand = BuildOperand(symbol);
                    if (operand == null)
                    {
                        return null;
                    }

                    return new LadContact
                    {
                        Operand = operand,
                        Kind = negate ? LadContactKind.Negated : LadContactKind.Normal
                    };
                }

            case SclUnaryExpression unary when unary.Operator == SclUnaryOperator.Not:
                return BuildLogic(unary.Operand, !negate, statement);

            case SclBinaryExpression binary:
                return BuildBinaryLogic(binary, negate, statement);
        }

        Report(ConversionSeverity.Error, "SCL2LAD013",
            $"'{SclExpressionPrinter.Print(expression)}' is not a boolean expression that ladder can evaluate.",
            expression.Line, FirstLine(statement.RawText));

        return null;
    }

    private LadLogic? BuildBinaryLogic(SclBinaryExpression binary, bool negate, SclStatement statement)
    {
        switch (binary.Operator)
        {
            case SclBinaryOperator.And:
            case SclBinaryOperator.Or:
                {
                    var left = BuildLogic(binary.Left, negate, statement);
                    var right = BuildLogic(binary.Right, negate, statement);

                    if (left == null || right == null)
                    {
                        return null;
                    }

                    // De Morgan: NOT (a AND b) is (NOT a) OR (NOT b), and vice versa.
                    var isSeries = (binary.Operator == SclBinaryOperator.And) != negate;

                    if (isSeries)
                    {
                        var series = new LadSeries();
                        AppendSeries(series, left);
                        AppendSeries(series, right);
                        return series;
                    }

                    var parallel = new LadParallel();
                    AppendParallel(parallel, left);
                    AppendParallel(parallel, right);
                    return parallel;
                }

            case SclBinaryOperator.Xor:
                return BuildXorLogic(binary, negate, statement);

            case SclBinaryOperator.Equal:
            case SclBinaryOperator.NotEqual:
            case SclBinaryOperator.Less:
            case SclBinaryOperator.LessEqual:
            case SclBinaryOperator.Greater:
            case SclBinaryOperator.GreaterEqual:
                return BuildCompareLogic(binary, negate, statement);
        }

        Report(ConversionSeverity.Error, "SCL2LAD013",
            $"'{SclExpressionPrinter.Print(binary)}' is an arithmetic expression used as a condition, which ladder cannot evaluate directly.",
            binary.Line, FirstLine(statement.RawText));

        return null;
    }

    /// <summary>
    /// Expands XOR into the two-branch ladder an engineer would draw. Both operands appear twice,
    /// which is unavoidable in ladder, so the duplication is reported.
    /// </summary>
    private LadLogic? BuildXorLogic(SclBinaryExpression binary, bool negate, SclStatement statement)
    {
        var leftTrue = BuildLogic(binary.Left, false, statement);
        var leftFalse = BuildLogic(binary.Left, true, statement);
        var rightTrue = BuildLogic(binary.Right, false, statement);
        var rightFalse = BuildLogic(binary.Right, true, statement);

        if (leftTrue == null || leftFalse == null || rightTrue == null || rightFalse == null)
        {
            return null;
        }

        Report(ConversionSeverity.Info, "SCL2LAD014",
            "XOR was expanded into two parallel branches, so both operands appear twice in the network.",
            binary.Line, FirstLine(statement.RawText));

        var first = new LadSeries();
        var second = new LadSeries();

        if (negate)
        {
            // NOT (a XOR b) is (a AND b) OR (NOT a AND NOT b).
            AppendSeries(first, leftTrue);
            AppendSeries(first, rightTrue);
            AppendSeries(second, leftFalse);
            AppendSeries(second, rightFalse);
        }
        else
        {
            AppendSeries(first, leftTrue);
            AppendSeries(first, rightFalse);
            AppendSeries(second, leftFalse);
            AppendSeries(second, rightTrue);
        }

        var parallel = new LadParallel();
        parallel.Branches.Add(first);
        parallel.Branches.Add(second);
        return parallel;
    }

    private LadLogic? BuildCompareLogic(SclBinaryExpression binary, bool negate, SclStatement statement)
    {
        var left = BuildOperand(binary.Left);
        var right = BuildOperand(binary.Right);

        if (left == null || right == null)
        {
            Report(ConversionSeverity.Error, "SCL2LAD015",
                $"Comparison '{SclExpressionPrinter.Print(binary)}' needs a calculation on one side; ladder compare boxes take operands only.",
                binary.Line, FirstLine(statement.RawText));

            return null;
        }

        var op = MapCompareOperator(binary.Operator);
        if (negate)
        {
            op = InvertCompareOperator(op);
        }

        var dataType = SclDataTypes.Widen(left.DataType, right.DataType, string.Empty);

        if (dataType.Length == 0)
        {
            dataType = _options.DefaultNumericType;

            Report(ConversionSeverity.Warning, "SCL2LAD016",
                $"Neither side of '{SclExpressionPrinter.Print(binary)}' has a resolvable type; the compare box was set to '{dataType}'.",
                binary.Line, FirstLine(statement.RawText));
        }

        return new LadCompare
        {
            Operator = op,
            Left = left,
            Right = right,
            DataType = dataType
        };
    }

    private static LadCompareOperator MapCompareOperator(SclBinaryOperator op)
    {
        switch (op)
        {
            case SclBinaryOperator.Equal: return LadCompareOperator.Equal;
            case SclBinaryOperator.NotEqual: return LadCompareOperator.NotEqual;
            case SclBinaryOperator.Less: return LadCompareOperator.Less;
            case SclBinaryOperator.LessEqual: return LadCompareOperator.LessEqual;
            case SclBinaryOperator.Greater: return LadCompareOperator.Greater;
            default: return LadCompareOperator.GreaterEqual;
        }
    }

    private static LadCompareOperator InvertCompareOperator(LadCompareOperator op)
    {
        switch (op)
        {
            case LadCompareOperator.Equal: return LadCompareOperator.NotEqual;
            case LadCompareOperator.NotEqual: return LadCompareOperator.Equal;
            case LadCompareOperator.Less: return LadCompareOperator.GreaterEqual;
            case LadCompareOperator.LessEqual: return LadCompareOperator.Greater;
            case LadCompareOperator.Greater: return LadCompareOperator.LessEqual;
            default: return LadCompareOperator.Less;
        }
    }

    private static bool TryMapMathOperator(SclBinaryOperator op, out LadMathOperator result)
    {
        switch (op)
        {
            case SclBinaryOperator.Add: result = LadMathOperator.Add; return true;
            case SclBinaryOperator.Subtract: result = LadMathOperator.Subtract; return true;
            case SclBinaryOperator.Multiply: result = LadMathOperator.Multiply; return true;
            case SclBinaryOperator.Divide: result = LadMathOperator.Divide; return true;
            case SclBinaryOperator.Modulo: result = LadMathOperator.Modulo; return true;
            default: result = LadMathOperator.Add; return false;
        }
    }

    /// <summary>
    /// Folds constant rung states away. A network whose condition simplifies to a constant true is
    /// wired straight to the power rail; a constant false network is dropped by the caller.
    /// </summary>
    private static LadLogic? Simplify(LadLogic? logic)
    {
        switch (logic)
        {
            case null:
                return null;

            case LadSeries series:
                {
                    var result = new LadSeries();

                    foreach (var element in series.Elements)
                    {
                        var simplified = Simplify(element);

                        if (simplified is LadConstantLogic constant)
                        {
                            if (constant.Value)
                            {
                                continue;
                            }

                            return new LadConstantLogic { Value = false };
                        }

                        if (simplified != null)
                        {
                            AppendSeries(result, simplified);
                        }
                    }

                    if (result.Elements.Count == 0)
                    {
                        return new LadConstantLogic { Value = true };
                    }

                    return result.Elements.Count == 1 ? result.Elements[0] : result;
                }

            case LadParallel parallel:
                {
                    var result = new LadParallel();

                    foreach (var branch in parallel.Branches)
                    {
                        var simplified = Simplify(branch);

                        if (simplified is LadConstantLogic constant)
                        {
                            if (constant.Value)
                            {
                                return new LadConstantLogic { Value = true };
                            }

                            continue;
                        }

                        if (simplified != null)
                        {
                            AppendParallel(result, simplified);
                        }
                    }

                    if (result.Branches.Count == 0)
                    {
                        return new LadConstantLogic { Value = false };
                    }

                    return result.Branches.Count == 1 ? result.Branches[0] : result;
                }

        }

        return logic;
    }

    #endregion

    #region operands and types

    private static bool? AsBooleanConstant(SclExpression expression)
    {
        if (expression is SclLiteralExpression literal && literal.Kind == SclLiteralKind.Boolean)
        {
            return string.Equals(SclDataTypes.ConstantValueText(literal), "true", StringComparison.Ordinal);
        }

        return null;
    }

    /// <summary>
    /// Decides whether an assignment drives a coil or moves a value. The target's declared type wins;
    /// when nothing is known the shape of the right-hand side decides, and a plain operand on both
    /// sides is reported because it could legitimately be either.
    /// </summary>
    private bool IsBooleanAssignment(SclAssignmentStatement statement, LadOperand target)
    {
        var targetType = target.DataType;

        if (!string.IsNullOrEmpty(targetType))
        {
            return SclDataTypes.IsBooleanTypeName(targetType);
        }

        switch (statement.Value)
        {
            case SclLiteralExpression literal:
                return literal.Kind == SclLiteralKind.Boolean;

            case SclUnaryExpression unary:
                return unary.Operator == SclUnaryOperator.Not;

            case SclBinaryExpression binary:
                return IsBooleanOperator(binary.Operator);

            case SclFunctionCallExpression _:
                return false;
        }

        if (statement.Value is SclSymbolExpression valueSymbol)
        {
            var valueType = ResolveSymbolType(valueSymbol);

            if (!string.IsNullOrEmpty(valueType))
            {
                return SclDataTypes.IsBooleanTypeName(valueType);
            }

            Report(ConversionSeverity.Warning, "SCL2LAD017",
                $"Neither '{target.Text}' nor '{valueSymbol.Text}' has a resolvable type; the assignment was drawn as a coil. If these are numeric, replace the coil with a MOVE box.",
                statement.Line, FirstLine(statement.RawText));

            return true;
        }

        return false;
    }

    private static bool IsBooleanOperator(SclBinaryOperator op)
    {
        switch (op)
        {
            case SclBinaryOperator.And:
            case SclBinaryOperator.Or:
            case SclBinaryOperator.Xor:
            case SclBinaryOperator.Equal:
            case SclBinaryOperator.NotEqual:
            case SclBinaryOperator.Less:
            case SclBinaryOperator.LessEqual:
            case SclBinaryOperator.Greater:
            case SclBinaryOperator.GreaterEqual:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Builds a ladder operand, or returns null when the expression is not a single operand.</summary>
    private LadOperand? BuildOperand(SclExpression expression)
    {
        if (expression is SclLiteralExpression literal)
        {
            return LadOperand.FromConstant(
                SclDataTypes.InferConstantType(literal),
                SclDataTypes.ConstantValueText(literal),
                SclExpressionPrinter.Print(literal));
        }

        if (expression is SclUnaryExpression unary && unary.Operator == SclUnaryOperator.Negate
            && unary.Operand is SclLiteralExpression negated && negated.Kind == SclLiteralKind.Number)
        {
            return LadOperand.FromConstant(
                SclDataTypes.InferConstantType(negated),
                "-" + SclDataTypes.ConstantValueText(negated),
                "-" + SclExpressionPrinter.Print(negated));
        }

        if (!(expression is SclSymbolExpression symbol))
        {
            return null;
        }

        if (symbol.IsAbsolute)
        {
            return BuildAddressOperand(symbol);
        }

        var operand = new LadOperand
        {
            Kind = symbol.IsLocal ? LadOperandKind.Local : LadOperandKind.Global,
            Text = symbol.Text,
            DataType = ResolveSymbolType(symbol)
        };

        for (var i = 0; i < symbol.Components.Count; i++)
        {
            var component = new LadOperandComponent(symbol.Components[i]);
            var indices = symbol.ComponentIndices[i];

            if (indices != null)
            {
                foreach (var index in indices)
                {
                    var indexOperand = BuildOperand(index);

                    if (indexOperand == null)
                    {
                        Report(ConversionSeverity.Error, "SCL2LAD018",
                            $"The array index in '{symbol.Text}' is a calculation; ladder needs a constant or a single tag.",
                            symbol.Line);

                        return null;
                    }

                    component.Indices.Add(indexOperand);
                }
            }

            operand.Components.Add(component);
        }

        return operand;
    }

    private LadOperand? BuildAddressOperand(SclSymbolExpression symbol)
    {
        var text = symbol.Components.Count > 0 ? symbol.Components[0] : symbol.Text;
        var match = AddressPattern.Match(text);

        if (!match.Success)
        {
            Report(ConversionSeverity.Error, "SCL2LAD019",
                $"Absolute operand '{text}' is not an I/Q/M area access that the converter can encode; use a symbolic tag.",
                symbol.Line);

            return null;
        }

        var area = match.Groups["area"].Value.ToUpperInvariant();
        var size = match.Groups["size"].Success ? match.Groups["size"].Value.ToUpperInvariant() : "X";
        var byteOffset = int.Parse(match.Groups["byte"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var bitOffset = match.Groups["bit"].Success
            ? int.Parse(match.Groups["bit"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;

        string areaName;
        switch (area)
        {
            case "I":
            case "E":
                areaName = "Input";
                break;
            case "Q":
            case "A":
                areaName = "Output";
                break;
            case "M":
                areaName = "Memory";
                break;
            case "PI":
                areaName = "PeripheryInput";
                break;
            default:
                areaName = "PeripheryOutput";
                break;
        }

        string type;
        switch (size)
        {
            case "B": type = "Byte"; break;
            case "W": type = "Word"; break;
            case "D": type = "DWord"; break;
            case "L": type = "LWord"; break;
            default: type = "Bool"; break;
        }

        Report(ConversionSeverity.Info, "SCL2LAD020",
            $"Absolute operand '{text}' was kept as an address; a symbolic tag would be easier to maintain.",
            symbol.Line);

        return new LadOperand
        {
            Kind = LadOperandKind.Address,
            Text = text,
            DataType = type,
            Address = new LadAddress
            {
                Area = areaName,
                BitOffset = (byteOffset * 8) + bitOffset,
                Type = type
            }
        };
    }

    /// <summary>
    /// Resolves the declared type of a block-local operand by walking the interface sections.
    /// Global operands are unknown offline, because their type lives in the PLC tag table or a DB.
    /// </summary>
    private string? ResolveSymbolType(SclSymbolExpression symbol)
    {
        if (!symbol.IsLocal || symbol.Components.Count == 0)
        {
            return null;
        }

        SclInterfaceMember? member = null;

        foreach (var section in _result.Block.Sections)
        {
            member = FindMember(section.Members, symbol.Components[0]);

            if (member != null)
            {
                break;
            }
        }

        if (member == null)
        {
            return null;
        }

        var type = TypeOfAccess(member.DataType, symbol.ComponentIndices[0]);

        for (var i = 1; i < symbol.Components.Count; i++)
        {
            member = FindMember(member.Members, symbol.Components[i]);

            if (member == null)
            {
                return null;
            }

            type = TypeOfAccess(member.DataType, symbol.ComponentIndices[i]);
        }

        return SclDataTypes.Normalize(type);
    }

    private static string TypeOfAccess(string declaredType, List<SclExpression>? indices)
    {
        if (indices == null || indices.Count == 0)
        {
            return declaredType;
        }

        var match = ArrayElementPattern.Match(declaredType);
        return match.Success ? match.Groups["element"].Value.Trim() : declaredType;
    }

    private static SclInterfaceMember? FindMember(List<SclInterfaceMember> members, string name)
    {
        foreach (var member in members)
        {
            if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return member;
            }
        }

        return null;
    }

    #endregion

    private void Report(ConversionSeverity severity, string code, string message, int line, string? snippet = null)
    {
        _result.Diagnostics.Add(new ConversionDiagnostic(severity, code, message, line, snippet));
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var index = text.IndexOf('\n');
        var line = index >= 0 ? text.Substring(0, index) : text;
        return line.TrimEnd('\r').Trim();
    }
}
