using System.Threading;
using System.Threading.Tasks;

namespace Microwalk.FrameworkBase.Stages
{
    /// <summary>
    /// Abstract base class for the test case generation stage.
    /// </summary>
    public abstract class TestcaseStage : PipelineStage
    {
        /// <summary>
        /// Factory object for modules implementing this stage.
        /// </summary>
        public static ModuleFactory<TestcaseStage> Factory { get; } = new();

        /// <summary>
        /// Generates a new testcase and returns a fitting <see cref="TraceEntity"/> object.
        /// </summary>
        /// <param name="token">Cancellation token to stop test case generation early.</param>
        /// <returns></returns>
        public abstract Task<TraceEntity> NextTestcaseAsync(CancellationToken token);

        /// <summary>
        /// Returns whether the test case stage has completed and does not produce further inputs. This method is called before requesting a new test case.
        /// </summary>
        /// <returns></returns>
        public abstract Task<bool> IsDoneAsync();

        /// <summary>
        /// The testcase stage does not allow parallelism.
        /// </summary>
        public sealed override bool SupportsParallelism { get; } = false;
    }
}