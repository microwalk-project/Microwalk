using System;
using System.Reflection;
using System.Runtime.Loader;

namespace Microwalk
{
    /// <summary>
    /// Custom resolver to reduce probability of dependency conflicts.
    /// See https://docs.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support
    /// </summary>
    public class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return assemblyPath != null ? LoadFromAssemblyPath(assemblyPath) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string dllName)
        {
            var libraryPath = _resolver.ResolveUnmanagedDllToPath(dllName);
            return libraryPath != null ? LoadUnmanagedDllFromPath(libraryPath) : IntPtr.Zero;
        }
    }
}