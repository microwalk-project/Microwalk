// ReSharper disable CollectionNeverUpdated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
#pragma warning disable CS8618
namespace CodeQualityReportGenerator;

public class CallStackData
{
    public List<CallStackEntry> CallStack { get; set; }

    public IEnumerable<CodeQualityReportEntry> ProduceReport(Dictionary<(string imageName, uint instructionOffset), (string fileName, int lineNumber, int columnNumber)> statements, string reportIdentifier)
    {
        return CallStack.SelectMany(c => c.ProduceReport("", statements, reportIdentifier));
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

    public IEnumerable<CodeQualityReportEntry> ProduceReport(string formattedCallStack, Dictionary<(string imageName, uint instructionOffset), (string fileName, int lineNumber, int columnNumber)> statements, string reportIdentifier)
    {
        formattedCallStack += $"  {SourceInstructionFormatted} -> {TargetInstructionFormatted}\n";

        // Format leakages for this call stack entry
        foreach(var leakageEntry in LeakageEntries)
        {
            // Find corresponding statement
            // We may have to look at earlier instructions, if a statement spans more than one
            (string fileName, int lineNumber, int columnNumber) statementInfo = ("", 0, 0);
            bool found = false;
            for(uint i = 0; i < 16; ++i)
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
        foreach(var codeQualityReportEntry in Children.SelectMany(c => c.ProduceReport(formattedCallStack, statements, reportIdentifier)))
            yield return codeQualityReportEntry;
    }
}

public class LeakageInfo
{
    public string ImageName { get; set; }
    public uint Offset { get; set; }
    public string Type { get; set; }

    public int NumberOfCalls { get; set; }

    public StatisticsEntry TreeDepth { get; set; }
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