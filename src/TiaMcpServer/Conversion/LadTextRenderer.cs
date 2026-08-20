using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TiaMcpServer.Conversion;

/// <summary>
/// Draws converted networks as ASCII ladder.
/// </summary>
/// <remarks>
/// The preview exists so the conversion can be reviewed without opening TIA Portal: it is rendered
/// from the same ladder model the SimaticML writer consumes, so what it shows is what gets imported.
/// </remarks>
public static class LadTextRenderer
{
    /// <summary>A rectangular block of text with the row that carries the rung's horizontal wire.</summary>
    private sealed class Block
    {
        public List<string> Lines { get; } = new List<string>();

        /// <summary>Index into <see cref="Lines"/> of the row holding the horizontal wire.</summary>
        public int ConnectRow { get; set; }

        public int Width
        {
            get { return Lines.Count > 0 ? Lines[0].Length : 0; }
        }
    }

    public static string Render(LadConversionResult result)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < result.Networks.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine();
            }

            sb.Append(RenderNetwork(result.Networks[i], i + 1));
        }

        if (result.Networks.Count == 0)
        {
            sb.AppendLine("(no networks were produced)");
        }

        return sb.ToString();
    }

    public static string RenderNetwork(LadNetwork network, int number)
    {
        var sb = new StringBuilder();

        sb.Append("Network ");
        sb.Append(number.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(network.Title))
        {
            sb.Append(": ");
            sb.Append(network.Title!.Trim());
        }

        if (network.SourceLine > 0)
        {
            sb.Append("   [SCL line ");
            sb.Append(network.SourceLine.ToString(CultureInfo.InvariantCulture));
            sb.Append(']');
        }

        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(network.Comment))
        {
            foreach (var line in SplitLines(network.Comment!))
            {
                sb.Append("  // ");
                sb.AppendLine(line);
            }
        }

        if (network.IsPlaceholder || network.Outputs.Count == 0)
        {
            sb.AppendLine("  (empty network - nothing was converted)");
            return sb.ToString();
        }

        var rung = BuildRung(network);

        for (var i = 0; i < rung.Lines.Count; i++)
        {
            sb.Append("  ");
            sb.Append(i == rung.ConnectRow ? '|' : ' ');
            sb.AppendLine(rung.Lines[i].TrimEnd());
        }

        return sb.ToString();
    }

    private static Block BuildRung(LadNetwork network)
    {
        var blocks = new List<Block>();

        if (network.Condition != null)
        {
            blocks.Add(RenderLogic(network.Condition));
        }

        foreach (var output in network.Outputs)
        {
            blocks.Add(RenderOutput(output));
        }

        return Concatenate(blocks);
    }

    #region logic

    private static Block RenderLogic(LadLogic logic)
    {
        switch (logic)
        {
            case LadContact contact:
                return Element(contact.Operand.Text, contact.Kind == LadContactKind.Negated ? "--|/|--" : "--| |--", null);

            case LadCompare compare:
                {
                    var label = compare.Left.Text + " " + CompareSymbol(compare.Operator) + " " + compare.Right.Text;
                    return Element(label, "--[" + CompareSymbol(compare.Operator) + " " + compare.DataType + "]--", null);
                }

            case LadSeries series:
                {
                    var blocks = new List<Block>();

                    foreach (var element in series.Elements)
                    {
                        blocks.Add(RenderLogic(element));
                    }

                    return Concatenate(blocks);
                }

            case LadParallel parallel:
                {
                    var blocks = new List<Block>();

                    foreach (var branch in parallel.Branches)
                    {
                        blocks.Add(RenderLogic(branch));
                    }

                    return Stack(blocks);
                }

            case LadConstantLogic constant:
                return Element(string.Empty, constant.Value ? "-------" : "--(X)--", null);
        }

        return Element(string.Empty, "-------", null);
    }

    private static string CompareSymbol(LadCompareOperator op)
    {
        switch (op)
        {
            case LadCompareOperator.Equal: return "==";
            case LadCompareOperator.NotEqual: return "<>";
            case LadCompareOperator.Less: return "<";
            case LadCompareOperator.LessEqual: return "<=";
            case LadCompareOperator.Greater: return ">";
            default: return ">=";
        }
    }

    #endregion

    #region outputs

    private static Block RenderOutput(LadOutput output)
    {
        switch (output)
        {
            case LadCoil coil:
                {
                    string wire;

                    switch (coil.Kind)
                    {
                        case LadCoilKind.Set: wire = "--( S )--"; break;
                        case LadCoilKind.Reset: wire = "--( R )--"; break;
                        default: wire = "--(   )--"; break;
                    }

                    return Element(coil.Operand.Text, wire, null);
                }

            case LadMove move:
                return Element("MOVE", "--[ MOVE ]--", new[]
                {
                    "IN   = " + move.Source.Text,
                    "OUT1 = " + move.Destination.Text
                });

            case LadMath math:
                return Element(MathName(math.Operator) + " (" + math.DataType + ")",
                    "--[ " + MathName(math.Operator) + " ]--",
                    new[]
                    {
                        "IN1 = " + math.Left.Text,
                        "IN2 = " + math.Right.Text,
                        "OUT = " + math.Destination.Text
                    });

            case LadCall call:
                {
                    var details = new List<string>();

                    if (call.Instance != null)
                    {
                        details.Add("instance = " + call.Instance.Text);
                    }

                    foreach (var parameter in call.Parameters)
                    {
                        var arrow = string.Equals(parameter.Section, "Input", StringComparison.OrdinalIgnoreCase) ? " := " : " => ";
                        details.Add(parameter.Name + arrow + parameter.Operand.Text);
                    }

                    return Element(call.BlockType + " \"" + call.BlockName + "\"", "--[ CALL ]--", details);
                }
        }

        return Element(string.Empty, "-------", null);
    }

    private static string MathName(LadMathOperator op)
    {
        switch (op)
        {
            case LadMathOperator.Subtract: return "SUB";
            case LadMathOperator.Multiply: return "MUL";
            case LadMathOperator.Divide: return "DIV";
            case LadMathOperator.Modulo: return "MOD";
            default: return "ADD";
        }
    }

    #endregion

    #region layout

    /// <summary>Builds a single ladder element: a label above the wire and optional detail lines below.</summary>
    private static Block Element(string label, string wire, IEnumerable<string>? details)
    {
        var detailLines = new List<string>();

        if (details != null)
        {
            detailLines.AddRange(details);
        }

        var width = Math.Max(label.Length + 2, wire.Length);

        foreach (var detail in detailLines)
        {
            width = Math.Max(width, detail.Length + 4);
        }

        var block = new Block();
        block.Lines.Add(Center(label, width));
        block.Lines.Add(wire.PadRight(width, '-'));

        foreach (var detail in detailLines)
        {
            block.Lines.Add(("  " + detail).PadRight(width));
        }

        block.ConnectRow = 1;
        return block;
    }

    private static string Center(string text, int width)
    {
        if (text.Length >= width)
        {
            return text;
        }

        var left = (width - text.Length) / 2;
        return new string(' ', left) + text + new string(' ', width - text.Length - left);
    }

    /// <summary>Places blocks side by side, aligned on their wire rows - a series connection.</summary>
    private static Block Concatenate(List<Block> blocks)
    {
        if (blocks.Count == 0)
        {
            return Element(string.Empty, "-------", null);
        }

        if (blocks.Count == 1)
        {
            return blocks[0];
        }

        var above = 0;
        var below = 0;

        foreach (var block in blocks)
        {
            above = Math.Max(above, block.ConnectRow);
            below = Math.Max(below, block.Lines.Count - block.ConnectRow - 1);
        }

        var height = above + below + 1;
        var rows = new string[height];

        for (var i = 0; i < height; i++)
        {
            rows[i] = string.Empty;
        }

        foreach (var block in blocks)
        {
            var offset = above - block.ConnectRow;

            for (var i = 0; i < height; i++)
            {
                var index = i - offset;
                var fill = i == above ? '-' : ' ';

                rows[i] += index >= 0 && index < block.Lines.Count
                    ? block.Lines[index]
                    : new string(fill, block.Width);
            }
        }

        var result = new Block { ConnectRow = above };
        result.Lines.AddRange(rows);
        return result;
    }

    /// <summary>Stacks blocks vertically between two vertical rails - a parallel branch.</summary>
    private static Block Stack(List<Block> blocks)
    {
        if (blocks.Count == 0)
        {
            return Element(string.Empty, "-------", null);
        }

        if (blocks.Count == 1)
        {
            return blocks[0];
        }

        var width = 0;

        foreach (var block in blocks)
        {
            width = Math.Max(width, block.Width);
        }

        var lines = new List<string>();
        var connectRows = new List<int>();

        foreach (var block in blocks)
        {
            for (var i = 0; i < block.Lines.Count; i++)
            {
                if (i == block.ConnectRow)
                {
                    connectRows.Add(lines.Count);
                    lines.Add(block.Lines[i].PadRight(width, '-'));
                }
                else
                {
                    lines.Add(block.Lines[i].PadRight(width));
                }
            }
        }

        var first = connectRows[0];
        var last = connectRows[connectRows.Count - 1];
        var isConnectRow = new bool[lines.Count];

        foreach (var row in connectRows)
        {
            isConnectRow[row] = true;
        }

        var result = new Block { ConnectRow = first };

        for (var i = 0; i < lines.Count; i++)
        {
            char rail;

            if (isConnectRow[i])
            {
                rail = '+';
            }
            else if (i > first && i < last)
            {
                rail = '|';
            }
            else
            {
                rail = ' ';
            }

            result.Lines.Add(rail + lines[i] + rail);
        }

        return result;
    }

    private static string[] SplitLines(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    #endregion
}
