using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Wang_MCP_AutoCAD;

public class TestFunc
{
    [CommandMethod("MCPHELLO")]
    public void Hello()
    {
        Log.Info("MCPHELLO");
        var doc = Application.DocumentManager.MdiActiveDocument;
        doc.Editor.WriteMessage("\nHello from Wang_MCP_AutoCAD v9!\n");
    }

    [CommandMethod("MCPGREET")]
    public void Greet()
    {
        Log.Info("MCPGREET");
        var ed = Application.DocumentManager.MdiActiveDocument.Editor;

        var options = new PromptStringOptions("\nEnter your name: ")
        {
            AllowSpaces = true
        };
        var result = ed.GetString(options);

        if (result.Status != PromptStatus.OK)
        {
            Log.Debug($"MCPGREET cancelled at name prompt ({result.Status}).");
            return;
        }

        ed.WriteMessage($"\nHello, {result.StringResult}!\n");
    }

    [CommandMethod("MCPCircleAndConcentricSquare")]
    public void CircleAndConcentricSquare()
    {
        Log.Info("MCPCircleAndConcentricSquare");
        var doc = Application.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;

        var centerResult = ed.GetPoint("\nSpecify center point: ");
        if (centerResult.Status != PromptStatus.OK)
        {
            Log.Debug($"Cancelled at center prompt ({centerResult.Status}).");
            return;
        }

        var radiusOptions = new PromptDistanceOptions("\nSpecify radius: ")
        {
            BasePoint = centerResult.Value,
            UseBasePoint = true,
            AllowNegative = false,
            AllowZero = false
        };
        var radiusResult = ed.GetDistance(radiusOptions);
        if (radiusResult.Status != PromptStatus.OK)
        {
            Log.Debug($"Cancelled at radius prompt ({radiusResult.Status}).");
            return;
        }

        var center = centerResult.Value;
        var radius = radiusResult.Value;

        try
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var circle = new Circle(center, Vector3d.ZAxis, radius);
                btr.AppendEntity(circle);
                tr.AddNewlyCreatedDBObject(circle, true);

                var square = new Polyline();
                square.AddVertexAt(0, new Point2d(center.X - radius, center.Y - radius), 0, 0, 0);
                square.AddVertexAt(1, new Point2d(center.X + radius, center.Y - radius), 0, 0, 0);
                square.AddVertexAt(2, new Point2d(center.X + radius, center.Y + radius), 0, 0, 0);
                square.AddVertexAt(3, new Point2d(center.X - radius, center.Y + radius), 0, 0, 0);
                square.Closed = true;
                btr.AppendEntity(square);
                tr.AddNewlyCreatedDBObject(square, true);

                tr.Commit();
            }
        }
        catch (System.Exception ex)
        {
            Log.Error("MCPCircleAndConcentricSquare failed.", ex);
            throw;
        }

        Log.Info($"Drew circle (r={radius}) and concentric square at ({center.X}, {center.Y}).");
        ed.WriteMessage($"\nDrew circle (r={radius}) and concentric square at ({center.X}, {center.Y}).\n");
    }

    /// <summary>
    /// Reports where the log lives and lets the level be changed without a rebuild.
    /// </summary>
    [CommandMethod("MCPLOG")]
    public void LogInfo()
    {
        var ed = Application.DocumentManager.MdiActiveDocument.Editor;
        ed.WriteMessage($"\nLog file: {Log.Path}");
        ed.WriteMessage($"\nLevel:    {Log.MinimumLevel}");

        var options = new PromptKeywordOptions("\nSet level")
        {
            AllowNone = true
        };
        foreach (var name in Enum.GetNames<LogLevel>())
        {
            options.Keywords.Add(name);
        }
        options.Keywords.Default = Log.MinimumLevel.ToString();

        var pick = ed.GetKeywords(options);
        if (pick.Status != PromptStatus.OK)
        {
            return;
        }

        Log.MinimumLevel = Enum.Parse<LogLevel>(pick.StringResult);
        ed.WriteMessage($"\nLevel set to {Log.MinimumLevel}.\n");
    }
}
