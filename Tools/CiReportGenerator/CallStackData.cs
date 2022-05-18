// ReSharper disable CollectionNeverUpdated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable ClassNeverInstantiated.Global

using System.Reflection;

#pragma warning disable CS8618

namespace CiReportGenerator;

public class CallStackData
{
    public List<CallStackEntry> CallStack { get; set; }

    public IEnumerable<CodeQualityReportEntry> ProduceCodeQualityReport(Dictionary<(string imageName, uint instructionOffset), (string fileName, int lineNumber, int columnNumber)> statements, string reportIdentifier)
    {
        return CallStack.SelectMany(c => c.ProduceCodeQualityReportEntries("", statements, reportIdentifier));
    }

    public SarifReport ProduceSarifReport(Dictionary<(string imageName, uint instructionOffset), (string fileName, int lineNumber, int columnNumber)> statements, string reportIdentifier)
    {
        // Get assembly version
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? throw new Exception("Could not determine application version.");

        // Prepare report object
        var report = new SarifReport
        {
            Runs = new List<SarifReportRun>
            {
                new()
                {
                    Tool = new SarifReportRunTool
                    {
                        Driver = new SarifReportToolComponent
                        {
                            Name = "Microwalk",
                            Version = $"{version.Major}.{version.Minor}.{version.Build}",
                            SemanticVersion = $"{version.Major}.{version.Minor}.{version.Build}",
                            Rules = new List<SarifReportReportingDescriptor>
                            {
                                new()
                                {
                                    Id = "branch-leakage",
                                    Name = "BranchLeakage",
                                    ShortDescription = new SarifReportReportingDescriptorDescription { Text = "Secret-dependent branch" },
                                    FullDescription = new SarifReportReportingDescriptorDescription { Text = "This line branches depending on a secret input value. An attacker may monitor the branch targets of this line and learn something about the secret." },
                                    Help = new SarifReportReportingDescriptorHelp
                                    {
                                        Text = "TODO", // TODO
                                        Markdown = "TODO"
                                    },
                                    Properties = new SarifReportReportingDescriptorProperties
                                    {
                                        Tags = new List<string> { "leakage-analysis", "branch-leakage" },
                                        Precision = "high",
                                        ProblemSeverity = "warning",
                                        SecuritySeverity = null
                                    }
                                },
                                new()
                                {
                                    Id = "memory-access-leakage",
                                    Name = "MemoryAccessLeakage",
                                    ShortDescription = new SarifReportReportingDescriptorDescription { Text = "Secret-dependent memory access" },
                                    FullDescription = new SarifReportReportingDescriptorDescription { Text = "This line accesses a memory location at a secret-dependent address or index. An attacker may monitor the addresses of memory accesses and learn something about the secret." },
                                    Help = new SarifReportReportingDescriptorHelp
                                    {
                                        Text = "TODO", // TODO
                                        Markdown = "TODO"
                                    },
                                    Properties = new SarifReportReportingDescriptorProperties
                                    {
                                        Tags = new List<string> { "leakage-analysis", "memory-access-leakage" },
                                        Precision = "high",
                                        ProblemSeverity = "warning",
                                        SecuritySeverity = null
                                    }
                                }
                            }
                        },
                        Extensions = new List<SarifReportToolComponent>()
                    }
                }
            }
        };

        // Generate results
        report.Runs[0].Results = new List<SarifReportResult>(CallStack.SelectMany(c => c.ProduceSarifReportEntries("", statements)));

        return report;
    }
}

public class CallStackEntry
{
    public string SourceInstructionImageName { get; set; }
    public uint SourceInstructionOffset { get; set; }
    public string SourceInstructionFormatted { get; set; }

    public string TargetInstructionImageName { get; set; }
    public uint TargetInstructionOffset { get; set; }
    public string TargetInstructionFormatted { get; set; }

    public string CallStackId { get; set; }

    public List<LeakageInfo> LeakageEntries { get; set; }

    public List<CallStackEntry> Children { get; set; }

