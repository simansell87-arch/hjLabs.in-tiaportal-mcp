using System.Text;

namespace TiaMcpServer.Conversion;

/// <summary>
/// Renders an expression tree back to readable SCL. Used for diagnostics, network comments and the
/// ladder preview, so the engineer can match a rung against the source it came from.
/// </summary>
public static class SclExpressionPrinter
{
    public static string Print(SclExpression? expression)
    {
        var sb = new StringBuilder();
        Write(sb, expression, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, SclExpression? expression, int parentPrecedence)
    {
        switch (expression)
        {
            case null:
                return;

            case SclLiteralExpression literal:
                sb.Append(literal.Kind == SclLiteralKind.Text ? "'" + literal.Text + "'" : literal.Text);
                return;

            case SclSymbolExpression symbol:
                sb.Append(symbol.Text);
                return;

            case SclUnaryExpression unary:
                {
                    var precedence = 2;
                    var needsParentheses = precedence < parentPrecedence;

                    if (needsParentheses)
                    {
                        sb.Append('(');
                    }

                    sb.Append(unary.Operator == SclUnaryOperator.Not ? "NOT " : "-");
                    Write(sb, unary.Operand, precedence);

                    if (needsParentheses)
                    {
                        sb.Append(')');
                    }

                    return;
                }

            case SclBinaryExpression binary:
                {
                    var precedence = PrecedenceOf(binary.Operator);
                    var needsParentheses = precedence < parentPrecedence;

                    if (needsParentheses)
                    {
                        sb.Append('(');
                    }

                    Write(sb, binary.Left, precedence);
                    sb.Append(' ');
                    sb.Append(SymbolOf(binary.Operator));
                    sb.Append(' ');
                    Write(sb, binary.Right, precedence + 1);

                    if (needsParentheses)
                    {
                        sb.Append(')');
                    }

                    return;
                }

            case SclFunctionCallExpression call:
                {
                    sb.Append(call.IsLocal ? "#" + call.Name : "\"" + call.Name + "\"");
                    sb.Append('(');

                    for (var i = 0; i < call.Arguments.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }

                        var argument = call.Arguments[i];

                        if (!string.IsNullOrEmpty(argument.Name))
                        {
                            sb.Append(argument.Name);
                            sb.Append(argument.Direction == SclArgumentDirection.Output ? " => " : " := ");
                        }

                        Write(sb, argument.Value, 0);
                    }

                    sb.Append(')');
                    return;
                }
        }
    }

    /// <summary>Higher numbers bind tighter, mirroring the parser's descent order.</summary>
    private static int PrecedenceOf(SclBinaryOperator op)
    {
        switch (op)
        {
            case SclBinaryOperator.Or: return 1;
            case SclBinaryOperator.Xor: return 2;
            case SclBinaryOperator.And: return 3;
            case SclBinaryOperator.Equal:
            case SclBinaryOperator.NotEqual: return 4;
            case SclBinaryOperator.Less:
            case SclBinaryOperator.LessEqual:
            case SclBinaryOperator.Greater:
            case SclBinaryOperator.GreaterEqual: return 5;
            case SclBinaryOperator.Add:
            case SclBinaryOperator.Subtract: return 6;
            case SclBinaryOperator.Multiply:
            case SclBinaryOperator.Divide:
            case SclBinaryOperator.Modulo: return 7;
            default: return 8;
        }
    }

    public static string SymbolOf(SclBinaryOperator op)
    {
        switch (op)
        {
            case SclBinaryOperator.And: return "AND";
            case SclBinaryOperator.Or: return "OR";
            case SclBinaryOperator.Xor: return "XOR";
            case SclBinaryOperator.Equal: return "=";
            case SclBinaryOperator.NotEqual: return "<>";
            case SclBinaryOperator.Less: return "<";
            case SclBinaryOperator.LessEqual: return "<=";
            case SclBinaryOperator.Greater: return ">";
            case SclBinaryOperator.GreaterEqual: return ">=";
            case SclBinaryOperator.Add: return "+";
            case SclBinaryOperator.Subtract: return "-";
            case SclBinaryOperator.Multiply: return "*";
            case SclBinaryOperator.Divide: return "/";
            case SclBinaryOperator.Modulo: return "MOD";
            default: return "**";
        }
    }
}
