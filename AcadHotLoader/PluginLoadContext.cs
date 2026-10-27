using System.Reflection;
using System.Runtime.Loader;

namespace AcadHotLoader;

/// <summary>
/// A fresh, unloadable context is created per HOTLOAD. Types from assemblies already
/// resident in AssemblyLoadContext.Default (AutoCAD's own managed DLLs, the BCL) are shared
/// rather than duplicated, so the hot loader and the freshly loaded plugin agree on type
/// identity.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    public PluginLoadContext(string name) : base(name, isCollectible: true)
    {
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        foreach (Assembly assembly in Default.Assemblies)
        {
            if (string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
            {
                return assembly;
            }
        }

        return null;
    }
}
