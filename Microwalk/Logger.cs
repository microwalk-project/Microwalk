using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microwalk.FrameworkBase.Exceptions;
using Nito.AsyncEx;
using YamlDotNet.RepresentationModel;

namespace Microwalk
{
    /// <summary>
    /// Provides functionality to write log messages to console and file system.
    /// The static logging functions are thread safe.
    /// </summary>
    internal class Logger : ILogger
    {
        /// <summary>
        /// Data assigned with each log level.
        /// </summary>
        private static readonly Dictionary<LogLevel, (ConsoleColor Foreground, ConsoleColor Background, string String)> _logLevelData =
            new()
            {
                [LogLevel.Debug] = (ConsoleColor.Gray, ConsoleColor.Black, "dbug"),
                [LogLevel.Info] = (ConsoleColor.Cyan, ConsoleColor.Black, "info"),
                [LogLevel.Warning] = (ConsoleColor.Yellow, ConsoleColor.Black, "warn"),
                [LogLevel.Error] = (ConsoleColor.White, ConsoleColor.Red, "fail"),
                [LogLevel.Result] = (ConsoleColor.Green, ConsoleColor.Black, "rslt")
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
        private readonly LogLevel _logLevel = LogLevel.Error;

        /// <summary>
        /// Stream writer for the log output file ("null" if unused).
        /// </summary>
        private readonly StreamWriter? _outputFileWriter = null;

        /// <summary>
        /// Lock for coordinated access to the console.
        /// </summary>
        private readonly AsyncLock _lock = new();

        /// <summary>
        /// Initializes the logger.
        /// </summary>
        /// <param name="loggerOptions">The logger options, as specified in the configuration file.</param>
        internal Logger(YamlMappingNode? loggerOptions)
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
                            throw new ConfigurationException($"Invalid node value for \"{keyNode.Value}\"");
                        break;
                    }

                    case "file":
                    {
                        // Parse file name
                        if(!(optionNode.Value is YamlScalarNode valueNode))
                            throw new ConfigurationException($"Invalid node value for \"{keyNode.Value}\"");

                        // Initialize file stream
                        // Exceptions will be handled by caller
                        _outputFileWriter = new StreamWriter(File.Open(valueNode.Value!, FileMode.Create, FileAccess.Write, FileShare.Read));
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
        private static void SetConsoleColor(ConsoleColor foreground, ConsoleColor background)
        {
            Console.ForegroundColor = foreground;
            Console.BackgroundColor = background;
        }

        /// <summary>
        /// Logs the given message, if the log level is high enough.
        /// </summary>
        /// <param name="logLevel">Log level.</param>
        /// <param name="message">Message.</param>
        private async Task LogAsync(LogLevel logLevel, string message)
        {
            // Check log level
            // Only debug outputs and warnings can be suppressed
            if(logLevel < _logLevel && (logLevel == LogLevel.Debug || logLevel == LogLevel.Warning))
                return;

            // Message format:
            // <hour>:<min>:<sec> [<log level>] <message>

            // Prepare message parts
            var logLevelData = _logLevelData[logLevel];
            TimeSpan elapsedTime = DateTime.Now - _startupTime;
            string elapsedTimeString = elapsedTime.ToString("hh\\:mm\\:ss");
            string logLevelString = logLevelData.String;

            // Ensure that message is correctly indented (align with first line)
            int indent = elapsedTimeString.Length + 2 + logLevelString.Length + 2;
            string indentedMessage = IndentString(message, indent, 1);

            // Wait for I/O to be available
            using(await _lock.LockAsync())
            {
                // Time
                Console.Write(elapsedTimeString);

                // Log level
                Console.Write(" [");
                SetConsoleColor(logLevelData.Foreground, logLevelData.Background);
                Console.Write(logLevelData.String);
                SetConsoleColor(_defaultConsoleColor.Foreground, _defaultConsoleColor.Background);
                Console.Write("] ");

                // Message
                Console.Write(indentedMessage);

                // Line break
                Console.WriteLine();

                // Write to file, if requested
                if(_outputFileWriter != null)
                    await _outputFileWriter.WriteLineAsync($"{elapsedTimeString} [{logLevelString}] {indentedMessage}");
            }
        }

        public void Dispose()
        {
            // Close file stream
            _outputFileWriter?.Dispose();
        }

        /// <summary>
        /// Utility function. Indents all lines of the given string by the given amount of spaces.
        /// </summary>
        /// <param name="str">String to be indented.</param>
        /// <param name="indentation">Indentation.</param>
        /// <param name="skipLines">Optional. Allows to skip the n first lines.</param>
        /// <returns></returns>
        public static string IndentString(string str, int indentation, int skipLines = 0)
        {
            string indentString = new(' ', indentation);

            string[] lines = str.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            for(int i = skipLines; i < lines.Length; ++i)
                lines[i] = indentString + lines[i];

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// Logs the given debug message.
        /// </summary>
        /// <param name="message">Message.</param>
        public async Task LogDebugAsync(string message) => await LogAsync(LogLevel.Debug, message);

        /// <summary>
        /// Logs the given warning.
        /// </summary>
        /// <param name="message">Message.</param>
        public async Task LogWarningAsync(string message) => await LogAsync(LogLevel.Warning, message);

        /// <summary>
        /// Logs the given error.
        /// </summary>
        /// <param name="message">Message.</param>
        public async Task LogErrorAsync(string message) => await LogAsync(LogLevel.Error, message);

        /// <summary>
        /// Logs the given result.
        /// </summary>
        /// <param name="message">Message.</param>
        public async Task LogResultAsync(string message) => await LogAsync(LogLevel.Result, message);

        /// <summary>
        /// Logs the given info message.
        /// Use this for status messages, which should always be displayed.
        /// </summary>
        /// <param name="message">Message.</param>
        public async Task LogInfoAsync(string message) => await LogAsync(LogLevel.Info, message);
    }

    /// <summary>
    /// Contains the different log levels.
    /// </summary>
    internal enum LogLevel
    {
        Debug = 0,
        Warning = 10,
        Error = 20,
        Result = 30,
        Info = 40,
    }
}