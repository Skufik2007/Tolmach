using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Tolmach;

// The translation card next to the cursor. It never takes focus, so a game
// or a document keeps it; a click elsewhere or Esc closes it.
internal sealed class PopupWindow : Window
{
    const double CardWidth = 420;

    readonly Border _card;
    readonly TextBlock _direction;
    readonly TextBlock _original;
    readonly TextBlock _translation;
    readonly TextBlock _status;
    readonly TextBlock _service;
    readonly TextBlock _pinGlyph;
    readonly TextBlock _copyGlyph;
    readonly WrapPanel _languages = new() { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    readonly FlatButton _speak;
    readonly FlatButton _copy;
    readonly DispatcherTimer _watch;
    readonly TranslateTransform _slide = new();

    TextDocument? _source;
    Translation? _result;
    bool _pinned;
    Rect _anchor;

    // Asked for another language from the chips.
    public event Action<string>? LanguageChosen;

    public PopupWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = CardWidth + 24;
        SizeToContent = SizeToContent.Height;
        Title = "Толмач";

        _direction = Theme.Label("", 11.5, Theme.AccentBrush, FontWeights.SemiBold);
        var directionContent = new StackPanel { Orientation = Orientation.Horizontal };
        directionContent.Children.Add(Theme.Glyph("", 12, Theme.AccentBrush));
        _direction.Margin = new Thickness(6, 0, 4, 0);
        directionContent.Children.Add(_direction);
        directionContent.Children.Add(Theme.Glyph("", 8, Theme.Dim));
        var directionButton = new FlatButton(directionContent, "Перевести на другой язык") { Padding = new Thickness(6, 4, 7, 4) };
        directionButton.Click += () =>
            _languages.Visibility = _languages.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        _speak = new FlatButton(Theme.Glyph("", 14), "Озвучить перевод");
        _speak.Click += () =>
        {
            if (_result != null)
            {
                Speech.Speak(_result.Text, _result.TargetLanguage);
            }
        };
        _copyGlyph = Theme.Glyph("", 14);
        _copy = new FlatButton(_copyGlyph, "Копировать перевод");
        _copy.Click += CopyTranslation;
        _pinGlyph = Theme.Glyph("", 14);
        var pin = new FlatButton(_pinGlyph, "Закрепить: не закрывать по щелчку мимо");
        pin.Click += () =>
        {
            _pinned = !_pinned;
            _pinGlyph.Text = _pinned ? "" : "";
            _pinGlyph.Foreground = _pinned ? Theme.AccentBrush : Theme.Foreground;
        };
        var close = new FlatButton(Theme.Glyph("", 12), "Закрыть (Esc)");
        close.Click += HidePopup;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _speak, _copy, pin, close } };
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(directionButton);
        // Drag the card by its header (when pinned, to keep it aside).
        header.Background = Brushes.Transparent;
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == header)
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                }
            }
        };

        _original = new TextBlock
        {
            FontFamily = Theme.Text,
            FontSize = 12,
            Foreground = Theme.Dim,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 52,
            Margin = new Thickness(6, 0, 6, 8),
            Cursor = Cursors.Hand,
            ToolTip = "Показать оригинал целиком",
        };
        _original.MouseLeftButtonUp += (_, _) => _original.MaxHeight = _original.MaxHeight > 1000 ? 52 : double.PositiveInfinity;

        _translation = new TextBlock
        {
            FontFamily = Theme.Text,
            FontSize = 15,
            Foreground = Theme.Foreground,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
        };
        _status = Theme.Label("", 12.5, Theme.Dim);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.TextTrimming = TextTrimming.None;

        _service = Theme.Label("", 10.5, Theme.Faint);
        _service.HorizontalAlignment = HorizontalAlignment.Right;
        _service.Margin = new Thickness(0, 6, 0, 0);

        var body = new StackPanel { Margin = new Thickness(6, 0, 6, 0), Children = { _translation, _status, _service } };
        var scroll = new ScrollViewer
        {
            Content = body,
            MaxHeight = 380,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };

        _card = new Border
        {
            Background = Theme.Card,
            BorderBrush = Theme.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 8, 8, 12),
            Margin = new Thickness(12),
            Child = new StackPanel { Children = { header, _original, scroll, _languages } },
            RenderTransform = _slide,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Opacity = 0.45,
                Color = Colors.Black,
            },
        };
        Content = _card;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        // No focus means no keyboard events: Esc and clicks elsewhere are
        // watched instead.
        _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _watch.Tick += (_, _) => Watch();

        SourceInitialized += (_, _) => Native.MakeNoActivate(this);
        SizeChanged += (_, _) => Place();
    }

    // ----------------------------------------------------------- contents

    // original null: the text is still being read from the screen.
    public void ShowWorking(TextDocument? original, Rect anchor)
    {
        _source = original;
        _result = null;
        _anchor = anchor;
        _original.MaxHeight = 52;
        ShowSource(original);
        _direction.Text = "→ " + Translator.ShortOf(Settings.Current.TargetLanguage);
        _translation.Inlines.Clear();
        _service.Text = "";
        _translation.Visibility = Visibility.Collapsed;
        _status.Text = original != null ? "Перевожу…" : "Читаю текст с экрана…";
        _status.Foreground = Theme.Dim;
        _status.Visibility = Visibility.Visible;
        _speak.IsEnabled = _copy.IsEnabled = false;
        _languages.Visibility = Visibility.Collapsed;
        Present();
    }

    public void ShowOriginal(TextDocument original)
    {
        _source = original;
        ShowSource(original);
        _status.Text = "Перевожу…";
    }

    void ShowSource(TextDocument? original)
    {
        _original.Inlines.Clear();
        if (original != null)
        {
            Render(_original, original);
        }
        _original.Visibility = Settings.Current.ShowOriginal && original != null ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowResult(TextDocument translated, Translation result)
    {
        _result = result;
        _direction.Text = $"{Translator.ShortOf(result.SourceLanguage)} → {Translator.ShortOf(result.TargetLanguage)}";
        _translation.Inlines.Clear();
        Render(_translation, translated);
        _service.Text = result.Service;
        _translation.Visibility = Visibility.Visible;
        _status.Visibility = Visibility.Collapsed;
        _speak.IsEnabled = _copy.IsEnabled = true;
        _copyGlyph.Text = "";
        BuildLanguages(result.TargetLanguage);
    }

    // The layout of the text: titles in bold, list markers dimmed, one item
    // per line, a gap between blocks.
    static void Render(TextBlock target, TextDocument document)
    {
        bool firstBlock = true;
        foreach (var block in document.Blocks)
        {
            if (!firstBlock)
            {
                target.Inlines.Add(new LineBreak());
                target.Inlines.Add(new Run(" ") { FontSize = target.FontSize * 0.45 });
                target.Inlines.Add(new LineBreak());
            }
            firstBlock = false;
            bool firstItem = true;
            foreach (var item in block.Items)
            {
                if (!firstItem)
                {
                    target.Inlines.Add(item.SameRow
                        ? new Run("   —   ") { Foreground = Theme.Faint }
                        : new LineBreak());
                }
                firstItem = false;
                if (item.Kind == ItemKind.ListItem)
                {
                    string marker = item.Marker.Length == 1 && "-–—*·".Contains(item.Marker) ? "•" : item.Marker;
                    target.Inlines.Add(new Run(marker + "  ") { Foreground = Theme.AccentBrush });
                }
                var run = new Run(item.Text);
                if (item.Kind == ItemKind.Heading)
                {
                    run.FontWeight = FontWeights.SemiBold;
                }
                target.Inlines.Add(run);
            }
        }
    }

    public void ShowError(string message)
    {
        _translation.Visibility = Visibility.Collapsed;
        _status.Text = message;
        _status.Foreground = Theme.Danger;
        _status.Visibility = Visibility.Visible;
        if (!IsVisible)
        {
            Present();
        }
    }

    public TextDocument? Source => _source;

    void BuildLanguages(string current)
    {
        _languages.Children.Clear();
        foreach (string code in Settings.Current.Languages)
        {
            bool selected = string.Equals(code.Split('-')[0], current.Split('-')[0], StringComparison.OrdinalIgnoreCase);
            var label = Theme.Label(Translator.NameOf(code), 11.5, selected ? Theme.Foreground : Theme.Dim);
            var chip = new FlatButton(label) { Padding = new Thickness(9, 3, 9, 4), CornerRadius = new CornerRadius(10) };
            string target = code;
            chip.Click += () =>
            {
                _languages.Visibility = Visibility.Collapsed;
                LanguageChosen?.Invoke(target);
            };
            _languages.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush = selected ? Theme.AccentBrush : Theme.Brush(0x28, 0xFF, 0xFF, 0xFF),
                Background = selected ? Theme.AccentSoft : Brushes.Transparent,
                Margin = new Thickness(0, 0, 4, 4),
                Child = chip,
            });
        }
    }

    void CopyTranslation()
    {
        if (_result == null)
        {
            return;
        }
        try
        {
            Clipboard.SetText(_result.Text);
            _copyGlyph.Text = "";
        }
        catch (Exception ex)
        {
            Log.Write("Copy failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------- show / hide

    void Present()
    {
        bool wasVisible = IsVisible;
        if (!wasVisible)
        {
            _card.Opacity = 0;
            Show();
        }
        Place();
        if (!wasVisible)
        {
            _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
            _slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        _watch.Start();
    }

    public void HidePopup()
    {
        _watch.Stop();
        _pinned = false;
        _pinGlyph.Text = "";
        _pinGlyph.Foreground = Theme.Foreground;
        Hide();
    }

    // Under the text it translates (anchor, physical pixels), or above it
    // when there's no room below; inside the monitor either way.
    void Place()
    {
        if (!IsVisible || _anchor.IsEmpty)
        {
            return;
        }
        var center = new Native.POINT { X = (int)(_anchor.Left + _anchor.Width / 2), Y = (int)(_anchor.Top + _anchor.Height / 2) };
        var (scale, _, work) = Native.MonitorAt(center);
        int w = (int)(ActualWidth * scale);
        int h = (int)(ActualHeight * scale);
        int gap = (int)(4 * scale);
        int x = (int)_anchor.Left - (int)(12 * scale);
        int y = (int)_anchor.Bottom + gap;
        if (y + h > work.Bottom)
        {
            y = (int)_anchor.Top - h - gap;
        }
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010);
    }

    bool _mouseWasDown;

    void Watch()
    {
        if (Native.IsDown(Native.VK_ESCAPE))
        {
            HidePopup();
            return;
        }
        bool down = Native.IsDown(Native.VK_LBUTTON) || Native.IsDown(Native.VK_RBUTTON) || Native.IsDown(Native.VK_MBUTTON);
        if (down && !_mouseWasDown && !_pinned && !IsMouseOver)
        {
            HidePopup();
        }
        _mouseWasDown = down;
    }

    // ------------------------------------------------------------ testing

    public void RenderToPng(string path)
    {
        _card.BeginAnimation(OpacityProperty, null);
        _card.Opacity = 1;
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        _card.Effect = null;
        _card.Margin = new Thickness(0);
        _card.Measure(new Size(CardWidth, double.PositiveInfinity));
        _card.Arrange(new Rect(_card.DesiredSize));
        _card.UpdateLayout();
        const double scale = 1.5;
        var bitmap = new RenderTargetBitmap((int)(_card.ActualWidth * scale), (int)(_card.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(_card);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    public void ShowLanguagesForTest() => _languages.Visibility = Visibility.Visible;
}
