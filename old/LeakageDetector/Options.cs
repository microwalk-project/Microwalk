
using CommandLine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LeakageDetector
{
    [Verb("dump", HelpText = "Convert preprocessed trace file into human readable string representation.")]
    internal class DumpOptions
    {
        [Value(0, MetaName = "tracefile", HelpText = "The preprocessed trace file to be dumped.", Required = true)]
        public string TraceFile { get; set; }

        [Option('o', "output", Default = null, HelpText = "The output file where the dump shall be stored.", Required = false)]
        public string OutputFile { get; set; }

        [Option('c', "call-trace", Default = null, HelpText = "Address of an instruction (returns all possible call chains for an instruction). ", Required = false)]
        public string CallChainTarget { get; set; }

        [Option('r', "relative-addresses", Default = false, HelpText = "Print relative (not absolute) allocation block access addresses. ", Required = false)]
        public bool RelativeAddresses { get; set; }

        [Option('m', "mapfiles", HelpText = "A list of linker MAP files containing symbol information, separated with semicolon (;).", Separator = ';', Required = false)]
        public IEnumerable<string> MapFileNames { get; set; }
    }

    [Verb("run", HelpText = "Generate testcases, trace and analyze for leakages.")]
    internal class RunOptions
    {
        [Value(0, MetaName = "program", HelpText = "The program to be analyzed, usually this is \"FuzzingWrapper.exe\".", Required = true)]
        public string Program { get; set; }

        [Value(1, MetaName = "library", HelpText = "The name (not path) of the library to be investigated, e.g. \"SampleLibrary.dll\".", Required = true)]
        public string Library { get; set; }

        [Value(2, MetaName = "work-directory", HelpText = "The path to a directory where the intermediate data shall be stored. Must contain a folder /in/ containing initial testcases.", Required = true)]
        public string WorkDirectory { get; set; }

        [Value(3, MetaName = "result-directory", HelpText = "The path to a directory where the result data shall be stored.", Required = true)]
        public string ResultDirectory { get; set; }

        [Option('d', "disable-fuzzing", Default = false, HelpText = "Sets whether the input testcases should be directly retrieved from a pre-generated /testcases/ directory.", Required = false)]
        public bool DisableFuzzing { get; set; }

        [Option('l', "random-testcase-length", Default = -1, HelpText = "The byte length of generated testcases.", Required = false)]
        public int RandomTestcaseLength { get; set; }

        [Option('a', "random-testcase-amount", Default = -1, HelpText = "Overrides the disable-fuzzing parameter, such that random testcases with the given byte length are generated.", Required = false)]
        public int RandomTestcaseAmount { get; set; }

        [Option('t', "trace-directory", Default = null, HelpText = "The directory where trace files shall be loaded from (use if existing traces shall be analyzed).", Required = false)]
        public string TraceDirectory { get; set; }

        [Option('c', "emulate-cpu", Default = 0, HelpText = "Sets the CPU model to be emulated. 0 = Default, 1 = Pentium3, 2 = Merom, 3 = Westmere, 4 = Ivybridge (your own CPU should form a superset of the selected option).", Required = false)]
        public int EmulatedCpuModel { get; set; }

        [Option('r', "detect-randomization-multiplier", Default = 1, HelpText = "Run each testcase for the given amount of times (only works in single instruction mutual information mode).", Required = false)]
        public int RandomizationDetectionMultiplier { get; set; }

        [Option('m', "analysis-mode", Default = 0, HelpText = "Sets the analysis mode. 0 = None (only generate traces, sets -k switch automatically), 1 = Trace comparison, 2 = Mutual information of whole trace, 3 = Mutual information of trace prefixes, 4 = Mutual information of single memory access instructions.")]
        public AnalysisModes AnalysisMode { get; set; }

        [Option('k', "keep-traces", Default = false, HelpText = "Keep preprocessed trace files, not only the testcases used to generate them. WARNING: This might take up a large amount of disk space!", Required = false)]
        public bool KeepTraces { get; set; }

        [Option('g', "memory-granularity", Default = 1u, HelpText = "Specify the granularity of memory access comparisons in bytes, which will be used to align all addresses. This must be a power of 2. Default is 1-byte granularity.", Required = false)]
        public uint Granularity { get; set; }

        [Option('x', "rdrand-emulation", Default = 841534158063459245UL, HelpText = "Specify the constant value to be returned by RDRAND instruction, which will be used to override hardware TRNG. Default is 0xBADBADBADBADBAD, which means no override.", Required = false)]
        public ulong RDRAND { get; set; }
    }
}
