# SCL to LAD Converter

## Overview

Three MCP tools convert SCL source code into a Ladder (LAD) block:

| Tool | Input | Needs TIA Portal |
|------|-------|------------------|
| `ConvertSclToLad` | SCL text or an `.scl` file | No |
| `ImportSclAsLad` | SCL text or an `.scl` file | Yes — imports the result |
| `ConvertBlockToLad` | An SCL block already in the project | Yes — V20+ |

The conversion runs in three stages, none of which touch TIA Portal:

1. **Parse** — the SCL block header, its `VAR` sections and its statements become an
   abstract syntax tree (`Conversion/SclLexer.cs`, `Conversion/SclParser.cs`).
2. **Lower** — statements become ladder networks: contacts, coils, compare boxes, MOVE
   and math boxes, and call boxes (`Conversion/SclToLadConverter.cs`).
3. **Write** — the networks become a SimaticML document with `ProgrammingLanguage` set to
   `LAD` (`Conversion/SimaticMlWriter.cs`, `Conversion/FlgNetBuilder.cs`).

TIA Portal is only involved when the caller asks for the block to be imported, or when the
source has to be read back out of a project.

## Preconditions

- `ConvertSclToLad` needs nothing beyond the SCL. Use it to review a conversion before
  touching a project.
- `ImportSclAsLad` and `ConvertBlockToLad` need an open project and a valid `softwarePath`.
- `ConvertBlockToLad` reads the SCL back with `ExportAsDocuments`, which requires
  **TIA Portal V20 or newer** and a consistent (compiled) source block.

## Ladder is smaller than SCL

Ladder cannot express everything SCL can. The converter never guesses and never silently
drops logic: a statement it cannot draw is reported as an **error diagnostic** and kept as
an empty network whose comment holds the original SCL, ready to be reworked by hand.

**Converted**

| SCL | Ladder |
|-----|--------|
| `q := a AND b;` | contacts in series feeding a coil |
| `q := a OR b;` | contacts in parallel branches |
| `q := NOT a;` | normally closed contact |
| `q := NOT (a AND b);` | `NOT a` in parallel with `NOT b` (De Morgan) |
| `q := a XOR b;` | two parallel branches; both operands appear twice |
| `IF c THEN q := TRUE; END_IF;` | set coil driven by `c` |
| `IF c THEN q := FALSE; END_IF;` | reset coil driven by `c` |
| `IF c THEN ... ELSE ... END_IF;` | the ELSE arm gets the inverted condition |
| `ELSIF` | earlier conditions are inverted and wired in series |
| `CASE x OF 1: ... 2..4: ...` | `x = 1`, and `x >= 2 AND x <= 4`, as compare boxes |
| `x := y;` (non-boolean) | MOVE box |
| `x := a + b;` | ADD box with EN/ENO |
| `"FC_Name"(p := v);` | call box |
| `#instance(IN := x, PT := T#5s);` | FB call box with an instance |
| `REGION Name ... END_REGION` | network titles |
| `// comments` | network titles and comments |

**Not converted** (reported, and preserved as a network comment)

| SCL | Why |
|-----|-----|
| `FOR`, `WHILE`, `REPEAT` | ladder has no loop construct |
| `RETURN`, `EXIT`, `CONTINUE`, `GOTO` | no ladder equivalent |
| `x := a + b * c;` | a nested calculation needs a temporary the converter will not invent |
| `x := f(y) AND z;` | a call cannot sit inside a boolean expression |
| `arr[i + 1]` | a subscript must be a constant or a single tag |
| `%DB1.DBX0.0` | only I/Q/M and periphery areas are encoded; use a symbolic tag |

Split a rejected statement into single operations, or leave that part of the program in SCL
and call it from the ladder block.

## Type inference

Compare and math boxes carry a data type. It is taken from the block interface for `#local`
operands and from the literal for constants. Global tags and DB members have no type in the
SCL text, so when neither side of a comparison resolves, the box falls back to `Int` and a
warning names the comparison. Declare the operands in the block interface, or check the box
type in TIA Portal after import.

The same ambiguity affects assignments: `"A" := "B";` could be a coil or a MOVE. With no
type information the converter draws a coil — the common case in ladder-targeted code — and
warns. Replace it with a MOVE box if the operands are numeric.

## Block calls

`"Name"(...)` is ambiguous in isolation: `Name` may be an FC, or an instance data block of
an FB. `ImportSclAsLad` and `ConvertBlockToLad` resolve it against the open project, so the
call box gets the right block type, block number, parameter sections and parameter types.

`ConvertSclToLad` has no project to look at. It assumes an FC, infers the section from the
argument syntax (`:=` is an input, `=>` is an output), omits parameter types, and warns.
Convert against a project when the SCL calls other blocks.

Positional arguments cannot be mapped to a call box; name every parameter.

## Parameters

### `ConvertSclToLad`

