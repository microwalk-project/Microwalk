using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using CommandLine;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Configuration;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Stages;

namespace Microwalk
{
    /// <summary>
    /// Main class. Contains initialization and pipeline logic.
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// Contains information about the configured pipeline stage modules.
        /// </summary>
        private static readonly ModuleConfigurationData _moduleConfiguration = new();

        /// <summary>
        /// Logger instance.
        /// </summary>
        private static ILogger? _logger;

        /// <summary>
        /// Program entry point.
        /// </summary>
        /// <param name="args">Command line.</param>
        public static void Main(string[] args)
        {
            // Force default culture to avoid problems with formatting decimal numbers
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            // Register generic modules
            {
                // Testcase generation
                TestcaseStage.Factory.Register<TestcaseGeneration.Modules.TestcaseLoader>();
                TestcaseStage.Factory.Register<TestcaseGeneration.Modules.RandomTestcaseGenerator>();
                TestcaseStage.Factory.Register<TestcaseGeneration.Modules.ExternalCommand>();

                // Trace generation
                TraceStage.Factory.Register<TraceGeneration.Modules.TraceLoader>();
                TraceStage.Factory.Register<TraceGeneration.Modules.Passthrough>();

                // Trace preprocessing
                PreprocessorStage.Factory.Register<TracePreprocessing.Modules.Passthrough>();
                PreprocessorStage.Factory.Register<TracePreprocessing.Modules.PreprocessedTraceLoader>();

                // Analysis
                AnalysisStage.Factory.Register<Analysis.Modules.TraceDumper>();
                AnalysisStage.Factory.Register<Analysis.Modules.InstructionMemoryAccessTraceLeakage>();
                AnalysisStage.Factory.Register<Analysis.Modules.CallStackMemoryAccessTraceLeakage>();
                AnalysisStage.Factory.Register<Analysis.Modules.ControlFlowLeakage>();
                AnalysisStage.Factory.Register<Analysis.Modules.Passthrough>();
            }

            // Parse command line and execute framework using these options
            var parser = new Parser(options =>
            {
                options.AutoHelp = true;
                options.AutoVersion = false;
                options.CaseSensitive = true;
                options.HelpWriter = Console.Error;
                options.AllowMultiInstance = true;
            });
            parser.ParseArguments<CommandLineOptions>(args).WithParsed(
                opts => RunAsync(opts)
                    .ContinueWith(t =>
                    {
                        if(t.IsFaulted && t.Exception != null)
                            Console.WriteLine(t.Exception.ToString());
                    })
                    .Wait()
            );
        }

        /// <summary>
        /// Executes the framework using the given command line options.
        /// </summary>
        /// <param name="commandLineOptions">Command line options.</param>
        private static async Task RunAsync(CommandLineOptions commandLineOptions)
        {
            // Register plugins, if there are any
            foreach(var pluginDir in (commandLineOptions.PluginDirectories ?? Enumerable.Empty<string>()).Append(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)))
            {
                if(string.IsNullOrWhiteSpace(pluginDir) || !Directory.Exists(pluginDir))
                    Console.WriteLine($"Could not find plugin directory '{pluginDir}'.");
                else
                {
                    var currentAssemblyName = Assembly.GetExecutingAssembly().GetName();
                    foreach(var pluginPath in Directory.EnumerateFiles(pluginDir, "*.dll"))
                    {
                        // Do not load the main application
                        var pluginAssemblyName = new AssemblyName(Path.GetFileNameWithoutExtension(pluginPath));
                        if(pluginAssemblyName == currentAssemblyName)
                            continue;

                        // Try to load assembly
                        var pluginAssembly = new PluginLoadContext(pluginPath).LoadFromAssemblyName(pluginAssemblyName);

                        // Find plugin main class(es)
                        foreach(var type in pluginAssembly.GetTypes().Where(t => typeof(PluginBase).IsAssignableFrom(t)))
                        {
                            // Initialize plugin
                            PluginBase? pluginBase = Activator.CreateInstance(type) as PluginBase;
                            pluginBase?.Register();
                        }
                    }
                }
            }

