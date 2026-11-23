using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AVAQueryBuilder.Agents.Mcp;
using AVASdCanvas.Controls;

namespace AVAQueryBuilder.Agents;

/// <summary>
/// Draws a query's diagram as a picture (render_view, render_checkpoint, the chat's timeline): a canvas of its own,
/// filled as the window's is (<see cref="QueryLook.Fill"/>), inside <paramref name="host"/>, a panel in a window that
/// has no size and clips, so controls take their look but nothing shows on screen.
/// </summary>
public sealed class QueryLibrary(Panel host) : IQueryLibrary
{
    private const double Margin = 20;

    public byte[] Picture(QueryFile query, double scale)
    {
        var canvas = new SdCanvas();
        QueryLook.Fill(canvas, query);
        var right = query.Entities.Select(e => e.X + e.Width).DefaultIfEmpty(300).Max() + Margin;
        var bottom = query.Entities.Select(e => e.Y + e.Height).DefaultIfEmpty(120).Max() + Margin;
        var size = new Size(Math.Max(320, right), Math.Max(160, bottom));
        canvas.Width = size.Width;
        canvas.Height = size.Height;
        var frame = new Border { Background = Brushes.White, Child = canvas };
        // Scaled by a layout transform and drawn at 96 DPI (a bitmap's own DPI would scale it a second time).
        var scaled = new LayoutTransformControl { Child = frame, LayoutTransform = new ScaleTransform(scale, scale) };
        var stage = new Canvas();
        stage.Children.Add(scaled);
        host.Children.Add(stage);
        try
        {
            scaled.Measure(Size.Infinity);
            var want = scaled.DesiredSize;
            scaled.Arrange(new Rect(want));
            scaled.UpdateLayout();
            var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(want.Width)), Math.Max(1, (int)Math.Ceiling(want.Height)));
            using var bitmap = new RenderTargetBitmap(pixels, new Vector(96, 96));
            bitmap.Render(scaled);
            using var png = new MemoryStream();
            bitmap.Save(png);
            return png.ToArray();
        }
        finally
        {
            stage.Children.Clear();
            host.Children.Remove(stage);
        }
    }
}
