using CommandLine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LeakageDetector
{
    internal class Program
    {
        /// <summary>
        /// Locking object to avoid interleaved log outputs.
        /// </summary>
        private static object _logLock = new object();

        /// <summary>
        /// Program entry point.
        /// </summary>
        /// <param name="args">The options passed to the program.</param>
        /// <returns></returns>
        private static int Main(string[] args)
        {
            // Parse command line
            return Parser.Default.ParseArguments<DumpOptions, RunOptions>(args).MapResult(
                 (DumpOptions opts) => Dump(opts),
                 (RunOptions opts) => Run(opts),
                 errs => 1
             );
        }

        /// <summary>
        /// Generates a set of unique testcases and simultaneously feeds them into the Pin tool.
        /// </summary>
        /// <param name="opts">The run configuration.</param>
        /// <returns></returns>
        private static int Run(RunOptions opts)
        {
            // Create pipeline and run
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            Pipeline pipeline = new Pipeline(opts.WorkDirectory, opts.ResultDirectory, opts.TraceDirectory, opts.AnalysisMode);
            pipeline.Start(opts.Program, opts.Library, !opts.DisableFuzzing, opts.RandomTestcaseLength, opts.RandomTestcaseAmount, opts.EmulatedCpuModel, opts.RandomizationDetectionMultiplier, opts.KeepTraces, opts.Granularity, opts.RDRAND);
            stopwatch.Stop();

            // Print program run time
            Log($"Total execution time: {stopwatch.Elapsed.Hours}:{stopwatch.Elapsed.Minutes}:{stopwatch.Elapsed.Seconds}.{stopwatch.Elapsed.Milliseconds} (total {stopwatch.Elapsed.TotalMilliseconds}ms)", LogLevel.Info);

            // OK
            Console.ReadLine();


            return 0;
        }

        /// <summary>
        /// Exports a formatted trace.
        /// </summary>
        /// <returns></returns>
        private static int Dump(DumpOptions opts)
        {
            // Load trace file
            Log("Loading trace file...\n", LogLevel.Info);
            List<string> knownImages = new List<string>();

            // Dump trace
            TraceFile tf = new TraceFile(opts.TraceFile, knownImages, 1);
            new TraceDump(opts.MapFileNames).Dump(tf, opts.OutputFile, opts.CallChainTarget == null ? 0 : Convert.ToUInt32(opts.CallChainTarget, 16), opts.RelativeAddresses);

            Log("Trace successfully dumped.\n", LogLevel.Info);
            return 0;
        }

        /// <summary>
        /// Outputs the given message.
        /// </summary>
        /// <param name="message">The message to be output. Line-terminating characters must be added manually!</param>
        /// <param name="logLevel">The log level of the message.</param>
        public static void Log(string message, LogLevel logLevel)
        {
            // Avoid interleaved messages
            lock(_logLock)
            {
                // Save console color
                var oldConsoleColor = Console.ForegroundColor;

                // Set console color depending on log level
                if(logLevel == LogLevel.Debug)
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                else if(logLevel == LogLevel.Success)
                    Console.ForegroundColor = ConsoleColor.Green;
                else if(logLevel == LogLevel.Warning)
                    Console.ForegroundColor = ConsoleColor.Yellow;
                else if(logLevel == LogLevel.Error)
                    Console.ForegroundColor = ConsoleColor.Red;

                // Show message
                Console.Write(message);

                // Restore console color
                Console.ForegroundColor = oldConsoleColor;
            }
        }

        /// <summary>
        /// The different log levels for output.
        /// </summary>
        public enum LogLevel : int
        {
            Debug = 0,
            Info = 1,
            Success = 2,
            Warning = 3,
            Error = 4
        }
    }
}
