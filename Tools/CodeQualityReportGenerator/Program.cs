using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeQualityReportGenerator;

if(args.Length < 4)
{
    Console.WriteLine("Please specify the following parameters:");
    Console.WriteLine("- Call stacks JSON file, e.g. \"/mw/call-stacks.json\"");
    Console.WriteLine("- Directory with DWARF dumps, e.g. \"/mw/dwarf/\"");
    Console.WriteLine("- Path prefix which all source files share and which should be removed, e.g. \"/mw/src/\"");
    Console.WriteLine("- Identifier for the report, e.g. name of the target");
    Console.WriteLine("- (output) Report file, e.g. \"/mw/report.json\"");
    return;
}

// Read call stack data
await using var callStackStream = File.Open(args[0], FileMode.Open, FileAccess.Read);
var callStackData = await JsonSerializer.DeserializeAsync<CallStackData>(callStackStream, new JsonSerializerOptions
{
    AllowTrailingCommas = true
});
if(callStackData == null)
    throw new Exception("Could not deserialize call stack data.");

// Read DWARF files and extract statement info
Dictionary<(string imageName, uint instructionOffset), (string fileName, int lineNumber, int columnNumber)> statements = new();
foreach(var dwarfFile in Directory.EnumerateFiles(args[1], "*.dwarf"))
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
    string pathPrefix = args[2];
    string currentFileName = "";
    while((line = await dwarfReader.ReadLineAsync()) != null)
    {
        // Parse entry
        Match match = lineRegex.Match(line);
        if(!match.Success)
            continue;

        uint offset = uint.Parse(match.Groups[1].ValueSpan, NumberStyles.HexNumber);
        if(statements.ContainsKey((currentImageName, offset)))
            continue;

        int lineNumber = int.Parse(match.Groups[2].ValueSpan);
        int columnNumber = int.Parse(match.Groups[3].ValueSpan);

        // Parse info
        string[] infoParts = match.Groups[4].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for(int i = 0; i < infoParts.Length; ++i)
        {
            switch(infoParts[i])
            {
                case "uri:":
                {
                    // Read URI
                    string uri = infoParts[++i];
                    while(uri[^1] != '"')
                        uri += infoParts[i++];

                    // Remove quotation marks
                    uri = uri.Substring(1, uri.Length - 2);

                    // Remove path prefix
                    if(uri.StartsWith(pathPrefix))
                        uri = uri.Substring(pathPrefix.Length);
                    if(uri.StartsWith('/'))
                        uri = uri.Substring(1);

                    currentFileName = uri;

                    break;
                }
            }
        }

        statements.Add((currentImageName, offset), (currentFileName, lineNumber, columnNumber));
    }
}

// Produce code quality file
var reports = callStackData.ProduceReport(statements, args[3]).ToList();
await using var reportStream = File.Open(args[4], FileMode.Create, FileAccess.Write);
await JsonSerializer.SerializeAsync(reportStream, reports, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
});