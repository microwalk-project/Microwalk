using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Stages;

namespace Microwalk.Plugins.JavascriptTracer;

public class PluginMain : PluginBase
{
    public override void Register()
    {
        PreprocessorStage.Factory.Register<JsTracePreprocessor>();
    }
}