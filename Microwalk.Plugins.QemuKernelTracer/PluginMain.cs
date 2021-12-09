using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Stages;

namespace Microwalk.Plugins.QemuKernelTracer;

public class PluginMain : PluginBase
{
    public override void Register()
    {
        TraceStage.Factory.Register<TraceConverter>();
    }
}