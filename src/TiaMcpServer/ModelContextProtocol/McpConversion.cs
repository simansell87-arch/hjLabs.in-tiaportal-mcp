using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcpServer.Conversion;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol;

/// <summary>
/// MCP tools that turn SCL into ladder (LAD).
/// </summary>
/// <remarks>
/// <para>
/// Conversion runs in three steps: parse the SCL, lower it to a ladder model, then write that model
/// as a SimaticML document. Only the last step needs TIA Portal, and only when the caller asks for
/// the result to be imported, so <c>ConvertSclToLad</c> works with no project open at all.
/// </para>
/// <para>
/// Ladder is a strictly smaller language than SCL. Every statement that has no ladder form is
/// reported as an error diagnostic and preserved as an empty network carrying the original SCL, so a
/// conversion never silently loses logic. Callers should read <c>Diagnostics</c> before importing.
/// </para>
/// </remarks>
[McpServerToolType]
public static class McpConversion
{
    [McpServerTool(Name = "ConvertSclToLad"), Description("Convert SCL source code to a LAD (ladder) block as SimaticML XML. Works without an open project. Reports every construct that has no ladder equivalent.")]
    public static ResponseConvertSclToLad ConvertSclToLad(
        [Description("sclSource: the SCL source to convert. Leave empty when sclPath is given.")] string sclSource = "",
        [Description("sclPath: path to an .scl/.s7dcl file to convert. Leave empty when sclSource is given.")] string sclPath = "",
        [Description("blockName: name of the generated block. Defaults to the name in the SCL block header.")] string blockName = "",
        [Description("blockType: 'FC', 'FB' or 'OB'. Defaults to the kind of the SCL block header.")] string blockType = "",
        [Description("blockNumber: block number, or 0 to let TIA Portal assign one")] int blockNumber = 0,
        [Description("exportPath: file or directory to write the SimaticML XML to. Leave empty to skip writing a file.")] string exportPath = "",
        [Description("memoryLayout: 'Optimized' or 'Standard'")] string memoryLayout = "Optimized",
        [Description("includeXml: return the generated SimaticML in the response. The document can be large.")] bool includeXml = false,
        [Description("includePreview: return an ASCII ladder rendering of the converted networks")] bool includePreview = true)
    {
        try
        {
            var source = ReadSource(sclSource, sclPath);
            var outcome = RunConversion(source, blockName, blockType, blockNumber, memoryLayout, null);

            string? xmlPath = string.IsNullOrWhiteSpace(exportPath)
                ? null
                : WriteXml(outcome, exportPath);

            return BuildResponse(outcome, includeXml, includePreview, xmlPath, null,
                $"Converted {outcome.Result.ConvertedStatements} statement(s) into {outcome.Result.Networks.Count} LAD network(s)");
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException($"Unexpected error converting SCL to LAD: {ex.Message}", ex, McpErrorCode.InternalError);
        }
    }

