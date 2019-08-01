using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TraceGeneration.Modules
{
    [FrameworkModule("pin", "Generates traces using a Pin tool.")]
    class PinTraceGenerator : TraceStage
    {
        public override async Task GenerateTraceAsync(TraceEntity traceEntity)
        {
            await Logger.LogInfoAsync("Trace #" + traceEntity.Id);
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
