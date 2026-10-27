using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Tolmach;

public partial class App : Application
{
    Mutex? _singleInstance;
    PopupWindow? _popup;
    Tray? _tray;
    Hotkeys? _hotkeys;
    DoubleCopyWatcher? _doubleCopy;
    CancellationTokenSource? _current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Settings.Load();

        bool restarted = e.Args.Contains("--restarted");
        if (e.Args.Length > 0 && e.Args[0].StartsWith("--", StringComparison.Ordinal) && !restarted)
        {
            int code = RunCommandLine(e.Args);
            Shutdown(code);
            return;
        }

        // No "has stopped working" dialog: it would keep a crashed instance
        // alive and the replacement waiting for it.
        Native.WerSetFlags(Native.WER_FAULT_REPORTING_NO_UI);

        _singleInstance = new Mutex(false, "Tolmach.SingleInstance");
        bool owned;
        try
        {
            owned = _singleInstance.WaitOne(restarted ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }
        if (!owned)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("Unhandled: " + args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write("Unobserved: " + args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Write("Fatal: " + args.ExceptionObject);
            RestartAfterCrash();
        };

        // The program may have been moved, or set up with an older version.
        Task.Run(Autostart.Repair);
        // The dictionaries, so the first reading from the screen is as quick
        // as the others.
        Task.Run(Lexicon.Warmup);

        _popup = new PopupWindow();
        _popup.LanguageChosen += code =>
        {
            if (_popup.Source != null)
            {
                _ = TranslateAsync(_popup.Source, null, code);
            }
        };

        _hotkeys = new Hotkeys();
        var keys = Settings.Current.Hotkeys;
        _hotkeys.Register(keys.TranslateSelection, () => _ = TranslateSelectionAsync(), repeat: false);
        _hotkeys.Register(keys.TranslateRegion, () => _ = TranslateRegionAsync(), repeat: false);

        _doubleCopy = new DoubleCopyWatcher(Dispatcher);
        _doubleCopy.DoubleCopy += () =>
        {
            if (Settings.Current.DoubleCopy)
            {
                _ = TranslateClipboardAsync(waitForCopy: true);
            }
        };
        _doubleCopy.Start();

        _tray = new Tray(() => _ = TranslateClipboardAsync(waitForCopy: false), () => _ = TranslateRegionAsync(),
            () => _hotkeys.Failed, Shutdown);

        Log.Write($"Started; hotkeys failed: {string.Join(", ", _hotkeys.Failed)}");

        if (!File.Exists(Path.Combine(Settings.Folder, "welcomed")))
        {
            File.WriteAllText(Path.Combine(Settings.Folder, "welcomed"), "");
            _tray.Balloon("Толмач работает",
                $"Выделите текст и нажмите {keys.TranslateSelection} — или просто наведите курсор на текст в игре и нажмите то же. " +
                $"{keys.TranslateRegion} — перевести область экрана. Двойной Ctrl+C — перевести скопированное.");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance != null)
        {
            Log.Write("Exited");
        }
        _doubleCopy?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        if (_singleInstance != null)
        {
            try
            {
                _singleInstance.ReleaseMutex();
            }
            catch
            {
            }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }

    // --------------------------------------------------------------- actions

    static Rect CursorAnchor()
    {
        Native.GetCursorPos(out var cursor);
        return new Rect(cursor.X, cursor.Y, 1, 18);
    }

    // The selected text; without a selection (a game, a picture, a button
    // label) the text under the cursor, read from the screen.
    async Task TranslateSelectionAsync()
    {
        var anchor = CursorAnchor();
        string? text = await TextGrabber.GrabSelectionAsync();
        TextDocument? document = string.IsNullOrWhiteSpace(text) ? null : Layout.FromText(text);
        if (document == null)
        {
            _popup!.ShowWorking(null, anchor);
            try
            {
                document = await ScreenOcr.TextUnderCursorAsync();
            }
            catch (Exception ex)
            {
                Log.Write("OCR failed: " + ex);
                _popup.ShowError(ex.Message);
                return;
            }
            Log.Write("Under cursor: " + ScreenOcr.LastReport);
            if (document == null || document.IsEmpty)
            {
                _popup.ShowError("Под курсором не нашлось текста. Выделите его или нажмите " +
                                 $"{Settings.Current.Hotkeys.TranslateRegion}, чтобы обвести область.");
                return;
            }
        }
        await TranslateAsync(document, anchor, null);
    }

    async Task TranslateClipboardAsync(bool waitForCopy)
    {
        var anchor = CursorAnchor();
        if (waitForCopy)
        {
            // The second Ctrl+C is still being handled by the program.
            await Task.Delay(120);
        }
        string? text = TextGrabber.ReadText();
        if (string.IsNullOrWhiteSpace(text))
        {
            _popup!.ShowWorking(null, anchor);
            _popup.ShowError("В буфере обмена нет текста.");
            return;
        }
        await TranslateAsync(Layout.FromText(text), anchor, null);
    }

    async Task TranslateRegionAsync()
    {
        _popup?.HidePopup();
        var selection = await RegionSelector.SelectAsync();
        if (selection == null)
        {
            return;
        }
        var area = selection.ScreenArea;
        var anchor = new Rect(area.Left, area.Top, area.Width, area.Height);
        _popup!.ShowWorking(null, anchor);
        TextDocument document;
        try
        {
            using (selection.Image)
            {
                document = Layout.FromLines(await ScreenOcr.RecognizeAsync(selection.Image));
            }
            Log.Write("Region: " + ScreenOcr.LastReport);
        }
        catch (Exception ex)
        {
            Log.Write("OCR failed: " + ex);
            _popup.ShowError(ex.Message);
            return;
        }
        if (document.IsEmpty)
        {
            _popup.ShowError("В выделенной области не нашлось текста.");
            return;
        }
        await TranslateAsync(document, anchor, null);
    }

    // anchor null: keep the popup where it is (another language chosen).
    async Task TranslateAsync(TextDocument document, Rect? anchor, string? to)
    {
        _current?.Cancel();
        var cts = new CancellationTokenSource();
        _current = cts;

        if (anchor is Rect at)
        {
            _popup!.ShowWorking(document, at);
        }
        else
        {
            _popup!.ShowOriginal(document);
        }

        try
        {
            var (translated, result) = await Translator.TranslateDocumentAsync(document, to, cts.Token);
            if (!cts.IsCancellationRequested)
            {
                _popup.ShowResult(translated, result);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Write("Translation failed: " + ex);
            _popup.ShowError(ex is System.Net.Http.HttpRequestException or TaskCanceledException
                ? "Нет связи с сервисом перевода. Проверьте интернет."
                : ex.Message);
        }
    }

    // Brings the program back after a crash, but gives up after three crashes
    // in ten minutes rather than looping.
    static void RestartAfterCrash()
    {
        try
        {
            string file = Path.Combine(Settings.Folder, "restarts.txt");
            DateTime now = DateTime.UtcNow;
            var recent = new List<DateTime>();
            if (File.Exists(file))
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    if (DateTime.TryParse(line, null, System.Globalization.DateTimeStyles.RoundtripKind, out var time) &&
                        now - time < TimeSpan.FromMinutes(10))
                    {
                        recent.Add(time);
                    }
                }
            }
            if (recent.Count >= 3)
            {
                Log.Write("Crashed 3 times in 10 minutes, not restarting");
                return;
            }
            recent.Add(now);
            File.WriteAllLines(file, recent.Select(t => t.ToString("o")));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--restarted")
            {
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Log.Write("Restart after crash failed: " + ex.Message);
        }
    }

    // ---------------------------------------------------------- command line

    // For checking without the keyboard:
    //   --translate <report> <text>                     translation, layout kept
    //   --ocr-file <report> <image> [<x> <y>...]        text from a picture: readings,
    //                                                   fixes, layout, block at points
    //   --ocr-screen <report> <x> <y> <w> <h>           the same for a part of the screen
    //   --render <out.png> <text>                       the popup with a translation
    //   --install-autostart                             the logon task
    int RunCommandLine(string[] args)
    {
        try
        {
            switch (args[0])
            {
                case "--translate":
                {
                    var document = Layout.FromText(args[2]);
                    var (translated, result) = Task.Run(() => Translator.TranslateDocumentAsync(document, null, CancellationToken.None)).GetAwaiter().GetResult();
                    File.WriteAllLines(args[1], new[] { $"{result.SourceLanguage} -> {result.TargetLanguage} ({result.Service})", "--- layout:" }
                        .Concat(Describe(document)).Concat(new[] { "--- translation:" }).Concat(Describe(translated)));
                    return 0;
                }
                case "--ocr-file":
                case "--ocr-screen":
                {
                    System.Drawing.Bitmap bitmap;
                    int pointsFrom;
                    if (args[0] == "--ocr-file")
                    {
                        bitmap = new System.Drawing.Bitmap(args[2]);
                        pointsFrom = 3;
                    }
                    else
                    {
                        bitmap = ScreenOcr.Capture(new System.Drawing.Rectangle(int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5])));
                        pointsFrom = 6;
                    }
                    using (bitmap)
                    {
                        var raw = Task.Run(() => ScreenOcr.RecognizeAsync(bitmap, correct: false)).GetAwaiter().GetResult();
                        var lines = Task.Run(() => ScreenOcr.RecognizeAsync(bitmap)).GetAwaiter().GetResult();
                        var output = new List<string> { "languages: " + string.Join(", ", ScreenOcr.Languages), ScreenOcr.LastReport, "--- lines (read → fixed):" };
                        for (int i = 0; i < lines.Count; i++)
                        {
                            string before = i < raw.Count ? raw[i].Text : "";
                            output.Add($"[{lines[i].Bounds.X:0},{lines[i].Bounds.Y:0} h{lines[i].Height:0}] {lines[i].Text}" +
                                       (before != lines[i].Text ? $"    ← {before}" : ""));
                        }
                        output.Add("--- layout:");
                        output.AddRange(Describe(Layout.FromLines(lines)));
                        for (int i = pointsFrom; i + 1 < args.Length; i += 2)
                        {
                            var point = new System.Drawing.PointF(float.Parse(args[i]), float.Parse(args[i + 1]));
                            var block = Layout.BlockAt(lines, point);
                            output.Add($"--- at {point.X},{point.Y}:");
                            output.AddRange(block == null ? new[] { "(nothing)" } : Describe(block));
                        }
                        File.WriteAllLines(args[1], output);
                    }
                    return 0;
                }
                case "--translate-image":
                {
                    // --translate-image <report> <image> [<popup.png>]: the whole way, as
                    // the region hotkey does it.
                    using var bitmap = new System.Drawing.Bitmap(args[2]);
                    var document = Layout.FromLines(Task.Run(() => ScreenOcr.RecognizeAsync(bitmap)).GetAwaiter().GetResult());
                    var (translated, result) = Task.Run(() => Translator.TranslateDocumentAsync(document, null, CancellationToken.None)).GetAwaiter().GetResult();
                    File.WriteAllLines(args[1], new[] { $"{result.SourceLanguage} -> {result.TargetLanguage} ({result.Service})", ScreenOcr.LastReport, "--- original:" }
                        .Concat(Describe(document)).Concat(new[] { "--- translation:" }).Concat(Describe(translated)));
                    if (args.Length > 3)
                    {
                        var popup = new PopupWindow();
                        popup.ShowWorking(document, Rect.Empty);
                        popup.ShowResult(translated, result);
                        popup.HidePopup();
                        popup.RenderToPng(args[3]);
                    }
                    return 0;
                }
                case "--render":
                {
                    var popup = new PopupWindow();
                    var document = Layout.FromText(args[2]);
                    popup.ShowWorking(document, Rect.Empty);
                    var (translated, result) = Task.Run(() => Translator.TranslateDocumentAsync(document, null, CancellationToken.None)).GetAwaiter().GetResult();
                    popup.ShowResult(translated, result);
                    popup.HidePopup();
                    popup.RenderToPng(args[1]);
                    return 0;
                }
                case "--install-autostart":
                    Autostart.Set(true);
                    return Autostart.IsEnabled ? 0 : 1;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Command line failed: " + ex);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "tolmach-cli-error.txt"), ex.ToString());
            return 2;
        }
        return 3;
    }

    static IEnumerable<string> Describe(TextDocument document)
    {
        foreach (var block in document.Blocks)
        {
            yield return "  BLOCK";
            foreach (var item in block.Items)
            {
                string style = item.Case == CaseStyle.Normal ? "" : "/" + item.Case;
                yield return $"    {(item.SameRow ? "  | " : "")}[{item.Kind}{style}] {item}";
            }
        }
    }
}
