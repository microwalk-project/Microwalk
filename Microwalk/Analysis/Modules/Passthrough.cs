using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

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

        internal override Task InitAsync(YamlMappingNode moduleOptions)
        {
            return Task.CompletedTask;
        }
    }
}
