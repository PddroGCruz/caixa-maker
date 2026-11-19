using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CaixaMaker.App.Helpers;

/// <summary>Resolve pincéis e recursos de design respeitando o tema efetivo (Light/Default) do elemento WinUI 3.</summary>
internal static class ThemeBrushHelper
{
    public static Brush? ResolveBrush(FrameworkElement scope, string key)
    {
        ElementTheme theme = scope.ActualTheme;
        string dictKey = theme == ElementTheme.Light ? "Light" : "Default";

        if (TryGetFromThemeDictionaries(Application.Current.Resources, dictKey, key, out Brush? themed))
        {
            return themed;
        }

        return Application.Current.Resources.TryGetValue(key, out object? fallbackObj) && fallbackObj is Brush fallback
            ? fallback
            : null;
    }

    private static bool TryGetFromThemeDictionaries(
        ResourceDictionary dictionary,
        string dictKey,
        string resourceKey,
        out Brush? brush)
    {
        for (int i = dictionary.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (TryGetFromThemeDictionaries(dictionary.MergedDictionaries[i], dictKey, resourceKey, out brush))
            {
                return true;
            }
        }

        if (dictionary.ThemeDictionaries.TryGetValue(dictKey, out object? dictObj)
            && dictObj is ResourceDictionary themedDict
            && themedDict.TryGetValue(resourceKey, out object? val)
            && val is Brush resolvedBrush)
        {
            brush = resolvedBrush;
            return true;
        }

        brush = null;
        return false;
    }

    public static T? ResolveResource<T>(string key) where T : class
        => Application.Current.Resources.TryGetValue(key, out object? val) ? val as T : null;
}
