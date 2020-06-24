using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk
{
    /// <summary>
    /// Contains factory functionality to register and create modules for a given pipeline stage.
    /// </summary>
    /// <typeparam name="TStage">The base class type of the pipeline stage.</typeparam>
    internal class ModuleFactory<TStage> where TStage : PipelineStage
    {
        /// <summary>
        /// Contains a list of modules that can be implement this stage.
        /// </summary>
        private readonly Dictionary<string, Type> _registeredModules = new Dictionary<string, Type>();

        /// <summary>
        /// Registers the given type as a module for the given name.
        /// </summary>
        /// <typeparam name="TModule">Module type.</typeparam>
        public void Register<TModule>() where TModule : TStage
        {
            // Check whether module attributes are present
            var attribute = typeof(TModule).GetCustomAttributes<FrameworkModule>().FirstOrDefault();
            if(attribute == null)
                throw new ArgumentException(
                    $"The given module implementation \"{typeof(TModule).FullName}\" does not implement the \"{typeof(FrameworkModule).FullName}\" attribute.");

            // Register module
            if(_registeredModules.ContainsKey(attribute.Name))
                throw new ArgumentException($"There is already a module \"{attribute.Name}\" registered.");
            _registeredModules.Add(attribute.Name, typeof(TModule));
        }

        /// <summary>
        /// Returns a new, initialized instance of the given module.
        /// </summary>
        /// <param name="name">The name of the requested module.</param>
        /// <param name="moduleOptions">The module-specific options, as defined in the configuration file.</param>
        /// <returns></returns>
        public async Task<TStage> CreateAsync(string name, YamlMappingNode moduleOptions)
        {
            // Check parameters
            if(!_registeredModules.ContainsKey(name))
                throw new ArgumentException($"Can not find a module named \"{name}\".");

            // Create module
            var module = (TStage)Activator.CreateInstance(_registeredModules[name]);
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
                // The attribute can be safely assumed to be present, since it has been retrieved upon registering
                var attribute = module.Value.GetCustomAttributes<FrameworkModule>().FirstOrDefault();
                yield return (attribute.Name, attribute.Description);
            }
        }
    }
}