using System;
using System.Collections.Generic;
using System.Text;

namespace TiaMcpServer.Conversion;

/// <summary>
/// Result of parsing one SCL source file.
/// </summary>
public sealed class SclParseResult
{
    public List<SclBlockDefinition> Blocks { get; } = new List<SclBlockDefinition>();
    public List<ConversionDiagnostic> Diagnostics { get; } = new List<ConversionDiagnostic>();
}

/// <summary>
/// Recursive descent parser for the subset of SCL that is relevant to ladder conversion.
/// </summary>
/// <remarks>
/// The parser never throws on malformed input. Anything it cannot understand becomes an
/// <see cref="SclUnsupportedStatement"/> plus a diagnostic, so that conversion of the surrounding code
/// still succeeds and the engineer sees exactly what was left behind.
/// </remarks>
public sealed class SclParser
{
    private static readonly string[] BlockEndKeywords =
    {
        "END_FUNCTION_BLOCK", "END_FUNCTION", "END_ORGANIZATION_BLOCK", "END_DATA_BLOCK", "END_TYPE"
    };

    private readonly string _source;
    private readonly List<SclToken> _tokens = new List<SclToken>();
    private readonly Dictionary<int, List<string>> _leadingComments = new Dictionary<int, List<string>>();
    private readonly Dictionary<int, string> _trailingComments = new Dictionary<int, string>();
    private readonly List<ConversionDiagnostic> _diagnostics = new List<ConversionDiagnostic>();

    private int _pos;

    public SclParser(string source)
    {
        _source = source ?? string.Empty;
        SplitTokensAndComments(new SclLexer(_source).Tokenize());
    }

    public SclParseResult Parse()
    {
        var result = new SclParseResult();

        while (!AtEnd)
        {
            var before = _pos;

            if (IsBlockHeaderStart())
            {
                var block = ParseBlock();
                if (block != null)
                {
                    result.Blocks.Add(block);
                }
            }
            else
            {
                var fragment = new SclBlockDefinition { Kind = SclBlockKind.Fragment };
                ParseStatementList(fragment.Body, () => IsBlockHeaderStart());

                if (fragment.Body.Count > 0)
                {
                    result.Blocks.Add(fragment);
                }
            }

            if (_pos == before)
            {
                _pos++;
            }
        }

        result.Diagnostics.AddRange(_diagnostics);
        return result;
    }

    #region token plumbing

    private void SplitTokensAndComments(List<SclToken> all)
    {
        var pending = new List<string>();
        var lastCodeLine = -1;

        foreach (var token in all)
        {
            if (token.Kind == SclTokenKind.Comment)
            {
                if (token.Line == lastCodeLine)
                {
                    if (!_trailingComments.ContainsKey(token.Line) && token.Text.Length > 0)
                    {
                        _trailingComments[token.Line] = token.Text;
                    }
                }
                else if (token.Text.Length > 0)
                {
                    pending.Add(token.Text);
                }

                continue;
            }

            if (pending.Count > 0)
            {
                _leadingComments[_tokens.Count] = new List<string>(pending);
                pending.Clear();
            }

            _tokens.Add(token);
            lastCodeLine = token.Line;
        }

        if (_tokens.Count == 0)
        {
            _tokens.Add(new SclToken(SclTokenKind.EndOfFile, string.Empty, _source.Length, 0, 1, 1));
        }
    }

    private SclToken Current
    {
        get { return _tokens[Math.Min(_pos, _tokens.Count - 1)]; }
    }

    private SclToken Peek(int offset)
    {
        return _tokens[Math.Min(Math.Max(_pos + offset, 0), _tokens.Count - 1)];
    }

    private SclToken Previous
    {
        get { return _tokens[Math.Min(Math.Max(_pos - 1, 0), _tokens.Count - 1)]; }
    }

    private bool AtEnd
    {
        get { return Current.Kind == SclTokenKind.EndOfFile; }
    }

    private bool MatchOperator(string op)
    {
        if (!Current.IsOperator(op))
        {
            return false;
        }

        _pos++;
        return true;
    }

    private bool MatchKeyword(string keyword)
    {
        if (!Current.IsKeyword(keyword))
        {
            return false;
        }

        _pos++;
        return true;
    }

