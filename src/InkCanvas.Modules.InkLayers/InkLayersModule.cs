using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.Core.Ink;
using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Modules.InkLayers;

public sealed class InkLayersModule : IApplicationModule, ISelectionPaletteExtension
{
    private ICanvasToolsHost _host;

    public void Start(IModuleContext context)
    {
        _host = context.GetService<ICanvasToolsHost>();
        context.Export<ISelectionPaletteExtension>(this);
    }

    public void Stop() { }

    public UIElement CreateControls(Func<InkController> controller, int theme)
    {
        var layers = new ComboBox { MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
        var create = new TouchIconButton
        {
            Glyph = Glyphs.Add,
            IconSize = 20,
            MinWidth = 44,
            MinHeight = 40,
            ContentForeground = PopupBrushes.FloatBarForeground(theme),
            HoverBackground = PopupBrushes.FloatBarButtonHover(theme),
            PressedBackground = PopupBrushes.FloatBarButtonPressed(theme),
        };
        AutomationProperties.SetName(layers, "当前书写图层");
        AutomationProperties.SetName(create, "新建图层");
        ToolTipService.SetToolTip(create, "新建透明图层");
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(layers);
        Grid.SetColumn(create, 1);
        row.Children.Add(create);

        InkController bound = null;
        bool updating = false;
        int count = 0;
        void Refresh()
        {
            var current = controller();
            if (!ReferenceEquals(bound, current))
            {
                Unbind();
                bound = current;
                if (bound is not null)
                {
                    bound.ActiveInkLayerChanged += Refresh;
                    bound.Document.Changed += Refresh;
                    bound.PlaybackChanged += Refresh;
                }
            }

            updating = true;
            try
            {
                int nextCount = bound?.Document.InkLayerCount ?? 0;
                if (count != nextCount)
                {
                    count = nextCount;
                    layers.ItemsSource = Enumerable.Range(0, count)
                        .Select(index => index == 0 ? "图层 1 · 底层" : $"书写图层 {index + 1}").Reverse().ToArray();
                }
                layers.SelectedIndex = bound is null ? -1 : count - 1 - bound.ActiveInkLayer;
                layers.IsEnabled = bound is not null && _host.CaptureTarget().CanEdit && bound.Playback is null;
                create.IsEnabled = layers.IsEnabled && count < InkStroke.MaximumInkLayers;
            }
            finally { updating = false; }
        }
        void Unbind()
        {
            if (bound is null) return;
            bound.ActiveInkLayerChanged -= Refresh;
            bound.Document.Changed -= Refresh;
            bound.PlaybackChanged -= Refresh;
            bound = null;
        }
        layers.SelectionChanged += (_, _) =>
        {
            if (updating || bound is null || layers.SelectedIndex < 0) return;
            if (_host.CaptureTarget().CanEdit) bound.TrySetActiveInkLayer(count - 1 - layers.SelectedIndex);
            Refresh();
        };
        create.Click += (_, _) =>
        {
            if (_host.CaptureTarget().CanEdit) bound?.TryCreateInkLayer();
            Refresh();
        };
        row.Loaded += (_, _) => { _host.TargetChanged += Refresh; Refresh(); };
        row.Unloaded += (_, _) => { _host.TargetChanged -= Refresh; Unbind(); };
        return row;
    }
}
