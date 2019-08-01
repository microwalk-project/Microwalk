using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk.Analysis.Modules
{
    [FrameworkModule("dump", "Provides functionality to dump trace files in a human-readable form.")]
    class TraceDumper : AnalysisStage
    {
        int c = 0;

        public override async Task AddTraceAsync(TraceEntity traceEntity)
        {
            await Task.Delay(50);
            ++c;
        }

        public override async Task FinishAsync()
        {
            await Logger.LogResultAsync("Analysis result: " + c);
        }

        internal override Task InitAsync(YamlMappingNode moduleOptions)
        {
            return Task.CompletedTask;
        }

        public override async Task UninitAsync()
        {
            await base.UninitAsync();
        }
    }
}