    public IEnumerable<CodeQualityReportEntry> ProduceCodeQualityReportEntries(string formattedCallStack, Dictionary<(string imageName, uint instructionOffset), (string fileName, int lineNumber, int columnNumber)> statements, string reportIdentifier)
    {
        formattedCallStack += $"  {SourceInstructionFormatted} -> {TargetInstructionFormatted}\n";

        // Format leakages for this call stack entry
        foreach(var leakageEntry in LeakageEntries)
        {
            // Find corresponding statement
            // We may have to look at earlier instructions, if a statement spans more than one
            (string fileName, int lineNumber, int columnNumber) statementInfo = ("", 0, 0);
            bool found = false;
            for(uint i = 0; i < 4096; ++i)
            {
                if(statements.TryGetValue((leakageEntry.ImageName, leakageEntry.Offset - i), out statementInfo))
                {
                    found = true;
                    break;
                }
            }

            if(!found)
            {
                Console.WriteLine($"Warning: Could not find instruction info for leakage entry {leakageEntry.ImageName}+{leakageEntry.Offset:x} ({leakageEntry.Type})\n{formattedCallStack}");
                continue;
            }

            string severity = "minor";
            if(leakageEntry.MinimumConditionalGuessingEntropy.Score!.Value > 20)
                severity = "major";
            if(leakageEntry.MinimumConditionalGuessingEntropy.Score!.Value > 80)
                severity = "critical";

            var reportEntry = new CodeQualityReportEntry
            {
                Description = $"({reportIdentifier}) Found vulnerable {leakageEntry.Type} instruction, leakage score {leakageEntry.MinimumConditionalGuessingEntropy.Score:F2}% +/- {leakageEntry.MinimumConditionalGuessingEntropy.ScoreStandardDeviation}%. Check analysis result in artifacts for details.",
                Severity = severity,
                Fingerprint = $"{CallStackId}-{leakageEntry.ImageName}-{leakageEntry.Offset:x}",
                Location = new CodeQualityReportEntryLocation
                {
                    Path = statementInfo.fileName,
                    Lines = new CodeQualityReportEntryLocationLines
                    {
                        Begin = statementInfo.lineNumber
                    }
                }
            };

            yield return reportEntry;
        }

        // Format children
        foreach(var codeQualityReportEntry in Children.SelectMany(c => c.ProduceCodeQualityReportEntries(formattedCallStack, statements, reportIdentifier)))
            yield return codeQualityReportEntry;
    }

    public IEnumerable<SarifReportResult> ProduceSarifReportEntries(string formattedCallStack, Dictionary<(string imageName, uint instructionOffset), (string fileName, int lineNumber, int columnNumber)> statements)
    {
        formattedCallStack += $"  {SourceInstructionFormatted} -> {TargetInstructionFormatted}\n";

        // Format leakages for this call stack entry
        foreach(var leakageEntry in LeakageEntries)
        {
            // Find corresponding statement
            // We may have to look at earlier instructions, if a statement spans more than one
            (string fileName, int lineNumber, int columnNumber) statementInfo = ("", 0, 0);
            bool found = false;
            for(uint i = 0; i < 4096; ++i)
            {
                if(statements.TryGetValue((leakageEntry.ImageName, leakageEntry.Offset - i), out statementInfo))
                {
                    found = true;
                    break;
                }
            }

            if(!found)
            {
                Console.WriteLine($"Warning: Could not find instruction info for leakage entry {leakageEntry.ImageName}+{leakageEntry.Offset:x} ({leakageEntry.Type})\n{formattedCallStack}");
                continue;
            }

            string severity = "warning";
            if(leakageEntry.MinimumConditionalGuessingEntropy.Score!.Value > 80)
                severity = "error";

            var resultEntry = new SarifReportResult
            {
                RuleId = leakageEntry.Type switch
                {
                    "memory access" => "memory-access-leakage",
                    "jump" => "branch-leakage",
                    "call" => "branch-leakage",
                    "return" => "branch-leakage",
                    _ => throw new Exception("Unknown leakage type.")
                },
                Level = severity,
                Message = new SarifReportMessage
                {
                    Text = $"Found vulnerable {leakageEntry.Type} instruction, leakage score {leakageEntry.MinimumConditionalGuessingEntropy.Score:F2}% +/- {leakageEntry.MinimumConditionalGuessingEntropy.ScoreStandardDeviation}%."
                },
                Locations = new List<SarifReportLocation>
                {
                    new()
                    {
                        PhysicalLocation = new SarifReportPhysicalLocation
                        {
                            ArtifactLocation = new SarifReportPhysicalLocationArtifactLocation
                            {
                                Uri = statementInfo.fileName
                            },
                            Region = new SarifReportPhysicalLocationRegion
                            {
                                StartLine = statementInfo.lineNumber,
                                StartColumn = statementInfo.columnNumber,
                                EndLine = statementInfo.lineNumber,
                                EndColumn = statementInfo.columnNumber
                            }
                        },
                        Message = null
                    }
                },
                PartialFingerprints = new Dictionary<string, string>
                {
                    ["primaryLocationLineHash"] = $"{CallStackId}-{leakageEntry.ImageName}-{leakageEntry.Offset:x}"
                }
                // TODO codeFlows[].threadFlows[].locations[] for call stack
            };

            yield return resultEntry;
        }

        // Format children
        foreach(var sarifResultEntry in Children.SelectMany(c => c.ProduceSarifReportEntries(formattedCallStack, statements)))
            yield return sarifResultEntry;
    }
}

public class LeakageInfo
{
    public string ImageName { get; set; }
    public uint Offset { get; set; }
    public string Type { get; set; }

    public int NumberOfCalls { get; set; }

    public StatisticsEntry TreeDepth { get; set; }
    public StatisticsEntry MutualInformation { get; set; }
    public StatisticsEntry ConditionalGuessingEntropy { get; set; }
    public StatisticsEntry MinimumConditionalGuessingEntropy { get; set; }
}

public class StatisticsEntry
{
    public double Mean { get; set; }
    public double StandardDeviation { get; set; }

    public double Minimum { get; set; }
    public double Maximum { get; set; }

    public double? Score { get; set; }
    public double? ScoreStandardDeviation { get; set; }
}