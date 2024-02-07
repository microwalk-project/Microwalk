using System;
using System.Globalization;
using Microwalk.FrameworkBase.Exceptions;

namespace Microwalk.FrameworkBase.Configuration;

/// <summary>
/// Generic configuration node.
/// </summary>
public abstract class Node
{
    /// <summary>
    /// Returns the string value of a value node.
    /// This method asserts that this object is an instance of <see cref="ValueNode"/>.
    /// </summary>
    public string? AsString()
    {
        if(this is not ValueNode scalarNode)
            throw new ConfigurationException("Invalid node type.");

        return scalarNode.Value;
    }

    /// <summary>
    /// Parses a value node as signed 32-bit integer.
    /// This method asserts that this object is an instance of <see cref="ValueNode"/>.
    /// </summary>
    public int AsInteger()
    {
        if(this is not ValueNode scalarNode)
            throw new ConfigurationException("Invalid node type.");

        if(scalarNode.Value == null)
            throw new ConfigurationException("Value of integer node is null.");

        if(!int.TryParse(scalarNode.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nodeValue)
           && !int.TryParse(scalarNode.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nodeValue))
            throw new ConfigurationException("Invalid node value.");

        return nodeValue;
    }

    /// <summary>
    /// Parses a value node as unsigned 64-bit hex string.
    /// This method asserts that this object is an instance of <see cref="ValueNode"/>.
    /// </summary>
    public ulong AsUnsignedLongHex()
    {
        if(this is not ValueNode scalarNode)
            throw new ConfigurationException("Invalid node type.");

        if(scalarNode.Value == null)
            throw new ConfigurationException("Value of unsigned hex node is null.");

        if(!scalarNode.Value.StartsWith("0x", StringComparison.InvariantCultureIgnoreCase) || !ulong.TryParse(scalarNode.Value.AsSpan()[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong nodeValue))
            throw new ConfigurationException("Invalid node value.");

        return nodeValue;
    }

    /// <summary>
    /// Parses a value node as boolean.
    /// This method asserts that this object is an instance of <see cref="ValueNode"/>.
    /// </summary>
    public bool AsBoolean()
    {
        if(this is not ValueNode scalarNode)
            throw new ConfigurationException("Invalid node type.");

        if(scalarNode.Value == null)
            throw new ConfigurationException("Value of boolean node is null.");

        if(!bool.TryParse(scalarNode.Value, out bool nodeValue))
            throw new ConfigurationException("Invalid node value.");

        return nodeValue;
    }
}