using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassBar
{
    /// Volume click sound (like the Windows taskbar) and a per-app volume mixer.
    public partial class BarWindow
    {
        [DllImport("user32.dll")] static extern bool MessageBeep(uint type);
        DispatcherTimer beepTimer;

        /// Plays the "Default Beep" so you hear the new level. Debounced so a wheel spin plays it once.
        public void VolumeBeep(int delayMs = 0)
        {
            if (delayMs <= 0) { MessageBeep(0); return; }
            beepTimer ??= new DispatcherTimer();
            beepTimer.Stop();
            beepTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
            beepTimer.Tick -= BeepTick;
            beepTimer.Tick += BeepTick;
            beepTimer.Start();
        }
        void BeepTick(object s, EventArgs e) { beepTimer.Stop(); MessageBeep(0); }

        void InitVolumeSound()
        {
            // click sound when the slider is let go (drag or click on the track)
            VolSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((s, e) => VolumeBeep()));
            VolSlider.PreviewMouseLeftButtonUp += (s, e) => { if (e.OriginalSource is not Thumb) VolumeBeep(60); };
        }

        MixerFlyout mixer;
        void Mixer_Click(object s, RoutedEventArgs e)
        {
            mixer ??= Register(new MixerFlyout());
            mixer.Toggle(VolumeCard);
        }
    }

    public class MixerFlyout : Flyout
    {
        readonly StackPanel rows = new() { Margin = new Thickness(14, 0, 14, 12) };
        readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(800) };
        readonly Dictionary<uint, (Slider slider, TextBlock pct, Button mute, Audio.Session session)> live = new();
        bool updating;

        public MixerFlyout() : base("Volume mixer", 400, 470)
        {
            Body.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rows });
            AddHeaderButton("", "Sound settings", () => BarWindow.Instance.Launch("ms-settings:apps-volume"));
            timer.Tick += (s, e) => { if (IsVisible) Refresh(false); else timer.Stop(); };
        }

        protected override void OnShown() { Refresh(true); timer.Start(); }

        void Refresh(bool rebuild)
        {
            var sessions = Audio.Sessions();
            var ids = sessions.Select(x => x.Pid).ToHashSet();
            if (rebuild || !ids.SetEquals(live.Keys))
            {
                rows.Children.Clear();
                live.Clear();
                AddMaster();
                if (sessions.Count == 0)
                    rows.Children.Add(new TextBlock { Text = "No apps are playing sound", Foreground = Res("TextSecondary"), Margin = new Thickness(6, 14, 0, 0) });
                foreach (var se in sessions) AddRow(se);
                return;
            }
            updating = true;
            foreach (var se in sessions)
            {
                var r = live[se.Pid];
                r = (r.slider, r.pct, r.mute, se);
                live[se.Pid] = r;
                if (!r.slider.IsMouseCaptureWithin) r.slider.Value = Math.Round(se.Volume * 100);
                r.pct.Text = $"{Math.Round(r.slider.Value)}";
                r.mute.Content = se.Muted ? "" : "";
            }
            updating = false;
        }

        void AddMaster()
        {
            var v = Audio.Get();
            if (v == null) return;
            var (row, slider, pct, mute) = Row(Glyph("", 20, Res("Accent")), v.Value.name, "Device volume", v.Value.level, v.Value.muted);
            slider.ValueChanged += (s, e) => { if (!updating) { Audio.SetLevel((float)(e.NewValue / 100)); pct.Text = $"{Math.Round(e.NewValue)}"; } };
            mute.Click += (s, e) => { Audio.ToggleMute(); var g = Audio.Get(); mute.Content = g?.muted == true ? "" : ""; if (g?.muted == false) BarWindow.Instance.VolumeBeep(); };
            rows.Children.Add(row);
            rows.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x20, 255, 255, 255)), Margin = new Thickness(0, 4, 0, 6) });
        }

        void AddRow(Audio.Session se)
        {
            FrameworkElement icon = se.Icon != null
                ? new Image { Source = se.Icon, Width = 26, Height = 26 }
                : Glyph(se.IsSystem ? "" : "", 18);
            var (row, slider, pct, mute) = Row(icon, se.Name, se.IsSystem ? "Windows sounds" : null, se.Volume, se.Muted);
            uint pid = se.Pid;
            slider.ValueChanged += (s, e) =>
            {
                if (updating) return;
                live[pid].session.SetVolume((float)(e.NewValue / 100));
                pct.Text = $"{Math.Round(e.NewValue)}";
            };
            mute.Click += (s, e) => { var x = live[pid].session; x.SetMute(!x.Muted); mute.Content = x.Muted ? "" : ""; };
            live[pid] = (slider, pct, mute, se);
            rows.Children.Add(row);
        }

        (Grid row, Slider slider, TextBlock pct, Button mute) Row(FrameworkElement icon, string name, string sub, float level, bool muted)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            g.Children.Add(icon);

            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            if (sub != null) title.ToolTip = sub;
            mid.Children.Add(title);
            var slider = new Slider
            {
                Style = (Style)BarWindow.Instance.FindResource("BarSlider"), Minimum = 0, Maximum = 100,
                Value = Math.Round(level * 100), Margin = new Thickness(0, 4, 4, 0)
            };
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((s, e) => BarWindow.Instance.VolumeBeep()));
            slider.PreviewMouseLeftButtonUp += (s, e) => { if (e.OriginalSource is not Thumb) BarWindow.Instance.VolumeBeep(60); };
            mid.Children.Add(slider);
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            var pct = new TextBlock { Text = $"{Math.Round(level * 100)}", FontSize = 12, Foreground = Res("TextSecondary"), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 2) };
            Grid.SetColumn(pct, 2);
            g.Children.Add(pct);

            var mute = new Button { Style = (Style)Application.Current.Resources["IconButton"], Width = 30, Height = 30, FontSize = 12, Content = muted ? "" : "", ToolTip = "Mute", VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetColumn(mute, 3);
            g.Children.Add(mute);
            return (g, slider, pct, mute);
        }
    }

    static partial class Audio
    {
        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr ctl);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr vol);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int n);
            [PreserveSig] int GetSession(int i, out IAudioSessionControl2 s);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName(string n, ref Guid ctx);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath(string p, ref Guid ctx);
            [PreserveSig] int GetGroupingParam(out Guid g);
            [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference(bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        public class Session
        {
            internal ISimpleAudioVolume Vol;
            public uint Pid;
            public string Name;
            public ImageSource Icon;
            public bool IsSystem;
            public float Volume;
            public bool Muted;
            public void SetVolume(float v) { try { Vol.SetMasterVolume(Math.Clamp(v, 0, 1), ref ctx); Volume = v; if (v > 0 && Muted) SetMute(false); } catch { } }
            public void SetMute(bool m) { try { Vol.SetMute(m, ref ctx); Muted = m; } catch { } }
        }

        static readonly Dictionary<string, ImageSource> icons = new(StringComparer.OrdinalIgnoreCase);

        /// The apps that currently have an audio session on the default output device (one row per process).
        public static List<Session> Sessions()
        {
            var list = new List<Session>();
            try
            {
                var d = Device(); if (d == null) return list;
                var iid = typeof(IAudioSessionManager2).GUID;
                if (d.Activate(ref iid, 1, IntPtr.Zero, out var o) != 0) return list;
                var mgr = (IAudioSessionManager2)o;
                if (mgr.GetSessionEnumerator(out var en) != 0) return list;
                en.GetCount(out int n);
                var seen = new HashSet<uint>();
                for (int i = 0; i < n; i++)
                {
                    if (en.GetSession(i, out var c) != 0 || c == null) continue;
                    c.GetState(out int state);
                    if (state == 2) continue;   // expired
                    c.GetProcessId(out uint pid);
                    bool sys = c.IsSystemSoundsSession() == 0;
                    if (!seen.Add(sys ? 0 : pid)) continue;
                    var vol = (ISimpleAudioVolume)c;
                    vol.GetMasterVolume(out float lv);
                    vol.GetMute(out bool mu);
                    var s = new Session { Vol = vol, Pid = sys ? 0 : pid, IsSystem = sys, Volume = lv, Muted = mu };
                    if (sys) s.Name = "System sounds";
                    else
                    {
                        string exe = Native.ProcessPath(pid);
                        string desc = null;
                        if (exe != null)
                        {
                            try { desc = FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { }
                            if (!icons.TryGetValue(exe, out var ic)) icons[exe] = ic = Native.ShellImage(exe, 48);
                            s.Icon = ic;
                        }
                        c.GetDisplayName(out var dn);
                        s.Name = !string.IsNullOrWhiteSpace(desc) ? desc
                               : !string.IsNullOrWhiteSpace(dn) && !dn.StartsWith("@") ? dn
                               : exe != null ? Path.GetFileNameWithoutExtension(exe) : "App " + pid;
                    }
                    list.Add(s);
                }
            }
            catch { }
            return list.OrderBy(x => x.IsSystem ? 0 : 1).ThenBy(x => x.Name).ToList();
        }
    }
}
