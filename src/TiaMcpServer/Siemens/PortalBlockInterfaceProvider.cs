using Microsoft.Extensions.Logging;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcpServer.Conversion;

namespace TiaMcpServer.Siemens;

/// <summary>
/// Resolves interfaces of called blocks from the open TIA Portal project.
/// </summary>
/// <remarks>
/// The interface is read back from a block export rather than from the Openness object model: an
/// export is the same XML the converted block will be imported as, so parameter names, sections and
/// data types line up exactly with what TIA Portal expects in a call box.
/// Results are cached per instance, because a single conversion usually calls the same few blocks.
/// </remarks>
public sealed class PortalBlockInterfaceProvider : IBlockInterfaceProvider, IDisposable
{
    /// <summary>Interface sections that appear on a call box; Static, Temp and Constant never do.</summary>
    private static readonly string[] ParameterSections = { "Input", "Output", "InOut", "Return" };

    private readonly Portal _portal;
    private readonly string _softwarePath;
    private readonly ILogger? _logger;
    private readonly string _workingDirectory;
    private readonly Dictionary<string, BlockInterfaceInfo?> _cache =
        new Dictionary<string, BlockInterfaceInfo?>(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public PortalBlockInterfaceProvider(Portal portal, string softwarePath, ILogger? logger = null)
    {
        _portal = portal;
        _softwarePath = softwarePath;
        _logger = logger;
        _workingDirectory = Path.Combine(Path.GetTempPath(), "TiaMcpServer", "scl2lad", Guid.NewGuid().ToString("N"));
    }

    public BlockInterfaceInfo? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (_cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        BlockInterfaceInfo? info = null;

        try
        {
            info = Resolve(name);
        }
        catch (Exception ex)
        {
            // A block we cannot resolve just falls back to a best-effort call box.
            _logger?.LogDebug(ex, "Could not resolve the interface of block {BlockName}", name);
        }

        _cache[name] = info;
        return info;
    }

    private BlockInterfaceInfo? Resolve(string name)
    {
        var block = FindBlock(name);

        if (block == null)
        {
            return null;
        }

        var info = new BlockInterfaceInfo(block.Name) { Number = block.Number };

        if (block is InstanceDB instanceDb)
        {
            info.IsInstanceDb = true;
            info.BlockType = "FB";
            info.InstanceOfBlock = instanceDb.InstanceOfName;

            // The parameters of an instance call are the interface of the function block behind it.
            var functionBlock = string.IsNullOrEmpty(instanceDb.InstanceOfName) ? null : FindBlock(instanceDb.InstanceOfName);

            if (functionBlock != null)
            {
                info.Number = functionBlock.Number;
                ReadParameters(functionBlock, info);
            }

            return info;
        }

        if (block is FB)
        {
            info.BlockType = "FB";
        }
        else if (block is OB)
        {
            info.BlockType = "OB";
        }
        else if (block is FC)
        {
            info.BlockType = "FC";
        }
        else
        {
            // Global data blocks are operands, not callable code.
            return null;
        }

        ReadParameters(block, info);
        return info;
    }

    private PlcBlock? FindBlock(string name)
    {
        var blocks = _portal.GetBlocks(_softwarePath, "^" + Regex.Escape(name) + "$");

        if (blocks == null || blocks.Count == 0)
        {
            return null;
        }

        return blocks.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private void ReadParameters(PlcBlock block, BlockInterfaceInfo info)
    {
        if (!block.IsConsistent)
        {
            _logger?.LogDebug("Block {BlockName} is inconsistent, so its interface cannot be exported", block.Name);
            return;
        }

        if (!Directory.Exists(_workingDirectory))
        {
            Directory.CreateDirectory(_workingDirectory);
        }

        var blockPath = _portal.GetBlockPath(block);
        _portal.ExportBlock(_softwarePath, blockPath, _workingDirectory);

        var file = Path.Combine(_workingDirectory, block.Name + ".xml");

        if (!File.Exists(file))
        {
            return;
        }

        var document = XDocument.Load(file);

        foreach (var section in document.Descendants().Where(e => e.Name.LocalName == "Section"))
        {
            var sectionName = (string?)section.Attribute("Name");

            if (sectionName == null || Array.IndexOf(ParameterSections, sectionName) < 0)
            {
                continue;
            }

            foreach (var member in section.Elements().Where(e => e.Name.LocalName == "Member"))
            {
                var memberName = (string?)member.Attribute("Name");
                var dataType = (string?)member.Attribute("Datatype");

                if (string.IsNullOrEmpty(memberName))
                {
                    continue;
                }

                info.Parameters[memberName!] = new BlockParameterInfo
                {
                    Name = memberName!,
                    Section = sectionName,
                    DataType = dataType ?? string.Empty
                };
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (Directory.Exists(_workingDirectory))
            {
                Directory.Delete(_workingDirectory, true);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not remove the temporary export directory {Directory}", _workingDirectory);
        }
    }
}
