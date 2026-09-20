using System.Reflection;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace Wang_MCP_AutoCAD.Loader;

/// <summary>
/// Dev-time convenience only: production deployments NETLOAD Wang_MCP_AutoCAD.dll
/// directly, same as any other AutoCAD plugin. NETLOAD'ed assemblies live in
/// AssemblyLoadContext.Default forever, so re-NETLOADing after a rebuild fails
/// with "Assembly with same name is already loaded" (see CLAUDE.md). NETLOAD
/// this Loader assembly once per dev session instead; MCPRELOAD hot-reloads a
/// chosen assembly from disk into a fresh collectible AssemblyLoadContext,
/// mirroring what NETLOAD itself does: prompt for a .dll via the standard file
/// dialog, then find IExtensionApplication implementations and call
/// Initialize()/Terminate() on them.
/// </summary>
public class Loader : IExtensionApplication
{
    private PluginLoadContext? _context;
    private WeakReference? _contextRef;
    private List<IExtensionApplication> _apps = [];
    private Assembly? _pluginAssembly;
    private string? _lastDirectory;

    public void Initialize()
    {
        Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
            "\nWang_MCP_AutoCAD.Loader ready — run MCPRELOAD to select and load the plugin assembly.\n");
    }

    public void Terminate() => Unload();

    /// <summary>
    /// Where MCPRELOAD's file picker opens. There is no source tree on the AutoCAD machine,
    /// so the build's deploy.local.props is unreachable from here; the fallbacks below are
    /// what stand in for it.
    ///
    /// Preferring this assembly's own folder is the important one: the Loader is deployed
    /// alongside the plugin, so "next to me" is by construction the folder the build just
    /// wrote to. That makes the common case self-configuring and, more to the point, stops
    /// the picker opening on a stale copy in some other folder.
    /// </summary>
    private static string GetDeployDirectory()
    {
        string? envDir = Environment.GetEnvironmentVariable("WANG_MCP_DEPLOY_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            return envDir;
        }

        try
        {
            string ownPath = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrWhiteSpace(ownPath))
            {
                string? ownDir = Path.GetDirectoryName(ownPath);
                if (!string.IsNullOrWhiteSpace(ownDir) && Directory.Exists(ownDir))
                {
                    return ownDir;
                }
            }
        }
        catch (System.Exception)
        {
            // Location can be empty or throw for assemblies with no backing file. Fall
            // through rather than taking down MCPRELOAD over a directory default.
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wang_MCP_AutoCAD",
            "deploy");
    }

    [CommandMethod("MCPRELOAD")]
    public void Reload()
    {
        var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (ed is null)
        {
            return;
        }

        var fileOptions = new PromptOpenFileOptions("Select plugin assembly to load")
        {
            Filter = "Assembly (*.dll)|*.dll",
            InitialDirectory = _lastDirectory ?? GetDeployDirectory(),
        };

        var fileResult = ed.GetFileNameForOpen(fileOptions);
        if (fileResult.Status != PromptStatus.OK)
        {
            return;
        }

        var path = fileResult.StringResult;
        _lastDirectory = Path.GetDirectoryName(path);

        Unload();
        WaitForUnload();

        try
        {
            var (context, assembly, apps) = LoadPlugin(path);
            _context = context;
            _pluginAssembly = assembly;
            _apps = apps;
            foreach (var app in _apps)
            {
                app.Initialize();
            }
            ed.WriteMessage($"\nWang_MCP_AutoCAD: loaded {Path.GetFileName(path)} ({_apps.Count} IExtensionApplication(s) initialized).\n");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nWang_MCP_AutoCAD: reload failed: {ex.Message}\n");
        }
    }

    [CommandMethod("MCPUNLOAD")]
    public void UnloadCommand()
    {
        var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        Unload();
        WaitForUnload();
        ed?.WriteMessage("\nWang_MCP_AutoCAD: plugin unloaded.\n");
    }

    /// <summary>
    /// Lists and runs [CommandMethod]s discovered by reflection on the currently
    /// loaded plugin assembly. Commands defined there are NOT auto-registered by
    /// AutoCAD when loaded this way (that scan only happens for NETLOAD'ed
    /// assemblies), so this is the dev-time stand-in for invoking them from the
    /// command line directly.
    /// </summary>
    [CommandMethod("MCPINVOKE")]
    public void InvokeCommand()
    {
        var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (ed is null)
        {
            return;
        }

        if (_pluginAssembly is null)
        {
            ed.WriteMessage("\nWang_MCP_AutoCAD: no plugin loaded. Run MCPRELOAD first.\n");
            return;
        }

        var candidates = _pluginAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<CommandMethodAttribute>()))
            .Where(x => x.Attr is not null)
            .ToList();

        if (candidates.Count == 0)
        {
            ed.WriteMessage("\nWang_MCP_AutoCAD: no [CommandMethod]s found in the loaded plugin.\n");
            return;
        }

        var keywordOptions = new PromptKeywordOptions("\nPlugin command to run");
        foreach (var c in candidates)
        {
            keywordOptions.Keywords.Add(c.Attr!.GlobalName);
        }

        var pick = ed.GetKeywords(keywordOptions);
        if (pick.Status != PromptStatus.OK)
        {
            return;
        }

        var target = candidates.FirstOrDefault(c =>
            string.Equals(c.Attr!.GlobalName, pick.StringResult, StringComparison.OrdinalIgnoreCase));

        if (target.Method is null)
        {
            ed.WriteMessage("\nWang_MCP_AutoCAD: no matching command.\n");
            return;
        }

        var instance = target.Method.IsStatic ? null : Activator.CreateInstance(target.Method.DeclaringType!);
        target.Method.Invoke(instance, null);
    }

    private void Unload()
    {
        foreach (var app in _apps)
        {
            try
            {
                app.Terminate();
            }
            catch
            {
                // Best-effort: a broken plugin must not block unloading it.
            }
        }

        _apps = [];
        _pluginAssembly = null;

        if (_context is not null)
        {
            _contextRef = new WeakReference(_context, trackResurrection: true);
            _context.Unload();
            _context = null;
        }
    }

    private void WaitForUnload()
    {
        for (var i = 0; i < 10 && (_contextRef?.IsAlive ?? false); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PluginLoadContext Context, Assembly Assembly, List<IExtensionApplication> Apps) LoadPlugin(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Plugin assembly not found at {path}. Build the Plugin project first.", path);
        }

        var context = new PluginLoadContext($"Wang_MCP_AutoCAD.Plugin_{Guid.NewGuid():N}");

        var assemblyBytes = File.ReadAllBytes(path);
        var pdbPath = Path.ChangeExtension(path, ".pdb");

        using var assemblyStream = new MemoryStream(assemblyBytes);
        Assembly assembly;
        if (File.Exists(pdbPath))
        {
            using var pdbStream = new MemoryStream(File.ReadAllBytes(pdbPath));
            assembly = context.LoadFromStream(assemblyStream, pdbStream);
        }
        else
        {
            assembly = context.LoadFromStream(assemblyStream);
        }

        var apps = assembly.GetTypes()
            .Where(t => typeof(IExtensionApplication).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .Select(t => (IExtensionApplication)Activator.CreateInstance(t)!)
            .ToList();

        return (context, assembly, apps);
    }
}
