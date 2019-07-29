using System;
using System.Collections.Generic;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TestcaseGeneration.Modules
{
    [Module("random", "Allows to generate (cryptographically) random testcases, that satisfy certain properties.")]
    class RandomTestcaseGenerator : TestcaseStage
    {
        protected override void Init(YamlMappingNode moduleOptions)
        {
            throw new NotImplementedException();
        }

        public override TraceEntity NextTestcase()
        {
            throw new NotImplementedException();
        }
    }
}
