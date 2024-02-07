using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Configuration;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Stages;

namespace Microwalk.TestcaseGeneration.Modules;

[FrameworkModule("command", "Calls an external application to generate test cases.")]
internal class ExternalCommand : TestcaseStage
{
    /// <summary>
    /// The amount of test cases to generate.
    /// </summary>
    private int _testcaseCount;

    /// <summary>
    /// The test case output directory.
    /// </summary>
    private DirectoryInfo _outputDirectory = null!;

    /// <summary>
    /// Path to target executable.
    /// </summary>
    private string _commandFilePath = null!;

    /// <summary>
    /// The command argument format string.
    /// </summary>
    private string _argumentTemplate = null!;

    /// <summary>
    /// The number of the next test case.
    /// </summary>
    private int _nextTestcaseNumber = 0;

    private string FormatCommand(int testcaseId, string testcaseFileName, string testcaseFilePath)
        => string.Format(_argumentTemplate, testcaseId, testcaseFileName, testcaseFilePath);

    public override Task<bool> IsDoneAsync()
    {
        return Task.FromResult(_nextTestcaseNumber >= _testcaseCount);
    }

    public override async Task<TraceEntity> NextTestcaseAsync(CancellationToken token)
    {
        // Format argument string
        string testcaseFileName = $"{_nextTestcaseNumber}.testcase";
        string testcaseFilePath = Path.Combine(_outputDirectory.FullName, testcaseFileName);
        string args = FormatCommand(_nextTestcaseNumber, testcaseFileName, testcaseFilePath);

        // Generate testcase
        ProcessStartInfo processStartInfo = new()
        {
            Arguments = args,
            FileName = _commandFilePath,
            WorkingDirectory = _outputDirectory.FullName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var process = Process.Start(processStartInfo);
        if(process == null)
            throw new Exception("Could not start external command process.");
        await process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(token);

        // Create trace entity object
        var traceEntity = new TraceEntity
        {
            Id = _nextTestcaseNumber,
            TestcaseFilePath = testcaseFilePath
        };

        // Done
        await Logger.LogDebugAsync("Testcase #" + traceEntity.Id);
        ++_nextTestcaseNumber;
        return traceEntity;
    }

    protected override async Task InitAsync(MappingNode? moduleOptions)
    {
        if(moduleOptions == null)
            throw new ConfigurationException("Missing module configuration.");

        // Parse options
        _testcaseCount = moduleOptions.GetChildNodeOrDefault("amount")?.AsInteger() ?? throw new ConfigurationException("Missing testcase count.");
        _commandFilePath = moduleOptions.GetChildNodeOrDefault("exe")?.AsString() ?? throw new ConfigurationException("Missing external command executable.");
        _argumentTemplate = moduleOptions.GetChildNodeOrDefault("args")?.AsString() ?? "";

        // Make sure output directory exists
        var outputDirectoryPath = moduleOptions.GetChildNodeOrDefault("output-directory")?.AsString() ?? throw new ConfigurationException("Missing output directory.");
        _outputDirectory = Directory.CreateDirectory(outputDirectoryPath);

        // Print example command for debugging
        await Logger.LogDebugAsync("Loaded command based testcase generator. Example command: \n> "
                                   + _commandFilePath + " " + FormatCommand(0, "0.testcase", Path.Combine(_outputDirectory.FullName, "0.testcase")));
    }

    public override Task UnInitAsync()
    {
        return Task.CompletedTask;
    }
}