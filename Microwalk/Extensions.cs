using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk
{
    internal static class Extensions
    {
        /// <summary>
        /// Returns the child node with the given key, or null.
        /// </summary>
        /// <param name="parent">Parent mapping node.</param>
        /// <param name="childKey">Key of the child node.</param>
        /// <returns></returns>
        public static YamlNode GetChildNodeWithKey(this YamlNode parent, string childKey)
        {
            if(!(parent is YamlMappingNode parentMapping))
                throw new ConfigurationException("Parent is not a mapping node.");
            if(parentMapping.Children.TryGetValue(new YamlScalarNode(childKey), out var childNode))
                return childNode;
            return null;
        }

        /// <summary>
        /// Gets the string value of the given scalar node. Error checking is included.
        /// </summary>
        /// <param name="node">Node.</param>
        /// <returns></returns>
        public static string GetNodeString(this YamlNode node)
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
        public static int GetNodeInteger(this YamlNode node, int defaultValue)
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
            if(!scalarNode.Value.StartsWith("0x", StringComparison.InvariantCultureIgnoreCase) || !ulong.TryParse(scalarNode.Value.AsSpan().Slice(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong nodeValue))
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

        /// <summary>
        /// Adds the given range of elements to the given collection.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="destination">The target collection.</param>
        /// <param name="source">The items to add.</param>
        public static void AddRange<T>(this ICollection<T> destination, IEnumerable<T> source)
        {
            foreach(T item in source)
                destination.Add(item);
        }

        /// <summary>
        /// Waits asynchronously for the process to exit.
        /// </summary>
        /// <param name="process">The process to wait for cancellation.</param>
        /// <returns></returns>
        public static Task WaitForExitAsync(this Process process)
        {
            var tcs = new TaskCompletionSource<object>();
            process.EnableRaisingEvents = true;
            process.Exited += (sender, args) => tcs.SetResult(null);
            return tcs.Task;
        }
    }
}
