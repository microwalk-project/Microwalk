using System;
using System.Globalization;
using Microwalk.FrameworkBase.Exceptions;
using YamlDotNet.RepresentationModel;

namespace Microwalk.FrameworkBase.Extensions
{
    public static class YamlNodeExtensions
    {
        /// <summary>
        /// Returns the child node with the given key, or null.
        /// </summary>
        /// <param name="parent">Parent mapping node.</param>
        /// <param name="childKey">Key of the child node.</param>
        /// <returns></returns>
        public static YamlNode? GetChildNodeWithKey(this YamlNode? parent, string childKey)
        {
            if(parent == null)
                return null;
            if(!(parent is YamlMappingNode parentMapping))
                throw new ConfigurationException("Parent is not a mapping node.");
            return parentMapping.Children.TryGetValue(new YamlScalarNode(childKey), out var childNode) ? childNode : null;
        }

        /// <summary>
        /// Gets the string value of the given scalar node. Error checking is included.
        /// </summary>
        /// <param name="node">Node.</param>
        /// <returns></returns>
        public static string? GetNodeString(this YamlNode node)
        {
            if(!(node is YamlScalarNode scalarNode))
                throw new ConfigurationException("Invalid node type.");
            return scalarNode.Value;
        }

        /// <summary>
        /// Gets the integer value of the given scalar node. Error checking is included. If the node is not present (null), the default value is returned.
        /// </summary>
        /// <param name="node">Node.</param>
        /// <param name="defaultValue">Default value, if node is null.</param>
        /// <returns></returns>
        public static int GetNodeInteger(this YamlNode? node, int defaultValue)
        {
            if(node == null)
                return defaultValue;
            if(!(node is YamlScalarNode scalarNode))
                throw new ConfigurationException("Invalid node type.");
            if(!int.TryParse(scalarNode.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nodeValue)
               && !int.TryParse(scalarNode.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nodeValue))
                throw new ConfigurationException("Invalid node value.");
            return nodeValue;
        }

        /// <summary>
        /// Gets the integer value of the given scalar node. Error checking is included.
        /// </summary>
        /// <param name="node">Node.</param>
        /// <returns></returns>
        public static int GetNodeInteger(this YamlNode node)
        {
            if(node == null)
                throw new ConfigurationException("The given node object is null. Probably it is a mandatory entry that was not specified in the configuration file?");
            if(!(node is YamlScalarNode scalarNode))
                throw new ConfigurationException("Invalid node type.");
            if(!int.TryParse(scalarNode.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nodeValue))
                throw new ConfigurationException("Invalid node value.");
            return nodeValue;
        }

        /// <summary>
        /// Gets the long integer value of the given scalar node (hexadecimal format). Error checking is included.
        /// </summary>
        /// <param name="node">Node.</param>
        /// <returns></returns>
        public static ulong GetNodeUnsignedLongHex(this YamlNode node)
        {
            if(node == null)
                throw new ConfigurationException("The given node object is null. Probably it is a mandatory entry that was not specified in the configuration file?");
            if(!(node is YamlScalarNode scalarNode))
                throw new ConfigurationException("Invalid node type.");
            if(!scalarNode.Value!.StartsWith("0x", StringComparison.InvariantCultureIgnoreCase) || !ulong.TryParse(scalarNode.Value.AsSpan().Slice(2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out ulong nodeValue))
                throw new ConfigurationException("Invalid node value.");
            return nodeValue;
        }

        /// <summary>
        /// Gets the boolean value of the given scalar node. Error checking is included.
        /// </summary>
        /// <param name="node">Node.</param>
        /// <returns></returns>
        public static bool GetNodeBoolean(this YamlNode node)
        {
            if(node == null)
                throw new ConfigurationException("The given node object is null. Probably it is a mandatory entry that was not specified in the configuration file?");
            if(!(node is YamlScalarNode scalarNode))
                throw new ConfigurationException("Invalid node type.");
            if(!bool.TryParse(scalarNode.Value, out bool nodeValue))
                throw new ConfigurationException("Invalid node value.");
            return nodeValue;
        }
    }
}