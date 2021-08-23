using System;

namespace Microwalk.FrameworkBase
{
    /// <summary>
    /// Contains metadata about a framework module.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public class FrameworkModule : Attribute
    {
        /// <summary>
        /// The module's name. This name is also used to specify the module in the configuration file.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The module's description. This is used for displaying help texts.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Contains metadata about a framework module.
        /// </summary>
        /// <param name="name">The module's name. This name is also used to specify the module in the configuration file.</param>
        /// <param name="description">The module's description. This is used for displaying help texts.</param>
        public FrameworkModule(string name, string description)
        {
            Name = name;
            Description = description;
        }
    }
}