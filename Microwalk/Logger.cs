using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk
{
    /// <summary>
    /// Provides functionality to write log messages to console and file system.
    /// The static logging functions are thread safe.
    /// </summary>
    class Logger : IDisposable
    {
        /// <summary>
        /// Logger instance.
        /// </summary>
        private static Logger _instance;

        /// <summary>
        /// Colors assigned for each log level.
        /// </summary>
        private readonly Dictionary<LogLevel, (ConsoleColor Foreground, ConsoleColor Background)> _logLevelColors = new Dictionary<LogLevel, (ConsoleColor Foreground, ConsoleColor Background)>
        {
            { LogLevel.Debug, ( ConsoleColor.Cyan, ConsoleColor.Black) },
            { LogLevel.Info, ( ConsoleColor.Gray, ConsoleColor.Black) },
            { LogLevel.Warning, ( ConsoleColor.Yellow, ConsoleColor.Black) },
            { LogLevel.Error, ( ConsoleColor.White, ConsoleColor.Red) },
            { LogLevel.Result, ( ConsoleColor.Green, ConsoleColor.Black) },
        };

        /// <summary>
        /// Contains the initial console colors, that are used as default text color.
        /// </summary>
        private readonly (ConsoleColor Foreground, ConsoleColor Background) _defaultConsoleColor;

        /// <summary>
        /// The time when the logger was initialized.
        /// </summary>
        private readonly DateTime _startupTime;

        /// <summary>
        /// The minimum log level that is displayed.
        /// </summary>
        private readonly LogLevel _logLevel = LogLevel.Info;

        /// <summary>
        /// Stream writer for the log output file ("null" if unused).
        /// </summary>
        private readonly StreamWriter _outputFileWriter = null;

        /// <summary>
        /// Lock for coordinated access to the console.
        /// </summary>
        private readonly AsyncLock _lock = new AsyncLock();

        /// <summary>
        /// Initializes the logger.
        /// </summary>
        /// <param name="loggerOptions">The logger options, as specified in the configuration file.</param>
        private Logger(YamlMappingNode loggerOptions)
        {
            // Setup internal state
            _defaultConsoleColor = (Console.ForegroundColor, Console.BackgroundColor);
            _startupTime = DateTime.Now;

            // Parse options
            if(loggerOptions == null)
                return;
            foreach(var optionNode in loggerOptions.Children)
            {
                // Sanity check
                if(!(optionNode.Key is YamlScalarNode keyNode))
                    throw new ConfigurationException("Invalid key node type.");

                // Get option
                switch(keyNode.Value)
                {
                    case "log-level":
                    {
                        // Parse log level
                        if(!(optionNode.Value is YamlScalarNode valueNode) || !Enum.TryParse(valueNode.Value, true, out _logLevel))
                            throw new ConfigurationException($"Invalid value node for \"{keyNode.Value}\"");
                        break;
                    }

                    case "file":
                    {
                        // Parse file name
                        if(!(optionNode.Value is YamlScalarNode valueNode))
                            throw new ConfigurationException($"Invalid value node for \"{keyNode.Value}\"");

                        // Initialize file stream
                        // Exceptions will be handled by caller
                        _outputFileWriter = new StreamWriter(File.Open(valueNode.Value, FileMode.Create, FileAccess.Write, FileShare.Read));
                        break;
                    }

                    default:
                    {
                        throw new ConfigurationException("Unknown configuration key.");
                    }
                }
            }
        }

        /// <summary>
        /// Sets the current console color.
        /// </summary>
        /// <param name="foreground">Foreground color.</param>
        /// <param name="background">Background color.</param>
        private void SetConsoleColor((ConsoleColor Foreground, ConsoleColor Background) color)
        {
            Console.ForegroundColor = color.Foreground;
            Console.BackgroundColor = color.Background;
        }

        /// <summary>
        /// Logs the given message, if the log level is high enough.
        /// </summary>
        /// <param name="logLevel">Log level.</param>
        /// <param name="message">Message.</param>
        private async Task LogAsync(LogLevel logLevel, string message)
        {
            // Check log level
            if(logLevel < _logLevel)
                return;

            // Message format:
            // <hour>:<min>:<sec> [<log level>] <message>

            // Prepare message parts
            TimeSpan elapsedTime = DateTime.Now - _startupTime;
            string elapsedTimeString = elapsedTime.ToString("hh\\:mm\\:ss");
            string logLevelString = logLevel.ToString().ToLower();

            // Ensure that message is correctly indented
            int indent = elapsedTimeString.Length + 2 + logLevelString.Length + 2;
            string indentString = new string(' ', indent);
            string[] messageLines = message.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            for(int i = 1; i < messageLines.Length; ++i)
                messageLines[i] = indentString + messageLines[i];
            string indentedMessage = string.Join(Environment.NewLine, messageLines);

            // Wait for I/O to be available
            using(await _lock.LockAsync())
            {
                // Time
                Console.Write(elapsedTimeString);

                // Log level
                Console.Write(" [");
                SetConsoleColor(_logLevelColors[logLevel]);
                Console.Write(logLevelString);
                SetConsoleColor(_defaultConsoleColor);
                Console.Write("] ");

                // Message
                Console.Write(indentedMessage);

                // Line break
                Console.WriteLine();

                // Write to file, if requested
                if(_outputFileWriter != null)
                    _outputFileWriter.WriteLine($"{elapsedTimeString} [{logLevelString}] {indentedMessage}");
            }
        }

        public void Dispose()
        {
            // Close file stream
            _outputFileWriter.Dispose();
        }

        /// <summary>
        /// Initializes the logger singleton.
        /// </summary>
        /// <param name="loggerOptions">The logger options, as specified in the configuration file.</param>
        internal static void Initialize(YamlMappingNode loggerOptions)
        {
            // Initialize singleton
            _instance = new Logger(loggerOptions);
        }

        /// <summary>
        /// Returns whether the logger has been initialized yet.
        /// </summary>
        /// <returns></returns>
        internal static bool IsInitialized() => _instance != null;

        /// <summary>
        /// Performs some cleanup, like closing the output file handle.
        /// </summary>
        internal static void Deinitialize()
        {
            // Clean up
            _instance.Dispose();
        }

        /// <summary>
        /// Logs the given debug message.
        /// </summary>
        /// <param name="message">Message.</param>
        public static async Task LogDebugAsync(string message) => await _instance.LogAsync(LogLevel.Debug, message);

        /// <summary>
        /// Logs the given info message.
        /// </summary>
        /// <param name="message">Message.</param>
        public static async Task LogInfoAsync(string message) => await _instance.LogAsync(LogLevel.Info, message);

        /// <summary>
        /// Logs the given warning.
        /// </summary>
        /// <param name="message">Message.</param>
        public static async Task LogWarningAsync(string message) => await _instance.LogAsync(LogLevel.Warning, message);

        /// <summary>
        /// Logs the given error.
        /// </summary>
        /// <param name="message">Message.</param>
        public static async Task LogErrorAsync(string message) => await _instance.LogAsync(LogLevel.Error, message);

        /// <summary>
        /// Logs the given result.
        /// </summary>
        /// <param name="message">Message.</param>
        public static async Task LogResultAsync(string message) => await _instance.LogAsync(LogLevel.Result, message);
    }

    /// <summary>
    /// Contains the different log levels.
    /// </summary>
    enum LogLevel : int
    {
        Debug = 0,
        Info = 10,
        Warning = 20,
        Error = 30,
        Result = 40
    }
}
