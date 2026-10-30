using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MathGPT.Core;

namespace MathGPT.App;

internal static class ThemeManager
{
    public static readonly (string Id, string Name, string Description)[] Themes =
    [
        ("aurora", "轻紫", "柔和紫色，清透留白"),
        ("orbit", "素白", "中性灰白，简洁专注"),
        ("paper", "纸感", "温润底色，轻盈阅读")
    ];
    public static string Name(string id) => Themes.FirstOrDefault(t => t.Id == id).Name ?? "轻紫";
    public static void Apply(AppState state)
    {
        var colors = state.Appearance switch
        {
            "orbit" => new[] { "#FAFAFA", "#202126", "#FFFFFF", "#6251D9", "#747784", "#E7E7EB", "#DEDEE6", "#EFEDFA", "#F5F5F8", "#F0EEF9" },
            "paper" => new[] { "#F5F5F3", "#292A2F", "#FFFFFF", "#6B58C7", "#7C7D83", "#E4E3DF", "#DEDDD9", "#EFECF7", "#F7F7F5", "#F0EEF5" },
            _ => new[] { "#F7F8FC", "#19202E", "#FFFFFF", "#7156E9", "#788399", "#E5E7F0", "#E0E4EE", "#EFEBFC", "#F7F7FB", "#ECE7FA" }
        };
        if (state.DarkMode) colors = ["#13151E", "#EDF0F7", "#1B1E29", "#A390FF", "#A3ADC0", "#303545", "#363C50", "#2B2541", "#222632", "#26213C"];
        var names = new[] { "Canvas", "Ink", "Surface", "Accent", "Muted", "Line", "InputLine", "Tint", "Field", "HeroStart" };
        for (int i = 0; i < names.Length; i++)
            SetBrush(names[i], colors[i]);
        SetBrush("AccentInk", state.DarkMode ? "#171326" : "#FFFFFF");
        SetBrush("AccentHover", state.DarkMode ? "#B6A5FF" : state.Appearance == "paper" ? "#7966D1" : "#8066EE");
        SetBrush("Success", state.DarkMode ? "#7BC7AD" : "#4A9B83");
        Application.Current.Resources["SoftShadow"] = state.DarkMode ? Colors.Black : Color.FromRgb(36, 28, 83);
        var hero = new RadialGradientBrush((Color)ColorConverter.ConvertFromString(colors[9]), (Color)ColorConverter.ConvertFromString(colors[0]))
        {
            Center = new Point(0.04, 0), GradientOrigin = new Point(0, 0), RadiusX = 0.9, RadiusY = 0.9
        };
        Application.Current.Resources["CanvasGlow"] = hero;
        foreach (Window window in Application.Current.Windows) Caption(window, state.DarkMode);
    }
    public static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static void SetBrush(string name, string color)
    {
        if (Application.Current.Resources[name] is SolidColorBrush current && !current.IsFrozen)
            current.Color = (Color)ColorConverter.ConvertFromString(color);
        else Application.Current.Resources[name] = Brush(color);
    }
    public static void Caption(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var value = dark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref value, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
