using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Tolmach;

internal sealed class Tray : IDisposable
{
    readonly NotifyIcon _icon;

    public Tray(Action translateClipboard, Action translateRegion, Func<IReadOnlyList<string>> failedHotkeys, Action exit)
    {
        var keys = Settings.Current.Hotkeys;
        var menu = new ContextMenuStrip();

        var clipboard = new ToolStripMenuItem("Перевести скопированное") { Font = new Font(menu.Font, FontStyle.Bold) };
        clipboard.Click += (_, _) => translateClipboard();
        menu.Items.Add(clipboard);

        var region = new ToolStripMenuItem($"Перевести область экрана ({keys.TranslateRegion})");
        region.Click += (_, _) => translateRegion();
        menu.Items.Add(region);

        var hint = new ToolStripMenuItem($"Выделенное или текст под курсором — {keys.TranslateSelection}") { Enabled = false };
        menu.Items.Add(hint);

        var problems = new ToolStripMenuItem { Enabled = false, Visible = false };
        menu.Items.Add(problems);

        menu.Items.Add(new ToolStripSeparator());

        var target = new ToolStripMenuItem("Переводить на");
        foreach (string code in Settings.Current.Languages)
        {
            var item = new ToolStripMenuItem(Translator.NameOf(code)) { Tag = code };
            item.Click += (_, _) =>
            {
                Settings.Current.TargetLanguage = (string)item.Tag!;
                // The other language for text already in the new target.
                if (Settings.Current.SecondLanguage == Settings.Current.TargetLanguage)
                {
                    Settings.Current.SecondLanguage = Settings.Current.TargetLanguage == "en" ? "ru" : "en";
                }
                Settings.Save();
            };
            target.DropDownItems.Add(item);
        }
        target.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in target.DropDownItems)
            {
                item.Checked = (string)item.Tag! == Settings.Current.TargetLanguage;
            }
        };
        menu.Items.Add(target);

        var doubleCopy = new ToolStripMenuItem("Двойной Ctrl+C переводит скопированное")
        {
            Checked = Settings.Current.DoubleCopy,
            CheckOnClick = true,
        };
        doubleCopy.CheckedChanged += (_, _) =>
        {
            Settings.Current.DoubleCopy = doubleCopy.Checked;
            Settings.Save();
        };
        menu.Items.Add(doubleCopy);

        var showOriginal = new ToolStripMenuItem("Показывать оригинал")
        {
            Checked = Settings.Current.ShowOriginal,
            CheckOnClick = true,
        };
        showOriginal.CheckedChanged += (_, _) =>
        {
            Settings.Current.ShowOriginal = showOriginal.Checked;
            Settings.Save();
        };
        menu.Items.Add(showOriginal);

        var service = new ToolStripMenuItem("Сервис перевода");
        foreach (var (id, label) in new[]
                 {
                     ("auto", "Автоматически (DeepL → Google → MyMemory)"),
                     ("google", "Сначала Google"),
                     ("mymemory", "Сначала MyMemory"),
                     ("deepl", "Сначала DeepL (ключ — DeepLKey в settings.json)"),
                 })
        {
            var item = new ToolStripMenuItem(label) { Tag = id };
            item.Click += (_, _) =>
            {
                Settings.Current.Provider = (string)item.Tag!;
                Settings.Save();
            };
            service.DropDownItems.Add(item);
        }
        service.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in service.DropDownItems)
            {
                item.Checked = string.Equals((string)item.Tag!, Settings.Current.Provider, StringComparison.OrdinalIgnoreCase);
            }
        };
        menu.Items.Add(service);

        menu.Items.Add(new ToolStripSeparator());

        var autostart = new ToolStripMenuItem("Запускать вместе с Windows")
        {
            Checked = Autostart.IsEnabled,
            CheckOnClick = true,
        };
        autostart.CheckedChanged += (_, _) => Autostart.Set(autostart.Checked);
        menu.Items.Add(autostart);

        var folder = new ToolStripMenuItem("Папка настроек (клавиши, языки, ключ DeepL)");
        folder.Click += (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.Folder}\"") { UseShellExecute = true });
        menu.Items.Add(folder);

        menu.Items.Add(new ToolStripSeparator());
        var quit = new ToolStripMenuItem("Выход");
        quit.Click += (_, _) => exit();
        menu.Items.Add(quit);

        menu.Opening += (_, _) =>
        {
            var failed = failedHotkeys();
            problems.Visible = failed.Count > 0;
            problems.Text = "Заняты другой программой: " + string.Join(", ", failed);
        };

        _icon = new NotifyIcon
        {
            Icon = DrawIcon(),
            Text = "Толмач — перевод под курсором",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                translateClipboard();
            }
        };
    }

    public void Balloon(string title, string text)
    {
        _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.None);
    }

    // A speech bubble in the accent color with "Aя" in it.
    static Icon DrawIcon()
    {
        var accent = Theme.Accent;
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(System.Drawing.Color.Transparent);
            using var bubble = new GraphicsPath();
            bubble.AddArc(1, 2, 10, 10, 180, 90);
            bubble.AddArc(21, 2, 10, 10, 270, 90);
            bubble.AddArc(21, 16, 10, 10, 0, 90);
            bubble.AddLine(16, 26, 9, 31);
            bubble.AddLine(9, 31, 10, 26);
            bubble.AddArc(1, 16, 10, 10, 90, 90);
            bubble.CloseFigure();
            using var fill = new SolidBrush(System.Drawing.Color.FromArgb(255, accent.R, accent.G, accent.B));
            g.FillPath(fill, bubble);
            using var font = new Font("Segoe UI", 11.5f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(System.Drawing.Color.White);
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("Aя", font, text, new RectangleF(0, 2, 32, 24), format);
        }
        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
