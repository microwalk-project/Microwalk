using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk
{
    /// <summary>
    /// Abstract base class for pipeline stages.
    /// TODO Convert this into an interface with default functions when C# 8 becomes available
    /// </summary>
    abstract class PipelineStage
    {
        /// <summary>
        /// Returns whether the stage is thread-safe and thus supports parallel execution.
        /// </summary>
        public abstract bool SupportsParallelism { get; }

        /// <summary>
        /// Initializes the stage with the given configuration data.
        /// </summary>
        /// <param name="moduleOptions">The module-specific options, as defined in the configuration file.</param>
        internal abstract Task InitAsync(YamlMappingNode moduleOptions);

        /// <summary>
        /// Performs clean up.
        /// </summary>
        /// <returns></returns>
        public virtual Task UninitAsync() => Task.CompletedTask;
    }
}