    [McpServerTool(Name = "ImportSclAsLad"), Description("Convert SCL source code to LAD and import the result into the plc software as a new block. Call interfaces are resolved against the open project.")]
    public static ResponseConvertSclToLad ImportSclAsLad(
        [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
        [Description("groupPath: defines the path in the project structure to the group, where to import the block. Use '' for the root group.")] string groupPath,
        [Description("sclSource: the SCL source to convert. Leave empty when sclPath is given.")] string sclSource = "",
        [Description("sclPath: path to an .scl/.s7dcl file to convert. Leave empty when sclSource is given.")] string sclPath = "",
        [Description("blockName: name of the generated block. Defaults to the name in the SCL block header.")] string blockName = "",
        [Description("blockType: 'FC', 'FB' or 'OB'. Defaults to the kind of the SCL block header.")] string blockType = "",
        [Description("blockNumber: block number, or 0 to let TIA Portal assign one")] int blockNumber = 0,
        [Description("memoryLayout: 'Optimized' or 'Standard'")] string memoryLayout = "Optimized",
        [Description("includeXml: return the generated SimaticML in the response")] bool includeXml = false,
        [Description("includePreview: return an ASCII ladder rendering of the converted networks")] bool includePreview = true)
    {
        PortalBlockInterfaceProvider? provider = null;

        try
        {
            var source = ReadSource(sclSource, sclPath);

            provider = new PortalBlockInterfaceProvider(McpServer.Portal, softwarePath, McpServer.Logger);
            var outcome = RunConversion(source, blockName, blockType, blockNumber, memoryLayout, provider);

            var importedTo = Import(outcome, softwarePath, groupPath);

            return BuildResponse(outcome, includeXml, includePreview, null, importedTo,
                $"Imported LAD block '{outcome.Options.BlockName}' with {outcome.Result.Networks.Count} network(s) into '{importedTo}'");
        }
        catch (PortalException pex)
        {
            throw MapPortalException(pex);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException($"Unexpected error importing SCL as LAD into '{groupPath}': {ex.Message}", ex, McpErrorCode.InternalError);
        }
        finally
        {
            provider?.Dispose();
        }
    }

    [McpServerTool(Name = "ConvertBlockToLad"), Description("Convert an SCL block that is already in the plc software to LAD. Requires TIA Portal V20+ because the SCL is read back with ExportAsDocuments.")]
    public static ResponseConvertSclToLad ConvertBlockToLad(
        [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
        [Description("blockPath: full path to the SCL block, e.g. 'Group/Subgroup/Name' (single names are ambiguous)")] string blockPath,
        [Description("blockName: name of the generated LAD block. Defaults to the source block name with a '_LAD' suffix.")] string blockName = "",
        [Description("import: import the converted block into the plc software")] bool import = false,
        [Description("groupPath: group to import the converted block into when import is true. Use '' for the root group.")] string groupPath = "",
        [Description("exportPath: file or directory to write the SimaticML XML to. Leave empty to skip writing a file.")] string exportPath = "",
        [Description("blockNumber: block number, or 0 to let TIA Portal assign one")] int blockNumber = 0,
        [Description("memoryLayout: 'Optimized' or 'Standard'")] string memoryLayout = "Optimized",
        [Description("includeXml: return the generated SimaticML in the response")] bool includeXml = false,
        [Description("includePreview: return an ASCII ladder rendering of the converted networks")] bool includePreview = true)
    {
        PortalBlockInterfaceProvider? provider = null;
        var workingDirectory = CreateWorkingDirectory();

        try
        {
            var sourceBlockName = blockPath.Contains("/")
                ? blockPath.Substring(blockPath.LastIndexOf("/", StringComparison.Ordinal) + 1)
                : blockPath;

            if (!McpServer.Portal.ExportAsDocuments(softwarePath, blockPath, workingDirectory))
            {
                throw new McpException($"Failed to read the SCL of block '{blockPath}'. ExportAsDocuments requires TIA Portal V20 or newer and a consistent block.", McpErrorCode.InternalError);
            }

            var declarationFile = Path.Combine(workingDirectory, sourceBlockName + ".s7dcl");

            if (!File.Exists(declarationFile))
            {
                throw new McpException($"Block '{blockPath}' was exported but no '{sourceBlockName}.s7dcl' was produced.", McpErrorCode.InternalError);
            }

            var source = File.ReadAllText(declarationFile);

            provider = new PortalBlockInterfaceProvider(McpServer.Portal, softwarePath, McpServer.Logger);

            var targetName = string.IsNullOrWhiteSpace(blockName) ? sourceBlockName + "_LAD" : blockName;
            var outcome = RunConversion(source, targetName, string.Empty, blockNumber, memoryLayout, provider);

            string? xmlPath = string.IsNullOrWhiteSpace(exportPath) ? null : WriteXml(outcome, exportPath);
            string? importedTo = import ? Import(outcome, softwarePath, groupPath) : null;

            var message = importedTo == null
                ? $"Converted block '{blockPath}' into {outcome.Result.Networks.Count} LAD network(s)"
                : $"Converted block '{blockPath}' into LAD block '{targetName}' in '{importedTo}'";

            return BuildResponse(outcome, includeXml, includePreview, xmlPath, importedTo, message);
        }
        catch (PortalException pex)
        {
            throw MapPortalException(pex);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException($"Unexpected error converting block '{blockPath}' to LAD: {ex.Message}", ex, McpErrorCode.InternalError);
        }
        finally
        {
            provider?.Dispose();
            TryDeleteDirectory(workingDirectory);
        }
    }

    #region conversion pipeline

    /// <summary>Everything one conversion produced, kept together so the tools can shape a response from it.</summary>
    private sealed class ConversionOutcome
    {
        public LadConversionResult Result { get; set; } = new LadConversionResult();
        public SimaticMlOptions Options { get; set; } = new SimaticMlOptions();
        public string Xml { get; set; } = string.Empty;
        public List<ConversionDiagnostic> Diagnostics { get; } = new List<ConversionDiagnostic>();
    }

    private static ConversionOutcome RunConversion(
        string source,
        string blockName,
        string blockType,
        int blockNumber,
        string memoryLayout,
        IBlockInterfaceProvider? provider)
    {
        var outcome = new ConversionOutcome();

        var parse = new SclParser(source).Parse();
        outcome.Diagnostics.AddRange(parse.Diagnostics);

        var definition = parse.Blocks.FirstOrDefault() ?? new SclBlockDefinition();

        if (parse.Blocks.Count > 1)
        {
            outcome.Diagnostics.Add(new ConversionDiagnostic(ConversionSeverity.Warning, "SCL2LAD005",
                $"The source declares {parse.Blocks.Count} blocks; only the first one ('{definition.Name}') was converted."));
        }

        var converter = new SclToLadConverter(new SclToLadOptions { InterfaceProvider = provider });
        outcome.Result = converter.Convert(definition);
        outcome.Diagnostics.AddRange(outcome.Result.Diagnostics);

        outcome.Options = new SimaticMlOptions
        {
            BlockName = ResolveBlockName(blockName, definition),
            BlockType = ResolveBlockType(blockType, definition),
            BlockNumber = blockNumber > 0 ? blockNumber : (int?)null,
            MemoryLayout = string.IsNullOrWhiteSpace(memoryLayout) ? "Optimized" : memoryLayout,
            TiaMajorVersion = Engineering.TiaMajorVersion,
            Title = definition.Title,
            HeaderAuthor = definition.Author,
            HeaderFamily = definition.Family,
            HeaderVersion = string.IsNullOrWhiteSpace(definition.Version) ? "0.1" : definition.Version,
            Comment = BuildBlockComment(definition)
        };

        outcome.Xml = new SimaticMlWriter(outcome.Options).Write(outcome.Result);
        return outcome;
    }

    private static string ResolveBlockName(string requested, SclBlockDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested.Trim();
        }

        return string.IsNullOrWhiteSpace(definition.Name) ? "LAD_Block" : definition.Name;
    }

    private static string ResolveBlockType(string requested, SclBlockDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested.Trim().ToUpperInvariant();
        }

        switch (definition.Kind)
        {
            case SclBlockKind.FunctionBlock: return "FB";
            case SclBlockKind.OrganizationBlock: return "OB";
            default: return "FC";
        }
    }

