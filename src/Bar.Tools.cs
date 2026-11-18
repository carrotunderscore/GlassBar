using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassBar
{
    /// Snipping, screen recording (ffmpeg) and the built-in terminal.
    public partial class BarWindow
    {
        void Snip_Click(object s, RoutedEventArgs e)
        {
            snipAsked = DateTime.Now;
            try { Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
            catch { Launch("SnippingTool.exe"); }
        }

        // =============================================================
        // Screen recorder
        // =============================================================
        Process rec;
        string recFile;
        DateTime recStart;
        DispatcherTimer recTimer;
        ListFlyout recFly;
        readonly StringBuilder recLog = new();

        public static string RecordingsDir
        {
            get
            {
                return SaveDir("Videos");
            }
        }

        static string ffmpegPath;
        static string FindFfmpeg()
        {
            if (ffmpegPath != null && File.Exists(ffmpegPath)) return ffmpegPath;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { var p = Path.Combine(dir.Trim(), "ffmpeg.exe"); if (File.Exists(p)) return ffmpegPath = p; } catch { }
            }
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var link = Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
            if (File.Exists(link)) return ffmpegPath = link;
            try
            {
                var pk = Path.Combine(local, "Microsoft", "WinGet", "Packages");
                foreach (var d in Directory.GetDirectories(pk, "*FFmpeg*"))
                {
                    var f = Directory.GetFiles(d, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (f != null) return ffmpegPath = f;
                }
            }
            catch { }
            return null;
        }

        void InitRecorder()
        {
            recTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            recTimer.Tick += (s, e) =>
            {
                var t = DateTime.Now - recStart;
                RecTime.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
                RecDot.Opacity = RecStop.Opacity = (DateTime.Now.Millisecond < 500) ? 1 : 0.55;
            };
        }

        async void Record_Click(object s, RoutedEventArgs e)
        {
            if (rec == null) StartRecording();
            else await StopRecording();
        }

        void StartRecording()
        {
            var ff = FindFfmpeg();
            if (ff == null) { ShowToast("Screen recording needs ffmpeg (winget install Gyan.FFmpeg)", 6); return; }
            var mon = Native.PrimaryMonitor();
            int w = (mon.Right - mon.Left) & ~1, h = (mon.Bottom - mon.Top) & ~1;
            recFile = Path.Combine(RecordingsDir, $"Recording {DateTime.Now:yyyy-MM-dd HH.mm.ss}.mp4");
            var args = new StringBuilder("-hide_banner -y ");
            args.Append($"-f gdigrab -framerate {Settings.RecordFps} -draw_mouse 1 -offset_x {mon.Left} -offset_y {mon.Top} -video_size {w}x{h} -i desktop ");
            bool mic = !string.IsNullOrEmpty(Settings.RecordMic);
            if (mic) args.Append($"-f dshow -i audio=\"{Settings.RecordMic}\" ");
            args.Append("-c:v libx264 -preset ultrafast -crf 26 -pix_fmt yuv420p ");
            if (mic) args.Append("-c:a aac -b:a 128k ");
            args.Append($"-movflags +faststart \"{recFile}\"");

            recLog.Clear();
            var psi = new ProcessStartInfo(ff, args.ToString())
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true
            };
            try
            {
                rec = Process.Start(psi);
                rec.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (recLog) { recLog.AppendLine(e.Data); if (recLog.Length > 20000) recLog.Remove(0, 10000); } };
                rec.OutputDataReceived += (s, e) => { };
                rec.BeginErrorReadLine();
                rec.BeginOutputReadLine();
                rec.EnableRaisingEvents = true;
                rec.Exited += (s, e) => Dispatcher.BeginInvoke(() => { if (rec != null && !stopping) _ = StopRecording(); });
            }
            catch (Exception ex) { rec = null; ShowToast("Couldn't start recording: " + ex.Message); return; }

            recStart = DateTime.Now;
            RecDot.Visibility = Visibility.Collapsed;
            RecStop.Visibility = RecInfo.Visibility = Visibility.Visible;
            RecBtn.ToolTip = "Stop recording (Ctrl+Alt+R)";
            recTimer.Start();
            ShowToast(mic ? "Recording screen + microphone… press Ctrl+Alt+R to stop" : "Recording screen… press Ctrl+Alt+R to stop", 3);
        }

        bool stopping;

        async Task StopRecording()
        {
            if (rec == null || stopping) return;
            stopping = true;
            var p = rec;
            try
            {
                if (!p.HasExited)
                {
                    try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { }
                    await Task.Run(() => p.WaitForExit(15000));
                    if (!p.HasExited) try { p.Kill(); } catch { }
                }
            }
            finally
            {
                rec = null;
                stopping = false;
                recTimer.Stop();
                RecDot.Visibility = Visibility.Visible;
                RecStop.Visibility = RecInfo.Visibility = Visibility.Collapsed;
                RecDot.Opacity = 1;
                RecBtn.ToolTip = "Record screen (Ctrl+Alt+R)";
            }

            var fi = new FileInfo(recFile);
            if (fi.Exists && fi.Length > 1000)
            {
                CopyFiles(recFile);
                ShowToast($"Saved {fi.Name} ({fi.Length / 1048576.0:0.0} MB) – copied, paste it into a chat with Ctrl+V", 7);
                if (recFly?.IsVisible == true) FillRecordings();
            }
            else
            {
                string log; lock (recLog) log = recLog.ToString();
                var last = log.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "unknown error";
                ShowToast("Recording failed: " + last, 8);
            }
        }

        void StopRecordingForExit()
        {
            if (rec == null) return;
            try { rec.StandardInput.Write("q"); rec.StandardInput.Flush(); rec.WaitForExit(8000); } catch { }
        }

        void RecMenu_Click(object s, RoutedEventArgs e)
        {
            if (recFly == null)
            {
                recFly = Register(new ListFlyout("Recordings", 480, 520)) ;
                recFly.EmptyText = "No recordings yet. Press the red button or Ctrl+Alt+R.";
                recFly.AddFooter(Flyout.Pill("Record", () => { recFly.Hide(); Record_Click(null, null); }, ""));
                recFly.AddFooter(Flyout.Pill("Microphone…", () => _ = MicMenu(), ""));
                recFly.AddFooter(Flyout.Pill("Open folder", () => Launch(RecordingsDir), ""));
            }
            FillRecordings();
            recFly.Toggle(ToolsCard);
        }

        void FillRecordings()
        {
            var files = new DirectoryInfo(RecordingsDir).GetFiles("*.mp4").OrderByDescending(f => f.LastWriteTime).Take(15).ToList();
            recFly.SetSubtitle(string.IsNullOrEmpty(Settings.RecordMic) ? "no microphone" : "mic: " + Settings.RecordMic);
            recFly.SetRows(files.Select(f => new ListRow
            {
                Glyph = "",
                Title = f.Name,
                Subtitle = $"{f.Length / 1048576.0:0.0} MB · {f.LastWriteTime:g}",
                Open = () => Launch(f.FullName),
                DragPath = f.FullName,
                Actions = new()
                {
                    ("", "Copy (paste into a chat with Ctrl+V)", () => { CopyFiles(f.FullName); ShowToast("Copied " + f.Name); }),
                    ("", "Show in folder", () => Launch("explorer.exe", $"/select,\"{f.FullName}\"")),
                }
            }));
        }

        async Task MicMenu()
        {
            var devices = await Task.Run(ListMics);
            var m = new ContextMenu();
            void Add(string label, string value)
            {
                var mi = new MenuItem { Header = label, IsChecked = (Settings.RecordMic ?? "") == value };
                mi.Click += (s, e) => { Settings.RecordMic = value; Settings.Save(); FillRecordings(); };
                m.Items.Add(mi);
            }
            Add("No microphone", "");
            foreach (var d in devices) Add(d, d);
            recFly.KeepOpen = true;
            m.Closed += (s, e) => recFly.KeepOpen = false;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            m.IsOpen = true;
        }

        static List<string> ListMics()
        {
            var list = new List<string>();
            var ff = FindFfmpeg();
            if (ff == null) return list;
            try
            {
                var p = Process.Start(new ProcessStartInfo(ff, "-hide_banner -list_devices true -f dshow -i dummy")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
                foreach (Match m in Regex.Matches(err, "\"([^\"]+)\" \\(audio\\)")) list.Add(m.Groups[1].Value);
            }
            catch { }
            return list;
        }

        // =============================================================
        // Terminal
        // =============================================================
        public TerminalSession Terminal;
        TerminalFlyout termFly;
        readonly List<string> termHistory = new();
        int termHistIndex;

        TerminalFlyout TermFly
        {
            get
            {
                if (termFly != null) return termFly;
                Terminal ??= new TerminalSession();
                termFly = Register(new TerminalFlyout(Terminal, RunCommand));
                return termFly;
            }
        }

        void TermBox_Focus(object s, KeyboardFocusChangedEventArgs e) { }

        void TermOpen_Click(object s, RoutedEventArgs e) => TermFly.Toggle(TermCard);

        void TermBox_KeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var cmd = TermBox.Text;
                TermBox.Text = "";
                RunCommand(cmd);
            }
            else if (e.Key == Key.Up || e.Key == Key.Down)
            {
                e.Handled = true;
                TermBox.Text = HistoryStep(e.Key == Key.Up ? -1 : 1);
                TermBox.CaretIndex = TermBox.Text.Length;
            }
            else if (e.Key == Key.Escape) { TermBox.Text = ""; termFly?.Hide(); }
        }

        public string HistoryStep(int dir)
        {
            if (termHistory.Count == 0) return "";
            termHistIndex = Math.Clamp(termHistIndex + dir, 0, termHistory.Count);
            return termHistIndex >= termHistory.Count ? "" : termHistory[termHistIndex];
        }

        void RunCommand(string cmd)
        {
            var fly = TermFly;
            if (!fly.IsVisible) fly.ShowAbove(TermCard);
            if (string.IsNullOrWhiteSpace(cmd)) return;
            termHistory.Remove(cmd);
            termHistory.Add(cmd);
            termHistIndex = termHistory.Count;
            if (cmd.Trim() is "cls" or "clear") { fly.ClearOutput(); return; }
            Terminal.Run(cmd);
            // keep typing in the bar
            Dispatcher.BeginInvoke(() => { Activate(); TermBox.Focus(); }, DispatcherPriority.Input);
        }
    }

    /// A persistent hidden PowerShell session (keeps the folder and variables between commands).
    public sealed class TerminalSession : IDisposable
    {
        const string Marker = "__GLASSBAR_DONE__";
        Process ps;
        public string Cwd { get; private set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public bool Busy { get; private set; }
        public event Action<string, bool> Output;     // text, isError
        public event Action StateChanged;

        public TerminalSession() => Start();

        void Start()
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -ExecutionPolicy Bypass -Command -")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Cwd
            };
            Busy = true;   // until the first marker arrives
            ps = Process.Start(psi);
            ps.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                if (e.Data.StartsWith(Marker))
                {
                    var dir = e.Data.Substring(Marker.Length);
                    Raise(() => { Cwd = dir; Busy = false; StateChanged?.Invoke(); Pump(); });
                }
                else Raise(() => Output?.Invoke(e.Data, false));
            };
            ps.ErrorDataReceived += (s, e) => { if (e.Data != null) Raise(() => Output?.Invoke(e.Data, true)); };
            ps.BeginOutputReadLine();
            ps.BeginErrorReadLine();
            Send("[Console]::OutputEncoding = [Text.Encoding]::UTF8; $OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'");
            Send($"Write-Host ('{Marker}' + (Get-Location).Path)");
        }

        static void Raise(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

        void Send(string line)
        {
            try { ps.StandardInput.WriteLine(line); ps.StandardInput.Flush(); } catch { }
        }

        readonly Queue<string> pending = new();
        public event Action<string> CommandStarted;

        /// Commands run one after another so their output never interleaves.
        public void Run(string cmd)
        {
            pending.Enqueue(cmd);
            Pump();
        }

        void Pump()
        {
            if (ps == null || ps.HasExited) { Start(); return; }
            if (Busy || pending.Count == 0) return;
            var cmd = pending.Dequeue();
            Busy = true;
            CommandStarted?.Invoke(cmd);
            StateChanged?.Invoke();
            // run, format as text (so tables print like in a console), then report the folder
            // dot-sourced block: variables and cd persist, output is formatted like a console
            Send(". { " + cmd.Replace("\r", "").Replace("\n", "; ") + " } | Out-String -Stream -Width 160");
            Send($"Write-Host ('{Marker}' + (Get-Location).Path)");
        }

        public void Restart()
        {
            try { if (ps != null && !ps.HasExited) ps.Kill(true); } catch { }
            pending.Clear();
            Start();
            StateChanged?.Invoke();
        }

        public void Dispose()
        {
            try { if (ps != null && !ps.HasExited) ps.Kill(true); } catch { }
        }
    }

    public class TerminalFlyout : Flyout
    {
        readonly TerminalSession session;
        readonly TextBox output;
        readonly TextBox input;
        readonly TextBlock cwd = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 };
        readonly TextBlock busy = new() { Text = " ●", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0xD6, 0x8A)), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

        public TerminalFlyout(TerminalSession s, Action<string> run) : base("Terminal", 720, 460)
        {
            session = s;
            StaysOpenOverBar = true;
            cwd.Foreground = Res("TextSecondary");
            HeaderExtra.Children.Add(cwd);
            HeaderExtra.Children.Add(busy);
            AddHeaderButton("", "Restart session", () => { session.Restart(); Append("— session restarted —\n"); });
            AddHeaderButton("", "Clear", ClearOutput);
            AddHeaderButton("", "Open in Windows Terminal", OpenFull);

            output = new TextBox
            {
                IsReadOnly = true, FontFamily = Mono, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xE2, 0xF5)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12, 10, 12, 10), CaretBrush = Brushes.White,
                SelectionBrush = Res("Accent")
            };
            input = new TextBox { FontFamily = Mono, FontSize = 12.5, Style = (Style)Application.Current.Resources["PlainBox"], Margin = new Thickness(6, 0, 0, 0) };
            input.KeyDown += (o, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; var c = input.Text; input.Text = ""; run(c); input.Focus(); }
                else if (e.Key == Key.Up || e.Key == Key.Down) { e.Handled = true; input.Text = BarWindow.Instance.HistoryStep(e.Key == Key.Up ? -1 : 1); input.CaretIndex = input.Text.Length; }
            };
            var prompt = new TextBlock { Text = "›", FontFamily = Mono, FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0xD6, 0x8A)), VerticalAlignment = VerticalAlignment.Center };
            var inRow = new DockPanel { Margin = new Thickness(14, 0, 14, 0), Height = 40 };
            inRow.Children.Add(prompt);
            inRow.Children.Add(input);
            var inBorder = new Border { Child = inRow, Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), BorderBrush = Res("ControlBorder"), BorderThickness = new Thickness(0, 1, 0, 0), CornerRadius = new CornerRadius(0, 0, 10, 10) };
            DockPanel.SetDock(inBorder, Dock.Bottom);

            var panel = new DockPanel();
            panel.Children.Add(inBorder);
            panel.Children.Add(output);
            Body.Children.Add(new Border
            {
                Margin = new Thickness(10, 0, 10, 10), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)),
                BorderBrush = Res("CardBorder"), BorderThickness = new Thickness(1), Child = panel
            });

            session.Output += (text, err) => Append(text + "\n");
            session.StateChanged += UpdateState;
            session.CommandStarted += Echo;
            UpdateState();
        }

        void UpdateState()
        {
            cwd.Text = session.Cwd;
            busy.Visibility = session.Busy ? Visibility.Visible : Visibility.Collapsed;
        }

        public void Echo(string cmd) => Append($"\nPS {session.Cwd}> {cmd}\n");

        void Append(string s)
        {
            output.AppendText(s);
            if (output.Text.Length > 150000) output.Text = output.Text[^100000..];
            output.ScrollToEnd();
        }

        public void ClearOutput() => output.Clear();

        void OpenFull()
        {
            try { Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{session.Cwd}\"") { UseShellExecute = true }); }
            catch { BarWindow.Instance.Launch("powershell.exe", "-NoExit", session.Cwd); }
        }
    }
}
