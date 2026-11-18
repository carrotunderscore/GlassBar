using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Radios;

namespace GlassBar
{
    /// Quick toggles: Wi-Fi and Bluetooth pills (click the icon to turn it on/off, the arrow for more),
    /// a snip button and "keep awake".
    public partial class BarWindow
    {
        Radio wifiRadio, btRadio;
        QuickPill wifiPill, btPill;
        Button awakeBtn;
        bool keepAwake;

        static Brush OnBg => Flyout.Res("AccentSoft");
        static readonly Brush OffBg = Make(0x12, 0xFF, 0xFF, 0xFF), OffBorder = Make(0x1F, 0xFF, 0xFF, 0xFF);
        static Brush Make(byte a, byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }

        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

        void InitQuick()
        {
            wifiPill = new QuickPill("", () => _ = ToggleRadio(wifiRadio, "ms-settings:network-wifi"), () => Launch("ms-availablenetworks:"), "Wi-Fi networks");
            btPill = new QuickPill("", () => _ = ToggleRadio(btRadio, "ms-settings:bluetooth"), () => Launch("ms-settings:bluetooth"), "Bluetooth devices");
            wifiPill.Margin = new Thickness(0, 0, 8, 0);
            btPill.Margin = new Thickness(0, 0, 8, 0);
            QuickPanel.Children.Add(wifiPill);
            QuickPanel.Children.Add(btPill);
            awakeBtn = RoundIcon("☕", "", ToggleAwake, new Thickness(0));
            awakeBtn.FontFamily = new FontFamily("Segoe UI Symbol");
            awakeBtn.FontSize = 16;
            QuickPanel.Children.Add(awakeBtn);
            SetAwakeLook();
            if (snapshotPath == null) _ = LoadRadios();
            else { wifiPill.Set(true, "Wi-Fi"); btPill.Set(false, "Bluetooth"); }

            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            t.Tick += (s, e) => UpdateRadios();
            t.Start();
        }

        Button RoundIcon(string glyph, string tip, Action click, Thickness margin)
        {
            var b = new Button
            {
                Style = (Style)FindResource("RoundBtn"), Width = 40, Height = 40, Content = glyph, ToolTip = tip, FontSize = 15,
                Background = OffBg, BorderBrush = OffBorder, Foreground = Flyout.Res("TextPrimary"), Margin = margin
            };
            b.Click += (s, e) => click();
            return b;
        }

        async Task LoadRadios()
        {
            try
            {
                await Radio.RequestAccessAsync();
                var all = await Radio.GetRadiosAsync();
                wifiRadio = all.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
                btRadio = all.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                foreach (var r in new[] { wifiRadio, btRadio }.Where(r => r != null))
                    r.StateChanged += (s, e) => Dispatcher.BeginInvoke(UpdateRadios);
            }
            catch { }
            UpdateRadios();
        }

        void UpdateRadios()
        {
            if (wifiPill == null || snapshotPath != null) return;
            bool wifiOn = wifiRadio?.State == RadioState.On;
            string ssid = null;
            try { ssid = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile()?.WlanConnectionProfileDetails?.GetConnectedSsid(); } catch { }
            wifiPill.Set(wifiOn, wifiRadio == null ? "Wi-Fi (open settings)"
                                 : !wifiOn ? "Wi-Fi is off: click to turn it on"
                                 : string.IsNullOrEmpty(ssid) ? "Wi-Fi on, not connected" : $"Wi-Fi: {ssid}");
            bool btOn = btRadio?.State == RadioState.On;
            btPill.Set(btOn, btRadio == null ? "Bluetooth (open settings)" : btOn ? "Bluetooth is on: click to turn it off" : "Bluetooth is off: click to turn it on");
            btPill.Visibility = Visibility.Visible;
        }

        async Task ToggleRadio(Radio r, string fallback)
        {
            if (r == null) { Launch(fallback); return; }
            try
            {
                var res = await r.SetStateAsync(r.State == RadioState.On ? RadioState.Off : RadioState.On);
                if (res != RadioAccessStatus.Allowed) { Launch(fallback); return; }
            }
            catch { Launch(fallback); return; }
            UpdateRadios();
        }

        void ToggleAwake()
        {
            keepAwake = !keepAwake;
            const uint CONTINUOUS = 0x80000000, SYSTEM = 0x1, DISPLAY = 0x2;
            SetThreadExecutionState(keepAwake ? CONTINUOUS | SYSTEM | DISPLAY : CONTINUOUS);
            SetAwakeLook();
            ShowToast(keepAwake ? "Keep awake on: the screen won't turn off or sleep" : "Keep awake off");
        }

        void SetAwakeLook()
        {
            awakeBtn.Background = keepAwake ? OnBg : OffBg;
            awakeBtn.Foreground = keepAwake ? Flyout.Res("Accent") : Flyout.Res("TextPrimary");
            awakeBtn.ToolTip = keepAwake ? "Keep awake is on (click to turn off)" : "Keep awake: stop the screen from turning off";
        }
    }

    /// Pill with two halves: icon (toggle) | arrow (more).
    public class QuickPill : Border
    {
        readonly Button main, more;
        static Brush OnBg => Flyout.Res("AccentSoft");
        static Brush OnBorder => Flyout.Res("AccentLine");
        static readonly Brush OffBg = Make(0x12, 0xFF, 0xFF, 0xFF), OffBorder = Make(0x22, 0xFF, 0xFF, 0xFF);
        static Brush Make(byte a, byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }
        readonly Border sep = new() { Width = 1, Margin = new Thickness(0, 9, 0, 9) };

        public QuickPill(string glyph, Action toggle, Action open, string moreTip)
        {
            Height = 40;
            CornerRadius = new CornerRadius(20);
            BorderThickness = new Thickness(1.2);
            main = Half(glyph, 15, toggle, new CornerRadius(20, 0, 0, 20), 44);
            more = Half("", 10, open, new CornerRadius(0, 20, 20, 0), 32);
            more.ToolTip = moreTip;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(main);
            sp.Children.Add(sep);
            sp.Children.Add(more);
            Child = sp;
            Set(false, "");
        }

        static Button Half(string glyph, double size, Action click, CornerRadius cr, double w)
        {
            var b = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = glyph, FontSize = size, Width = w, Height = 38 };
            b.Template = HalfTemplate(cr);
            b.Click += (s, e) => click();
            return b;
        }

        static ControlTemplate HalfTemplate(CornerRadius cr)
        {
            var bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.CornerRadiusProperty, cr);
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            var t = new ControlTemplate(typeof(Button)) { VisualTree = bd };
            t.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, Make(0x1A, 0xFF, 0xFF, 0xFF), "Bd") } });
            t.Triggers.Add(new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, Make(0x28, 0xFF, 0xFF, 0xFF), "Bd") } });
            return t;
        }

        public void Set(bool on, string tip)
        {
            Background = on ? OnBg : OffBg;
            BorderBrush = on ? OnBorder : OffBorder;
            sep.Background = on ? OnBorder : OffBorder;
            main.Foreground = on ? (Brush)Application.Current.Resources["Accent"] : (Brush)Application.Current.Resources["TextSecondary"];
            more.Foreground = (Brush)Application.Current.Resources["TextSecondary"];
            main.ToolTip = tip;
        }
    }
}
