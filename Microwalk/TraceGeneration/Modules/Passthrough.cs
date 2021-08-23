using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Stages;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TraceGeneration.Modules
{
    [FrameworkModule("passthrough", "Passes through the test cases without generating traces.")]
    internal class Passthrough : TraceStage
    {
        public override bool SupportsParallelism { get; } = true;
        protected override Task InitAsync(YamlMappingNode? moduleOptions)
        {
            return Task.CompletedTask;
        }

        public override Task UnInitAsync()
        {
            return Task.CompletedTask;
        }

        public override Task GenerateTraceAsync(TraceEntity traceEntity)
        {
            return Task.CompletedTask;
        }
    }
}