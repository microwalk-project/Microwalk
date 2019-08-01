using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk
{
    /// <summary>
    /// Contains factory functionality to register and create modules for a given pipeline stage.
    /// </summary>
    /// <typeparam name="S">The base class type of the pipeline stage.</typeparam>
    class ModuleFactory<S> where S : PipelineStage
    {
        /// <summary>
        /// Contains a list of modules that can be implement this stage.
        /// </summary>
        private readonly Dictionary<string, Type> _registeredModules = new Dictionary<string, Type>();

        /// <summary>
        /// Registers the given type as a module for the given name.
        /// </summary>
        /// <typeparam name="M">Module type.</typeparam>
        public void Register<M>() where M : S
        {
            // Check whether module attributes are present
            var attribute = typeof(M).GetCustomAttributes<FrameworkModule>().FirstOrDefault();
            if(attribute == null)
                throw new ArgumentException($"The given module implementation \"{ typeof(M).FullName }\" does not implement the \"{ typeof(FrameworkModule).FullName }\" attribute.");

            // Register module
            if(_registeredModules.ContainsKey(attribute.Name))
                throw new ArgumentException($"There is already a module \"{ attribute.Name }\" registered.");
            _registeredModules.Add(attribute.Name, typeof(M));
        }

        /// <summary>
        /// Returns a new, initialized instance of the given module.
        /// </summary>
        /// <param name="name">The name of the requested module.</param>
        /// <param name="moduleOptions">The module-specific options, as defined in the configuration file.</param>
        /// <returns></returns>
        public async Task<S> CreateAsync(string name, YamlMappingNode moduleOptions)
        {
            // Check parameters
            if(!_registeredModules.ContainsKey(name))
                throw new ArgumentException($"Can not find a module named \"{ name }\".");

            // Create module
            var module = (S)Activator.CreateInstance(_registeredModules[name]);
            await module.InitAsync(moduleOptions);
            return module;
        }

        /// <summary>
        /// Returns a list of supported module names and descriptions.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<(string Name, string Description)> GetSupportedModules()
        {
            // Returns module list
            foreach(var module in _registeredModules)
            {
                // Get metadata
                var attribute = module.Value.GetCustomAttributes<FrameworkModule>().FirstOrDefault();
                yield return (attribute.Name, attribute.Description);
            }
        }
    }
}
