using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;

namespace InkCanvas.Ink.Rendering;

/// <summary>Parses the persisted #RRGGBB / #AARRGGBB colour without throwing on bad input.</summary>
public static class AiRenderColor
{
    public static Windows.UI.Color Parse(string value)
    {
        string text = (value ?? "").Trim().TrimStart('#');
        try
        {
            if (text.Length == 6 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
                return Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            if (text.Length == 8 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint argb))
                return Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }
        catch
        {
            // Fall through to the default ink colour.
        }
        return Windows.UI.Color.FromArgb(255, 32, 32, 36);
    }
}

/// <summary>
/// Draws native board text as vector <see cref="CanvasTextLayout"/> output. Layouts are cached by
/// object id + content signature so dragging a page does not rebuild text every frame.
/// No <c>CanvasRenderTarget</c>/PNG is created for board text.
/// </summary>
public sealed class AiTextSceneRenderer : IDisposable
{
    private sealed class Entry
    {
        public CanvasDevice Device;
        public string Signature;
        public AiTextLayoutPlan Plan;
    }

    private readonly Dictionary<Guid, Entry> _cache = new();
    private bool _disposed;

    public void Draw(CanvasDrawingSession session, IBoardTextElement text, Matrix3x2 view)
    {
        if (_disposed || session is null || text is null) return;
        var frame = text.Frame;
        if (frame.Bounds.Width <= 0.5f || frame.Bounds.Height <= 0.5f) return;

        AiTextLayoutPlan plan = GetPlan(session.Device, text, frame.Bounds.Width);
        if (plan is null) return;

        var color = AiRenderColor.Parse(text.Color);
        var previous = session.Transform;
        try
        {
            session.Transform = frame.Transform * view;
            float x = frame.Bounds.Left;
            float y = frame.Bounds.Top;
            if (plan.Title is not null)
            {
                session.DrawTextLayout(plan.Title, x, y, color);
                y += (float)plan.Title.LayoutBounds.Height + plan.TitleGap;
            }
            if (plan.Header is not null)
            {
                session.DrawTextLayout(plan.Header, x, y, color);
                y += (float)plan.Header.LayoutBounds.Height + plan.HeaderGap;
            }

            float columnWidth = plan.ColumnWidth > 0 ? plan.ColumnWidth : frame.Bounds.Width;
            for (int index = 0; index < plan.Bodies.Count; index++)
            {
                CanvasTextLayout body = plan.Bodies[index];
                if (body is null) continue;
                float columnX = x + index * (columnWidth + plan.ColumnGap);
                session.DrawTextLayout(body, columnX, y, color);
            }
        }
        finally
        {
            session.Transform = previous;
        }
    }

    private AiTextLayoutPlan GetPlan(CanvasDevice device, IBoardTextElement text, float width)
    {
        AiTextRenderSpec spec = AiTextRenderSpec.From(text);
        string signature = string.Join('|',
            spec.Type, spec.Content, spec.Title, spec.Author, spec.Dynasty,
            spec.FontFamily, spec.FontSize.ToString("R"), spec.LineHeight.ToString("R"),
            spec.Align, spec.Columns.ToString(), spec.Color, width.ToString("R"));

        if (_cache.TryGetValue(text.Id, out Entry entry))
        {
            if (ReferenceEquals(entry.Device, device) && entry.Signature == signature) return entry.Plan;
            entry.Plan?.Dispose();
        }
        else
        {
            entry = new Entry();
            _cache[text.Id] = entry;
        }

        entry.Device = device;
        entry.Signature = signature;
        entry.Plan = AiTextLayoutBuilder.Build(device, spec, width);
        return entry.Plan;
    }

    public void Retain(IEnumerable<IBoardTextElement> texts)
    {
        if (texts is null)
        {
            Clear();
            return;
        }
        var present = texts.Select(text => text.Id).ToHashSet();
        foreach (var id in _cache.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            _cache[id].Plan?.Dispose();
            _cache.Remove(id);
        }
    }

    public void Clear()
    {
        foreach (Entry entry in _cache.Values) entry.Plan?.Dispose();
        _cache.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }
}