using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace LeakageDetector
{
    /// <summary>
    /// Implements the leakage detection toolchain using pipelining: Testcase generation -> Trace -> Preprocessor -> Analysis.
    /// </summary>
    internal class Pipeline
    {
        /// <summary>
        /// The path to the main Pin application. If the Pin directory is referenced in the system PATH variable, the name of the executable is sufficient.
        /// </summary>
        private const string PinPath = "pin.exe";

#if X86_MODE // This constant is defined in the x86 target platform configuration of the solution (this project is compiled as AnyCPU anyways)        
        /// <summary>
        /// The path to the directory containing the WinAFL binaries.
        /// </summary>
        private const string AflFuzzerBinariesPath = @"C:\Projects\winafl\bin32\x86-Release\RelWithDebInfo\";

        /// <summary>
        /// The path to the directory containing the DynamoRIO binaries.
        /// </summary>
        private const string DynamoRIOBinariesPath = @"C:\Projects\winafl\DynamoRIO-Windows-6.2.0-2\bin32\";
#else

        /// <summary>
        /// The path to the directory containing the WinAFL binaries.
        /// </summary>
        private const string AflFuzzerBinariesPath = @"C:\Projects\winafl\bin64\x64-Release\RelWithDebInfo\";

        /// <summary>
        /// The path to the directory containing the DynamoRIO binaries.
        /// </summary>
        private const string DynamoRIOBinariesPath = @"C:\Projects\winafl\DynamoRIO-Windows-6.2.0-2\bin64\";
#endif

        /// <summary>
        /// The directory where the initial inputs are stored.
        /// </summary>
        private string _initialInputDirectory;

        /// <summary>
        /// The directory where the temporary files of the Fuzzer are stored.
        /// </summary>
        private string _fuzzerDirectory;

        /// <summary>
        /// The directory where the Fuzzer-generated testcase files are stored.
        /// </summary>
        private string _testcaseDirectory;

        /// <summary>
        /// The directory where the Pin-generated trace data is stored.
        /// </summary>
        private string _traceDirectory;

        /// <summary>
        /// The directory where the analysis results are stored.
        /// </summary>
        private string _resultDirectory;

        /// <summary>
        /// The process handle of the Pin tool.
        /// </summary>
        private Process _pinToolProcess;

        /// <summary>
        /// Contains a mapping of testcase IDs used in Pin to the testcase names generated when fuzzing.
        /// </summary>
        private ConcurrentDictionary<int, string> _testcaseNames = new ConcurrentDictionary<int, string>();

        /// <summary>
        /// Contains a mapping of testcase IDs to the corresponding preprocessed trace files.
        /// </summary>
        private ConcurrentDictionary<int, string> _traceFileNames = new ConcurrentDictionary<int, string>();

        /// <summary>
        /// The preprocessor used for the generated traces.
        /// </summary>
        private TraceFilePreprocessor _preprocessor;

        /// <summary>
        /// The known image files.
        /// </summary>
        private List<TraceFilePreprocessor.ImageFileInfo> _images = new List<TraceFilePreprocessor.ImageFileInfo>();

        /// <summary>
        /// The first pipeline stage (schedule testcase).
        /// </summary>
        private TransformBlock<(string testcaseFilename, bool duplicate), int> _initialStage = null;

        /// <summary>
        /// The last pipeline stage.
        /// </summary>
        private ITargetBlock<int> _lastStage = null;

        /// <summary>
        /// The first pipeline stage that does analysis.
        /// </summary>
        private ITargetBlock<int> _firstAnalysisStage = null;

        /// <summary>
        /// The list of known image names, used for trace comparison.
        /// </summary>
        private List<string> _knownImages = new List<string>();

        /// <summary>
        /// Contains traces that were identified as unique until now.
        /// </summary>
        private Dictionary<int, TraceFile> _uniqueTraces = new Dictionary<int, TraceFile>();

        /// <summary>
        /// Allows early stopping of the pipeline.
        /// </summary>
        private CancellationTokenSource _pipelineCancellationToken = new CancellationTokenSource();

        /// <summary>
        /// The currently used analysis mode.
        /// </summary>
        private AnalysisModes _analysisMode;

        /// <summary>
        /// Lists of hashes per trace. Used for mutual information computation.
        /// </summary>
        private ConcurrentDictionary<int, List<ulong>> _mutualInformationHashes = null;

        /// <summary>
        /// The number of entries of the longest trace.
        /// </summary>
        private int _longestHashedTraceLength = 0;

        /// <summary>
        /// Sets whether preprocessed trace data shall be kept on disk. 
        /// </summary>
        private bool _keepTraces = false;

        /// <summary>
        /// Memory access hashes, per instruction (dictionary key), per trace (outer list index).
        /// </summary>
        private ConcurrentDictionary<uint, ConcurrentDictionary<int, ulong>> _instructionTraces = null;

        /// <summary>
        /// Contains a running count of completed (and not discarded) testcases.
        /// </summary>
        private int _testcaseCount = 0;

        /// <summary>
        /// Lookup to convert IDs of duplicate testcases to the original ones (only used for randomization detection).
        /// </summary>
        private Dictionary<int, int> _baseTestcaseIdLookup = new Dictionary<int, int>();

        /// <summary>
        /// The last testcase ID that wasn't a duplicate.
        /// </summary>
        private int _lastUniqueTestcaseId = 0;

        /// <summary>
        /// MD5 hash objects needed for compressing test cases in mutual information mode. Only used in the <see cref="CompressTrace(int)"/> method.
        /// </summary>
        private ConcurrentBag<MD5CryptoServiceProvider> _md5Objects = null;

        /// <summary>
        /// The length of the common trace prefix.
        /// </summary>
        private int _tracePrefixLength = 0;

        /// <summary>
        /// Determines whether traces from a previous run shall be loaded.
        /// </summary>
        private bool _directTraceLoad = false;

        /// <summary>
        /// The memory address comparison granularity.
        /// </summary>
        private uint _granularity = 1;

        /// <summary>
        /// Generates a set of unique testcases using the AFL fuzzer and simultaneously feeds them into the Pin tool.
        /// </summary>
        /// <param name="workDirectory">The path to a directory where the intermediate data shall be stored. Must contain a folder /in/ containing initial testcases.</param>
        /// <param name="resultDirectory">The directory where the results shall be stored.</param>
        /// <param name="sourceTraceDirectory">The directory where traces should be loaded from directly.</param>
        /// <param name="analysisMode">The requested analysis mode.</param>
        public Pipeline(string workDirectory, string resultDirectory, string sourceTraceDirectory, AnalysisModes analysisMode)
        {
            // Get full path of work directory
            workDirectory = Path.GetFullPath(workDirectory);
            _analysisMode = analysisMode;

            // Ensure input/output directories exist
            Program.Log($"Checking directories...\n", Program.LogLevel.Info);
            _initialInputDirectory = Path.GetFullPath(Path.Combine(workDirectory, "in\\"));
            _fuzzerDirectory = Path.GetFullPath(Path.Combine(workDirectory, "fuzz\\"));
            _testcaseDirectory = Path.GetFullPath(Path.Combine(workDirectory, "testcases\\"));
            _traceDirectory = Path.GetFullPath(sourceTraceDirectory ?? Path.Combine(workDirectory, "trace\\"));
            _directTraceLoad = (sourceTraceDirectory != null);
            _resultDirectory = Path.GetFullPath(resultDirectory);
            if(!Directory.Exists(_initialInputDirectory))
            {
                // Error
                Program.Log($"Directory \"in/\" with initial testcases could not be found (looking for \"{_initialInputDirectory}\").\n", Program.LogLevel.Error);
                return;
            }
            else if(!Directory.EnumerateFiles(_initialInputDirectory).Any())
            {
                // Error
                Program.Log($"Directory \"in/\" does not contain any testcases (looking at \"{_initialInputDirectory}\").\n", Program.LogLevel.Error);
                return;
            }
            if(!Directory.Exists(_testcaseDirectory))
                Directory.CreateDirectory(_testcaseDirectory);
            if(!Directory.Exists(_traceDirectory))
                Directory.CreateDirectory(_traceDirectory);
            if(!Directory.Exists(_resultDirectory))
                Directory.CreateDirectory(_resultDirectory);

            // Setup first part of pipeline
            // Slow down tracing such that the (much slower) preprocessing stage can keep up -> this avoids eating up all disk space
            DataflowLinkOptions pipelineOptions = new DataflowLinkOptions { PropagateCompletion = true };
            var scheduleTestcaseStage = new TransformBlock<(string testcaseFilename, bool duplicate), int>(param => ScheduleTestcase(param.testcaseFilename, param.duplicate));
            var generateTraceStage = new TransformBlock<int, int>(testcaseId => GenerateTrace(testcaseId), new ExecutionDataflowBlockOptions { BoundedCapacity = 3, CancellationToken = _pipelineCancellationToken.Token });
            var preprocessStage = new TransformBlock<int, int>(testcaseId => PreprocessTrace(testcaseId), new ExecutionDataflowBlockOptions { BoundedCapacity = 1, CancellationToken = _pipelineCancellationToken.Token });
            _initialStage = scheduleTestcaseStage;
            scheduleTestcaseStage.LinkTo(generateTraceStage, pipelineOptions);
            generateTraceStage.LinkTo(preprocessStage, pipelineOptions);

            // Create end of pipeline depending on requested analysis mode
            int analysisQueueSize = (_directTraceLoad ? int.MaxValue : 8);
            switch(analysisMode)
            {
                case AnalysisModes.None:
                {
                    // Preprocessing is the final stage
                    var finalDummyStage = new ActionBlock<int>(testcaseId => { });
                    preprocessStage.LinkTo(finalDummyStage, pipelineOptions);
                    _lastStage = finalDummyStage;
                    _keepTraces = true;

                    break;
                }

                case AnalysisModes.Compare:
                {
                    // Generate testcase -> generate trace -> preprocess trace -> compare with known unique traces
                    // This stage is also slow (comparable to the preprocessing stage), so keep the input queue short too
                    var compareStage = new ActionBlock<int>(testcaseId => CompareTraces(testcaseId), new ExecutionDataflowBlockOptions { BoundedCapacity = analysisQueueSize, CancellationToken = _pipelineCancellationToken.Token });
                    preprocessStage.LinkTo(compareStage, pipelineOptions);
                    _firstAnalysisStage = compareStage;
                    _lastStage = compareStage;

                    break;
                }

                case AnalysisModes.MutualInformation_WholeTrace:
                case AnalysisModes.MutualInformation_TracePrefix:
                case AnalysisModes.MutualInformation_SingleInstruction:
                {
                    // Generate testcase -> generate trace -> preprocess trace -> compress trace ---> compute mutual information when all traces are present
                    const int parallelismAmount = 4;
                    var compressStage = new ActionBlock<int>(testcaseId => CompressTrace(testcaseId), new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = parallelismAmount, BoundedCapacity = analysisQueueSize, CancellationToken = _pipelineCancellationToken.Token });
                    preprocessStage.LinkTo(compressStage, pipelineOptions);
                    _firstAnalysisStage = compressStage;
                    _lastStage = compressStage;

                    // Create MD5 hash objects
                    _md5Objects = new ConcurrentBag<MD5CryptoServiceProvider>();
                    for(int p = 0; p < parallelismAmount; ++p)
                        _md5Objects.Add(new MD5CryptoServiceProvider());

                    // Create storage objects for temporary data
                    if(analysisMode == AnalysisModes.MutualInformation_WholeTrace || analysisMode == AnalysisModes.MutualInformation_TracePrefix)
                        _mutualInformationHashes = new ConcurrentDictionary<int, List<ulong>>();
                    else if(analysisMode == AnalysisModes.MutualInformation_SingleInstruction)
                        _instructionTraces = new ConcurrentDictionary<uint, ConcurrentDictionary<int, ulong>>();
                    break;
                }
            }
        }

        /// <summary>
        /// Starts the leakage analysis pipeline.
        /// </summary>
        /// <param name="wrapper">The full path to the wrapper executable that calls the investigated library (should not contain spaces).</param>
        /// <param name="library">The name (not path) of the library to be fuzzed.</param>
        /// <param name="fuzz">Sets whether the inputs shall be generated by fuzzing, or read from a pre-generated testcases\ directory.</param>
        /// <param name="randomTestcaseLength">The length of random testcases (or -1 to disable). Overrides the fuzz parameter by generating random testcases.</param>
        /// <param name="randomTestcaseAmount">The number of random testcases (or -1 to disable). Overrides the fuzz parameter by generating random testcases.</param>
        /// <param name="emulatedCpuModel">The ID of the emulated CPU model (see Pin tool for list of provided models).</param>
        /// <param name="randomizationDetectionMultiplier">Determines whether randomization shall be detected, and sets the used testcase multiplier.</param>
        /// <param name="keepTraces">Determines whether preprocessed trace data shall be kept on disk.</param>
        /// <param name="granularity">The comparison granularity of memory addresses in byte. Must be a power of 2.</param>
        public void Start(string wrapper, string library, bool fuzz, int randomTestcaseLength, int randomTestcaseAmount, int emulatedCpuModel, int randomizationDetectionMultiplier, bool keepTraces, uint granularity, ulong rdrand)
        {
            // Check granularity
            if(granularity == 0 || (granularity & (granularity - 1)) != 0)
            {
                // Error
                Program.Log("The given memory address comparison granularity is not a power of 2!\n", Program.LogLevel.Error);
                return;
            }
            _granularity = granularity;

            // Only allow to set this flag; this might have already been set by the constructor
            if(!_keepTraces)
                _keepTraces = keepTraces;

            // Generate traces?
            if(!_directTraceLoad)
            {
                // Start Pin tool such that it is ready as early as possible (it takes some time to instrument the investigated executables)
                Program.Log("Starting Pin tool process...\n", Program.LogLevel.Info);
                ProcessStartInfo pinToolProcessStartInfo = new ProcessStartInfo
                {
                    Arguments = $"-t \"{Path.Combine(Environment.CurrentDirectory, "Trace.dll")}\" -o {Path.Combine(_traceDirectory, "testcase")} -r {rdrand} -c {emulatedCpuModel} -i {library} -- {wrapper} 2",
                    FileName = PinPath,
                    WorkingDirectory = _traceDirectory,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                pinToolProcessStartInfo.Environment["Path"] += ";" + Path.GetDirectoryName(wrapper);
                _pinToolProcess = Process.Start(pinToolProcessStartInfo);

                // Read error output of pin tool to avoid blocking on buffer overflow
                _pinToolProcess.ErrorDataReceived += (sender, e) => Program.Log($"Pin tool stderr: {e.Data}\n", Program.LogLevel.Debug);
                _pinToolProcess.BeginErrorReadLine();

                // Insert one dummy testcase to ensure that the prefix is ready
                // TODO Documentation: Library should not load any additional images at runtime
                Program.Log("Generating trace prefix...\n", Program.LogLevel.Info);
                File.Copy(Directory.EnumerateFiles(_initialInputDirectory).First(), Path.Combine(_testcaseDirectory, "dummy.testcase"), true); // Use one of the initial input files (there must be at least one, as checked above)
                _initialStage.Post(("dummy", false));

                // Acquire testcases from the given source
                if(randomTestcaseAmount <= 0 || randomTestcaseLength <= 0)
                {
                    // Use fuzzer or read testcases from directory
                    if(fuzz)
                    {
                        // Create working directory
                        if(!Directory.Exists(_fuzzerDirectory))
                            Directory.CreateDirectory(_fuzzerDirectory);

                        // Create pipe to get notified for new testcases
                        Program.Log($"Creating testcase notification pipe...\n", Program.LogLevel.Info);
                        using(NamedPipeServerStream pipe = new NamedPipeServerStream("LeakageDetectorFuzzingPipe", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 33 * 1024 * 1024, 1024)) // 1 million entries
                        using(StreamReader pipeReader = new StreamReader(pipe))
                        {
                            // Start fuzzer process
                            // TODO Libraries that load an optimized implementation DLL depending on CPUID require an additional "-coverage_module libraryname.dll" parameter in the winafl part.
                            //      It is currently unknown why the modified WinAFL version does not properly instrument the CPUID instructions in non-"coverage_module" modules.
                            // TODO Put patched version of WinAFL into repository
                            /*
                            Windows-Crypto:
                            C:\Users\ITS\Daten\VisualStudio\Projects\winafl\bin64\x64-Release\RelWithDebInfo>
                            afl-fuzz.exe -i R:\in -o R:\fuzz -D C:\Users\ITS\Daten\VisualStudio\Projects\winafl\DynamoRIO-Windows-6.2.0-2\bin64 -t 10000 -- -fuzz_iterations 1000000 -coverage_module BCryptPrimitives.dll -target_module IPPFuzzingWrapper.exe -target_method Fuzz -nargs 4 -- R:\IPPFuzzingWrapper.exe 1 @@ R:\testcases

                            gcrypt:
                            C:\Users\ITS\Daten\VisualStudio\Projects\winafl\bin32\x86-Release\RelWithDebInfo>
                            afl-fuzz.exe -i R:\in -o R:\fuzz -D C:\Users\ITS\Daten\VisualStudio\Projects\winafl\DynamoRIO-Windows-6.2.0-2\bin32 -t 10000 -- -fuzz_iterations 1000000 -coverage_module libgcrypt-20.dll -target_module IPPFuzzingWrapper.exe -target_method Fuzz -nargs 4 -- R:\IPPFuzzingWrapper.exe 1 @@ R:\testcases
                            */
                            ProcessStartInfo fuzzerProcessStartInfo = new ProcessStartInfo
                            {
                                Arguments = $"-i {_initialInputDirectory} -o {_fuzzerDirectory} -D {DynamoRIOBinariesPath} -t 10000 -- -fuzz_iterations 1000000 -coverage_module {library} -target_module {Path.GetFileName(wrapper)} -target_method Fuzz -nargs 4 -- {wrapper} 1 @@ {_testcaseDirectory}",
                                FileName = AflFuzzerBinariesPath + "afl-fuzz.exe",
                                WorkingDirectory = AflFuzzerBinariesPath,
                                UseShellExecute = true
                            };
                            Process fuzzerProcess = Process.Start(fuzzerProcessStartInfo);
                            DateTime fuzzerStartTime = DateTime.Now;

                            // Read filenames from pipe
                            const int fuzzingTime = 60; // TODO configurable
                            int testcaseCount = 1;
                            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
                            bool abort = false;
                            while(!abort)
                            {
                                // Wait for client (FuzzingWrapper instance)
                                Program.Log("Waiting for fuzzer connection...\n", Program.LogLevel.Info);
                                Task waitForConnectionTask = pipe.WaitForConnectionAsync(cancellationTokenSource.Token);
                                if(Task.WhenAny(waitForConnectionTask, Task.Delay(1000 * (fuzzingTime - (DateTime.Now - fuzzerStartTime).Seconds))).Result != waitForConnectionTask)
                                {
                                    // Leave run loop
                                    cancellationTokenSource.Cancel(false);
                                    abort = true;
                                    break;
                                }
                                Program.Log("Fuzzer connected!\n", Program.LogLevel.Info);

                                // Read until connection is closed
                                try
                                {
                                    Task<string> readLineTask = null;
                                    while(pipe.IsConnected)
                                    {
                                        // Check run time
                                        if((DateTime.Now - fuzzerStartTime).TotalSeconds >= fuzzingTime)
                                        {
                                            // Leave run loop
                                            cancellationTokenSource.Cancel(false);
                                            abort = true;
                                            break;
                                        }

                                        // Read next filename
                                        // TODO make timeout configurable
                                        if(readLineTask == null || readLineTask.IsCompleted)
                                            readLineTask = pipeReader.ReadLineAsync();
                                        if(Task.WhenAny(readLineTask, Task.Delay(500)).Result == readLineTask)
                                        {
                                            // Add to queue, if valid (might be null when the connection is closed)
                                            string testcaseFilename = readLineTask.Result;
                                            if(!string.IsNullOrWhiteSpace(testcaseFilename))
                                            {
                                                //Program.Log($"Scheduling testcase #{cnt} \"{testcaseFilename}\"...\n", Program.LogLevel.Debug);
                                                _initialStage.Post((testcaseFilename, false));
                                                //Program.Log($"Scheduled #{cnt}.\n", Program.LogLevel.Debug);
                                                ++testcaseCount;
                                                if(testcaseCount % 1000 == 0)
                                                    Program.Log($"Scheduled {testcaseCount} testcases.\n", Program.LogLevel.Info);
                                            }
                                        }
                                        else
                                        {
                                            // Read timeout?
                                            if(!pipe.IsConnected)
                                                break;
                                        }
                                    }
                                    pipe.Disconnect();
                                }
                                catch(IOException ex)
                                {
                                    // Some error occured
                                    Program.Log($"Exception thrown on pipe receive: {ex.Message}", Program.LogLevel.Error);
                                    break;
                                }
                            }

                            // Kill fuzzer process
                            if(!fuzzerProcess.HasExited)
                            {
                                Program.Log($"Killing fuzzer process...\n", Program.LogLevel.Info);
                                fuzzerProcess.Kill();
                            }
                            Program.Log($"Fuzzer process has exited.\n", Program.LogLevel.Info);
                        }
                    }
                    else
                    {
                        // Read testcase directory
                        Program.Log($"Loading testcases...\n", Program.LogLevel.Info);
                        int cnt = 1;
                        foreach(string testcaseFilename in Directory.EnumerateFiles(_testcaseDirectory, "*.testcase"))
                        {
                            _initialStage.Post((testcaseFilename, false));
                            ++cnt;
                            if(cnt % 1000 == 0)
                                Program.Log($"Scheduled {cnt} testcases.\n", Program.LogLevel.Info);
                        }
                    }
                }
                else
                {
                    // Generate unique random testcases
                    Program.Log($"Generating testcases...\n", Program.LogLevel.Info);
                    RNGCryptoServiceProvider random = new RNGCryptoServiceProvider();
                    byte[] testcaseData = new byte[randomTestcaseLength];
                    HashSet<byte[]> knownTestcases = new HashSet<byte[]>(new ByteArrayComparer());
                    for(int i = 1; i <= randomTestcaseAmount; ++i)
                    {
                        // Generate random bytes until they are unique
                        do
                            random.GetBytes(testcaseData);
                        while(knownTestcases.Contains(testcaseData));

                        //////////////// OPENSSL /////////////////////////////
                        /*
                        const int BIT_SIZE = 512;
                        const int BYTE_SIZE = BIT_SIZE / 8;
                        const int FLIP_INDEX = 64;

                        byte[] keyArr;
                        byte[] keyFlippedArr;

                        //random.GetBytes(testcaseData, 0, BYTE_SIZE);
                        var rsa = new RSACryptoServiceProvider(BIT_SIZE);
                        var key = rsa.ExportParameters(true);

                        using(MemoryStream keyStream = new MemoryStream())
                        {
                            using(TextWriter keyWriter = new StreamWriter(keyStream))
                                Temp.ExportPrivateKey(key, keyWriter);
                            keyArr = keyStream.GetBuffer();
                        }

                        byte flipMask = (byte)(0b10000000 >> (FLIP_INDEX % 8));
                        key.D[FLIP_INDEX / 8] ^= flipMask;
                        using(MemoryStream keyStream = new MemoryStream())
                        {
                            using(TextWriter keyWriter = new StreamWriter(keyStream))
                                Temp.ExportPrivateKey(key, keyWriter);
                            keyFlippedArr = keyStream.GetBuffer();
                        }

                        byte[] modulus = new byte[key.Modulus.Length + 1];
                        Array.Copy(key.Modulus.Reverse().ToArray(), modulus, key.Modulus.Length);
                        bool tick = true;

                        byte[] currentKeyArr = null;
                        if(tick)
                        {
                            //   tick = false;
                            currentKeyArr = keyArr;
                        }
                        //else
                        //{
                        //    tick = true;
                        //    currentKeyArr = keyFlippedArr;
                        //}

                        byte[] randomPlaintext = new byte[8];
                        random.GetBytes(randomPlaintext, 0, 7);
                        BigInteger cipher = BigInteger.ModPow(new BigInteger(0x1234567890abcdefUL), new BigInteger(65537), new BigInteger(modulus));
                        byte[] cipherBytes = cipher.ToByteArray().Reverse().ToArray();

                        using(MemoryStream testcaseStream = new MemoryStream())
                        {
                            using(BinaryWriter testcaseWriter = new BinaryWriter(testcaseStream))
                            {
                                testcaseWriter.Write(currentKeyArr.Length);
                                testcaseWriter.Write(currentKeyArr);
                                testcaseWriter.Write(cipherBytes.Length);
                                testcaseWriter.Write(cipherBytes);
                            }
                            testcaseData = testcaseStream.ToArray();
                        }
                        */
                        //////////////// LIBGCRYPT /////////////////////////////

                        /* const int BIT_SIZE = 512;

                         //random.GetBytes(testcaseData, 0, BYTE_SIZE);
                         var rsa = new RSACryptoServiceProvider(BIT_SIZE);
                         var key = rsa.ExportParameters(true);

                         byte[] randomPlaintext = new byte[8];
                         random.GetBytes(randomPlaintext, 0, 7);
                         byte[] modulus = new byte[key.Modulus.Length + 1];
                         Array.Copy(key.Modulus.Reverse().ToArray(), modulus, key.Modulus.Length);
                         BigInteger cipher = BigInteger.ModPow(new BigInteger(0x1234567890abcdefUL), new BigInteger(65537), new BigInteger(modulus));
                         byte[] cipherBytes = cipher.ToByteArray().Reverse().ToArray();

                         testcaseData = SexprExport.EncodeParameters(key, cipherBytes);*/

                        //////////////// BOTAN /////////////////////////////

                        /*const int BIT_SIZE = 512;

                        //random.GetBytes(testcaseData, 0, BYTE_SIZE);
                        var rsa = new RSACryptoServiceProvider(BIT_SIZE);
                        var key = rsa.ExportParameters(true);
                        byte[] keyEncoded;
                        using(MemoryStream keyStream = new MemoryStream())
                        {
                            using(TextWriter keyWriter = new StreamWriter(keyStream))
                                Temp.ExportPrivateKey(key, keyWriter);
                            keyEncoded = keyStream.GetBuffer();
                        }
                        byte[] randomPlaintext = new byte[8] { 0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0xde, 0xf0 };
                        //random.GetBytes(randomPlaintext, 0, 7);
                        byte[] modulus = new byte[key.Modulus.Length + 1];
                        Array.Copy(key.Modulus.Reverse().ToArray(), modulus, key.Modulus.Length);
                        //byte[] cipherBytes = rsa.Encrypt(randomPlaintext, RSAEncryptionPadding.Pkcs1);
                        BigInteger cipher = BigInteger.ModPow(new BigInteger(0x1234567890abcdefUL), new BigInteger(65537), new BigInteger(modulus));
                        byte[] cipherBytes = cipher.ToByteArray().Reverse().ToArray();

                        using(MemoryStream testcaseStream = new MemoryStream())
                        {
                            using(BinaryWriter testcaseWriter = new BinaryWriter(testcaseStream))
                            {
                                testcaseWriter.Write(keyEncoded.Length);
                                testcaseWriter.Write(keyEncoded);
                                testcaseWriter.Write(cipherBytes.Length);
                                testcaseWriter.Write(cipherBytes);
                            }
                            testcaseData = testcaseStream.ToArray();
                        }
                        */

                        //////////////// MICROSOFT /////////////////////////////

                        // RSA
                        /*const int BIT_SIZE = 1024;
                        const int BYTE_SIZE = BIT_SIZE / 8;

                        var rsa = new RSACryptoServiceProvider(BIT_SIZE);
                        var key = rsa.ExportParameters(true);
                        Array.Copy(key.P, 0, testcaseData, BYTE_SIZE, BYTE_SIZE / 2);
                        Array.Copy(key.Q, 0, testcaseData, BYTE_SIZE + BYTE_SIZE / 2, BYTE_SIZE / 2);
                        byte[] modulus = new byte[key.Modulus.Length + 1];
                        Array.Copy(key.Modulus.Reverse().ToArray(), modulus, key.Modulus.Length);
                        BigInteger cipher = BigInteger.ModPow(new BigInteger(0x0123456789abcdefUL), new BigInteger(65537), new BigInteger(modulus));
                        byte[] cipherBytes = cipher.ToByteArray().Reverse().ToArray();
                        int offset = 0;
                        if(cipherBytes[0] == 0)
                            ++offset;
                        testcaseData[0] = 0;
                        Array.Copy(cipherBytes, offset, testcaseData, BYTE_SIZE - (cipherBytes.Length - offset), cipherBytes.Length - offset);*/

                        // DSA keygen
                        /*const int BIT_SIZE = 512;
                        const int BYTE_SIZE = BIT_SIZE / 8;

                        var dsa = new DSACryptoServiceProvider(BIT_SIZE);
                        var key = dsa.ExportParameters(true);
                        Array.Copy(BitConverter.GetBytes(key.Counter), 0, testcaseData, 8, 4);
                        Array.Copy(key.Seed, 0, testcaseData, 8 + 4, 20);
                        Array.Copy(key.Q, 0, testcaseData, 8 + 4 + 20, 20);
                        Array.Copy(key.P, 0, testcaseData, 8 + 4 + 20 + 20, BYTE_SIZE);
                        Array.Copy(key.G, 0, testcaseData, 8 + 4 + 20 + 20 + BYTE_SIZE, BYTE_SIZE);
                        Array.Copy(key.Y, 0, testcaseData, 8 + 4 + 20 + 20 + BYTE_SIZE + BYTE_SIZE, BYTE_SIZE);
                        Array.Copy(key.X, 0, testcaseData, 8 + 4 + 20 + 20 + BYTE_SIZE + BYTE_SIZE + BYTE_SIZE, 20);

                        string text = "";
                        foreach(byte entry in testcaseData)
                            text += "0x" + entry.ToString("X2") + ", ";
                        */

                        //////////////// IPP /////////////////////////////

                        /*const int BIT_SIZE = 512;
                        const int BYTE_SIZE = BIT_SIZE / 8;
                        //random.GetBytes(testcaseData, 0, BYTE_SIZE);
                        var rsa = new RSACryptoServiceProvider(BIT_SIZE);
                        var key = rsa.ExportParameters(true);
                        Array.Copy(key.Modulus, 0, testcaseData, 0, BYTE_SIZE);
                        Array.Copy(key.D, 0, testcaseData, BYTE_SIZE, BYTE_SIZE);
                        byte[] modulus = new byte[key.Modulus.Length + 1];
                        Array.Copy(key.Modulus.Reverse().ToArray(), modulus, key.Modulus.Length);
                        BigInteger cipher = BigInteger.ModPow(new BigInteger(0x0123456789abcdefUL), new BigInteger(65537), new BigInteger(modulus));
                        byte[] cipherBytes = cipher.ToByteArray().Reverse().ToArray();
                        int offset = 0;
                        if(cipherBytes[0] == 0)
                            ++offset;
                        testcaseData[2 * BYTE_SIZE] = 0;
                        Array.Copy(cipherBytes, offset, testcaseData, 3 * BYTE_SIZE - (cipherBytes.Length - offset), cipherBytes.Length - offset);*/

                        ///////////////////////////////////////////////////////////////////////////

                        // Save testcase
                        string testcaseFilename = Path.Combine(_testcaseDirectory, i + ".testcase");
                        File.WriteAllBytes(testcaseFilename, testcaseData);
                        knownTestcases.Add(testcaseData);

                        // Post testcase into pipeline
                        _initialStage.Post((testcaseFilename, false));

                        // Duplicate this testcase?
                        for(int r = 0; r < randomizationDetectionMultiplier - 1; ++r)
                        {
                            // Duplicate testcase
                            _initialStage.Post((testcaseFilename, true));
                        }
                        if(i % 1000 == 0)
                            if(randomizationDetectionMultiplier == 1)
                                Program.Log($"Scheduled {i} testcases.\n", Program.LogLevel.Info);
                            else
                                Program.Log($"Scheduled {i}*{randomizationDetectionMultiplier} testcases.\n", Program.LogLevel.Info);
                    }
                }

                // Wait for pipeline to complete
                _initialStage.Complete();
                _lastStage.Completion.Wait();

                // Exit Pin tool process
                _pinToolProcess.StandardInput.WriteLine("e 0");
                _pinToolProcess.WaitForExit();
            }
            else
            {
                // Read given trace directory
                Program.Log($"Enqueueing traces...\n", Program.LogLevel.Info);
                _keepTraces = true;
                int testcaseId = 1;
                foreach(string traceFileName in Directory.EnumerateFiles(_traceDirectory, "*.trace.processed"))
                {
                    // Add trace to internal lists
                    ++testcaseId;
                    string traceName = Path.GetFileNameWithoutExtension(traceFileName);
                    _testcaseNames.AddOrUpdate(testcaseId, traceName, (k, v) => traceName);
                    _baseTestcaseIdLookup.Add(testcaseId, testcaseId);
                    _traceFileNames.AddOrUpdate(testcaseId, traceFileName, (k, v) => traceFileName);

                    // Run analysis
                    _firstAnalysisStage.Post(testcaseId);

                    // Info output
                    if((testcaseId - 1) % 1000 == 0)
                        Program.Log($"Scheduled {testcaseId - 1} traces.\n", Program.LogLevel.Info);
                }
                Program.Log($"Scheduled {testcaseId - 1} traces.\n", Program.LogLevel.Info);

                // Wait for pipeline to complete
                _firstAnalysisStage.Complete();
                _lastStage.Completion.Wait();
            }

            // Mutual information mode?
            if(_analysisMode == AnalysisModes.MutualInformation_TracePrefix || _analysisMode == AnalysisModes.MutualInformation_WholeTrace)
            {
                // TODO consider key duplication
                // Inform user that analysis starts now
                Program.Log($"Starting mutual information analysis...\n", Program.LogLevel.Info);
                if(randomizationDetectionMultiplier > 1)
                    Program.Log($"Randomization detection is not yet supported by this analysis mode!", Program.LogLevel.Error);

                // Calculate probabilities of keys, and keys with traces
                // Since the keys are uniquely generated, we have a uniform distribution
                double pX = 1.0 / _testcaseCount;
                double pXY = 1.0 / _testcaseCount;

                // Run calculation until the longest trace ends
                List<HashSet<int>> similarTraces = new List<HashSet<int>>() { new HashSet<int>(_mutualInformationHashes.Keys) };
                double mutualInformation = 0.0;
                for(int line = 0; line < _longestHashedTraceLength; ++line)
                {
                    // Divide all sets into subsets depending on the next trace line
                    List<HashSet<int>> newSimilarTraces = new List<HashSet<int>>();
                    foreach(HashSet<int> set in similarTraces)
                    {
                        Dictionary<ulong, HashSet<int>> newSets = new Dictionary<ulong, HashSet<int>>();
                        foreach(int testcaseId in set)
                        {
                            ulong nextHash = (_mutualInformationHashes[testcaseId].Count > line ? _mutualInformationHashes[testcaseId][line] : 0UL);
                            if(!newSets.ContainsKey(nextHash))
                                newSets.Add(nextHash, new HashSet<int>() { testcaseId });
                            else
                                newSets[nextHash].Add(testcaseId);
                        }
                        newSimilarTraces.AddRange(newSets.Values);
                    }

                    // Calculate mutual information
                    double newMutualInformation = 0.0;
                    foreach(HashSet<int> set in newSimilarTraces)
                    {
                        double pY = (double)set.Count / _testcaseCount;
                        newMutualInformation += set.Count * pXY * Math.Log(pXY / (pX * pY), 2);
                    }
                    if(newMutualInformation != mutualInformation)
                    {
                        string output = $"Mutual information after {_tracePrefixLength + line} lines: {newMutualInformation.ToString("N3")} bits\n";
                        File.AppendAllText(Path.Combine(_resultDirectory, "mutual_information.txt"), output);
                        Program.Log(output, Program.LogLevel.Success);
                        mutualInformation = newMutualInformation;
                    }
                    similarTraces = newSimilarTraces;
                }

                // Show warning if there likely were not enough testcases
                const double warnThreshold = 0.9;
                double testcaseCountBits = Math.Log(_testcaseCount, 2);
                if(testcaseCountBits - warnThreshold < mutualInformation && mutualInformation < testcaseCountBits + warnThreshold)
                    Program.Log("The calculated mutual information is suspiciously near to the testcase range. It is recommended to run more testcases.\n", Program.LogLevel.Warning);
                else if(mutualInformation == 0.0)
                    Program.Log("No leakage detected.\n", Program.LogLevel.Success);
            }
            else if(_analysisMode == AnalysisModes.MutualInformation_SingleInstruction)
            {
                // Inform user that analysis starts now
                Program.Log($"Starting mutual information analysis...\n", Program.LogLevel.Info);
                Dictionary<ulong, double> mutualInformationPerInstruction = new Dictionary<ulong, double>();
                unchecked
                {
                    // Calculation mutual information of each instruction
                    int instructionIndex = 0;
                    foreach(var instructionData in _instructionTraces)
                    {
                        // Collect counts of access traces by comparing the access trace hashes
                        Dictionary<ulong, int> instructionAccessTraceCounts = new Dictionary<ulong, int>();
                        Dictionary<ulong, List<int>> testcaseIdsGeneratingHashes = new Dictionary<ulong, List<int>>();
                        foreach(var instructionAccessTrace in instructionData.Value)
                        {
                            // Get access trace hash value
                            ulong hashValue = instructionAccessTrace.Value;

                            // New?
                            if(!instructionAccessTraceCounts.ContainsKey(hashValue))
                                instructionAccessTraceCounts.Add(hashValue, 1);
                            else
                                instructionAccessTraceCounts[hashValue] += 1;

                            // Testcase duplication?
                            if(randomizationDetectionMultiplier != 1)
                            {
                                // Save testcase IDs generating specific trace hashes
                                if(!testcaseIdsGeneratingHashes.ContainsKey(hashValue))
                                    testcaseIdsGeneratingHashes.Add(hashValue, new List<int>() { instructionAccessTrace.Key });
                                else
                                    testcaseIdsGeneratingHashes[hashValue].Add(instructionAccessTrace.Key);
                            }
                        }

                        // Calculate mutual information depending on randomization detection/testcase duplication mode
                        double mutualInformation = 0.0;
                        if(randomizationDetectionMultiplier == 1)
                        {
                            // Calculate probabilities of keys, and keys with traces (if they caused a call of this instruction)
                            // Since the keys are uniquely generated, we have a uniform distribution
                            double pX = 1.0 / instructionData.Value.Count;
                            double pXY = 1.0 / instructionData.Value.Count;

                            // Calculate mutual information
                            foreach(var addressCount in instructionAccessTraceCounts)
                            {
                                double pY = (double)addressCount.Value / instructionData.Value.Count;
                                mutualInformation += addressCount.Value * pXY * Math.Log(pXY / (pX * pY), 2);
                            }
                        }
                        else
                        {
                            // Build multiset of unique testcase/hash pairs, mapping these pairs to their counts
                            Dictionary<KeyValuePair<int, ulong>, int> testcaseIdHashMultiset = testcaseIdsGeneratingHashes
                                .SelectMany(ht => ht.Value.Select(t => new KeyValuePair<int, ulong>(_baseTestcaseIdLookup[t], ht.Key)))
                                .GroupBy(th => th)
                                .ToDictionary(th => th.Key, th => th.Count());

                            // Calculate mutual information term for each pair
                            foreach(var multisetEntry in testcaseIdHashMultiset)
                            {
                                double factor1 = (double)multisetEntry.Value / (_testcaseCount);
                                double factor2 = Math.Log((double)((_testcaseCount / randomizationDetectionMultiplier) * multisetEntry.Value) / instructionAccessTraceCounts[multisetEntry.Key.Value], 2);
                                mutualInformation += factor1 * factor2;
                            }
                        }
                        mutualInformationPerInstruction.Add(instructionData.Key, mutualInformation);
                        Console.Write($"\r  {++instructionIndex} of {_instructionTraces.Count}...");
                    }
                    Console.WriteLine("");
                }

                // Sort instructions by information loss and output
                Program.Log($"Analysis completed. Sorting instructions by mutual information value...\n", Program.LogLevel.Info);
                double maximumMutualInformation = 0.0;
                using(StreamWriter resultFileWriter = new StreamWriter(File.Open(Path.Combine(_resultDirectory, "mutual_information_instructions.txt"), FileMode.Create, FileAccess.Write)))
                    foreach(var instructionData in mutualInformationPerInstruction.OrderBy(mi => mi.Key).OrderByDescending(mi => mi.Value))
                    {
                        // Update maximum variable, so later a warning can be issued if there were not enough testcases
                        if(instructionData.Value > maximumMutualInformation)
                            maximumMutualInformation = instructionData.Value;

                        // Write entry
                        Program.Log($"Mutual information of instruction 0x{instructionData.Key.ToString("X8")}: {instructionData.Value.ToString("N3")} bits\n", Program.LogLevel.Success);
                        resultFileWriter.WriteLine($"Instruction 0x{instructionData.Key.ToString("X8")}: {instructionData.Value.ToString("N3")} bits");
                    }

                // Show warning if there likely were not enough testcases
                const double warnThreshold = 0.9;
                double testcaseCountBits = Math.Log((double)_testcaseCount / randomizationDetectionMultiplier, 2);
                if(testcaseCountBits - warnThreshold < maximumMutualInformation && maximumMutualInformation < testcaseCountBits + warnThreshold)
                    Program.Log("For some instructions the calculated mutual information is suspiciously near to the testcase range. It is recommended to run more testcases.\n", Program.LogLevel.Warning);
            }

            // Finished
            Program.Log($"Run completed.\n", Program.LogLevel.Info);
        }

        /// <summary>
        /// Schedules a testcase for tracing.
        /// This function is called when a new file in the testcase directory is detected. This represents the source step of the leakage detection pipeline.
        /// </summary>
        /// <param name="testcaseFilename">The filename of the new testcase.</param>
        /// <param name="duplicate">Sets whether this testcase is a duplicate of an immediately preceding one.</param>
        /// <returns>An ID for the new testcase.</returns>
        private int ScheduleTestcase(string testcaseFilename, bool duplicate)
        {
            // Get testcase name
            string testcaseName = Path.GetFileNameWithoutExtension(testcaseFilename);

            // Assign ID to the testcase
            int testcaseId = _testcaseNames.Count + 1; // 0 = prefix
            _testcaseNames.AddOrUpdate(testcaseId, testcaseName, (i, n) => n);

            // New testcase or duplicate?
            if(!duplicate)
                _lastUniqueTestcaseId = testcaseId;
            _baseTestcaseIdLookup.Add(testcaseId, _lastUniqueTestcaseId);
            return testcaseId;
        }

        /// <summary>
        /// Generates a Pin trace for the given testcase ID.
        /// </summary>
        /// <param name="testcaseId">The ID of the testcase for which a trace shall be created.</param>
        /// <returns>The ID of the current testcase.</returns>
        private int GenerateTrace(int testcaseId)
        {
            // Pass testcase to Pin tool and wait for output
            // TODO mention in documentation: FuzzingWrapper must not print to stdout, else this function might hang
            //Program.Log($"Generate trace for testcase #{testcaseId}...\n", Program.LogLevel.Debug);
            _pinToolProcess.StandardInput.WriteLine($"t {testcaseId}");
            _pinToolProcess.StandardInput.WriteLine(Path.Combine(_testcaseDirectory, _testcaseNames[testcaseId] + ".testcase"));
            while(true)
            {
                // Read Pin tool output
                //Program.Log($"Read from Pin tool stdout...\n", Program.LogLevel.Debug);
                string pinToolOutput = _pinToolProcess.StandardOutput.ReadLine();
                if(pinToolOutput == null)
                    Program.Log($"Error: Could not read from Pin tool standard output (null). Probably the process has exited early.", Program.LogLevel.Error);
                //Program.Log($"Pin tool output: {pinToolOutput}\n", Program.LogLevel.Debug);
                if(pinToolOutput[0] == 'i')
                {
                    // Image load
                    // Only handle this if we are still in prefix mode (testcase ID == 1 -> dummy)
                    if(testcaseId == 1 && _preprocessor == null)
                    {
                        // Parse image data
                        string[] imageData = pinToolOutput.Split('\t');
                        _images.Add(new TraceFilePreprocessor.ImageFileInfo
                        {
                            Interesting = (byte.Parse(imageData[1]) != 0),
                            StartAddress = ulong.Parse(imageData[2], System.Globalization.NumberStyles.HexNumber),
                            EndAddress = ulong.Parse(imageData[3], System.Globalization.NumberStyles.HexNumber),
                            Name = Path.GetFileName(imageData[4])
                        });
                    }
                    else
                        Program.Log("Warning: Image load ignored.\n", Program.LogLevel.Warning);
                }
                else if(pinToolOutput[0] == 't')
                {
                    // Testcase processing finished
                    int finishedTestcaseId = int.Parse(pinToolOutput.Split('\t')[1]);
                    if(finishedTestcaseId == 0)
                    {
                        // Prefix completed, time to create the preprocessor
                        Program.Log("Preprocessing prefix file...", Program.LogLevel.Info);
                        _preprocessor = new TraceFilePreprocessor(Path.Combine(_traceDirectory, "testcase_t0_0.trace"), Path.Combine(_traceDirectory, "testcase_"), _images);
                        _tracePrefixLength = _preprocessor.PrefixTraceEntryCount;
                        Program.Log($"Complete. Prefix length: {_tracePrefixLength}\n", Program.LogLevel.Success);

                        // Delete prefix file
                        File.Delete(Path.Combine(_traceDirectory, "testcase_t0_0.trace"));
                    }
                    else if(finishedTestcaseId == testcaseId)
                    {
                        // Pass testcase to next stage
                        //Program.Log($"Tracing testcase #{testcaseId} completed.\n", Program.LogLevel.Debug);
                        return testcaseId;
                    }
                    else
                        Program.Log("Error: Trace completion message with unexpected testcase ID received.\n", Program.LogLevel.Error);
                }
            }
        }

        /// <summary>
        /// Preprocesses the trace with the given testcase ID.
        /// </summary>
        /// <param name="testcaseId">The ID of the given testcase.</param>
        /// <returns>The ID of the current testcase.</returns>
        private int PreprocessTrace(int testcaseId)
        {
            // Load and preprocess the given testcase trace
            string traceFilePath = Path.Combine(_traceDirectory, $"testcase_t0_{testcaseId}.trace");
            _preprocessor.PreprocessTraceFile(traceFilePath, testcaseId);

            // Delete original trace to free up disk space
            // TODO make this configurable
            File.Delete(traceFilePath);

            // Store trace file name
            string preprocessedTraceFilePath = Path.Combine(_traceDirectory, $"testcase_{testcaseId}.trace.processed");
            _traceFileNames.AddOrUpdate(testcaseId, preprocessedTraceFilePath, (k, v) => v);

            // Pass testcase ID to next stage
            //Program.Log($"Preprocessing testcase #{testcaseId} completed.\n", Program.LogLevel.Debug);
            return testcaseId;
        }

        /// <summary>
        /// Compares the preprocessed trace of the given testcase with other testcases and removes all duplicates.
        /// Unique testcases are then compared with each other to identify mismatch classes.
        /// </summary>
        /// <param name="testcaseId">The ID of the given testcase.</param>
        private void CompareTraces(int testcaseId)
        {
            // Read file
            //Program.Log($"Filtering testcase #{testcaseId}...\n", Program.LogLevel.Debug);
            string testcaseFilePath = Path.Combine(_testcaseDirectory, _testcaseNames[testcaseId] + ".testcase");
            string preprocessedTraceFilePath = _traceFileNames[testcaseId];
            TraceFile currentTrace = new TraceFile(preprocessedTraceFilePath, _knownImages, _granularity);

            // Try to find identical trace, else add this one the list
            if(!_uniqueTraces.Any(te => new TraceFileComparer(te.Value, currentTrace, false).Compare()))
            {
                // This is a unique trace, that will be compared to all other later traces -> cache it in memory
                currentTrace.CacheEntries();

                // Compare the trace with all other unique traces to identify mismatches
                foreach(var uniqueTrace in _uniqueTraces)
                {
                    // Compare and write result to mismatch file (appending)
                    TraceFileComparer comparer = new TraceFileComparer(uniqueTrace.Value, currentTrace, false);
                    comparer.Compare();
                    using(StreamWriter mismatchFile = new StreamWriter(File.Open(Path.Combine(_resultDirectory, $"mismatch_{comparer.Result.ToString()}_{comparer.ComparisonErrorLine}.txt"), FileMode.Append)))
                    {
                        // Write testcase IDs and the failing entries
                        mismatchFile.WriteLine($"#{uniqueTrace.Key} {_testcaseNames[uniqueTrace.Key]}    ---    #{testcaseId} {_testcaseNames[testcaseId]}");
                        mismatchFile.WriteLine(comparer.ComparisonErrorItems.Item1.ToString());
                        mismatchFile.WriteLine(comparer.ComparisonErrorItems.Item2.ToString());
                        mismatchFile.WriteLine();
                    }
                }

                // Store trace
                _uniqueTraces.Add(testcaseId, currentTrace);

                // Copy testcase and trace file to "results" directory
                if(File.Exists(testcaseFilePath))
                    File.Copy(testcaseFilePath, Path.Combine(_resultDirectory, $"unique_{testcaseId}_{_testcaseNames[testcaseId]}.testcase"), true);
                File.Copy(preprocessedTraceFilePath, Path.Combine(_resultDirectory, $"unique_{testcaseId}_{_testcaseNames[testcaseId]}.trace.processed"), true);
                Program.Log($"New unique testcase! Unique testcases until now: {_uniqueTraces.Count}.\n", Program.LogLevel.Success);
            }

            // Delete original files to save disk space
            if(!_keepTraces)
                File.Delete(preprocessedTraceFilePath);

            // Finished
            //Program.Log($"Filtering testcase #{testcaseId} completed.\n", Program.LogLevel.Debug);
            if(testcaseId % 1000 == 0)
                Program.Log($"{testcaseId} testcases completed. Unique testcases until now: {_uniqueTraces.Count}.\n", Program.LogLevel.Info);
        }

        /// <summary>
        /// Further compresses the preprocessed trace of the given testcase, such that it can be used for efficient computation of mutual information.
        /// </summary>
        /// <param name="testcaseId">The ID of the given testcase.</param>
        private void CompressTrace(int testcaseId)
        {
            // Discard dummy testcase, since this often generates false positives due to some initialization calls
            if(testcaseId == 1)
                return;

            // Acquire MD5 object from pool
            MD5CryptoServiceProvider md5;
            while(!_md5Objects.TryTake(out md5))
            {
                // Wait a bit
                Program.Log("Could not instantly acquire a MD5 object.\n", Program.LogLevel.Warning);
                Task.Delay(100).Wait();
            }

            // Read trace file
            string preprocessedTraceFilePath = _traceFileNames[testcaseId];
            TraceFile currentTrace = new TraceFile(preprocessedTraceFilePath, _knownImages, _granularity);

            // Calculate hashes for full trace/prefix modes?
            if(_analysisMode == AnalysisModes.MutualInformation_TracePrefix || _analysisMode == AnalysisModes.MutualInformation_WholeTrace)
            {
                // Check whether hash storage object exists
                List<ulong> traceHashList = new List<ulong>(currentTrace.EntryCount - _tracePrefixLength);
                _mutualInformationHashes.AddOrUpdate(testcaseId, traceHashList, (k, v) => v);

                // Hash trace using partial MD5 (the hash only needs to be good enough to avoid too many collisions)
                unchecked
                {
                    // Hash depending on requested mode
                    byte[] traceHashState = new byte[16];
                    if(_analysisMode == AnalysisModes.MutualInformation_TracePrefix)
                    {
                        // Save hash of each prefix
                        foreach(long entry in TraceFileDiff.EncodeTraceEntries(currentTrace).Skip(_tracePrefixLength))
                        {
                            // Add entry to hash
                            Array.Copy(BitConverter.GetBytes(entry), traceHashState, 8);
                            traceHashState = md5.ComputeHash(traceHashState);
                            traceHashList.Add(BitConverter.ToUInt64(traceHashState, 8));
                        }
                    }
                    else
                    {
                        // Save hash of whole trace
                        foreach(long entry in TraceFileDiff.EncodeTraceEntries(currentTrace).Skip(_tracePrefixLength))
                        {
                            // Add entry to hash
                            Array.Copy(BitConverter.GetBytes(entry), traceHashState, 8);
                            traceHashState = md5.ComputeHash(traceHashState);
                        }
                        traceHashList.Add(BitConverter.ToUInt64(traceHashState, 8));
                    }
                }

                // Update longest trace hash variable
                _longestHashedTraceLength = traceHashList.Count;
            }
            else if(_analysisMode == AnalysisModes.MutualInformation_SingleInstruction)
            {
                // Generate list of encoded (relative) accesses for memory access instructions
                unchecked
                {
                    foreach(long entry in TraceFileDiff.EncodeTraceEntries(currentTrace).Skip(_tracePrefixLength))
                    {
                        // Memory access?
                        TraceEntryTypes entryType = (TraceEntryTypes)(entry & 0xF);
                        if(entryType != TraceEntryTypes.AllocMemoryRead && entryType != TraceEntryTypes.AllocMemoryWrite && entryType != TraceEntryTypes.ImageMemoryRead && entryType != TraceEntryTypes.ImageMemoryWrite)
                            continue;

                        // Extract instruction data from encoded entry
                        uint instructionOffset = ((uint)entry) >> 4; // Discard upper 32 and lower 4 bits
                        uint addressPart = (uint)(entry >> 32); // Discard lower 32 bits (since instruction offset is saved separately)

                        // Access test case hash list of current instruction
                        if(!_instructionTraces.ContainsKey(instructionOffset))
                            _instructionTraces.AddOrUpdate(instructionOffset, new ConcurrentDictionary<int, ulong>(), (k, v) => v);
                        ConcurrentDictionary<int, ulong> instructionTestcaseHashes = null;
                        while(!_instructionTraces.TryGetValue(instructionOffset, out instructionTestcaseHashes))
                            Debug.WriteLine("Race condition 1, retry..."); // Try again

                        if(!instructionTestcaseHashes.ContainsKey(testcaseId))
                            instructionTestcaseHashes.AddOrUpdate(testcaseId, 0, (k, v) => v);
                        ulong hashValue = 0;
                        while(!instructionTestcaseHashes.TryGetValue(testcaseId, out hashValue))
                            Debug.WriteLine("Race condition 2, retry..."); // Try again

                        // Update hash state using partial MD5 (the hash only needs to be good enough to avoid too many collisions)
                        instructionTestcaseHashes[testcaseId] = BitConverter.ToUInt64(md5.ComputeHash(BitConverter.GetBytes(hashValue ^ addressPart)), 0);
                    }
                }
            }

            // Put MD5 object back into pool
            _md5Objects.Add(md5);

            // Do cleanup
            if(!_keepTraces)
                File.Delete(preprocessedTraceFilePath);

            // Finished
            Interlocked.Increment(ref _testcaseCount);
            if(_testcaseCount % 100 == 0)
                Program.Log($"Processing {_testcaseCount} testcases completed.\n", Program.LogLevel.Info);
        }
    }

    /// <summary>
    /// The different supported analysis modes.
    /// </summary>
    public enum AnalysisModes : int
    {
        /// <summary>
        /// Do no analysis, just generate traces.
        /// </summary>
        None = 0,

        /// <summary>
        /// Do a plain trace comparison.
        /// </summary>
        Compare = 1,

        /// <summary>
        /// Compute the mutual information of whole traces.
        /// </summary>
        MutualInformation_WholeTrace = 2,

        /// <summary>
        /// Compute the mutual information of trace prefixes.
        /// </summary>
        MutualInformation_TracePrefix = 3,

        /// <summary>
        /// Compute the mutual information of single instructions.
        /// </summary>
        MutualInformation_SingleInstruction = 4,
    }

    /// <summary>
    /// Helper class for hashing and comparing byte arrays.
    /// </summary>
    class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[] x, byte[] y) => x.SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            // Simply return the most significant 4 bytes; if the byte arrays are random enough, this should have low collision probability
            uint hash = 0;
            for(int i = 0; i < Math.Min(4, obj.Length); ++i)
                hash ^= (uint)(obj[i] << (8 * i));
            return unchecked((int)hash);
        }
    }
}