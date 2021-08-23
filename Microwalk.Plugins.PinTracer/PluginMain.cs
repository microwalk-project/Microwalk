using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Stages;

namespace Microwalk.Plugins.PinTracer
{
    public class PluginMain : PluginBase
    {
        public override void Register()
        {
            TraceStage.Factory.Register<PinTraceGenerator>();
            PreprocessorStage.Factory.Register<PinTracePreprocessor>();
            PreprocessorStage.Factory.Register<PinTraceDumper>();
        }
    }
}