    private static string BuildBlockComment(SclBlockDefinition definition)
    {
        var parts = new List<string> { "Generated from SCL by the TIA Portal MCP server." };

        if (!string.IsNullOrWhiteSpace(definition.Comment))
        {
            parts.Add(definition.Comment!);
        }

        return string.Join(Environment.NewLine, parts.ToArray());
    }

    private static string ReadSource(string sclSource, string sclPath)
    {
        var hasSource = !string.IsNullOrWhiteSpace(sclSource);
        var hasPath = !string.IsNullOrWhiteSpace(sclPath);

        if (hasSource == hasPath)
        {
            throw new McpException("Provide exactly one of 'sclSource' or 'sclPath'.", McpErrorCode.InvalidParams);
        }

        if (hasSource)
        {
            return sclSource;
        }

        if (!File.Exists(sclPath))
        {
            throw new McpException($"SCL file not found: '{sclPath}'", McpErrorCode.InvalidParams);
        }

        return File.ReadAllText(sclPath);
    }

    /// <summary>Writes the SimaticML next to the caller's path, adding the block file name when a directory was given.</summary>
    private static string WriteXml(ConversionOutcome outcome, string exportPath)
    {
        var target = exportPath;

        var looksLikeFile = string.Equals(Path.GetExtension(exportPath), ".xml", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeFile)
        {
            target = Path.Combine(exportPath, outcome.Options.BlockName + ".xml");
        }

        var directory = Path.GetDirectoryName(target);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // TIA Portal expects UTF-8 with a byte order mark, as in its own exports.
        File.WriteAllText(target, outcome.Xml, new UTF8Encoding(true));
        return target;
    }

