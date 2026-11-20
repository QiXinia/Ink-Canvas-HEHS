using InkCanvas.Extensibility;

namespace InkCanvas.Controls.Controls;

/// <summary>Shared module icons for the screen and board toolbars.</summary>
public static class ExtensionCommandIcons
{
    public static string For(ExtensionCommand command) => (command.PluginId, command.CommandId) switch
    {
        ("inkcanvas.pdf", "open") => Glyphs.Pdf,
        ("inkcanvas.pdf", "import") => Glyphs.Import,
        ("inkcanvas.analytic-geometry", "open") => Glyphs.Calculator,
        _ => Glyphs.Tools,
    };
}
