using Avalonia.Media;
using AVASdCanvas.Controls;
using AVASdCanvas.Models;

namespace AVAQueryBuilder;

/// <summary>
/// How a query looks on the diagram: each kind of entity's colours and its lines' colour. The window and the pictures
/// AI agents ask for (render_view) both draw a query with <see cref="Fill"/>, so they look the same.
/// </summary>
public static class QueryLook
{
    private static readonly Dictionary<string, (IBrush Fill, IBrush Stroke, IBrush Line)> Kinds = new()
    {
        ["Table"] = (new SolidColorBrush(Color.Parse("#90EE90")), Brushes.DarkGreen, Brushes.Purple),
        ["Lookup"] = (new SolidColorBrush(Color.Parse("#D8B4FE")), new SolidColorBrush(Color.Parse("#7C3AED")), Brushes.Purple),
        ["Limiter"] = (new SolidColorBrush(Color.Parse("#FCA5A5")), new SolidColorBrush(Color.Parse("#DC2626")), Brushes.Red),
        ["Filter"] = (new SolidColorBrush(Color.Parse("#93C5FD")), new SolidColorBrush(Color.Parse("#2563EB")), Brushes.Blue),
        ["Sorting"] = (new SolidColorBrush(Color.Parse("#FEF9C3")), new SolidColorBrush(Color.Parse("#CA8A04")), Brushes.Goldenrod),
        ["Derived"] = (new SolidColorBrush(Color.Parse("#A8E0CC")), new SolidColorBrush(Color.Parse("#2D8B70")), Brushes.Teal),
        ["Distinct"] = (new SolidColorBrush(Color.Parse("#FDBA74")), new SolidColorBrush(Color.Parse("#C2410C")), Brushes.OrangeRed),
        ["CaseWhen"] = (new SolidColorBrush(Color.Parse("#94A3B8")), new SolidColorBrush(Color.Parse("#475569")), Brushes.SlateGray),
        ["GroupBy"] = (new SolidColorBrush(Color.Parse("#E879A8")), new SolidColorBrush(Color.Parse("#9D174D")), Brushes.DeepPink),
    };

    /// <summary>
    /// Draws <paramref name="query"/> on <paramref name="canvas"/>, replacing what was there. Returns which query entity
    /// each canvas entity shows (canvas id → query id).
    /// </summary>
    public static Dictionary<string, string> Fill(SdCanvas canvas, QueryFile query)
    {
        canvas.Clear();
        var shown = new Dictionary<string, string>();
        var byId = new Dictionary<string, GraphicEntity>();
        foreach (var e in query.Entities)
        {
            if (QueryEdits.Metadata(e) is not { } metadata || !Kinds.TryGetValue(e.EntityType, out var look)) continue;
            var g = canvas.AddEntity(label: e.Label, x: e.X, y: e.Y, width: e.Width, height: e.Height, fill: look.Fill, stroke: look.Stroke, metadata: metadata);
            shown[g.Id] = e.Id;
            byId[e.Id] = g;
        }
        foreach (var c in query.Connectors)
        {
            if (!byId.TryGetValue(c.SourceEntityId, out var source) || !byId.TryGetValue(c.TargetEntityId, out var target)) continue;
            var sourceEnd = Enum.TryParse<EndpointStyle>(c.SourceEndpoint, out var se) ? se : EndpointStyle.RoundDot;
            var targetEnd = Enum.TryParse<EndpointStyle>(c.TargetEndpoint, out var te) ? te : EndpointStyle.PointedArrow;
            canvas.AddConnector(source, target, sourceEnd, targetEnd, LineFor(source.Metadata, target.Metadata), null);
        }
        return shown;
    }

    /// <summary>A line's colour: the colour of the entity it joins to the table (purple for a lookup).</summary>
    private static IBrush LineFor(object? a, object? b)
    {
        foreach (var kind in new[] { "Limiter", "Filter", "Sorting", "Derived", "Distinct", "CaseWhen", "GroupBy" })
            if ((a != null && QueryEdits.KindOf(a) == kind) || (b != null && QueryEdits.KindOf(b) == kind))
                return Kinds[kind].Line;
        return Brushes.Purple;
    }
}
