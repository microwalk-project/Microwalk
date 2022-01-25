using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Configuration;
using Microwalk.FrameworkBase.Stages;

namespace Microwalk.Analysis.Modules
{
    [FrameworkModule("passthrough", "Ignores all passed traces.")]
    internal class Passthrough : AnalysisStage
    {
        public override bool SupportsParallelism => true;

        public override Task AddTraceAsync(TraceEntity traceEntity)
        {
            return Task.CompletedTask;
        }

        public override Task FinishAsync()
        {
            return Logger.LogResultAsync("Passthrough analysis module completed.");
        }

        protected override Task InitAsync(MappingNode? moduleOptions)
        {
            return Task.CompletedTask;
        }
       
        public override Task UnInitAsync()
        {
            return Task.CompletedTask;
        }
    }
}
