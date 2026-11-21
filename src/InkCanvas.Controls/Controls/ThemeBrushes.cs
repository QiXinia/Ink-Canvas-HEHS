using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace InkCanvas.Controls.Controls;

/// <summary>
/// Resolves theme token brushes against the theme requested for a control (or an explicitly
/// supplied theme), instead of reading <see cref="Application.Current"/>'s flat resource map
/// (which follows the app theme and ignores per-window / per-control theme overrides).
/// </summary>
public static class ThemeBrushes
{
    /// <summary>
    /// System accessibility state, created once. May be <c>null</c> when the API is unavailable
    /// (for example on a stripped host); callers treat that as "no high contrast".
    /// </summary>
    private static readonly AccessibilitySettings SystemAccessibility = CreateAccessibilitySettings();
    /// <summary>
    /// Resolve a token brush for the theme currently applied to <paramref name="element"/>.
    /// Falls back to the app-level lookup and finally to a transparent brush; never throws.
    /// </summary>
    public static Brush Resolve(FrameworkElement element, string key)
        => ResolveCore(ThemeKey(element), key);

    /// <summary>
    /// Resolve a token brush for an explicit theme value.
    /// <paramref name="theme"/>: 0 = follow system, 1 = light, 2 = dark, anything else = light.
    /// Never throws; an unknown key resolves to a transparent brush.
    /// </summary>
    public static Brush Resolve(int theme, string key)
        => ResolveCore(ThemeKey(theme), key);

    private static Brush ResolveCore(string themeKey, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return Transparent();
        }

        var resources = Application.Current?.Resources;

        if (resources is not null)
        {
            Brush themed = LookupInThemeDictionaries(resources, themeKey, key);
            if (themed is not null)
            {
                return themed;
            }

            if (resources.TryGetValue(key, out object value) && value is Brush brush)
            {
                return brush;
            }
        }

        return Transparent();
    }

    private static AccessibilitySettings CreateAccessibilitySettings()
    {
        try
        {
            return new AccessibilitySettings();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True when the OS high-contrast flag is on. High contrast wins over an explicit
    /// light/dark request; any failure (API missing, COM error) is reported as <c>false</c>
    /// so theme resolution never throws.
    /// </summary>
    private static bool IsSystemHighContrast()
    {
        try
        {
            return SystemAccessibility is not null && SystemAccessibility.HighContrast;
        }
        catch
        {
            return false;
        }
    }

    private static string ThemeKey(FrameworkElement element)
    {
        if (IsSystemHighContrast())
        {
            return "HighContrast";
        }

        ElementTheme theme = element is null ? ElementTheme.Default : element.ActualTheme;
        return theme switch
        {
            ElementTheme.Dark => "Dark",
            ElementTheme.Light => "Light",
            _ => SystemThemeKey(),
        };
    }

    private static string ThemeKey(int theme)
    {
        if (IsSystemHighContrast())
        {
            return "HighContrast";
        }

        return theme switch
        {
            1 => "Light",
            2 => "Dark",
            _ => SystemThemeKey(),
        };
    }

    private static string SystemThemeKey() =>
        Application.Current?.RequestedTheme == ApplicationTheme.Dark ? "Dark" : "Light";

    /// <summary>
    /// Walks the resource dictionary tree because the tokens live in a merged dictionary:
    /// its theme dictionaries are visible to lookups but are not promoted to the app-level
    /// <c>ThemeDictionaries</c> map.
    /// </summary>
    private static Brush LookupInThemeDictionaries(ResourceDictionary dictionary, string themeKey, string key)
    {
        var themeDictionaries = dictionary.ThemeDictionaries;
        if (themeDictionaries is not null
            && themeDictionaries.TryGetValue(themeKey, out object themed)
            && themed is ResourceDictionary themedDictionary
            && themedDictionary.TryGetValue(key, out object value)
            && value is Brush brush)
        {
            return brush;
        }

        foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
        {
            Brush result = LookupInThemeDictionaries(merged, themeKey, key);
            if (result is not null) return result;
        }

        return null;
    }

    private static SolidColorBrush Transparent() => new(Microsoft.UI.Colors.Transparent);
}