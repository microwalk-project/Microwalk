using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TracePreprocessing.Modules
{
    [FrameworkModule("passthrough", "Passes through the raw traces without preprocessing.")]
    internal class Passthrough : PreprocessorStage
    {
        public override bool SupportsParallelism { get; } = true;
        
        internal override Task InitAsync(YamlMappingNode moduleOptions)
        {
            return Task.CompletedTask;
        }

        public override Task PreprocessTraceAsync(TraceEntity traceEntity)
        {
            return Task.CompletedTask;
        }
    }
}