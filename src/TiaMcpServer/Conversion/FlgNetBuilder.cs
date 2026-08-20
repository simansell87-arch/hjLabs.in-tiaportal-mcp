using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;

namespace TiaMcpServer.Conversion;

/// <summary>
/// Builds the <c>FlgNet</c> element of a single ladder network.
/// </summary>
/// <remarks>
/// <para>
/// A ladder network is an electrical circuit, so the builder models it as a set of nodes. Every pin,
/// every operand and the power rail is attached to a node; connecting two elements merges their
/// nodes. Each node that ends up with more than one endpoint becomes one <c>&lt;Wire&gt;</c>, which is
/// exactly how SimaticML represents a junction: one wire element listing every endpoint on that
/// electrical point.
/// </para>
/// <para>
/// Series elements thread the node from one element's output to the next element's input. Parallel
/// branches all take the same input node and merge their output nodes, which produces the single
/// multi-endpoint wire that TIA Portal draws as a branch.
/// </para>
/// </remarks>
public sealed class FlgNetBuilder
{
    private const int FirstUid = 21;

    private readonly XNamespace _ns;
    private readonly List<XElement> _parts = new List<XElement>();
    private readonly List<List<Endpoint>> _nodes = new List<List<Endpoint>>();
    private readonly List<int> _nodeParent = new List<int>();

    private int _nextUid = FirstUid;

    public FlgNetBuilder(XNamespace flgNetNamespace)
    {
        _ns = flgNetNamespace;
    }

    #region nodes

    /// <summary>One connection point on an electrical node.</summary>
    private struct Endpoint
    {
        public string Kind;
        public int Uid;
        public string? Pin;

        /// <summary>True for endpoints that drive the node (power rail, operand access, output pins).</summary>
        public bool IsSource;
    }

    /// <summary>Creates an unconnected electrical node.</summary>
    public int NewNode()
    {
        _nodes.Add(new List<Endpoint>());
        _nodeParent.Add(_nodeParent.Count);
        return _nodeParent.Count - 1;
    }

    private int FindRoot(int node)
    {
        var root = node;

        while (_nodeParent[root] != root)
        {
            root = _nodeParent[root];
        }

        while (_nodeParent[node] != root)
        {
            var next = _nodeParent[node];
            _nodeParent[node] = root;
            node = next;
        }

        return root;
    }

    /// <summary>Joins two nodes into one electrical point.</summary>
    public void Merge(int first, int second)
    {
        var a = FindRoot(first);
        var b = FindRoot(second);

        if (a == b)
        {
            return;
        }

        _nodeParent[b] = a;
        _nodes[a].AddRange(_nodes[b]);
        _nodes[b].Clear();
    }

    private void Attach(int node, Endpoint endpoint)
    {
        _nodes[FindRoot(node)].Add(endpoint);
    }

    /// <summary>Creates a node that is wired to the left-hand power rail.</summary>
    public int PowerRailNode()
    {
        var node = NewNode();
        Attach(node, new Endpoint { Kind = "Powerrail", IsSource = true });
        return node;
    }

    /// <summary>Attaches a named pin of a part to a node.</summary>
    public void AttachPin(int node, int partUid, string pin, bool isSource)
    {
        Attach(node, new Endpoint { Kind = "NameCon", Uid = partUid, Pin = pin, IsSource = isSource });
    }

    /// <summary>Attaches an operand access to a node.</summary>
    public void AttachAccess(int node, int accessUid)
    {
        Attach(node, new Endpoint { Kind = "IdentCon", Uid = accessUid, IsSource = true });
    }

    /// <summary>Wires an operand access to a pin, creating the node that joins them.</summary>
    public void ConnectOperand(int accessUid, int partUid, string pin, bool accessIsSource)
    {
        var node = NewNode();

        Attach(node, new Endpoint { Kind = "IdentCon", Uid = accessUid, IsSource = accessIsSource });
        Attach(node, new Endpoint { Kind = "NameCon", Uid = partUid, Pin = pin, IsSource = !accessIsSource });
    }

    #endregion

    #region parts

    public int NextUid()
    {
        return _nextUid++;
    }

    /// <summary>Adds a ladder element and returns its UId.</summary>
    public int AddPart(string name, params XObject[] content)
    {
        var uid = NextUid();

        var part = new XElement(_ns + "Part",
            new XAttribute("Name", name),
            new XAttribute("UId", uid.ToString(CultureInfo.InvariantCulture)));

        foreach (var item in content)
        {
            part.Add(item);
        }

        _parts.Add(part);
        return uid;
    }

    /// <summary>
    /// Adds a call box and returns its UId. The factory runs after the call's own UId is taken, so any
    /// UId it allocates for an instance or a nested access sorts after it, as in a TIA Portal export.
    /// </summary>
    public int AddCall(Func<XElement> buildCallInfo)
    {
        var uid = NextUid();

        var call = new XElement(_ns + "Call",
            new XAttribute("UId", uid.ToString(CultureInfo.InvariantCulture)),
            buildCallInfo());

        _parts.Add(call);
        return uid;
    }

