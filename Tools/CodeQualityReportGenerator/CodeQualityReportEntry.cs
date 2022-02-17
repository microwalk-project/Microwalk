using System.Text.Json.Serialization;
// ReSharper disable UnusedAutoPropertyAccessor.Global
#pragma warning disable CS8618

namespace CodeQualityReportGenerator;

public class CodeQualityReportEntry
{
    [JsonPropertyName("description")]
    public string Description { get; set; }
    
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; }
    
    [JsonPropertyName("severity")]
    public string Severity { get; set; }

    [JsonPropertyName("location")]
    public CodeQualityReportEntryLocation Location { get; set; }
}

public class CodeQualityReportEntryLocation
{
    [JsonPropertyName("path")]
    public string Path { get; set; }
    
    
    [JsonPropertyName("lines")]
    public CodeQualityReportEntryLocationLines Lines { get; set; }
}

public class CodeQualityReportEntryLocationLines
{
    [JsonPropertyName("begin")]
    public int Begin { get; set; }
}