using System;
using System.Collections.Generic;

namespace TiaMcpServer.Conversion;

/// <summary>
/// One parameter of a called block.
/// </summary>
public sealed class BlockParameterInfo
{
    public string Name { get; set; } = string.Empty;

    /// <summary>SimaticML parameter section: Input, Output, InOut or Return.</summary>
    public string Section { get; set; } = "Input";

    /// <summary>SimaticML data type of the parameter.</summary>
    public string DataType { get; set; } = string.Empty;
}

/// <summary>
/// What the converter needs to know about a called block in order to draw a correct call box.
/// </summary>
public sealed class BlockInterfaceInfo
{
    public BlockInterfaceInfo(string name)
    {
        Name = name;
    }

    /// <summary>Name of the called block, or of the instance data block for an FB call.</summary>
    public string Name { get; }

    /// <summary>FC, FB or OB.</summary>
    public string BlockType { get; set; } = "FC";

    public int? Number { get; set; }

    /// <summary>True when <see cref="Name"/> refers to an instance data block rather than to code.</summary>
    public bool IsInstanceDb { get; set; }

    /// <summary>The function block the instance belongs to; set only when <see cref="IsInstanceDb"/> is true.</summary>
    public string? InstanceOfBlock { get; set; }

    public Dictionary<string, BlockParameterInfo> Parameters { get; } =
        new Dictionary<string, BlockParameterInfo>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Supplies interfaces of blocks that the SCL under conversion calls.
/// </summary>
/// <remarks>
/// Without a provider the converter has to guess whether <c>"Foo"(...)</c> is an FC call or an FB
/// instance call, and it cannot know parameter sections or types. The project-aware conversion tools
/// pass an implementation backed by the open TIA Portal project so the call boxes come out exact.
/// </remarks>
public interface IBlockInterfaceProvider
{
    /// <summary>Returns the interface of <paramref name="name"/>, or null when it is unknown.</summary>
    BlockInterfaceInfo? Find(string name);
}