    private static string Import(ConversionOutcome outcome, string softwarePath, string groupPath)
    {
        var workingDirectory = CreateWorkingDirectory();

        try
        {
            var file = Path.Combine(workingDirectory, outcome.Options.BlockName + ".xml");
            File.WriteAllText(file, outcome.Xml, new UTF8Encoding(true));

            if (!McpServer.Portal.ImportBlock(softwarePath, groupPath, file))
            {
                throw new McpException(
                    $"TIA Portal rejected the generated LAD block '{outcome.Options.BlockName}'. Write it out with 'exportPath' and import it manually to see the reason reported by TIA Portal.",
                    McpErrorCode.InternalError);
            }

            return string.IsNullOrEmpty(groupPath) ? "(root group)" : groupPath;
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    private static string CreateWorkingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TiaMcpServer", "scl2lad", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (Exception ex)
        {
            McpServer.Logger?.LogDebug(ex, "Could not remove the temporary directory {Directory}", directory);
        }
    }

    #endregion

    #region responses

    private static ResponseConvertSclToLad BuildResponse(
        ConversionOutcome outcome,
        bool includeXml,
        bool includePreview,
        string? xmlPath,
        string? importedTo,
        string message)
    {
        var errors = outcome.Diagnostics.Count(d => d.Severity == ConversionSeverity.Error);
        var warnings = outcome.Diagnostics.Count(d => d.Severity == ConversionSeverity.Warning);

        var response = new ResponseConvertSclToLad
        {
            Message = errors > 0
                ? message + $". {errors} statement(s) had no ladder equivalent - review the diagnostics before using the block."
                : message,
            BlockName = outcome.Options.BlockName,
            BlockType = outcome.Options.BlockType,
            NetworkCount = outcome.Result.Networks.Count,
            ConvertedStatements = outcome.Result.ConvertedStatements,
            UnsupportedStatements = outcome.Result.UnsupportedStatements,
            Preview = includePreview ? LadTextRenderer.Render(outcome.Result) : null,
            Xml = includeXml ? outcome.Xml : null,
            XmlPath = xmlPath,
            ImportedTo = importedTo,
            Diagnostics = outcome.Diagnostics.Select(ToResponse).ToList(),
            Meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = true,
                ["networks"] = outcome.Result.Networks.Count,
                ["converted"] = outcome.Result.ConvertedStatements,
                ["unsupported"] = outcome.Result.UnsupportedStatements,
                ["errors"] = errors,
                ["warnings"] = warnings
            }
        };

        return response;
    }

    private static ResponseConversionDiagnostic ToResponse(ConversionDiagnostic diagnostic)
    {
        return new ResponseConversionDiagnostic
        {
            Severity = diagnostic.Severity.ToString(),
            Code = diagnostic.Code,
            Message = diagnostic.Message,
            Line = diagnostic.Line,
            Snippet = diagnostic.Snippet
        };
    }

    private static McpException MapPortalException(PortalException exception)
    {
        switch (exception.Code)
        {
            case PortalErrorCode.NotFound:
                return new McpException(exception.Message, McpErrorCode.InvalidParams);

            case PortalErrorCode.InvalidParams:
            case PortalErrorCode.InvalidState:
                return new McpException(exception.Message, McpErrorCode.InvalidParams);

            default:
                McpServer.Logger?.LogError(exception, "SCL to LAD conversion failed in the portal layer");
                return new McpException(exception.Message, McpErrorCode.InternalError);
        }
    }

    #endregion
}
