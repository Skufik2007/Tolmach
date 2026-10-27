using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Tolmach;

internal static class Theme
{
    public static Color Accent { get; } = ReadAccent();

    static Color ReadAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
            }
        }
        catch
        {
        }
        return Color.FromRgb(0x4C, 0xC2, 0xFF);
    }

    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static readonly SolidColorBrush Card = Brush(0xF5, 0x1F, 0x21, 0x26);
    public static readonly SolidColorBrush CardBorder = Brush(0x38, 0xFF, 0xFF, 0xFF);
    public static readonly SolidColorBrush Foreground = Brush(0xFF, 0xF4, 0xF4, 0xF6);
    public static readonly SolidColorBrush Dim = Brush(0x99, 0xF4, 0xF4, 0xF6);
    public static readonly SolidColorBrush Faint = Brush(0x66, 0xF4, 0xF4, 0xF6);
    public static readonly SolidColorBrush Hover = Brush(0x14, 0xFF, 0xFF, 0xFF);
    public static readonly SolidColorBrush Pressed = Brush(0x22, 0xFF, 0xFF, 0xFF);
    public static readonly SolidColorBrush AccentBrush = Brush(Accent);
    public static readonly SolidColorBrush AccentSoft = Brush(Color.FromArgb(0x40, Accent.R, Accent.G, Accent.B));
    public static readonly SolidColorBrush Danger = Brush(0xFF, 0xFF, 0x6B, 0x6B);

    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => Brush(Color.FromArgb(a, r, g, b));

    public static TextBlock Glyph(string glyph, double size, Brush? brush = null) => new()
    {
        Text = glyph,
        FontFamily = Icons,
        FontSize = size,
        Foreground = brush ?? Foreground,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    public static TextBlock Label(string text, double size, Brush? brush = null, FontWeight? weight = null) => new()
    {
        Text = text,
        FontFamily = Text,
        FontSize = size,
        Foreground = brush ?? Foreground,
        FontWeight = weight ?? FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };
}

// A flat button: a glyph or any content with a hover background.
internal sealed class FlatButton : Border
{
    public event Action? Click;
    bool _pressed;

    public FlatButton(UIElement content, string? tooltip = null)
    {
        Child = content;
        CornerRadius = new CornerRadius(6);
        Background = Brushes.Transparent;
        Padding = new Thickness(6);
        Cursor = Cursors.Hand;
        ToolTip = tooltip;
        MouseEnter += (_, _) => Background = Theme.Hover;
        MouseLeave += (_, _) =>
        {
            Background = Brushes.Transparent;
            _pressed = false;
        };
        MouseLeftButtonDown += (_, e) =>
        {
            _pressed = true;
            Background = Theme.Pressed;
            e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_pressed)
            {
                _pressed = false;
                Background = Theme.Hover;
                e.Handled = true;
                Click?.Invoke();
            }
        };
    }
}

