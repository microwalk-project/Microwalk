﻿using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CiReportGenerator;
using Microwalk.FrameworkBase.Utilities;

if(args.Length < 5)
{
    Console.WriteLine("Please specify the following parameters:");
    Console.WriteLine("- Call stacks JSON file, e.g. \"/mw/call-stacks.json\"");
    Console.WriteLine("- Identifier for the report, e.g. name of the target");
    Console.WriteLine("- (output) Report file, e.g. \"/mw/report.json\"");
    Console.WriteLine("- <format>");
    Console.WriteLine("- <map mode>");
    Console.WriteLine("");
    Console.WriteLine("Supported output formats:");
    Console.WriteLine("  gitlab-code-quality");
    Console.WriteLine("  sarif");
    Console.WriteLine("");
    Console.WriteLine("Supported map modes:");
    Console.WriteLine("  dwarf");
    Console.WriteLine("  - Directory with DWARF dumps, e.g. \"/mw/dwarf/\"");
    Console.WriteLine("  - Path prefix(es) which all source files share and which should be removed, separated by colons, e.g. \"/mw/src/\"");
    Console.WriteLine("  js-map");
    Console.WriteLine("  - Directory with MAP files generated by the JavascriptTracer plugin, e.g. \"/mw/maps/\"");
    return;
}

int argIndex = 0;
string argCallStacksFile = args[argIndex++];
string argIdentifier = args[argIndex++];
string argReportFile = args[argIndex++];
string argFormat = args[argIndex++];
string argMode = args[argIndex++];


// Read call stack data
await using var callStackStream = File.Open(argCallStacksFile, FileMode.Open, FileAccess.Read);
var callStackData = await JsonSerializer.DeserializeAsync<CallStackData>(callStackStream, new JsonSerializerOptions
{
    AllowTrailingCommas = true
});
if(callStackData == null)
    throw new Exception("Could not deserialize call stack data.");

// Create instruction -> statement mapping
Dictionary<(string imageName, uint instructionOffset), (string fileName, int lineNumber, int columnNumber)> statements = new();
if(argMode == "dwarf")
{
    string argDwarfDirectory = args[argIndex++];
    string[] argUriPrefixes = args[argIndex++].Split(':');

    // Read DWARF files and extract statement info
    foreach(var dwarfFile in Directory.EnumerateFiles(argDwarfDirectory, "*.dwarf"))
    {
        string currentImageName = Path.GetFileNameWithoutExtension(dwarfFile);
        Console.WriteLine($"Reading DWARF dump '{dwarfFile}' as image '{currentImageName}'");

        using var dwarfReader = new StreamReader(File.Open(dwarfFile, FileMode.Open, FileAccess.Read));

        // Skip header
        string? line;
        while((line = await dwarfReader.ReadLineAsync()) != null && !line.StartsWith("<pc>"))
        {
        }

        // Read entries
        Regex lineRegex = new Regex("^0x([0-9a-f]+)\\s+\\[\\s*(\\d+),\\s*(\\d+)\\]\\s*(.*)$");
        string currentFileName = "";
        while((line = await dwarfReader.ReadLineAsync()) != null)
        {
            // Parse entry
            Match match = lineRegex.Match(line);
            if(!match.Success)
                continue;

            uint offset = uint.Parse(match.Groups[1].ValueSpan, NumberStyles.HexNumber);
            int lineNumber = int.Parse(match.Groups[2].ValueSpan);
            int columnNumber = int.Parse(match.Groups[3].ValueSpan);

            // Parse info
            string[] infoParts = match.Groups[4].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for(int i = 0; i < infoParts.Length; ++i)
            {
                switch(infoParts[i])
                {
                    case "ET":
                    {
                        // Skip, the next file may handle this entry
                        continue;
                    }

                    case "uri:":
                    {
                        // Read URI
                        string uri = infoParts[++i];
                        while(uri[^1] != '"')
                            uri += infoParts[i++];

                        // Remove quotation marks
                        uri = uri.Substring(1, uri.Length - 2);

                        // Remove path prefix and relative stuff
                        foreach(var prefix in argUriPrefixes)
                        {
                            if(uri.StartsWith(prefix))
                                uri = uri.Substring(prefix.Length);
                        }

                        uri = uri.Replace("/./", "/");
                        if(uri.StartsWith("./"))
                            uri = uri.Substring(2);
                        if(uri.StartsWith('/'))
                            uri = uri.Substring(1);

                        currentFileName = uri;

                        break;
                    }
                }
            }

            // Record entry, if not yet known
            if(!statements.ContainsKey((currentImageName, offset)))
                statements.Add((currentImageName, offset), (currentFileName, lineNumber, columnNumber));
        }
    }
}
else if(argMode == "js-map")
{
    string argMapsDirectory = args[argIndex++];

    // Parse all MAP files from the given directory
    foreach(var mapFileName in Directory.EnumerateFiles(argMapsDirectory))
    {
        var mapFile = new MapFile(null);
        await mapFile.InitializeFromFileAsync(mapFileName);

        string strippedFileName = mapFile.ImageName;
        if(strippedFileName.StartsWith('/'))
            strippedFileName = strippedFileName.Substring(1);

        foreach(var symbol in mapFile.SymbolNames)
        {
            string[] symbolParts = symbol.Value.Split(':');
            if(symbolParts.Length < 3)
                continue;

            // The last two symbol parts are always line number and column
            statements.Add((mapFile.ImageName, symbol.Key), (strippedFileName, int.Parse(symbolParts[^2]), int.Parse(symbolParts[^1])));
        }
    }
}

// Produce report
object? report = null;
if(argFormat == "gitlab-code-quality")
{
    report = callStackData.ProduceCodeQualityReport(statements, argIdentifier).ToList();
}
else if(argFormat == "sarif")
{
    report = callStackData.ProduceSarifReport(statements, argIdentifier);
}
else
{
    throw new Exception($"Unknown output format: {argFormat}");
}

// Store serialized report
await using var reportStream = File.Open(argReportFile, FileMode.Create, FileAccess.Write);
await JsonSerializer.SerializeAsync(reportStream, report, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
});