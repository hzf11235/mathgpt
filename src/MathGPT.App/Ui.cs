using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using System.Globalization;
using System.Windows.Data;
using MathGPT.Core;
using ShapePath = System.Windows.Shapes.Path;
using System.Windows.Shapes;

namespace MathGPT.App;

internal static class Ui
{
    public static StackPanel Stack(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }
    public static TextBlock Text(string text, double size = 13, string? color = null)
    {
        var block = new TextBlock { Text = text, FontSize = size };
        if (color is null or "#17202D" or "#19202E") block.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        else if (color is "#687384" or "#788399") block.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        else block.Foreground = ThemeManager.Brush(color);
        return block;
    }
    public static TextBlock Hint(string text)
    {
        var hint = Text(text, 12, "#788399"); hint.Margin = new(0, 7, 0, 2); return hint;
    }
    public static Border Card(string title, params UIElement[] children)
    {
        var panel = new StackPanel();
        if (title.Length > 0) panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 15) });
        foreach (var child in children) panel.Children.Add(child);
        var card = new Border { BorderThickness = new(1), CornerRadius = new(12), Padding = new(22), Margin = new(0, 0, 0, 18), Child = panel };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        return card;
    }
    public static UIElement Field(string label, FrameworkElement element)
    {
        element.Margin = new(0, 4, 0, 12);
        return Stack(Text(label, 12, "#687384"), element);
    }
    public static TextBox Input(string id, string text = "")
    {
        var box = new TextBox { Text = text };
        AutomationProperties.SetAutomationId(box, id); AutomationProperties.SetName(box, id);
        return box;
    }
    public static PasswordBox Secret(string id)
    {
        var box = new PasswordBox();
        AutomationProperties.SetAutomationId(box, id); AutomationProperties.SetName(box, id);
        return box;
    }
    public static ComboBox Select(string id)
    {
        var box = new ComboBox();
        AutomationProperties.SetAutomationId(box, id); return box;
    }
    public static void ProtocolNames(ComboBox box)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding { Converter = new ProtocolConverter() });
        box.ItemTemplate = new DataTemplate { VisualTree = text };
    }
    private sealed class ProtocolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is WireProtocol wire ? ModelRoute.ProtocolName(wire) : value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
    public static Button Button(string label, Action action, bool primary = false, string? id = null)
    {
        var button = new Button { Content = label, Margin = new(0, 4, 8, 4) };
        if (primary) button.Style = (Style)Application.Current.FindResource("Primary");
        if (id is not null) AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => action();
        return button;
    }
    public static WrapPanel Actions(params UIElement[] buttons)
    {
        var wrap = new WrapPanel { Margin = new(0, 2, 0, 0) };
        foreach (var button in buttons) wrap.Children.Add(button);
        return wrap;
    }
    public static FrameworkElement Icon(string name, double size = 18, string? resource = null)
    {
        var geometry = name switch
        {
            "grid" => "M3,3 H9 V9 H3 Z M15,3 H21 V9 H15 Z M3,15 H9 V21 H3 Z M15,15 H21 V21 H15 Z",
            "link" => "M10,13 L14,9 M8,16 L5,19 A4,4 0 0 1 1,15 L6,10 A4,4 0 0 1 12,10 M12,14 A4,4 0 0 0 18,14 L23,9 A4,4 0 0 0 19,5 L16,8",
            "monitor" => "M3,4 H21 V16 H3 Z M8,21 H16 M12,16 V21",
            "nodes" => "M12,2 A3,3 0 1 1 11.99,2 M5,15 A3,3 0 1 1 4.99,15 M19,15 A3,3 0 1 1 18.99,15 M10,8 L7,15 M14,8 L17,15 M8,18 H16",
            "settings" => "M3,6 H7 M13,6 H21 M3,18 H12 M18,18 H21 M10,3 A3,3 0 1 1 9.99,3 M15,15 A3,3 0 1 1 14.99,15",
            "palette" => "M12,3 A9,9 0 1 0 12,21 H14 A2,2 0 0 0 15,17 A2,2 0 0 1 16,13 H19 A2,2 0 0 0 21,11 A9,9 0 0 0 12,3 M7,9 L7.01,9 M11,6 L11.01,6 M16,7 L16.01,7 M6,14 L6.01,14",
            "sun" => "M12,7 A5,5 0 1 1 11.99,7 M12,1 V3 M12,21 V23 M1,12 H3 M21,12 H23 M4,4 L6,6 M18,18 L20,20 M4,20 L6,18 M18,6 L20,4",
            "moon" => "M20,15 A9,9 0 1 1 9,3 A7,7 0 0 0 20,15 Z",
            "arrow" => "M4,12 H20 M15,7 L20,12 L15,17",
            "external" => "M14,3 H21 V10 M12,12 L21,3 M10,5 H4 V20 H19 V14",
            "code" => "M8,6 L2,12 L8,18 M16,6 L22,12 L16,18 M14,3 L10,21",
            "copy" => "M8,8 H21 V21 H8 Z M16,8 V3 H3 V16 H8",
            "more" => "M5,12 L5.01,12 M12,12 L12.01,12 M19,12 L19.01,12",
            _ => "M5,12 H19"
        };
        var path = new ShapePath { Data = Geometry.Parse(geometry), StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        if (resource is not null) path.SetResourceReference(Shape.StrokeProperty, resource);
        else path.SetBinding(Shape.StrokeProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1) });
        var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = canvas };
    }
    public static Grid ResponsiveColumns(FrameworkElement left, FrameworkElement right, double leftRatio = 1)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new(leftRatio, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new(28) });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(left); grid.Children.Add(right); Grid.SetColumn(right, 2);
        grid.SizeChanged += (_, _) =>
        {
            var compact = grid.ActualWidth < 850;
            Grid.SetColumn(right, compact ? 0 : 2); Grid.SetRow(right, compact ? 1 : 0);
            grid.ColumnDefinitions[0].Width = new(leftRatio, GridUnitType.Star);
            grid.ColumnDefinitions[1].Width = new(compact ? 0 : 28);
            grid.ColumnDefinitions[2].Width = compact ? new(0) : new(1, GridUnitType.Star);
            right.Margin = compact ? new(0, 24, 0, 0) : new(0);
        };
        return grid;
    }
    public static Grid Columns(UIElement left, UIElement right)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new(16) });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        Grid.SetColumn(right, 2); grid.Children.Add(left); grid.Children.Add(right);
        return grid;
    }
    public static UIElement PathField(string label, TextBox input, string title)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(input);
        var browse = Button("浏览…", () =>
        {
            var dialog = new OpenFolderDialog { Title = title };
            if (Directory.Exists(input.Text)) dialog.InitialDirectory = input.Text;
            if (dialog.ShowDialog() == true) input.Text = dialog.FolderName;
        });
        browse.Margin = new(8, 0, 0, 0); Grid.SetColumn(browse, 1); grid.Children.Add(browse);
        grid.Margin = new(0, 4, 0, 12);
        return Stack(Text(label, 12, "#687384"), grid);
    }
    public static bool Confirm(Window owner, string text, string title) =>
        MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
