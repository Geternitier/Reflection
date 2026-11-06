using System.Windows;
using System.Windows.Media;

namespace Reflection.Desktop;

internal static class ThemeManager
{
    internal static bool IsDark { get; private set; }
    internal static int Revision { get; private set; }
    internal static event EventHandler? Changed;
    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["Ink"] = ("#302F2B", "#E9E6DC"),
        ["Muted"] = ("#858078", "#A6A196"),
        ["Accent"] = ("#BD654B", "#D58B6E"),
        ["AccentHover"] = ("#A9563F", "#E09A7F"),
        ["AccentSoft"] = ("#E9E6DD", "#3D3931"),
        ["Line"] = ("#E0DCD4", "#464239"),
        ["Surface"] = ("#FAF9F5", "#242320"),
        ["Sidebar"] = ("#EFEEE8", "#1D1C19"),
        ["Danger"] = ("#AD343B", "#F08B84"),
        ["Coral"] = ("#BD654B", "#D58B6E"),
        ["Yellow"] = ("#E2DFD5", "#3C382F"),
        ["Mint"] = ("#E2E7DC", "#2D3930"),
        ["Lilac"] = ("#EAE2DC", "#3A3030"),
        ["Panel"] = ("#FFFEFA", "#2B2925"),
        ["OnAccent"] = ("#FFFFFF", "#FFFFFF"),
        ["UserBubble"] = ("#EEECE5", "#35322C"),
        ["TableHeader"] = ("#EFEEE8", "#35322C"),
        ["ToneF8F8F3"] = ("#F8F8F3", "#35322C"),
        ["Tone898396"] = ("#898396", "#AAA293"),
        ["ToneE2DCCE"] = ("#E2DCCE", "#575147"),
        ["ToneBFB8CB"] = ("#BFB8CB", "#575147"),
        ["Tone8B8170"] = ("#8B8170", "#A99F8B"),
        ["ToneF5F1E8"] = ("#F5F1E8", "#35322C"),
        ["Tone836D58"] = ("#836D58", "#C3AC8E"),
        ["ToneE6E1D7"] = ("#E6E1D7", "#35322C"),
        ["Tone756D60"] = ("#756D60", "#AAA293"),
        ["ToneBCAE99"] = ("#BCAE99", "#AAA293"),
        ["ToneE1DCCE"] = ("#E1DCCE", "#575147"),
        ["Tone48624E"] = ("#48624E", "#BDD2BC"),
        ["Tone52735D"] = ("#52735D", "#A9C4A6"),
        ["ToneC4BCAC"] = ("#C4BCAC", "#575147"),
        ["ToneCCC8BF"] = ("#CCC8BF", "#575147"),
        ["ToneBCBDC9"] = ("#BCBDC9", "#575147"),
        ["ToneE7E2D7"] = ("#E7E2D7", "#35322C"),
        ["ToneB5B2AD"] = ("#B5B2AD", "#8B867B"),
        ["ToneD9D5C9"] = ("#D9D5C9", "#575147"),
        ["ToneF2F0F5"] = ("#F2F0F5", "#35322C"),
        ["ToneF3F1EB"] = ("#F3F1EB", "#35322C"),
        ["ToneC6B9A6"] = ("#C6B9A6", "#575147"),
        ["ToneEAE7DF"] = ("#EAE7DF", "#35322C"),
        ["Tone99968F"] = ("#99968F", "#AAA293"),
        ["Tone40000000"] = ("#40000000", "#80000000"),
        ["Tone8C819F"] = ("#8C819F", "#AAA293"),
        ["ToneF0EDE5"] = ("#F0EDE5", "#35322C"),
        ["Tone6B8971"] = ("#6B8971", "#AAA293"),
        ["ToneDDD7CA"] = ("#DDD7CA", "#575147"),
        ["ToneE7E4DA"] = ("#E7E4DA", "#35322C"),
        ["ToneEEEBE3"] = ("#EEEBE3", "#35322C"),
        ["ToneD9D5E0"] = ("#D9D5E0", "#575147"),
        ["ToneE8EDE4"] = ("#E8EDE4", "#2D392E"),
        ["Tone66302F2B"] = ("#66302F2B", "#A0000000"),
        ["ToneE6E1D6"] = ("#E6E1D6", "#35322C"),
    };
    internal static void Apply(string theme)
    {
        if (Revision > 0 && IsDark == (theme == "dark")) return;
        IsDark = theme == "dark";
        var resources = Application.Current.Resources;
        foreach (var (key, colors) in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(IsDark ? colors.Dark : colors.Light));
            brush.Freeze(); resources[key] = brush;
        }
        resources["ThemeIcon"] = resources[IsDark ? "IconSun" : "IconMoon"];
        resources["ThemeAction"] = IsDark ? "切换浅色模式" : "切换深色模式";
        Revision++; Changed?.Invoke(null, EventArgs.Empty);
    }
}
