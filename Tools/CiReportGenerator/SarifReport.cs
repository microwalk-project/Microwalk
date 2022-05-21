using System.Text.Json.Serialization;

// ReSharper disable UnusedAutoPropertyAccessor.Global
#pragma warning disable CS8618

namespace CiReportGenerator;

public class SarifReport
{
    [JsonPropertyName("version")]
    public string Version => "2.1.0";

    [JsonPropertyName("$schema")]
    public string Schema => "https://json.schemastore.org/sarif-2.1.0.json";

    [JsonPropertyName("runs")]
    public List<SarifReportRun> Runs { get; set; }
}

public class SarifReportRun
{
    [JsonPropertyName("tool")]
    public SarifReportRunTool Tool { get; set; }

    [JsonPropertyName("results")]
    public List<SarifReportResult> Results { get; set; }
}

public class SarifReportRunTool
{
    [JsonPropertyName("driver")]
    public SarifReportToolComponent Driver { get; set; }

    [JsonPropertyName("extensions")]
    public List<SarifReportToolComponent> Extensions { get; set; }
}

public class SarifReportToolComponent
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("semanticVersion")]
    public string SemanticVersion { get; set; }

    [JsonPropertyName("rules")]
    public List<SarifReportReportingDescriptor> Rules { get; set; }
}

public class SarifReportReportingDescriptor
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("shortDescription")]
    public SarifReportReportingDescriptorDescription ShortDescription { get; set; }

    [JsonPropertyName("fullDescription")]
    public SarifReportReportingDescriptorDescription FullDescription { get; set; }

    [JsonPropertyName("help")]
    public SarifReportReportingDescriptorHelp Help { get; set; }

    [JsonPropertyName("properties")]
    public SarifReportReportingDescriptorProperties Properties { get; set; }
}

public class SarifReportReportingDescriptorDescription
{
    [JsonPropertyName("text")]
    public string Text { get; set; }
}

public class SarifReportReportingDescriptorHelp
{
    [JsonPropertyName("text")]
    public string Text { get; set; }

    [JsonPropertyName("markdown")]
    public string Markdown { get; set; }
}

public class SarifReportReportingDescriptorProperties
{
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; }

    [JsonPropertyName("precision")]
    public string Precision { get; set; }

    [JsonPropertyName("problem.severity")]
    public string ProblemSeverity { get; set; }

    [JsonPropertyName("security-severity")]
    public string? SecuritySeverity { get; set; }
}

public class SarifReportResult
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; }

    [JsonPropertyName("message")]
    public SarifReportMessage Message { get; set; }

    [JsonPropertyName("locations")]
    public List<SarifReportLocation> Locations { get; set; }

    [JsonPropertyName("partialFingerprints")]
    public Dictionary<string, string> PartialFingerprints { get; set; }

    [JsonPropertyName("codeFlows")]
    public List<SarifCodeFlow> CodeFlows { get; set; }
}

public class SarifReportLocation
{
    [JsonPropertyName("physicalLocation")]
    public SarifReportPhysicalLocation PhysicalLocation { get; set; }

    [JsonPropertyName("message")]
    public SarifReportMessage? Message { get; set; }
}

public class SarifReportPhysicalLocation
{
    [JsonPropertyName("artifactLocation")]
    public SarifReportPhysicalLocationArtifactLocation ArtifactLocation { get; set; }

    [JsonPropertyName("region")]
    public SarifReportPhysicalLocationRegion Region { get; set; }
}

public class SarifReportPhysicalLocationArtifactLocation
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; }
}

public class SarifReportPhysicalLocationRegion
{
    [JsonPropertyName("startLine")]
    public int StartLine { get; set; }

    [JsonPropertyName("startColumn")]
    public int StartColumn { get; set; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }

    [JsonPropertyName("endColumn")]
    public int EndColumn { get; set; }
}

public class SarifReportMessage
{
    [JsonPropertyName("text")]
    public string Text { get; set; }
}

public class SarifCodeFlow
{
    [JsonPropertyName("threadFlows")]
    public List<SarifThreadFlow> ThreadFlows { get; set; }
}

public class SarifThreadFlow
{
    [JsonPropertyName("locations")]
    public List<SarifThreadFlowLocation> Locations { get; set; }
}

public class SarifThreadFlowLocation
{
    [JsonPropertyName("location")]
    public SarifReportLocation Location { get; set; }
}