using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TraceGeneration.Modules
{
    [FrameworkModule("passthrough", "Passes through the test cases without generating traces.")]
    internal class Passthrough : TraceStage
    {
        public override bool SupportsParallelism { get; } = true;
        internal override Task InitAsync(YamlMappingNode moduleOptions)
        {
            return Task.CompletedTask;
        }

        public override Task GenerateTraceAsync(TraceEntity traceEntity)
        {
            return Task.CompletedTask;
        }
    }
}