| Parameter | Default | Meaning |
|-----------|---------|---------|
| `sclSource` | `""` | The SCL source. Give exactly one of this or `sclPath`. |
| `sclPath` | `""` | Path to an `.scl` or `.s7dcl` file. |
| `blockName` | from the SCL header | Name of the generated block. |
| `blockType` | from the SCL header | `FC`, `FB` or `OB`. |
| `blockNumber` | `0` | `0` lets TIA Portal assign the number. |
| `exportPath` | `""` | File, or a directory to write `<blockName>.xml` into. |
| `memoryLayout` | `Optimized` | `Optimized` or `Standard`. |
| `includeXml` | `false` | Return the SimaticML in the response. |
| `includePreview` | `true` | Return the ASCII ladder preview. |

### `ImportSclAsLad`

Takes `softwarePath` and `groupPath` (use `""` for the root group) plus the same source and
block parameters. It writes the document to a temporary file and imports it with
`ImportBlock`.

### `ConvertBlockToLad`

Takes `softwarePath` and `blockPath`. `blockName` defaults to the source block name with a
`_LAD` suffix, so the SCL original is left untouched. Set `import` to `true` to place the
result in `groupPath`.

## Response

```json
{
  "message": "Converted 4 statement(s) into 4 LAD network(s)",
  "blockName": "FB_Motor",
  "blockType": "FB",
  "networkCount": 4,
  "convertedStatements": 4,
  "unsupportedStatements": 0,
  "preview": "Network 1: Motor run logic\n  ...",
  "xmlPath": "C:\\export\\FB_Motor.xml",
  "diagnostics": [
    { "severity": "Warning", "code": "SCL2LAD016", "message": "...", "line": 12, "snippet": "..." }
  ]
}
```

`preview` is an ASCII rendering of the same ladder model the XML is written from, so it can
be reviewed without opening TIA Portal:

```
Network 1: Motor run logic
  // SCL: #Running := (#Start OR #Running) AND NOT #Stop;
      #Start      #Stop    #Running
  |+--| |-----+--|/|-------(   )----
   | #Running |
   +--| |-----+
```

## Diagnostic codes

| Code | Severity | Meaning |
|------|----------|---------|
| `SCL2LAD001` | Error | The statement has no ladder equivalent; the SCL was preserved. |
| `SCL2LAD002` | Warning | The source did not parse as expected at this point. |
| `SCL2LAD003` | Error | A `DATA_BLOCK` or `TYPE` declaration was skipped; it is not ladder code. |
| `SCL2LAD005` | Warning | The source declares several blocks; only the first was converted. |
| `SCL2LAD010` | Warning | A call was drawn without a resolved interface. |
| `SCL2LAD011` | Error | A call used positional arguments. |
| `SCL2LAD012` | Warning | The condition is always false, so no network was generated. |
| `SCL2LAD013` | Error | A non-boolean expression was used as a condition. |
| `SCL2LAD014` | Info | XOR was expanded into two branches; operands appear twice. |
| `SCL2LAD015` | Error | A comparison had a calculation on one side. |
| `SCL2LAD016` | Warning | A compare box type had to be guessed. |
| `SCL2LAD017` | Warning | An assignment was drawn as a coil because no type was known. |
| `SCL2LAD018` | Error | An array subscript was a calculation. |
| `SCL2LAD019` | Error | An absolute operand could not be encoded. |
| `SCL2LAD020` | Info | An absolute operand was kept; a symbolic tag would be better. |

## Error model

The tools follow [`../error-model.md`](../error-model.md):

- Missing or contradictory source arguments, and a missing SCL file, map to `InvalidParams`.
- A `PortalException` with `NotFound`, `InvalidParams` or `InvalidState` maps to
  `InvalidParams`; everything else maps to `InternalError`.
- A rejected import maps to `InternalError`, with the advice to write the document out with
  `exportPath` and import it by hand to see the reason TIA Portal reports.

Conversion findings are **not** errors: they are returned in `diagnostics` so a partial
conversion is still usable. Read them before importing.

## Troubleshooting

**TIA Portal rejects the imported block.** Write the document out with `exportPath` and
import it through the TIA Portal UI, which reports the offending element. The elements the
writer emits and the pin names it wires them with are collected in `SimaticMlWriter.cs`
(`ComparePartName`, `CoilPartName`, `MathPartName` and the `Pin...` constants), so a
mismatch with your TIA Portal version can be corrected in one place. Comparing against an
`ExportBlock` of a hand-drawn LAD block from the same version is the quickest way to see
what differs.

**The schema version is wrong.** The document is written for the version the server was
started with (`--tia-major-version`). Check that it matches the project.

**A network is empty.** That is a preserved statement: its comment holds the original SCL
and a diagnostic explains why it could not be drawn.

## Limits

- One SCL statement becomes one network. The converter does not merge statements that share
  a condition, because keeping the mapping one to one makes the result reviewable.
- Only the first block in a source is converted; convert multi-block sources one at a time.
- Block titles, comments and interface comments are written in one culture (`en-US`).
- The ladder layout is left to TIA Portal, which lays networks out on import.

## See also

- [`../error-model.md`](../error-model.md)
- `tests/TiaMcpServer.Test/Test6Conversion.cs` — the conversion tests, none of which need
  TIA Portal.