    private void ExpectOperator(string op)
    {
        if (!MatchOperator(op))
        {
            Report(ConversionSeverity.Warning, "SCL2LAD002", $"Expected '{op}' but found '{Describe(Current)}'.", Current.Line);
        }
    }

    private void ExpectKeyword(string keyword)
    {
        if (!MatchKeyword(keyword))
        {
            Report(ConversionSeverity.Warning, "SCL2LAD002", $"Expected '{keyword}' but found '{Describe(Current)}'.", Current.Line);
        }
    }

    private static string Describe(SclToken token)
    {
        return token.Kind == SclTokenKind.EndOfFile ? "end of file" : token.Text;
    }

    private void Report(ConversionSeverity severity, string code, string message, int line, string? snippet = null)
    {
        _diagnostics.Add(new ConversionDiagnostic(severity, code, message, line, snippet));
    }

    private string RawTextBetween(int startTokenIndex, int endTokenIndexExclusive)
    {
        if (startTokenIndex >= _tokens.Count)
        {
            return string.Empty;
        }

        var start = _tokens[Math.Min(startTokenIndex, _tokens.Count - 1)].Position;
        var lastIndex = Math.Min(Math.Max(endTokenIndexExclusive - 1, startTokenIndex), _tokens.Count - 1);
        var end = _tokens[lastIndex].EndPosition;

        if (end <= start || start < 0 || end > _source.Length)
        {
            return string.Empty;
        }

        return _source.Substring(start, end - start).Trim();
    }

    /// <summary>
    /// Consumes every remaining token on <paramref name="referenceLine"/> and returns the raw source.
    /// Returns an empty string when the line already ended, so a keyword alone on its line cannot
    /// swallow the statement that follows it.
    /// </summary>
    private string ReadRestOfLine(int referenceLine)
    {
        if (AtEnd || Current.Line != referenceLine)
        {
            return string.Empty;
        }

        var startIndex = _pos;

        while (!AtEnd && Current.Line == referenceLine)
        {
            _pos++;
        }

        return RawTextBetween(startIndex, _pos).TrimEnd(';').Trim();
    }

    #endregion

    #region block header and interface

    private bool IsBlockHeaderStart()
    {
        return Current.IsKeyword("FUNCTION_BLOCK")
            || Current.IsKeyword("FUNCTION")
            || Current.IsKeyword("ORGANIZATION_BLOCK")
            || Current.IsKeyword("DATA_BLOCK")
            || Current.IsKeyword("TYPE");
    }

    private SclBlockDefinition? ParseBlock()
    {
        var headerToken = Current;
        var keyword = headerToken.Text.ToUpperInvariant();
        _pos++;

        var block = new SclBlockDefinition();

        switch (keyword)
        {
            case "FUNCTION_BLOCK":
                block.Kind = SclBlockKind.FunctionBlock;
                break;
            case "FUNCTION":
                block.Kind = SclBlockKind.Function;
                break;
            case "ORGANIZATION_BLOCK":
                block.Kind = SclBlockKind.OrganizationBlock;
                break;
            default:
                Report(ConversionSeverity.Error, "SCL2LAD003",
                    $"'{headerToken.Text}' declarations are not ladder blocks and were skipped.", headerToken.Line);
                SkipToBlockEnd();
                return null;
        }

        block.Name = ReadDeclaredName();

        if (Current.IsOperator(":"))
        {
            // The return type of a FUNCTION ends with the header line; ReadDataTypeText would run on
            // to the first ';' and swallow the whole first VAR declaration.
            var returnTypeLine = Current.Line;
            _pos++;
            block.ReturnType = ReadRestOfLine(returnTypeLine);
        }

        ParseBlockMetadata(block);
        ParseInterfaceSections(block);

        if (!MatchKeyword("BEGIN") && block.Kind != SclBlockKind.Function)
        {
            // OB/FB sources always carry BEGIN; a missing one usually means an unexpected declaration.
            Report(ConversionSeverity.Warning, "SCL2LAD002", "Expected 'BEGIN' before the block body.", Current.Line);
        }

        ParseStatementList(block.Body, () => IsAnyOf(BlockEndKeywords));

        if (IsAnyOf(BlockEndKeywords))
        {
            _pos++;
            MatchOperator(";");
        }
        else
        {
            Report(ConversionSeverity.Warning, "SCL2LAD002", $"Block '{block.Name}' is not terminated by an END_... keyword.", Current.Line);
        }

        return block;
    }

