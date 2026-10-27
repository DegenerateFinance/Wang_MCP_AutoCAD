using System.Reflection;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace AcadHotLoader;

/// <summary>
/// A generic dev-time hot reloader for AutoCAD .NET plugins. It knows nothing about any
/// particular plugin: production deployments NETLOAD their plugin directly, as usual.
///
/// NETLOAD'ed assemblies live in AssemblyLoadContext.Default forever, so re-NETLOADing after
/// a rebuild fails with "Assembly with same name is already loaded". NETLOAD this assembly
/// once per AutoCAD session instead; HOTLOAD then loads a chosen plugin from disk into a
/// fresh collectible AssemblyLoadContext, mirroring what NETLOAD itself does: prompt for a
/// .dll via the standard file dialog, then find IExtensionApplication implementations and
/// call Initialize()/Terminate() on them. Every HOTLOAD unloads the previous plugin first, so
/// the same command serves for the first load and every reload.
/// </summary>
public class HotLoader : IExtensionApplication
{
    private PluginLoadContext? _context;
    private WeakReference? _contextRef;
    private List<IExtensionApplication> _apps = [];
    private Assembly? _pluginAssembly;
    private string? _lastDirectory;

    public void Initialize()
    {
        Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
            "\nAcadHotLoader ready — run HOTLOAD to select and load a plugin assembly.\n");
    }

    public void Terminate() => Unload();

    /// <summary>
    /// Where HOTLOAD's file picker opens the first time: this assembly's own folder. The
    /// hot loader is deployed alongside the plugin it serves, so "next to me" is by
    /// construction the folder the build just wrote to — which also stops the picker
    /// opening on a stale copy somewhere else. After the first pick, the last folder used
    /// wins.
    /// </summary>
    private static string GetInitialDirectory()
    {
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
            // through rather than taking down HOTLOAD over a directory default.
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    [CommandMethod("HOTLOAD")]
    public void LoadCommand()
    {
        Editor? ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (ed is null)
        {
            return;
        }

        PromptOpenFileOptions fileOptions = new("Select plugin assembly to load")
        {
            Filter = "Assembly (*.dll)|*.dll",
            InitialDirectory = _lastDirectory ?? GetInitialDirectory(),
        };

        PromptFileNameResult fileResult = ed.GetFileNameForOpen(fileOptions);
        if (fileResult.Status != PromptStatus.OK)
        {
            return;
        }

        string path = fileResult.StringResult;
        _lastDirectory = Path.GetDirectoryName(path);

        Unload();
        WaitForUnload();

        try
        {
            (PluginLoadContext context, Assembly assembly, List<IExtensionApplication> apps) = LoadPlugin(path);
            _context = context;
            _pluginAssembly = assembly;
            _apps = apps;
            foreach (IExtensionApplication app in _apps)
            {
                app.Initialize();
            }
            ed.WriteMessage($"\nAcadHotLoader: loaded {Path.GetFileName(path)} ({_apps.Count} IExtensionApplication(s) initialized).\n");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nAcadHotLoader: load failed: {ex.Message}\n");
        }
    }

    [CommandMethod("HOTUNLOAD")]
    public void UnloadCommand()
    {
        Editor? ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        Unload();
        WaitForUnload();
        ed?.WriteMessage("\nAcadHotLoader: plugin unloaded.\n");
    }

    /// <summary>
    /// Lists and runs the [CommandMethod]s of the currently loaded plugin. AutoCAD does NOT
    /// register a plugin's commands when it is loaded this way (that scan only happens for
    /// NETLOAD'ed assemblies), so this is the dev-time stand-in for typing them at the command
    /// line directly.
    /// </summary>
    [CommandMethod("HOTRUN")]
    public void RunCommand()
    {
        Editor? ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (ed is null)
        {
            return;
        }

        if (_pluginAssembly is null)
        {
            ed.WriteMessage("\nAcadHotLoader: no plugin loaded. Run HOTLOAD first.\n");
            return;
        }

        List<(MethodInfo Method, CommandMethodAttribute? Attr)> candidates = _pluginAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<CommandMethodAttribute>()))
            .Where(x => x.Attr is not null)
            .ToList();

        if (candidates.Count == 0)
        {
            ed.WriteMessage("\nAcadHotLoader: no [CommandMethod]s found in the loaded plugin.\n");
            return;
        }

        PromptKeywordOptions keywordOptions = new("\nPlugin command to run");
        foreach ((MethodInfo Method, CommandMethodAttribute? Attr) c in candidates)
        {
            keywordOptions.Keywords.Add(c.Attr!.GlobalName);
        }

        PromptResult pick = ed.GetKeywords(keywordOptions);
        if (pick.Status != PromptStatus.OK)
        {
            return;
        }

        (MethodInfo Method, CommandMethodAttribute? Attr) target = candidates.FirstOrDefault(c =>
            string.Equals(c.Attr!.GlobalName, pick.StringResult, StringComparison.OrdinalIgnoreCase));

        if (target.Method is null)
        {
            ed.WriteMessage("\nAcadHotLoader: no matching command.\n");
            return;
        }

        object? instance = target.Method.IsStatic ? null : Activator.CreateInstance(target.Method.DeclaringType!);
        target.Method.Invoke(instance, null);
    }

    private void Unload()
    {
        foreach (IExtensionApplication app in _apps)
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
        for (int i = 0; i < 10 && (_contextRef?.IsAlive ?? false); i++)
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
            throw new FileNotFoundException($"Plugin assembly not found at {path}. Build the plugin project first.", path);
        }

        PluginLoadContext context = new($"AcadHotLoader.Plugin_{Guid.NewGuid():N}");

        byte[] assemblyBytes = File.ReadAllBytes(path);
        string pdbPath = Path.ChangeExtension(path, ".pdb");

        using MemoryStream assemblyStream = new(assemblyBytes);
        Assembly assembly;
        if (File.Exists(pdbPath))
        {
            using MemoryStream pdbStream = new(File.ReadAllBytes(pdbPath));
            assembly = context.LoadFromStream(assemblyStream, pdbStream);
        }
        else
        {
            assembly = context.LoadFromStream(assemblyStream);
        }

        List<IExtensionApplication> apps = assembly.GetTypes()
            .Where(t => typeof(IExtensionApplication).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .Select(t => (IExtensionApplication)Activator.CreateInstance(t)!)
            .ToList();

        return (context, assembly, apps);
    }
}
