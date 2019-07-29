using Microwalk.TestcaseGeneration;
using System;

namespace Microwalk
{
    class Program
    {
        static void Main(string[] args)
        {
            // Register modules
            // Testcase generation
            TestcaseStage.Register<TestcaseGeneration.Modules.RandomTestcaseGenerator>();

            TestcaseStage.Create("random", null);

            Console.ReadLine();
        }
    }
}
