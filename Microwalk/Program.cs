using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using CommandLine;
using Microwalk.Analysis.Modules;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Extensions;
using Microwalk.FrameworkBase.Stages;
using Microwalk.TestcaseGeneration.Modules;
using Microwalk.TraceGeneration.Modules;
using Microwalk.TracePreprocessing.Modules;
using YamlDotNet.RepresentationModel;

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
            // Register generic modules
            {
                // Testcase generation
                TestcaseStage.Factory.Register<TestcaseLoader>();
                TestcaseStage.Factory.Register<RandomTestcaseGenerator>();
                TestcaseStage.Factory.Register<ExternalCommand>();

                // Trace generation
                TraceStage.Factory.Register<TraceLoader>();
                TraceStage.Factory.Register<TraceGeneration.Modules.Passthrough>();

                // Trace preprocessing
                PreprocessorStage.Factory.Register<TracePreprocessing.Modules.Passthrough>();
                PreprocessorStage.Factory.Register<PreprocessedTraceLoader>();

                // Analysis
                AnalysisStage.Factory.Register<TraceDumper>();
                AnalysisStage.Factory.Register<InstructionMemoryAccessTraceLeakage>();
                AnalysisStage.Factory.Register<CallStackMemoryAccessTraceLeakage>();
                AnalysisStage.Factory.Register<Analysis.Modules.Passthrough>();
            }

            // Parse command line and execute framework using these options
            Parser.Default.ParseArguments<CommandLineOptions>(args).WithParsed(
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
            string? pluginDir = commandLineOptions.PluginDirectory;
            if(string.IsNullOrWhiteSpace(pluginDir) || !Directory.Exists(pluginDir))
                pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if(pluginDir == null)
                Console.WriteLine("Could not determine plugin directory.");
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

            // Load configuration file
            // TODO delay the module initialization until the configuration load is complete, _or_ ensure that already active modules are properly cancelled
            //      -> The Pin process loves to stay alive and generate HUGE traces
            try
            {
                // Open file and read YAML
                YamlStream yaml = new();
                using(var configFileStream = new StreamReader(File.Open(commandLineOptions.ConfigurationFile, FileMode.Open, FileAccess.Read, FileShare.Read)))
                    yaml.Load(configFileStream);
                var rootNode = (YamlMappingNode)yaml.Documents[0].RootNode;

                // Read general configuration first
                var generalConfigurationNode = rootNode.GetChildNodeWithKey("general");

                // Initialize logger
                _logger = new Logger((YamlMappingNode?)generalConfigurationNode?.GetChildNodeWithKey("logger"));
                await _logger.LogDebugAsync("Loaded configuration file, initialized logger");

                // Read stages
                await _logger.LogDebugAsync("Reading pipeline configuration");
                foreach(var mainNode in rootNode.Children)
                {
                    // Read key
                    switch(mainNode.Key.GetNodeString())
                    {
                        case "testcase":
                        {
                            await _logger.LogDebugAsync("Reading and applying 'testcase' stage configuration");

                            // There must be a module name node
                            string moduleName = mainNode.Value.GetChildNodeWithKey("module")?.GetNodeString() ?? throw new ConfigurationException("Missing testcase module name.");

                            // Check for options
                            if(mainNode.Value.GetChildNodeWithKey("module-options") is not YamlMappingNode optionNode)
                                throw new ConfigurationException("Missing or invalid module options in 'testcase' stage configuration.");

                            // Create module, if possible
                            _moduleConfiguration.TestcaseStageModule = await TestcaseStage.Factory.CreateAsync(moduleName, _logger, optionNode);

                            // Remember general stage options
                            _moduleConfiguration.TestcaseStageOptions = (YamlMappingNode?)mainNode.Value.GetChildNodeWithKey("options");

                            break;
                        }

                        case "trace":
                        {
                            await _logger.LogDebugAsync("Reading and applying 'trace' stage configuration");

                            // There must be a module name node
                            string moduleName = mainNode.Value.GetChildNodeWithKey("module")?.GetNodeString() ?? throw new ConfigurationException("Missing trace module name.");

                            // Check for options
                            if(mainNode.Value.GetChildNodeWithKey("module-options") is not YamlMappingNode optionNode)
                                throw new ConfigurationException("Missing or invalid module options in 'trace' stage configuration.");

                            // Create module, if possible
                            _moduleConfiguration.TraceStageModule = await TraceStage.Factory.CreateAsync(moduleName, _logger, optionNode);

                            // Remember general stage options
                            _moduleConfiguration.TraceStageOptions = (YamlMappingNode?)mainNode.Value.GetChildNodeWithKey("options");

                            break;
                        }

                        case "preprocess":
                        {
                            await _logger.LogDebugAsync("Reading and applying 'preprocess' stage configuration");

                            // There must be a module name node
                            string moduleName = mainNode.Value.GetChildNodeWithKey("module")?.GetNodeString() ?? throw new ConfigurationException("Missing preprocessor module name.");

                            // Check for options
                            if(mainNode.Value.GetChildNodeWithKey("module-options") is not YamlMappingNode optionNode)
                                throw new ConfigurationException("Missing or invalid module options in 'preprocess' stage configuration.");

                            // Create module, if possible
                            _moduleConfiguration.PreprocessorStageModule = await PreprocessorStage.Factory.CreateAsync(moduleName, _logger, optionNode);

                            // Remember general stage options
                            _moduleConfiguration.PreprocessorStageOptions = (YamlMappingNode?)mainNode.Value.GetChildNodeWithKey("options");

                            break;
                        }

                        case "analysis":
                        {
                            await _logger.LogDebugAsync("Reading and applying 'analysis' stage configuration");

                            // There must be a module list node
                            var moduleListNode = mainNode.Value.GetChildNodeWithKey("modules");
                            if(!(moduleListNode is YamlSequenceNode moduleListSequenceNode))
                                throw new ConfigurationException("Module list node does not contain a sequence.");
                            _moduleConfiguration.AnalysesStageModules = new List<AnalysisStage>();
                            foreach(var yamlNode in moduleListSequenceNode)
                            {
                                var moduleEntryNode = (YamlMappingNode)yamlNode;

                                // There must be a module name node
                                string moduleName = moduleEntryNode.GetChildNodeWithKey("module")?.GetNodeString() ?? throw new ConfigurationException("Missing analysis module name.");

                                // Check for options
                                if(moduleEntryNode.GetChildNodeWithKey("module-options") is not YamlMappingNode optionNode)
                                    throw new ConfigurationException($"Missing or invalid options for module '{moduleName}' in 'analysis' stage configuration.");

                                // Create module, if possible
                                _moduleConfiguration.AnalysesStageModules.Add(await AnalysisStage.Factory.CreateAsync(moduleName, _logger, optionNode));
                            }

                            // Remember general stage options
                            _moduleConfiguration.AnalysisStageOptions = (YamlMappingNode?)mainNode.Value.GetChildNodeWithKey("options");

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
                // -> [buffer] -> trace -> [buffer]-> preprocess -> [buffer] -> analysis
                await _logger.LogDebugAsync("Initializing pipeline stages");
                var traceStageBuffer = new BufferBlock<TraceEntity>(new DataflowBlockOptions
                {
                    BoundedCapacity = _moduleConfiguration.TraceStageOptions.GetChildNodeWithKey("input-buffer-size").GetNodeInteger(1),
                    EnsureOrdered = true
                });
                var traceStage = new TransformBlock<TraceEntity, TraceEntity>(TraceStageFunc, new ExecutionDataflowBlockOptions
                {
                    BoundedCapacity = 1,
                    EnsureOrdered = true,
                    MaxDegreeOfParallelism = _moduleConfiguration.TraceStageModule.SupportsParallelism
                        ? _moduleConfiguration.TraceStageOptions.GetChildNodeWithKey("max-parallel-threads").GetNodeInteger(1)
                        : 1
                });
                var preprocessorStageBuffer = new BufferBlock<TraceEntity>(new DataflowBlockOptions
                {
                    BoundedCapacity = _moduleConfiguration.PreprocessorStageOptions.GetChildNodeWithKey("input-buffer-size").GetNodeInteger(1),
                    EnsureOrdered = true
                });
                var preprocessorStage = new TransformBlock<TraceEntity, TraceEntity>(PreprocessorStageFunc, new ExecutionDataflowBlockOptions
                {
                    BoundedCapacity = 1,
                    EnsureOrdered = true,
                    MaxDegreeOfParallelism = _moduleConfiguration.PreprocessorStageModule.SupportsParallelism
                        ? _moduleConfiguration.PreprocessorStageOptions.GetChildNodeWithKey("max-parallel-threads").GetNodeInteger(1)
                        : 1
                });
                var analysisStageBuffer = new BufferBlock<TraceEntity>(new DataflowBlockOptions
                {
                    BoundedCapacity = _moduleConfiguration.AnalysisStageOptions.GetChildNodeWithKey("input-buffer-size").GetNodeInteger(1),
                    EnsureOrdered = true
                });
                var analysisStage = new ActionBlock<TraceEntity>(AnalysisStageFunc, new ExecutionDataflowBlockOptions
                {
                    BoundedCapacity = 1,
                    EnsureOrdered = true,
                    MaxDegreeOfParallelism = _moduleConfiguration.AnalysesStageModules.All(asm => asm.SupportsParallelism)
                        ? _moduleConfiguration.AnalysisStageOptions.GetChildNodeWithKey("max-parallel-threads").GetNodeInteger(1)
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
                var testcaseTaskCancellationTokenSource = new CancellationTokenSource();
                var testcaseTask = PostTestcases(traceStageBuffer, testcaseTaskCancellationTokenSource.Token)
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
                    }, testcaseTaskCancellationTokenSource.Token);

                // Handle pipeline exceptions
                try
                {
                    // Wait for all stages to complete
                    await analysisStage.Completion;
                    await _logger.LogInfoAsync("Pipeline completed.");

                    // Do final analysis steps
                    foreach(var module in _moduleConfiguration.AnalysesStageModules)
                        await module.FinishAsync();
                }
                catch(Exception ex)
                {
                    // Stop test case generator for clean exit
                    testcaseTaskCancellationTokenSource.Cancel();

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
                finally
                {
                    testcaseTaskCancellationTokenSource.Dispose();
                }

                // Do some cleanup
                await _logger.LogDebugAsync("Performing some clean up");
                await _moduleConfiguration.TestcaseStageModule.UnInitAsync();
                await _moduleConfiguration.TraceStageModule.UnInitAsync();
                await _moduleConfiguration.PreprocessorStageModule.UnInitAsync();
                await Task.WhenAll(_moduleConfiguration.AnalysesStageModules.Select(module => module.UnInitAsync()));

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
            }
            finally
            {
                // Make sure logger is disposed properly
                _logger?.Dispose();
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
            public YamlMappingNode? TestcaseStageOptions { get; set; }
            public YamlMappingNode? TraceStageOptions { get; set; }
            public YamlMappingNode? PreprocessorStageOptions { get; set; }
            public YamlMappingNode? AnalysisStageOptions { get; set; }
        }
    }
}