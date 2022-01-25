﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microwalk.FrameworkBase.Configuration;
using Microwalk.FrameworkBase.Exceptions;
using YamlDotNet.RepresentationModel;

namespace Microwalk;

/// <summary>
/// Provides functionality for parsing YAML configuration files.
/// </summary>
public class YamlConfigurationParser
{
    /// <summary>
    /// Accumulated root nodes.
    /// </summary>
    public Dictionary<string, Node> RootNodes { get; set; } = new();

    /// <summary>
    /// Accumulated constants.
    /// </summary>
    public Dictionary<string, string> Constants { get; set; } = new();

    private Node TraverseYamlNode(YamlNode currentNode)
    {
        if(currentNode is YamlMappingNode mappingNode)
        {
            Dictionary<string, Node> children = new();
            foreach(var node in mappingNode.Children)
            {
                string key = (node.Key as YamlScalarNode)?.Value ?? throw new ConfigurationException("Could not parse mapping node key.");
                children.Add(key, TraverseYamlNode(node.Value));
            }

            return new MappingNode(children);
        }

        if(currentNode is YamlSequenceNode sequenceNode)
        {
            List<Node> children = new();
            foreach(var node in sequenceNode.Children)
            {
                children.Add(TraverseYamlNode(node));
            }

            return new ListNode(children);
        }

        if(currentNode is YamlScalarNode scalarNode)
        {
            return new ValueNode(scalarNode.Value);
        }

        throw new ConfigurationException($"Unexpected YAML node type: {currentNode.GetType()}");
    }

    private void ParseConfigurationFile(string path)
    {
        // Open file and read YAML
        YamlStream yaml = new();
        using(var configFileStream = new StreamReader(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)))
            yaml.Load(configFileStream);

        // Is there a preprocessor section?
        int dataSectionIndex;
        if(yaml.Documents.Count == 1)
        {
            dataSectionIndex = 0;
        }
        else if(yaml.Documents.Count == 2)
        {
            dataSectionIndex = 1;

            // Parse preprocessor info
            var preprocessorRoot = (YamlMappingNode)yaml.Documents[0].RootNode;
            foreach(var node in preprocessorRoot)
            {
                switch((node.Key as YamlScalarNode)?.Value)
                {
                    case "base-file":
                    {
                        // Load base file
                        string baseFile = (node.Value as YamlScalarNode)?.Value ?? throw new ConfigurationException("Missing base file path.");
                        ParseConfigurationFile(baseFile);

                        break;
                    }

                    case "constants":
                    {
                        // Parse constants
                        var constantsList = node.Value as YamlMappingNode ?? throw new ConfigurationException("The constants must be a key/value mapping.");
                        foreach(var constantNode in constantsList)
                        {
                            string constantName = (constantNode.Key as YamlScalarNode)?.Value ?? throw new ConfigurationException("Could not parse constant name.");
                            string constantValue = (constantNode.Value as YamlScalarNode)?.Value ?? throw new ConfigurationException("Could not parse constant value.");

                            // Apply existing constants to value
                            constantValue = Constants.Aggregate(constantValue, (current, replacement) => current.Replace(replacement.Key, replacement.Value));

                            // Store/update constant
                            Constants[$"$${constantName}$$"] = constantValue;
                        }

                        break;
                    }
                }
            }
        }
        else
            throw new ConfigurationException($"Invalid number of YAML documents in configuration file '{path}'.");

        // Load data section
        foreach(var node in (YamlMappingNode)yaml.Documents[dataSectionIndex].RootNode)
        {
            string key = (node.Key as YamlScalarNode)?.Value ?? throw new ConfigurationException("Could not parse root node key.");
            var parsedNode = TraverseYamlNode(node.Value);

            // TODO We may even support partial configuration keys, which are then automatically merged. Right now we just fail in such cases

            RootNodes.Add(key, parsedNode);
        }
    }

    /// <summary>
    /// Recursively applies constants to the given node and its children.
    /// </summary>
    /// <param name="currentNode">Node.</param>
    private void ApplyConstants(Node currentNode)
    {
        if(currentNode is MappingNode mappingNode)
        {
            foreach(var child in mappingNode.Children)
                ApplyConstants(child.Value);
        }
        else if(currentNode is ListNode listNode)
        {
            foreach(var child in listNode.Children)
                ApplyConstants(child);
        }
        else if(currentNode is ValueNode valueNode)
        {
            valueNode.Value = Constants.Aggregate(valueNode.Value, (current, replacement) => current?.Replace(replacement.Key, replacement.Value));
        }
    }

    /// <summary>
    /// Loads the given YAML configuration file, while recursively resolving base files and preprocessor constants.
    /// </summary>
    /// <param name="path">Configuration file path.</param>
    public void LoadConfigurationFile(string path)
    {
        // Prepare state objects
        RootNodes = new Dictionary<string, Node>();
        Constants = new Dictionary<string, string>
        {
            { "$$CONFIG_PATH$$", Path.GetDirectoryName(path) ?? throw new Exception("Could not resolve configuration directory.")},
            { "$$CONFIG_FILENAME$$", Path.GetFileNameWithoutExtension(path) }
        };

        // Load passed configuration file
        ParseConfigurationFile(path);

        // Apply constants
        foreach(var rootNode in RootNodes)
            ApplyConstants(rootNode.Value);
    }
}