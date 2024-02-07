using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microwalk.FrameworkBase.Configuration;
using Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes;

namespace Microwalk.FrameworkBase.Stages;

/// <summary>
/// Abstract base class for pipeline stages.
/// </summary>
public abstract class PipelineStage
{
    /// <summary>
    /// A logger instance for printing infos, errors and debug outputs.
    /// </summary>
    protected ILogger Logger { get; private set; } = null!; // Will always be initialized by CreateAsync() method

    /// <summary>
    /// Returns whether the stage is thread-safe and thus supports parallel execution.
    /// </summary>
    public abstract bool SupportsParallelism { get; }

    /// <summary>
    /// Cancellation token for controlling the pipeline.
    /// If this is cancelled, the respective pipeline stages should abort.
    /// </summary>
    protected CancellationToken PipelineToken { get; private set; }

    /// <summary>
    /// The maximum size of a preprocessed trace entry.
    /// </summary>
    protected static readonly int MaxPreprocessedTraceEntrySize = new[]
    {
        Branch.EntrySize,
        HeapAllocation.EntrySize,
        HeapFree.EntrySize,
        HeapMemoryAccess.EntrySize,
        ImageMemoryAccess.EntrySize,
        StackAllocation.EntrySize,
        StackMemoryAccess.EntrySize
    }.Max();

    /// <summary>
    /// Creates a new instance of this stage with the given configuration data.
    /// </summary>
    /// <param name="logger">A logger instance for printing infos, errors and debug outputs.</param>
    /// <param name="moduleOptions">The module-specific options, as defined in the configuration file.</param>
    /// <param name="cancellationToken">Cancellation token for safely stopping the pipeline.</param>
    public Task CreateAsync(ILogger logger, MappingNode? moduleOptions, CancellationToken cancellationToken)
    {
        Logger = logger;
        PipelineToken = cancellationToken;

        return InitAsync(moduleOptions);
    }

    /// <summary>
    /// Initializes the stage with the given configuration data.
    /// </summary>
    /// <param name="moduleOptions">The module-specific options, as defined in the configuration file.</param>
    protected abstract Task InitAsync(MappingNode? moduleOptions);

    /// <summary>
    /// Performs clean up.
    /// </summary>
    /// <returns></returns>
    public abstract Task UnInitAsync();
}