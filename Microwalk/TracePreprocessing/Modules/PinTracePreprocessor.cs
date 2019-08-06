using Microwalk.TraceGeneration;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TracePreprocessing.Modules
{
    [FrameworkModule("pin", "Preprocesses trace generated with the Pin tool.")]
    class PinTracePreprocessor : PreprocessorStage
    {
        int c = 0;
        public override Task PreprocessTraceAsync(TraceEntity traceEntity)
        {
            c++;
            //if(c == 30)
            //    throw new ArgumentException("test");
            return Task.CompletedTask;
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
