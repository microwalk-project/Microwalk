using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TestcaseGeneration
{
    /// <summary>
    /// Abstract base class for the test case generation stage.
    /// </summary>
    abstract class TestcaseStage
    {
        #region Abstract methods

        /// <summary>
        /// Initializes the stage with the given configuration data.
        /// </summary>
        /// <param name="moduleOptions">The module-specific options, as defined in the configuration file.</param>
        protected abstract void Init(YamlMappingNode moduleOptions);

        /// <summary>
        /// Generates a new testcase and returns a fitting <see cref="TraceEntity"/> object.
        /// </summary>
        /// <returns></returns>
        public abstract TraceEntity NextTestcase();

        #endregion

        #region Factory code

        /// <summary>
        /// Contains a list of modules that can be implement this stage.
        /// </summary>
        private static readonly Dictionary<string, Type> _registeredModules = new Dictionary<string, Type>();

        /// <summary>
        /// Registers the given type as a module for the given name.
        /// </summary>
        /// <typeparam name="T">Module type.</typeparam>
        /// <param name="name">Module name.</param>
        public static void Register<T>() where T : TestcaseStage
        {
            // Check whether module attributes are present
            var attribute = typeof(T).GetCustomAttributes<Module>().FirstOrDefault();
            if(attribute == null)
                throw new ArgumentException($"The given module implementation \"{ typeof(T).FullName }\" does not implement the \"{ typeof(Module).FullName }\" attribute.");

            // Register module
            _registeredModules.Add(attribute.Name, typeof(T));
        }

        /// <summary>
        /// Returns a new, initialized instance of the given module.
        /// </summary>
        /// <param name="name">The name of the requested module.</param>
        /// <param name="moduleOptions">The module-specific options, as defined in the configuration file.</param>
        /// <returns></returns>
        public static TestcaseStage Create(string name, YamlMappingNode moduleOptions)
        {
            // Check parameters
            if(!_registeredModules.ContainsKey(name))
                throw new ArgumentException($"Can not find a module named \"{ name }\".");

            // Create module
            var module = (TestcaseStage)Activator.CreateInstance(_registeredModules[name]);
            module.Init(moduleOptions);
            return module;
        }

        #endregion
    }
}
