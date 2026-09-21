using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad;

/// <summary>
/// The single registration point for every database tool.
///
/// McpExtension calls this one method rather than one per tool group, so adding a group
/// touches this file instead of the command that starts the server.
/// </summary>
internal static class AcadTools
{
    public static void RegisterAll(ToolRegistry registry)
    {
        LayerTools.RegisterAll(registry);
        DrawingTools.RegisterAll(registry);
        PropertyTools.RegisterAll(registry);
    }
}
