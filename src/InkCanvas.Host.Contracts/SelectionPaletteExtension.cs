using InkCanvas.Ink;
using Microsoft.UI.Xaml;

namespace InkCanvas.Host.Contracts;

public interface ISelectionPaletteExtension
{
    UIElement CreateControls(Func<InkController> controller, int theme);
}
