using System;
using System.Collections.Generic;
using System.Text;

namespace Microwalk
{
    /// <summary>
    /// Contains metadata about a framework module.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    class Module : Attribute
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
        public Module(string name, string description)
        {
            Name = name;
            Description = description;
        }
    }
}