    private void SkipToBlockEnd()
    {
        while (!AtEnd && !IsAnyOf(BlockEndKeywords))
        {
            _pos++;
        }

        if (IsAnyOf(BlockEndKeywords))
        {
            _pos++;
            MatchOperator(";");
        }
    }

    private bool IsAnyOf(string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (Current.IsKeyword(keyword))
            {
                return true;
            }
        }

        return false;
    }

    private string ReadDeclaredName()
    {
        if (Current.Kind == SclTokenKind.QuotedSymbol || Current.Kind == SclTokenKind.Identifier)
        {
            var name = Current.Text;
            _pos++;
            return name;
        }

        Report(ConversionSeverity.Warning, "SCL2LAD002", "Expected a block name.", Current.Line);
        return string.Empty;
    }

    private void ParseBlockMetadata(SclBlockDefinition block)
    {
        while (!AtEnd)
        {
            var line = Current.Line;

            if (Current.IsKeyword("TITLE"))
            {
                _pos++;
                MatchOperator("=");
                MatchOperator(":");
                block.Title = ReadRestOfLine(line);
                continue;
            }

            if (Current.IsKeyword("VERSION"))
            {
                _pos++;
                MatchOperator(":");
                MatchOperator("=");
                block.Version = ReadRestOfLine(line);
                continue;
            }

            if (Current.IsKeyword("AUTHOR"))
            {
                _pos++;
                MatchOperator(":");
                MatchOperator("=");
                block.Author = ReadRestOfLine(line);
                continue;
            }

            if (Current.IsKeyword("FAMILY"))
            {
                _pos++;
                MatchOperator(":");
                MatchOperator("=");
                block.Family = ReadRestOfLine(line);
                continue;
            }

            if (Current.IsKeyword("NAME"))
            {
                _pos++;
                MatchOperator(":");
                MatchOperator("=");
                ReadRestOfLine(line);
                continue;
            }

            return;
        }
    }

    /// <summary>Maps an SCL VAR keyword to the SimaticML interface section name, or null when it is not a VAR keyword.</summary>
    private static string? MapSectionName(string keyword)
    {
        switch (keyword.ToUpperInvariant())
        {
            case "VAR_INPUT": return "Input";
            case "VAR_OUTPUT": return "Output";
            case "VAR_IN_OUT": return "InOut";
            case "VAR_TEMP": return "Temp";
            case "VAR_STAT": return "Static";
            case "VAR": return "Static";
            default: return null;
        }
    }

    private void ParseInterfaceSections(SclBlockDefinition block)
    {
        while (!AtEnd)
        {
            if (Current.Kind != SclTokenKind.Identifier)
            {
                return;
            }

            var sectionName = MapSectionName(Current.Text);
            if (sectionName == null)
            {
                return;
            }

            _pos++;

            // 'VAR CONSTANT' and 'VAR_TEMP RETAIN' style modifiers.
            while (Current.Kind == SclTokenKind.Identifier && MapSectionName(Current.Text) == null && !Current.IsKeyword("END_VAR"))
            {
                if (Current.IsKeyword("CONSTANT"))
                {
                    sectionName = "Constant";
                    _pos++;
                    continue;
                }

                if (Current.IsKeyword("RETAIN") || Current.IsKeyword("NON_RETAIN"))
                {
                    _pos++;
                    continue;
                }

                break;
            }

            var section = GetOrCreateSection(block, sectionName);
            ParseMemberList(section.Members, "END_VAR");

            ExpectKeyword("END_VAR");
            MatchOperator(";");
        }
    }

    private static SclInterfaceSection GetOrCreateSection(SclBlockDefinition block, string name)
    {
        foreach (var existing in block.Sections)
        {
            if (string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                return existing;
            }
        }

        var section = new SclInterfaceSection(name);
        block.Sections.Add(section);
        return section;
    }

    private void ParseMemberList(List<SclInterfaceMember> members, string terminator)
    {
        while (!AtEnd && !Current.IsKeyword(terminator))
        {
            var before = _pos;
            ParseMemberInto(members);

            if (_pos == before)
            {
                _pos++;
            }
        }
    }

    /// <summary>
    /// Parses one declaration and appends a member per declared name, so that <c>a, b : Bool;</c>
    /// yields two interface members rather than losing 'b'.
    /// </summary>
    private void ParseMemberInto(List<SclInterfaceMember> members)
    {
        if (Current.Kind != SclTokenKind.Identifier && Current.Kind != SclTokenKind.QuotedSymbol)
        {
            Report(ConversionSeverity.Warning, "SCL2LAD002", $"Expected a variable name but found '{Describe(Current)}'.", Current.Line);
            SkipToStatementEnd();
            return;
        }

        var names = new List<string> { Current.Text };
        _pos++;

        while (MatchOperator(","))
        {
            if (Current.Kind == SclTokenKind.Identifier || Current.Kind == SclTokenKind.QuotedSymbol)
            {
                names.Add(Current.Text);
                _pos++;
            }
            else
            {
                break;
            }
        }

        ExpectOperator(":");

        var dataType = "Struct";
        var structMembers = new List<SclInterfaceMember>();

        if (Current.IsKeyword("STRUCT"))
        {
            _pos++;
            ParseMemberList(structMembers, "END_STRUCT");
            ExpectKeyword("END_STRUCT");
        }
        else
        {
            dataType = ReadDataTypeText();
        }

        string? startValue = null;
        if (MatchOperator(":="))
        {
            startValue = ReadUntilStatementEndRaw();
        }

        MatchOperator(";");

        _trailingComments.TryGetValue(Previous.Line, out var comment);

        foreach (var name in names)
        {
            var member = new SclInterfaceMember
            {
                Name = name,
                DataType = dataType,
                StartValue = startValue,
                Comment = comment
            };

            member.Members.AddRange(structMembers);
            members.Add(member);
        }
    }

    /// <summary>Reads a data type as raw source text, stopping at ':=' or ';' outside of brackets.</summary>
    private string ReadDataTypeText()
    {
        var startIndex = _pos;
        var depth = 0;

        while (!AtEnd)
        {
            if (Current.IsOperator("[") || Current.IsOperator("("))
            {
                depth++;
            }
            else if (Current.IsOperator("]") || Current.IsOperator(")"))
            {
                depth--;
            }
            else if (depth <= 0 && (Current.IsOperator(";") || Current.IsOperator(":=")))
            {
                break;
            }

            _pos++;
        }

        return RawTextBetween(startIndex, _pos);
    }

    private string ReadUntilStatementEndRaw()
    {
        var startIndex = _pos;
        var depth = 0;

        while (!AtEnd)
        {
            if (Current.IsOperator("[") || Current.IsOperator("("))
            {
                depth++;
            }
            else if (Current.IsOperator("]") || Current.IsOperator(")"))
            {
                depth--;
            }
            else if (depth <= 0 && Current.IsOperator(";"))
            {
                break;
            }

            _pos++;
        }

        return RawTextBetween(startIndex, _pos);
    }

    private void SkipToStatementEnd()
    {
        while (!AtEnd && !Current.IsOperator(";"))
        {
            _pos++;
        }

        MatchOperator(";");
    }

    #endregion

    #region statements

    private void ParseStatementList(List<SclStatement> body, Func<bool> isTerminator)
    {
        while (!AtEnd && !isTerminator())
        {
            var before = _pos;
            var statement = ParseStatement();

            if (statement != null)
            {
                body.Add(statement);
            }

            if (_pos == before)
            {
                _pos++;
            }
        }
    }

    private SclStatement? ParseStatement()
    {
        if (MatchOperator(";"))
        {
            return null;
        }

        var startIndex = _pos;
        var startToken = Current;

        SclStatement? statement;

        if (startToken.Kind == SclTokenKind.Identifier)
        {
            switch (startToken.Text.ToUpperInvariant())
            {
                case "IF":
                    statement = ParseIf();
                    return Finalize(statement, startIndex, false);

                case "CASE":
                    statement = ParseCase();
                    return Finalize(statement, startIndex, false);

                case "REGION":
                    statement = ParseRegion();
                    return Finalize(statement, startIndex, false);

                case "FOR":
                    return Finalize(ParseUnsupportedBlock("FOR", "END_FOR",
                        "FOR loops have no ladder equivalent; the loop body must be unrolled or kept in SCL."), startIndex, true);

                case "WHILE":
                    return Finalize(ParseUnsupportedBlock("WHILE", "END_WHILE",
                        "WHILE loops have no ladder equivalent; keep this logic in an SCL block."), startIndex, true);

                case "REPEAT":
                    return Finalize(ParseUnsupportedBlock("REPEAT", "END_REPEAT",
                        "REPEAT loops have no ladder equivalent; keep this logic in an SCL block."), startIndex, true);

                case "RETURN":
                case "EXIT":
                case "CONTINUE":
                case "GOTO":
                    return Finalize(ParseUnsupportedSimple(startToken.Text.ToUpperInvariant()), startIndex, true);
            }
        }

        var expression = ParseExpression();

        if (MatchOperator(":="))
        {
            var assignment = new SclAssignmentStatement
            {
                Target = expression,
                Value = ParseExpression()
            };

            MatchOperator(";");
            return Finalize(assignment, startIndex, true);
        }

        if (expression is SclFunctionCallExpression call)
        {
            var callStatement = new SclCallStatement
            {
                Name = call.Name,
                IsLocal = call.IsLocal
            };

            callStatement.Arguments.AddRange(call.Arguments);
            MatchOperator(";");
            return Finalize(callStatement, startIndex, true);
        }

        MatchOperator(";");

        var unsupported = new SclUnsupportedStatement
        {
            Construct = "statement",
            Reason = "The statement is neither an assignment nor a block call and has no ladder equivalent."
        };

        return Finalize(unsupported, startIndex, true);
    }

    private SclStatement Finalize(SclStatement statement, int startIndex, bool captureRawText)
    {
        statement.Line = _tokens[Math.Min(startIndex, _tokens.Count - 1)].Line;

        if (_leadingComments.TryGetValue(startIndex, out var comments))
        {
            statement.LeadingComments.AddRange(comments);
        }

        if (_trailingComments.TryGetValue(Previous.Line, out var trailing))
        {
            statement.TrailingComment = trailing;
        }

        if (captureRawText || statement is SclUnsupportedStatement)
        {
            statement.RawText = RawTextBetween(startIndex, _pos);
        }

        return statement;
    }

    private SclStatement ParseIf()
    {
        var statement = new SclIfStatement();

        ExpectKeyword("IF");

        var branch = new SclIfBranch { Condition = ParseExpression() };
        ExpectKeyword("THEN");
        ParseStatementList(branch.Body, () => Current.IsKeyword("ELSIF") || Current.IsKeyword("ELSE") || Current.IsKeyword("END_IF"));
        statement.Branches.Add(branch);

        while (Current.IsKeyword("ELSIF"))
        {
            _pos++;
            var elsif = new SclIfBranch { Condition = ParseExpression() };
            ExpectKeyword("THEN");
            ParseStatementList(elsif.Body, () => Current.IsKeyword("ELSIF") || Current.IsKeyword("ELSE") || Current.IsKeyword("END_IF"));
            statement.Branches.Add(elsif);
        }

        if (MatchKeyword("ELSE"))
        {
            var elseBody = new List<SclStatement>();
            ParseStatementList(elseBody, () => Current.IsKeyword("END_IF"));
            statement.ElseBody = elseBody;
        }

        ExpectKeyword("END_IF");
        MatchOperator(";");

        return statement;
    }

    private SclStatement ParseCase()
    {
        var statement = new SclCaseStatement();

        ExpectKeyword("CASE");
        statement.Selector = ParseExpression();
        ExpectKeyword("OF");

        while (!AtEnd && !Current.IsKeyword("END_CASE") && !Current.IsKeyword("ELSE"))
        {
            var before = _pos;
            var branch = new SclCaseBranch();

            while (!AtEnd)
            {
                var label = new SclCaseLabel { From = ParseUnary() };

                if (MatchOperator(".."))
                {
                    label.To = ParseUnary();
                }

                branch.Labels.Add(label);

                if (MatchOperator(","))
                {
                    continue;
                }

                break;
            }

            ExpectOperator(":");

            ParseStatementList(branch.Body,
                () => Current.IsKeyword("END_CASE") || Current.IsKeyword("ELSE") || IsCaseLabelAhead());

            statement.Branches.Add(branch);

            if (_pos == before)
            {
                _pos++;
            }
        }

        if (MatchKeyword("ELSE"))
        {
            var elseBody = new List<SclStatement>();
            ParseStatementList(elseBody, () => Current.IsKeyword("END_CASE"));
            statement.ElseBody = elseBody;
        }

        ExpectKeyword("END_CASE");
        MatchOperator(";");

        return statement;
    }

    /// <summary>
    /// True when the current position starts a new CASE label, i.e. a constant followed by ':', ',' or '..'.
    /// ':=' is a single token, so an assignment can never be mistaken for a label.
    /// </summary>
    private bool IsCaseLabelAhead()
    {
        if (Current.Kind != SclTokenKind.Number && Current.Kind != SclTokenKind.Identifier)
        {
            return false;
        }

        var next = Peek(1);
        return next.IsOperator(":") || next.IsOperator(",") || next.IsOperator("..");
    }

    private SclStatement ParseRegion()
    {
        var line = Current.Line;
        ExpectKeyword("REGION");

        var statement = new SclRegionStatement { Title = ReadRestOfLine(line) };

        ParseStatementList(statement.Body, () => Current.IsKeyword("END_REGION"));

        ExpectKeyword("END_REGION");
        MatchOperator(";");

        return statement;
    }

    private SclStatement ParseUnsupportedBlock(string openKeyword, string endKeyword, string reason)
    {
        var depth = 0;

        while (!AtEnd)
        {
            if (Current.IsKeyword(openKeyword))
            {
                depth++;
            }
            else if (Current.IsKeyword(endKeyword))
            {
                depth--;
                if (depth <= 0)
                {
                    _pos++;
                    MatchOperator(";");
                    break;
                }
            }

            _pos++;
        }

        return new SclUnsupportedStatement
        {
            Construct = openKeyword,
            Reason = reason
        };
    }

    private SclStatement ParseUnsupportedSimple(string keyword)
    {
        SkipToStatementEnd();

        return new SclUnsupportedStatement
        {
            Construct = keyword,
            Reason = $"'{keyword}' has no ladder equivalent and was preserved as a network comment."
        };
    }

    #endregion

    #region expressions

    private SclExpression ParseExpression()
    {
        return ParseOr();
    }

    private SclExpression ParseOr()
    {
        var left = ParseXor();

        while (Current.IsKeyword("OR"))
        {
            _pos++;
            left = MakeBinary(SclBinaryOperator.Or, left, ParseXor());
        }

        return left;
    }

    private SclExpression ParseXor()
    {
        var left = ParseAnd();

        while (Current.IsKeyword("XOR"))
        {
            _pos++;
            left = MakeBinary(SclBinaryOperator.Xor, left, ParseAnd());
        }

        return left;
    }

    private SclExpression ParseAnd()
    {
        var left = ParseEquality();

        while (Current.IsKeyword("AND") || Current.IsOperator("&"))
        {
            _pos++;
            left = MakeBinary(SclBinaryOperator.And, left, ParseEquality());
        }

        return left;
    }

    private SclExpression ParseEquality()
    {
        var left = ParseComparison();

        while (Current.IsOperator("=") || Current.IsOperator("<>"))
        {
            var op = Current.IsOperator("=") ? SclBinaryOperator.Equal : SclBinaryOperator.NotEqual;
            _pos++;
            left = MakeBinary(op, left, ParseComparison());
        }

        return left;
    }

    private SclExpression ParseComparison()
    {
        var left = ParseAdditive();

        while (Current.IsOperator("<") || Current.IsOperator(">") || Current.IsOperator("<=") || Current.IsOperator(">="))
        {
            var op = Current.Text switch
            {
                "<" => SclBinaryOperator.Less,
                ">" => SclBinaryOperator.Greater,
                "<=" => SclBinaryOperator.LessEqual,
                _ => SclBinaryOperator.GreaterEqual
            };

            _pos++;
            left = MakeBinary(op, left, ParseAdditive());
        }

        return left;
    }

    private SclExpression ParseAdditive()
    {
        var left = ParseMultiplicative();

        while (Current.IsOperator("+") || Current.IsOperator("-"))
        {
            var op = Current.IsOperator("+") ? SclBinaryOperator.Add : SclBinaryOperator.Subtract;
            _pos++;
            left = MakeBinary(op, left, ParseMultiplicative());
        }

        return left;
    }

    private SclExpression ParseMultiplicative()
    {
        var left = ParseUnary();

        while (Current.IsOperator("*") || Current.IsOperator("/") || Current.IsKeyword("MOD") || Current.IsKeyword("DIV"))
        {
            SclBinaryOperator op;

            if (Current.IsOperator("*"))
            {
                op = SclBinaryOperator.Multiply;
            }
            else if (Current.IsKeyword("MOD"))
            {
                op = SclBinaryOperator.Modulo;
            }
            else
            {
                op = SclBinaryOperator.Divide;
            }

            _pos++;
            left = MakeBinary(op, left, ParseUnary());
        }

        return left;
    }

    private SclExpression ParseUnary()
    {
        var token = Current;

        if (token.IsKeyword("NOT"))
        {
            _pos++;
            return new SclUnaryExpression { Operator = SclUnaryOperator.Not, Operand = ParseUnary(), Line = token.Line, Column = token.Column };
        }

        if (token.IsOperator("-"))
        {
            _pos++;
            return new SclUnaryExpression { Operator = SclUnaryOperator.Negate, Operand = ParseUnary(), Line = token.Line, Column = token.Column };
        }

        if (token.IsOperator("+"))
        {
            _pos++;
            return ParseUnary();
        }

        return ParsePower();
    }

    private SclExpression ParsePower()
    {
        var left = ParsePrimary();

        if (Current.IsOperator("**"))
        {
            _pos++;
            return MakeBinary(SclBinaryOperator.Power, left, ParseUnary());
        }

        return left;
    }

    private SclExpression ParsePrimary()
    {
        var token = Current;

        if (token.IsOperator("("))
        {
            _pos++;
            var inner = ParseExpression();
            ExpectOperator(")");
            return inner;
        }

        switch (token.Kind)
        {
            case SclTokenKind.Number:
                _pos++;
                return MakeNumberLiteral(token);

            case SclTokenKind.StringLiteral:
                _pos++;
                return new SclLiteralExpression { Text = token.Text, Kind = SclLiteralKind.Text, Line = token.Line, Column = token.Column };

            case SclTokenKind.Identifier:
                if (string.Equals(token.Text, "TRUE", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(token.Text, "FALSE", StringComparison.OrdinalIgnoreCase))
                {
                    _pos++;
                    return new SclLiteralExpression
                    {
                        Text = token.Text.ToUpperInvariant(),
                        Kind = SclLiteralKind.Boolean,
                        Line = token.Line,
                        Column = token.Column
                    };
                }

                return ParseOperand();

            case SclTokenKind.QuotedSymbol:
            case SclTokenKind.LocalSymbol:
            case SclTokenKind.AbsoluteAddress:
                return ParseOperand();
        }

        Report(ConversionSeverity.Warning, "SCL2LAD002", $"Unexpected '{Describe(token)}' in an expression.", token.Line);

        if (!AtEnd)
        {
            _pos++;
        }

        return new SclLiteralExpression { Text = "0", Kind = SclLiteralKind.Number, Line = token.Line, Column = token.Column };
    }

    private static SclExpression MakeNumberLiteral(SclToken token)
    {
        var text = token.Text;
        var hashIndex = text.IndexOf('#');
        string? prefix = null;
        var kind = SclLiteralKind.Number;

        if (hashIndex > 0)
        {
            prefix = text.Substring(0, hashIndex);

            // 2#, 8# and 16# are numeric bases, not type prefixes.
            if (prefix == "2" || prefix == "8" || prefix == "16")
            {
                prefix = null;
            }
            else if (SclDataTypes.IsTimeTypeName(prefix))
            {
                kind = SclLiteralKind.Time;
            }
            else if (string.Equals(prefix, "BOOL", StringComparison.OrdinalIgnoreCase))
            {
                kind = SclLiteralKind.Boolean;
            }
        }

        return new SclLiteralExpression
        {
            Text = text,
            Kind = kind,
            TypePrefix = prefix,
            Line = token.Line,
            Column = token.Column
        };
    }

    private SclExpression ParseOperand()
    {
        var token = Current;
        var isLocal = token.Kind == SclTokenKind.LocalSymbol;
        var isAbsolute = token.Kind == SclTokenKind.AbsoluteAddress;
        var baseName = token.Text;
        _pos++;

        if (!isAbsolute && Current.IsOperator("("))
        {
            var call = new SclFunctionCallExpression
            {
                Name = baseName,
                IsLocal = isLocal,
                Line = token.Line,
                Column = token.Column
            };

            ParseArgumentList(call.Arguments);
            return call;
        }

        var symbol = new SclSymbolExpression
        {
            IsLocal = isLocal,
            IsAbsolute = isAbsolute,
            Line = token.Line,
            Column = token.Column
        };

        AddComponent(symbol, baseName);

        if (!isAbsolute)
        {
            ReadIndicesInto(symbol);

            while (Current.IsOperator("."))
            {
                var next = Peek(1);
                if (next.Kind != SclTokenKind.Identifier && next.Kind != SclTokenKind.QuotedSymbol)
                {
                    break;
                }

                _pos += 2;
                AddComponent(symbol, next.Text);
                ReadIndicesInto(symbol);
            }
        }

        symbol.Text = BuildSymbolText(symbol);
        return symbol;
    }

    private static void AddComponent(SclSymbolExpression symbol, string name)
    {
        symbol.Components.Add(name);
        symbol.ComponentIndices.Add(null);
    }

    private void ReadIndicesInto(SclSymbolExpression symbol)
    {
        if (!Current.IsOperator("["))
        {
            return;
        }

        _pos++;

        var indices = new List<SclExpression>();

        while (!AtEnd && !Current.IsOperator("]"))
        {
            indices.Add(ParseExpression());

            if (!MatchOperator(","))
            {
                break;
            }
        }

        ExpectOperator("]");

        if (symbol.ComponentIndices.Count > 0)
        {
            symbol.ComponentIndices[symbol.ComponentIndices.Count - 1] = indices;
        }
    }

    private void ParseArgumentList(List<SclArgument> arguments)
    {
        ExpectOperator("(");

        if (MatchOperator(")"))
        {
            return;
        }

        while (!AtEnd)
        {
            var argument = new SclArgument();
            var nameToken = Current;

            if ((nameToken.Kind == SclTokenKind.Identifier || nameToken.Kind == SclTokenKind.QuotedSymbol)
                && (Peek(1).IsOperator(":=") || Peek(1).IsOperator("=>")))
            {
                argument.Name = nameToken.Text;
                argument.Direction = Peek(1).IsOperator("=>") ? SclArgumentDirection.Output : SclArgumentDirection.Input;
                _pos += 2;
            }

            argument.Value = ParseExpression();
            arguments.Add(argument);

            if (MatchOperator(","))
            {
                continue;
            }

            break;
        }

        ExpectOperator(")");
    }

    private static string BuildSymbolText(SclSymbolExpression symbol)
    {
        if (symbol.IsAbsolute)
        {
            return symbol.Components.Count > 0 ? symbol.Components[0] : string.Empty;
        }

        var sb = new StringBuilder();

        for (var i = 0; i < symbol.Components.Count; i++)
        {
            if (i == 0)
            {
                if (symbol.IsLocal)
                {
                    sb.Append('#');
                    sb.Append(QuoteIfNeeded(symbol.Components[i], false));
                }
                else
                {
                    sb.Append(QuoteIfNeeded(symbol.Components[i], true));
                }
            }
            else
            {
                sb.Append('.');
                sb.Append(QuoteIfNeeded(symbol.Components[i], false));
            }

            var indices = symbol.ComponentIndices[i];
            if (indices != null && indices.Count > 0)
            {
                sb.Append('[');
                for (var j = 0; j < indices.Count; j++)
                {
                    if (j > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(SclExpressionPrinter.Print(indices[j]));
                }
                sb.Append(']');
            }
        }

        return sb.ToString();
    }

    private static string QuoteIfNeeded(string name, bool alwaysQuote)
    {
        if (alwaysQuote || NeedsQuotes(name))
        {
            return "\"" + name + "\"";
        }

        return name;
    }

    private static bool NeedsQuotes(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return true;
            }
        }

        return false;
    }

    private static SclExpression MakeBinary(SclBinaryOperator op, SclExpression left, SclExpression right)
    {
        return new SclBinaryExpression
        {
            Operator = op,
            Left = left,
            Right = right,
            Line = left.Line,
            Column = left.Column
        };
    }

    #endregion
}
