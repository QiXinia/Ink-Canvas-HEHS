using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Controls.Controls;

public static class KeyboardShortcutScope
{
    public static bool IsTextInput(DependencyObject source)
    {
        for (var element = source; element is not null;
            element = (element as FrameworkElement)?.Parent ?? (element is UIElement ? VisualTreeHelper.GetParent(element) : null))
            if (element is TextBox or RichEditBox or PasswordBox or NumberBox or AutoSuggestBox or ComboBox or Slider or ContentDialog) return true;
        return false;
    }

    public static bool HasTextInputFocus(FrameworkElement root) => root?.XamlRoot is { } xamlRoot
        && IsTextInput(FocusManager.GetFocusedElement(xamlRoot) as DependencyObject);
}
