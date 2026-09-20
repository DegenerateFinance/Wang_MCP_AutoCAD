using System.Reflection;
using System.Runtime.Loader;

namespace Wang_MCP_AutoCAD.Loader;

/// <summary>
/// A fresh, unloadable context is created per reload. Types from assemblies
/// already resident in AssemblyLoadContext.Default (AutoCAD's own managed DLLs,
/// Wang_MCP_AutoCAD.Contracts, the BCL) are shared rather than duplicated, so
/// the Loader and the freshly loaded plugin agree on type identity.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    public PluginLoadContext(string name) : base(name, isCollectible: true)
    {
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        foreach (var assembly in Default.Assemblies)
        {
            if (string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
            {
                return assembly;
            }
        }

        return null;
    }
}
