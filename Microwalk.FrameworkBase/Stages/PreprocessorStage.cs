using System.Threading.Tasks;

namespace Microwalk.FrameworkBase.Stages
{
    /// <summary>
    /// Abstract base class for the trace preprocessor stage.
    /// </summary>
    public abstract class PreprocessorStage : PipelineStage
    {
        /// <summary>
        /// Factory object for modules implementing this stage.
        /// </summary>
        public static ModuleFactory<PreprocessorStage> Factory { get; } = new();

        /// <summary>
        /// Performs preprocessing on the given trace. This method is expected to be thread-safe.
        /// </summary>
        /// <param name="traceEntity">The trace entity pointing to the raw trace data.</param>
        /// <returns></returns>
        public abstract Task PreprocessTraceAsync(TraceEntity traceEntity);
    }
}