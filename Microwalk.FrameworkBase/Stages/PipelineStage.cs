using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk.FrameworkBase.Stages
{
    /// <summary>
    /// Abstract base class for pipeline stages.
    /// </summary>
    public abstract class PipelineStage
    {
        protected ILogger Logger { get; private set; } = null!; // Will always be initialized by CreateAsync() method

        /// <summary>
        /// Returns whether the stage is thread-safe and thus supports parallel execution.
        /// </summary>
        public abstract bool SupportsParallelism { get; }

        /// <summary>
        /// Creates a new instance of this stage with the given configuration data.
        /// </summary>
        /// <param name="logger">A logger instance for printing infos, errors and debug outputs.</param>
        /// <param name="moduleOptions">The module-specific options, as defined in the configuration file.</param>
        public Task CreateAsync(ILogger logger, YamlMappingNode? moduleOptions)
        {
            Logger = logger;

            return InitAsync(moduleOptions);
        }

        /// <summary>
        /// Initializes the stage with the given configuration data.
        /// </summary>
        /// <param name="moduleOptions">The module-specific options, as defined in the configuration file.</param>
        protected abstract Task InitAsync(YamlMappingNode? moduleOptions);

        /// <summary>
        /// Performs clean up.
        /// </summary>
        /// <returns></returns>
        public abstract Task UnInitAsync();
    }
}