            // Configuration file supplied?
            if(commandLineOptions.ConfigurationFile == null)
            {
                Console.WriteLine("Please specify a configuration file, as described in the documentation.");
                Console.WriteLine();

                // Print module list
                Console.WriteLine("Available modules:");
                Console.WriteLine("  Testcase generation:");
                foreach(var (name, description) in TestcaseStage.Factory.GetSupportedModules())
                    Console.WriteLine(new string(' ', 4) + name + new string(' ', Math.Max(1, 24 - name.Length)) + description);
                Console.WriteLine();
                Console.WriteLine("  Trace generation:");
                foreach(var (name, description) in TraceStage.Factory.GetSupportedModules())
                    Console.WriteLine(new string(' ', 4) + name + new string(' ', Math.Max(1, 24 - name.Length)) + description);
                Console.WriteLine();
                Console.WriteLine("  Trace preprocessing:");
                foreach(var (name, description) in PreprocessorStage.Factory.GetSupportedModules())
                    Console.WriteLine(new string(' ', 4) + name + new string(' ', Math.Max(1, 24 - name.Length)) + description);
                Console.WriteLine();
                Console.WriteLine("  Analysis:");
                foreach(var (name, description) in AnalysisStage.Factory.GetSupportedModules())
                    Console.WriteLine(new string(' ', 4) + name + new string(' ', Math.Max(1, 24 - name.Length)) + description);
                Console.WriteLine();

                // Done
                return;
            }

            // Our working directory is where the configuration resides
            Environment.CurrentDirectory = Path.GetDirectoryName(commandLineOptions.ConfigurationFile) ?? throw new Exception("Could not determine working directory.");

            // Global cancellation token for graceful shutdown
            CancellationTokenSource globalCancellationToken = new();
            Console.CancelKeyPress += (_, args) =>
            {
                Console.WriteLine("Received exit signal, performing cleanup...");
                args.Cancel = true;

                // ReSharper disable once AccessToDisposedClosure
                globalCancellationToken.Cancel();
            };

