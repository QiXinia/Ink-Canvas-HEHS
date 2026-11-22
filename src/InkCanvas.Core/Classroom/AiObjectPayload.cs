using System.Text.Json;

namespace InkCanvas.Core.Classroom;

/// <summary>
/// 时光机回放中的一个 AI 板书对象（PNG 卡片或占位）。
/// <see cref="RenderPngBase64"/> 只来自录制时保存的 PNG，回放路径不重新渲染、不请求模型。
/// </summary>
public sealed class TimelineAiObject
{
    public string Id { get; init; } = "";

    /// <summary>BoardPlan 卡片类型：formula / text / wordlist / poem；普通图片为 image。</summary>
    public string Kind { get; init; } = "";

    public int PageIndex { get; init; }

    /// <summary>0 = 主展板，1 = 板中板；旧会话缺失时为 0。</summary>
    public int BoardId { get; init; }

    public double Left { get; init; }

    public double Top { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public long? LayerOrder { get; init; }

    public string Latex { get; init; } = "";

    public string Text { get; init; } = "";

    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();

    /// <summary>录制时直接保存的 PNG（Base64）；为空或 <see cref="RenderMissing"/> 时回放显示占位框。</summary>
    public string RenderPngBase64 { get; init; } = "";

    /// <summary>单对象 PNG 超过阈值或录制时不可用，回放退化为占位显示。</summary>
    public bool RenderMissing { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public bool HasRenderPng => !RenderMissing && !string.IsNullOrEmpty(RenderPngBase64);
}

/// <summary>一条 <see cref="TimelineEventKind.AiStep"/> 事件的结构化载荷。</summary>
public sealed class AiObjectsPayload
{
    /// <summary>add / replace / update / remove。</summary>
    public string Action { get; init; } = "";

    public int PageIndex { get; init; }

    public int BoardId { get; init; }

    /// <summary>本次事件涉及的对象快照；remove 时同样保留被移除对象，便于回放与排障。</summary>
    public IReadOnlyList<TimelineAiObject> Objects { get; init; } = Array.Empty<TimelineAiObject>();
}

/// <summary>
/// AI 板书对象事件的 JSON 编解码。字段顺序固定为 objectId → action → schema，
/// 便于 <see cref="InkRecorder"/> 的去重签名在前缀内包含对象 id 与动作。
/// 纯数据、无 UI/网络依赖。
/// </summary>
public static class AiObjectPayloadCodec
{
    public const int Schema = 1;

    /// <summary>单对象 PNG 直接入时间轴的上限；超过则只存 renderMissing=true 并告警。</summary>
    public const int MaxRenderPngBytes = 400 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string Encode(
        string action, int pageIndex, int boardId, IReadOnlyList<TimelineAiObject> objects)
    {
        if (objects is null || objects.Count == 0) return null;

        var dto = new PayloadDto
        {
            ObjectId = objects[0].Id ?? "",
            Action = action ?? "",
            Schema = Schema,
            PageIndex = pageIndex,
            BoardId = boardId,
            Objects = objects.Select(ToDto).ToList(),
        };
        return JsonSerializer.Serialize(dto, Options);
    }

    public static bool TryDecode(string payload, out AiObjectsPayload result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(payload)) return false;

        try
        {
            PayloadDto dto = JsonSerializer.Deserialize<PayloadDto>(payload, Options);
            if (dto is null || dto.Objects is null || dto.Objects.Count == 0) return false;

            int pageIndex = dto.PageIndex;
            int boardId = dto.BoardId;
            var objects = new List<TimelineAiObject>(dto.Objects.Count);
            foreach (ObjectDto item in dto.Objects)
            {
                if (item is null || string.IsNullOrEmpty(item.Id)) continue;
                objects.Add(new TimelineAiObject
                {
                    Id = item.Id,
                    Kind = item.Kind ?? "",
                    PageIndex = pageIndex,
                    BoardId = boardId,
                    Left = item.Left,
                    Top = item.Top,
                    Width = item.Width,
                    Height = item.Height,
                    LayerOrder = item.LayerOrder,
                    Latex = item.Latex ?? "",
                    Text = item.Text ?? "",
                    Lines = item.Lines ?? new List<string>(),
                    RenderPngBase64 = item.RenderPngBase64 ?? "",
                    RenderMissing = item.RenderMissing,
                    Notes = item.Notes ?? new List<string>(),
                });
            }

            if (objects.Count == 0) return false;
            result = new AiObjectsPayload
            {
                Action = (dto.Action ?? "").Trim().ToLowerInvariant(),
                PageIndex = pageIndex,
                BoardId = boardId,
                Objects = objects,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static ObjectDto ToDto(TimelineAiObject item) => new()
    {
        Id = item.Id ?? "",
        Kind = item.Kind ?? "",
        Left = item.Left,
        Top = item.Top,
        Width = item.Width,
        Height = item.Height,
        LayerOrder = item.LayerOrder,
        Latex = item.Latex ?? "",
        Text = item.Text ?? "",
        Lines = item.Lines?.ToList() ?? new List<string>(),
        RenderPngBase64 = item.RenderPngBase64 ?? "",
        RenderMissing = item.RenderMissing,
        Notes = item.Notes?.ToList() ?? new List<string>(),
    };

    private sealed class PayloadDto
    {
        public string ObjectId { get; set; } = "";
        public string Action { get; set; } = "";
        public int Schema { get; set; } = AiObjectPayloadCodec.Schema;
        public int PageIndex { get; set; }
        public int BoardId { get; set; }
        public List<ObjectDto> Objects { get; set; } = new();
    }

    private sealed class ObjectDto
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public long? LayerOrder { get; set; }
        public string Latex { get; set; } = "";
        public string Text { get; set; } = "";
        public List<string> Lines { get; set; } = new();
        public string RenderPngBase64 { get; set; } = "";
        public bool RenderMissing { get; set; }
        public List<string> Notes { get; set; } = new();
    }
}