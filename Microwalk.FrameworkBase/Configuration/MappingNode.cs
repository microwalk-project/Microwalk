using System.Collections.Generic;

namespace Microwalk.FrameworkBase.Configuration;

public class MappingNode : Node
{
    public MappingNode(Dictionary<string, Node> children)
    {
        Children = children;
    }

    public Dictionary<string, Node> Children { get; }

    /// <summary>
    /// Returns the child node with the given key, if it exists, else null.
    /// </summary>
    /// <param name="key">Key.</param>
    /// <returns></returns>
    public Node? GetChildNodeOrDefault(string key)
    {
        return Children.TryGetValue(key, out var node) ? node : null;
    }
}