    /// <summary>Adds an operand access and returns its UId.</summary>
    public int AddAccess(LadOperand operand)
    {
        var uid = NextUid();

        var access = new XElement(_ns + "Access",
            new XAttribute("Scope", ScopeOf(operand)),
            new XAttribute("UId", uid.ToString(CultureInfo.InvariantCulture)));

        switch (operand.Kind)
        {
            case LadOperandKind.Constant:
                access.Add(new XElement(_ns + "Constant",
                    new XElement(_ns + "ConstantType", operand.ConstantType ?? "Int"),
                    new XElement(_ns + "ConstantValue", operand.ConstantValue ?? string.Empty)));
                break;

            case LadOperandKind.Address:
                {
                    var address = operand.Address ?? new LadAddress();
                    access.Add(new XElement(_ns + "Address",
                        new XAttribute("Area", address.Area),
                        new XAttribute("BitOffset", address.BitOffset.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("Type", address.Type)));
                    break;
                }

            default:
                access.Add(BuildSymbol(operand));
                break;
        }

        _parts.Add(access);
        return uid;
    }

    /// <summary>Builds the <c>Instance</c> element of an FB call box. It carries its own UId but needs no wire.</summary>
    public XElement BuildInstance(LadOperand operand)
    {
        var instance = new XElement(_ns + "Instance",
            new XAttribute("Scope", ScopeOf(operand)),
            new XAttribute("UId", NextUid().ToString(CultureInfo.InvariantCulture)));

        foreach (var component in operand.Components)
        {
            instance.Add(BuildComponent(component));
        }

        return instance;
    }

    private XElement BuildSymbol(LadOperand operand)
    {
        var symbol = new XElement(_ns + "Symbol");

        foreach (var component in operand.Components)
        {
            symbol.Add(BuildComponent(component));
        }

        return symbol;
    }

    private XElement BuildComponent(LadOperandComponent component)
    {
        var element = new XElement(_ns + "Component", new XAttribute("Name", component.Name));

        if (component.Indices.Count == 0)
        {
            return element;
        }

        element.Add(new XAttribute("AccessModifier", "Array"));

        foreach (var index in component.Indices)
        {
            // Subscripts are nested accesses with their own UIds; they are never wired.
            var indexUid = NextUid();

            var access = new XElement(_ns + "Access",
                new XAttribute("Scope", ScopeOf(index)),
                new XAttribute("UId", indexUid.ToString(CultureInfo.InvariantCulture)));

            if (index.Kind == LadOperandKind.Constant)
            {
                access.Add(new XElement(_ns + "Constant",
                    new XElement(_ns + "ConstantType", index.ConstantType ?? "DInt"),
                    new XElement(_ns + "ConstantValue", index.ConstantValue ?? "0")));
            }
            else
            {
                access.Add(BuildSymbol(index));
            }

            element.Add(access);
        }

        return element;
    }

    private static string ScopeOf(LadOperand operand)
    {
        switch (operand.Kind)
        {
            case LadOperandKind.Local: return "LocalVariable";
            case LadOperandKind.Constant: return "LiteralConstant";
            case LadOperandKind.Address: return "Address";
            default: return "GlobalVariable";
        }
    }

    #endregion

    /// <summary>
    /// Produces the finished <c>FlgNet</c>. Nodes with a single endpoint are left unwired, which is how
    /// an unused ENO output is represented.
    /// </summary>
    public XElement ToXml()
    {
        var parts = new XElement(_ns + "Parts");

        foreach (var part in _parts)
        {
            parts.Add(part);
        }

        var wires = new XElement(_ns + "Wires");

        for (var i = 0; i < _nodes.Count; i++)
        {
            if (FindRoot(i) != i || _nodes[i].Count < 2)
            {
                continue;
            }

            var wire = new XElement(_ns + "Wire",
                new XAttribute("UId", NextUid().ToString(CultureInfo.InvariantCulture)));

            foreach (var endpoint in OrderEndpoints(_nodes[i]))
            {
                wire.Add(BuildEndpoint(endpoint));
            }

            wires.Add(wire);
        }

        return new XElement(_ns + "FlgNet", parts, wires);
    }

    /// <summary>Sources first, mirroring how TIA Portal writes a wire: what drives the node, then what it feeds.</summary>
    private static List<Endpoint> OrderEndpoints(List<Endpoint> endpoints)
    {
        var ordered = new List<Endpoint>();

        foreach (var endpoint in endpoints)
        {
            if (endpoint.IsSource)
            {
                ordered.Add(endpoint);
            }
        }

        foreach (var endpoint in endpoints)
        {
            if (!endpoint.IsSource)
            {
                ordered.Add(endpoint);
            }
        }

        return ordered;
    }

    private XElement BuildEndpoint(Endpoint endpoint)
    {
        switch (endpoint.Kind)
        {
            case "Powerrail":
                return new XElement(_ns + "Powerrail");

            case "IdentCon":
                return new XElement(_ns + "IdentCon",
                    new XAttribute("UId", endpoint.Uid.ToString(CultureInfo.InvariantCulture)));

            default:
                return new XElement(_ns + "NameCon",
                    new XAttribute("UId", endpoint.Uid.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Name", endpoint.Pin ?? string.Empty));
        }
    }
}
