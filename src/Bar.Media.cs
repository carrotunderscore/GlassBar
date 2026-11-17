using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace GlassBar
{
    /// Spotify (or any app using Windows media controls) + speaker volume.
    public partial class BarWindow
    {
        GlobalSystemMediaTransportControlsSessionManager mediaMgr;
        GlobalSystemMediaTransportControlsSession media;
        bool mediaPlaying;
        string lastArtKey;

        async void InitMedia()
        {
            MediaArtBtn.MouseEnter += (s, e) => PipBadge.Visibility = IsVideoSource ? Visibility.Visible : Visibility.Collapsed;
            MediaArtBtn.MouseLeave += (s, e) => PipBadge.Visibility = Visibility.Collapsed;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            t.Tick += (s, e) => UpdateProgress();
            t.Start();
            try
            {
                mediaMgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                mediaMgr.SessionsChanged += (s, e) => Dispatcher.BeginInvoke(PickSession);
                mediaMgr.CurrentSessionChanged += (s, e) => Dispatcher.BeginInvoke(PickSession);
                PickSession();
            }
            catch { }
        }

        void PickSession()
        {
            if (mediaMgr == null) return;
            var sessions = mediaMgr.GetSessions();
            var pick = sessions.FirstOrDefault(x => x.SourceAppUserModelId?.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0)
                       ?? mediaMgr.GetCurrentSession() ?? sessions.FirstOrDefault();
            if (pick == media && media != null) { _ = UpdateMedia(); return; }
            if (media != null)
            {
                media.MediaPropertiesChanged -= OnMediaEvent;
                media.PlaybackInfoChanged -= OnMediaEvent;
            }
            media = pick;
            if (media != null)
            {
                media.MediaPropertiesChanged += OnMediaEvent;
                media.PlaybackInfoChanged += OnMediaEvent;
            }
            _ = UpdateMedia();
        }

        void OnMediaEvent(GlobalSystemMediaTransportControlsSession s, object e) => Dispatcher.BeginInvoke(() => _ = UpdateMedia());

        async Task UpdateMedia()
        {
            if (media == null)
            {
                MediaTitle.Text = "Nothing playing";
                MediaArtist.Text = "Open Spotify to start";
                MediaArt.ImageSource = null;
                mediaPlaying = false;
                PlayBtn.Content = "";
                lastArtKey = null;
                return;
            }
            try
            {
                var info = media.GetPlaybackInfo();
                mediaPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                PlayBtn.Content = mediaPlaying ? "" : "";

                var props = await media.TryGetMediaPropertiesAsync();
                MediaTitle.Text = string.IsNullOrWhiteSpace(props.Title) ? "Nothing playing" : props.Title;
                MediaArtist.Text = string.IsNullOrWhiteSpace(props.Artist) ? AppName(media.SourceAppUserModelId) : props.Artist;
                PipBadge.Visibility = IsVideoSource && MediaArtBtn.IsMouseOver ? Visibility.Visible : Visibility.Collapsed;
                MediaArtBtn.ToolTip = IsVideoSource ? "Pop out into a mini player" : "Open Spotify";

                var key = props.Title + "|" + props.Artist;
                if (key != lastArtKey)
                {
                    lastArtKey = key;
                    MediaArt.ImageSource = null;
                    if (props.Thumbnail != null)
                    {
                        using var ras = await props.Thumbnail.OpenReadAsync();
                        using var stream = ras.AsStreamForRead();
                        var ms = new MemoryStream();
                        await stream.CopyToAsync(ms);
                        ms.Position = 0;
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 120;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();
                        MediaArt.ImageSource = bmp;
                    }
                }
            }
            catch { }
        }

        static string AppName(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return "";
            if (aumid.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0) return "Spotify";
            var n = aumid.Split('!', '\\').Last();
            return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
        }

        void UpdateProgress()
        {
            double ratio = 0;
            try
            {
                if (media != null)
                {
                    var tl = media.GetTimelineProperties();
                    var len = (tl.EndTime - tl.StartTime).TotalSeconds;
                    var pos = tl.Position.TotalSeconds;
                    if (mediaPlaying) pos += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
                    if (len > 0) ratio = Math.Clamp(pos / len, 0, 1);
                }
            }
            catch { }
            var parent = (FrameworkElement)MediaProgress.Parent;
            MediaProgress.Width = parent.ActualWidth * ratio;
        }

        async void Play_Click(object s, RoutedEventArgs e)
        {
            if (media == null) { OpenSpotify(); return; }
            try { await media.TryTogglePlayPauseAsync(); } catch { }
        }

        async void Prev_Click(object s, RoutedEventArgs e) { try { if (media != null) await media.TrySkipPreviousAsync(); } catch { } }
        async void Next_Click(object s, RoutedEventArgs e) { try { if (media != null) await media.TrySkipNextAsync(); } catch { } }
        bool IsVideoSource => media != null && AppName(media.SourceAppUserModelId) != "Spotify";

        async void MediaArt_Click(object s, RoutedEventArgs e)
        {
            if (!IsVideoSource) { OpenSpotify(); return; }
            await OpenMiniPlayer();
        }

        /// Pops the browser's video out into a floating mini player, continuing from the same second.
        async Task OpenMiniPlayer()
        {
            var title = MediaTitle.Text;
            var channel = MediaArtist.Text;
            int seconds = 0;
            try
            {
                var tl = media.GetTimelineProperties();
                var pos = tl.Position.TotalSeconds;
                if (mediaPlaying) pos += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
                seconds = (int)pos;
            }
            catch { }
            ShowToast("Opening mini player…", 2);
            var source = media;
            if (await MiniPlayer.Open(title, channel, seconds))
            {
                try { await source.TryPauseAsync(); } catch { }   // so the sound doesn't play twice
            }
            else ShowToast("Couldn't find this video on YouTube for the mini player", 5);
        }

        void OpenSpotify()
        {
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe");
            if (File.Exists(exe)) Launch(exe);
            else Launch("spotify:");
        }

        // ---------------------------------------------------------------
        // volume (Core Audio)
        // ---------------------------------------------------------------
        bool volUpdating;

        void InitVolume()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            t.Tick += (s, e) => UpdateVolume();
            t.Start();
            UpdateVolume();
        }

        void UpdateVolume()
        {
            var v = Audio.Get();
            if (v == null) { VolDevice.Text = "No speakers"; VolPercent.Text = "–"; return; }
            volUpdating = true;
            if (!VolSlider.IsMouseCaptureWithin) VolSlider.Value = Math.Round(v.Value.level * 100);
            volUpdating = false;
            VolPercent.Text = $"{Math.Round(v.Value.level * 100)}%";
            VolDevice.Text = v.Value.name;
            MuteBtn.Content = v.Value.muted ? "" : "";
            MuteBtn.ToolTip = v.Value.muted ? "Unmute" : "Mute";
        }

        void Vol_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (volUpdating || VolPercent == null) return;
            Audio.SetLevel((float)(e.NewValue / 100));
            VolPercent.Text = $"{Math.Round(e.NewValue)}%";
        }

        void Mute_Click(object s, RoutedEventArgs e) { Audio.ToggleMute(); UpdateVolume(); if (Audio.Get()?.muted == false) VolumeBeep(); }
    }

    static partial class Audio
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int mask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int c);
            [PreserveSig] int GetAt(int i, out PROPERTYKEY k);
            [PreserveSig] int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
        }

        [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
        [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p; public IntPtr p2; }
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT v);

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr n);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr n);
            [PreserveSig] int GetChannelCount(out int c);
            [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float db);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(int ch, float db, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(int ch, float level, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(int ch, out float db);
            [PreserveSig] int GetChannelVolumeLevelScalar(int ch, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        static IMMDeviceEnumerator enumerator;
        static Guid ctx = Guid.Empty;

        static IMMDevice Device()
        {
            try
            {
                enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumerator();
                return enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var d) == 0 ? d : null;
            }
            catch { return null; }
        }

        static IAudioEndpointVolume Endpoint(IMMDevice d)
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            return d.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var o) == 0 ? (IAudioEndpointVolume)o : null;
        }

        static string Name(IMMDevice d)
        {
            try
            {
                if (d.OpenPropertyStore(0, out var store) != 0) return "Speakers";
                // PKEY_Device_DeviceDesc ("Speakers", "Headphones") reads nicer than the full friendly name
                var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 2 };
                if (store.GetValue(ref key, out var v) != 0) return "Speakers";
                var s = v.vt == 31 ? Marshal.PtrToStringUni(v.p) : null;
                PropVariantClear(ref v);
                return string.IsNullOrWhiteSpace(s) ? "Speakers" : s;
            }
            catch { return "Speakers"; }
        }

        public static (float level, bool muted, string name)? Get()
        {
            var d = Device();
            if (d == null) return null;
            var ep = Endpoint(d);
            if (ep == null) return null;
            ep.GetMasterVolumeLevelScalar(out float l);
            ep.GetMute(out bool m);
            return (l, m, Name(d));
        }

        public static void SetLevel(float l)
        {
            var d = Device(); if (d == null) return;
            var ep = Endpoint(d);
            ep?.SetMasterVolumeLevelScalar(Math.Clamp(l, 0, 1), ref ctx);
            if (l > 0) ep?.SetMute(false, ref ctx);
        }

        public static void ToggleMute()
        {
            var d = Device(); if (d == null) return;
            var ep = Endpoint(d); if (ep == null) return;
            ep.GetMute(out bool m);
            ep.SetMute(!m, ref ctx);
        }
    }
}