            // Load configuration file
            ProcessMonitor? processMonitor = null;
            try
            {
                // Load configuration file
                var configurationParser = new YamlConfigurationParser();
                configurationParser.LoadConfigurationFile(commandLineOptions.ConfigurationFile);

                // Read general configuration first
                var generalConfigurationNode = configurationParser.RootNodes.GetValueOrDefault("general") as MappingNode;

                // Initialize logger
                _logger = new Logger(generalConfigurationNode?.Children.GetValueOrDefault("logger") as MappingNode);
                await _logger.LogDebugAsync("Loaded configuration file, initialized logger");

                // Run process monitor?
                var monitorConfigurationNode = generalConfigurationNode?.GetChildNodeOrDefault("monitor") as MappingNode;
                if(monitorConfigurationNode?.GetChildNodeOrDefault("enable")?.AsBoolean() ?? false)
                {
                    await _logger.LogInfoAsync("Enabling process monitor");
                    processMonitor = new ProcessMonitor(monitorConfigurationNode, _logger);
                }

                // Read stages
                await _logger.LogDebugAsync("Reading pipeline configuration");
                foreach(var rootNode in configurationParser.RootNodes)
                {
                    // Read key
                    switch(rootNode.Key)
                    {
                        case "testcase":
                        {
                            await _logger.LogDebugAsync("Reading and applying 'testcase' stage configuration");

                            if(rootNode.Value is not MappingNode mappingNode)
                                throw new ConfigurationException("Could not parse root node for 'testcase' stage configuration.");

                            // There must be a module name node
                            string moduleName = mappingNode.GetChildNodeOrDefault("module")?.AsString() ?? throw new ConfigurationException("Missing testcase module name.");

                            // Create module, if possible
                            _moduleConfiguration.TestcaseStageModule = await TestcaseStage.Factory.CreateAsync(moduleName, _logger, mappingNode.GetChildNodeOrDefault("module-options") as MappingNode, globalCancellationToken.Token);

                            // Remember general stage options
                            _moduleConfiguration.TestcaseStageOptions = mappingNode.GetChildNodeOrDefault("options") as MappingNode;

                            break;
                        }

                        case "trace":
                        {
                            await _logger.LogDebugAsync("Reading and applying 'trace' stage configuration");

                            if(rootNode.Value is not MappingNode mappingNode)
                                throw new ConfigurationException("Could not parse root node for 'trace' stage configuration.");

                            // There must be a module name node
                            string moduleName = mappingNode.GetChildNodeOrDefault("module")?.AsString() ?? throw new ConfigurationException("Missing trace module name.");

                            // Create module, if possible
                            _moduleConfiguration.TraceStageModule = await TraceStage.Factory.CreateAsync(moduleName, _logger, mappingNode.GetChildNodeOrDefault("module-options") as MappingNode, globalCancellationToken.Token);

                            // Remember general stage options
                            _moduleConfiguration.TraceStageOptions = mappingNode.GetChildNodeOrDefault("options") as MappingNode;

                            break;
                        }

                        case "preprocess":
                        {
                            await _logger.LogDebugAsync("Reading and applying 'preprocess' stage configuration");

                            if(rootNode.Value is not MappingNode mappingNode)
                                throw new ConfigurationException("Could not parse root node for 'preprocess' stage configuration.");

                            // There must be a module name node
                            string moduleName = mappingNode.GetChildNodeOrDefault("module")?.AsString() ?? throw new ConfigurationException("Missing preprocess module name.");

                            // Create module, if possible
                            _moduleConfiguration.PreprocessorStageModule = await PreprocessorStage.Factory.CreateAsync(moduleName, _logger, mappingNode.GetChildNodeOrDefault("module-options") as MappingNode, globalCancellationToken.Token);

                            // Remember general stage options
                            _moduleConfiguration.PreprocessorStageOptions = mappingNode.GetChildNodeOrDefault("options") as MappingNode;

                            break;
                        }

                        case "analysis":
                        {
                            await _logger.LogDebugAsync("Reading and applying 'analysis' stage configuration");

                            if(rootNode.Value is not MappingNode mappingNode)
                                throw new ConfigurationException("Could not parse root node for 'analysis' stage configuration.");

                            // There must be a module list node
                            var moduleListNode = mappingNode.GetChildNodeOrDefault("modules");
                            if(moduleListNode is not ListNode moduleListSequenceNode)
                                throw new ConfigurationException("Module list node does not contain a sequence.");
                            _moduleConfiguration.AnalysesStageModules = new List<AnalysisStage>();
                            foreach(var moduleListEntryNode in moduleListSequenceNode.Children)
                            {
                                if(moduleListEntryNode is not MappingNode moduleEntryNode)
                                    throw new ConfigurationException("Could not module list entry node in 'analysis' stage configuration.");

                                // There must be a module name node
                                string moduleName = moduleEntryNode.GetChildNodeOrDefault("module")?.AsString() ?? throw new ConfigurationException("Missing analysis module name.");

                                // Create module, if possible
                                _moduleConfiguration.AnalysesStageModules.Add(await AnalysisStage.Factory.CreateAsync(moduleName, _logger, moduleEntryNode.GetChildNodeOrDefault("module-options") as MappingNode, globalCancellationToken.Token));
                            }

                            // Remember general stage options
                            _moduleConfiguration.AnalysisStageOptions = mappingNode.GetChildNodeOrDefault("options") as MappingNode;

                            break;
                        }
                    }
                }

                // Check presence of needed pipeline modules and generic options
                await _logger.LogDebugAsync("Doing some sanity checks");
                if(_moduleConfiguration.TestcaseStageModule == null
                   || _moduleConfiguration.TraceStageModule == null
                   || _moduleConfiguration.PreprocessorStageModule == null
                   || _moduleConfiguration.AnalysesStageModules == null
                   || !_moduleConfiguration.AnalysesStageModules.Any())
                    throw new ConfigurationException(
                        "Incomplete module specification. Make sure that there is at least one module for testcase generation, trace generation, preprocessing and analysis, respectively.");

                // Initialize pipeline stages
                // -> [buffer] -> trace -> [buffer] -> preprocess -> [buffer] -> analysis
                await _logger.LogDebugAsync("Initializing pipeline stages");
                var traceStageBuffer = new BufferBlock<TraceEntity>(new DataflowBlockOptions
                {
                    BoundedCapacity = _moduleConfiguration.TraceStageOptions?.GetChildNodeOrDefault("input-buffer-size")?.AsInteger() ?? 1,
                    CancellationToken = globalCancellationToken.Token,
                    EnsureOrdered = true
                });
                var traceStage = new TransformBlock<TraceEntity, TraceEntity>(TraceStageFunc, new ExecutionDataflowBlockOptions
                {
                    CancellationToken = globalCancellationToken.Token,
                    EnsureOrdered = true,
                    MaxDegreeOfParallelism = _moduleConfiguration.TraceStageModule.SupportsParallelism
                        ? _moduleConfiguration.TraceStageOptions?.GetChildNodeOrDefault("max-parallel-threads")?.AsInteger() ?? 1
                        : 1,
                    BoundedCapacity = _moduleConfiguration.TraceStageModule.SupportsParallelism
                        ? _moduleConfiguration.TraceStageOptions?.GetChildNodeOrDefault("max-parallel-threads")?.AsInteger() ?? 1
                        : 1
                });
                var preprocessorStageBuffer = new BufferBlock<TraceEntity>(new DataflowBlockOptions
                {
                    BoundedCapacity = _moduleConfiguration.PreprocessorStageOptions?.GetChildNodeOrDefault("input-buffer-size")?.AsInteger() ?? 1,
                    CancellationToken = globalCancellationToken.Token,
                    EnsureOrdered = true
                });
                var preprocessorStage = new TransformBlock<TraceEntity, TraceEntity>(PreprocessorStageFunc, new ExecutionDataflowBlockOptions
                {
                    CancellationToken = globalCancellationToken.Token,
                    EnsureOrdered = true,
                    MaxDegreeOfParallelism = _moduleConfiguration.PreprocessorStageModule.SupportsParallelism
                        ? _moduleConfiguration.PreprocessorStageOptions?.GetChildNodeOrDefault("max-parallel-threads")?.AsInteger() ?? 1
                        : 1,
                    BoundedCapacity = _moduleConfiguration.PreprocessorStageModule.SupportsParallelism
                        ? _moduleConfiguration.PreprocessorStageOptions?.GetChildNodeOrDefault("max-parallel-threads")?.AsInteger() ?? 1
                        : 1
                });
                var analysisStageBuffer = new BufferBlock<TraceEntity>(new DataflowBlockOptions
                {
                    BoundedCapacity = _moduleConfiguration.AnalysisStageOptions?.GetChildNodeOrDefault("input-buffer-size")?.AsInteger() ?? 1,
                    CancellationToken = globalCancellationToken.Token,
                    EnsureOrdered = true
                });
                var analysisStage = new ActionBlock<TraceEntity>(AnalysisStageFunc, new ExecutionDataflowBlockOptions
                {
                    CancellationToken = globalCancellationToken.Token,
                    EnsureOrdered = true,
                    MaxDegreeOfParallelism = _moduleConfiguration.AnalysesStageModules.All(asm => asm.SupportsParallelism)
                        ? _moduleConfiguration.AnalysisStageOptions?.GetChildNodeOrDefault("max-parallel-threads")?.AsInteger() ?? 1
                        : 1,
                    BoundedCapacity = _moduleConfiguration.AnalysesStageModules.All(asm => asm.SupportsParallelism)
                        ? _moduleConfiguration.AnalysisStageOptions?.GetChildNodeOrDefault("max-parallel-threads")?.AsInteger() ?? 1
                        : 1
                });

                // Link pipeline stages
                await _logger.LogDebugAsync("Linking pipeline stages");
                var linkOptions = new DataflowLinkOptions
                {
                    PropagateCompletion = true
                };
                traceStageBuffer.LinkTo(traceStage, linkOptions);
                traceStage.LinkTo(preprocessorStageBuffer, linkOptions);
                preprocessorStageBuffer.LinkTo(preprocessorStage, linkOptions);
                preprocessorStage.LinkTo(analysisStageBuffer, linkOptions);
                analysisStageBuffer.LinkTo(analysisStage, linkOptions);

                // Start posting test cases
                await _logger.LogInfoAsync("Start testcase thread -> pipeline start");
                var testcaseTask = PostTestcases(traceStageBuffer, globalCancellationToken.Token)
                    .ContinueWith(async t =>
                    {
                        if(t.IsFaulted && t.Exception != null && !t.Exception.Flatten().InnerExceptions.Any(e => e is TaskCanceledException))
                        {
                            // Log exception
                            // ReSharper disable AccessToDisposedClosure
                            await _logger.LogErrorAsync("Testcase generation has stopped due to an unhandled exception:");
                            await _logger.LogErrorAsync(FormatException(t.Exception));
                            await _logger.LogWarningAsync("Pipeline execution will be continued with the existing test cases.");
                            // ReSharper restore AccessToDisposedClosure
                        }
                    }, globalCancellationToken.Token);

                // Handle pipeline exceptions
                try
                {
                    // Wait for all stages to complete
                    await analysisStage.Completion;
                    await _logger.LogInfoAsync("Pipeline completed, executing final analysis steps");

                    // Do final analysis steps
                    foreach(var module in _moduleConfiguration.AnalysesStageModules)
                        await module.FinishAsync();
                }
                catch(Exception ex)
                {
                    // Abort entire pipeline for clean exit
                    globalCancellationToken.Cancel();

                    // Log exception
                    await _logger.LogErrorAsync("An exception occured in the pipeline:");
                    await _logger.LogErrorAsync(FormatException(ex));
                    await _logger.LogInfoAsync("Trying to stop gracefully");
                }

                // Wait for test case generator to stop
                try
                {
                    await testcaseTask;
                    await _logger.LogDebugAsync("Testcase thread completed.");
                }
                catch(TaskCanceledException)
                {
                    // Ignore
                }

                // Do some cleanup
                await _logger.LogDebugAsync("Performing some clean up");
                await _moduleConfiguration.TestcaseStageModule.UnInitAsync();
                await _moduleConfiguration.TraceStageModule.UnInitAsync();
                await _moduleConfiguration.PreprocessorStageModule.UnInitAsync();
                await Task.WhenAll(_moduleConfiguration.AnalysesStageModules.Select(module => module.UnInitAsync()));

                // Statistics       
                if(processMonitor != null)
                    await processMonitor.ConcludeAsync();

                // Done
                await _logger.LogInfoAsync("Program completed.");
            }
            catch(Exception ex)
            {
                // Use logger, if already initialized
                if(_logger != null)
                {
                    await _logger.LogErrorAsync("A general error occurred:");
                    await _logger.LogErrorAsync(FormatException(ex));
                }
                else
                {
                    Console.WriteLine("A general error occured:");
                    Console.WriteLine(FormatException(ex));
                }

                // Stop pipeline
                globalCancellationToken.Cancel();
            }
            finally
            {
                processMonitor?.Dispose();
                _logger?.Dispose();
                globalCancellationToken.Dispose();
            }
        }

        /// <summary>
        /// Posts testcases into the first pipeline block, and completes the block afterwards.
        /// </summary>
        /// <param name="traceStageBuffer">First pipeline block.</param>
        /// <param name="token">Cancellation token to stop posting testcases, in case an exception is encountered.</param>
        /// <returns></returns>
        private static async Task PostTestcases(BufferBlock<TraceEntity> traceStageBuffer, CancellationToken token)
        {
            // Feed testcases into pipeline
            while(!await _moduleConfiguration.TestcaseStageModule!.IsDoneAsync())
                await traceStageBuffer.SendAsync(await _moduleConfiguration.TestcaseStageModule.NextTestcaseAsync(token), token);

            // Mark first block as completed
            // This should propagate through the entire pipeline
            traceStageBuffer.Complete();
        }

        /// <summary>
        /// Trace stage implementation.
        /// </summary>
        /// <param name="t">Input trace entity.</param>
        /// <returns></returns>
        private static async Task<TraceEntity> TraceStageFunc(TraceEntity t)
        {
            // Run module
            await _moduleConfiguration.TraceStageModule!.GenerateTraceAsync(t);
            return t;
        }

        /// <summary>
        /// Preprocessor stage implementation.
        /// </summary>
        /// <param name="t">Input trace entity.</param>
        /// <returns></returns>
        private static async Task<TraceEntity> PreprocessorStageFunc(TraceEntity t)
        {
            // Run module
            await _moduleConfiguration.PreprocessorStageModule!.PreprocessTraceAsync(t);
            return t;
        }

        /// <summary>
        /// Analysis stage implementation.
        /// </summary>
        /// <param name="t">Input trace entity.</param>
        /// <returns></returns>
        private static Task AnalysisStageFunc(TraceEntity t)
        {
            // Run modules in parallel
            return Task.WhenAll(_moduleConfiguration.AnalysesStageModules!.Select(module => module.AddTraceAsync(t)));
        }

        /// <summary>
        /// Pretty prints exceptions.
        /// This function traverses <see cref="AggregateException"/> trees, and only outputs necessary information, to make the output less noisy.
        /// </summary>
        /// <param name="baseException">Exception object.</param>
        private static string FormatException(Exception baseException)
        {
            // This will hold the formatted exception string
            StringBuilder exceptionStringBuilder = new();

            // Recursive tree traversal function
            int currentLevel = -1;
            const int indentationPerLevel = 4;

            void TraverseExceptionTree(Exception ex)
            {
                ++currentLevel;
                int indentation = currentLevel * indentationPerLevel;

                // Treat exception types differently
                if(ex is AggregateException ag)
                {
                    // Print exception name (message is irrelevant here)
                    exceptionStringBuilder.AppendLine(Logger.IndentString($"{ex.GetType().FullName}", indentation));

                    // Traverse children
                    foreach(var child in ag.InnerExceptions)
                        TraverseExceptionTree(child);
                }
                else if(ex.InnerException != null)
                {
                    // Print exception name and message
                    exceptionStringBuilder.AppendLine(Logger.IndentString($"{ex.GetType().FullName}: {ex.Message}", indentation));

                    // Render child
                    TraverseExceptionTree(ex.InnerException);
                }
                else
                {
                    // Print exception name and message
                    exceptionStringBuilder.AppendLine(Logger.IndentString($"{ex.GetType().FullName}: {ex.Message}", indentation));
                }

                // Print stack trace, if there is any
                if(!string.IsNullOrWhiteSpace(ex.StackTrace))
                    exceptionStringBuilder.AppendLine(Logger.IndentString(ex.StackTrace, indentation));

                --currentLevel;
            }

            // Log exception
            TraverseExceptionTree(baseException);
            return exceptionStringBuilder.ToString();
        }

        /// <summary>
        /// Container class for pipeline stage module configuration.
        /// </summary>
        private class ModuleConfigurationData
        {
            public TestcaseStage? TestcaseStageModule { get; set; }
            public TraceStage? TraceStageModule { get; set; }
            public PreprocessorStage? PreprocessorStageModule { get; set; }
            public List<AnalysisStage>? AnalysesStageModules { get; set; }

            // ReSharper disable once UnusedAutoPropertyAccessor.Local
            public MappingNode? TestcaseStageOptions { get; set; }
            public MappingNode? TraceStageOptions { get; set; }
            public MappingNode? PreprocessorStageOptions { get; set; }
            public MappingNode? AnalysisStageOptions { get; set; }
        }
    }
}