using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassBar
{
    /// Auto-hide: the dock doesn't take any screen space. It slides in when the mouse touches the
    /// screen edge (or a pop-up opens) and slides away again when the mouse leaves it.
    /// Also keeps the old Windows taskbar hidden while "Replace Windows taskbar" is on.
    public partial class BarWindow
    {
        public bool AutoHide => Settings.AutoHide;
        bool reserved;                 // registered as an appbar that reserves screen space
        Native.RECT shownRect;         // where the window is when fully shown
        Native.RECT edgeMon;           // monitor the dock lives on
        double slide = 1, slideTarget = 1;
        bool sliding;
        TimeSpan slideLast;
        DispatcherTimer hideTimer, taskbarTimer, edgeTimer;
        int awayTicks, menusOpen;

        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);

        void InitAutoHide()
        {
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, e) => menusOpen++));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((s, e) => menusOpen = Math.Max(0, menusOpen - 1)));

            hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            hideTimer.Tick += (s, e) => CheckHide();
            if (AutoHide) hideTimer.Start();

            // backup for the mouse hook: look at the cursor a few times a second while hidden
            edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            edgeTimer.Tick += (s, e) =>
            {
                if (!AutoHide || slideTarget >= 1) return;
                if (GetCursorPos(out var p)) OnMouseMoveHook(p.X, p.Y);
            };
            edgeTimer.Start();

            // explorer likes to show its taskbar again (Start menu, explorer restart): keep it hidden
            taskbarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            taskbarTimer.Tick += (s, e) => { if (Settings.ReplaceTaskbar) Native.SetWindowsTaskbarHidden(true); };
            taskbarTimer.Start();
            if (Settings.ReplaceTaskbar) Native.SetWindowsTaskbarHidden(true);
        }

        void SetAutoHide(bool on)
        {
            Settings.AutoHide = on;
            Settings.Save();
            slide = slideTarget = 1;
            Dock();
            if (on) hideTimer.Start(); else hideTimer.Stop();
            ShowToast(on ? "Auto-hide on: move the mouse to the screen edge to show the dock" : "Auto-hide off: the dock stays and reserves its space");
        }

        // ---------------------------------------------------------------
        // position: shownRect pushed off-screen by (1 - slide)
        // ---------------------------------------------------------------
        void ApplySlide(bool sized = false)
        {
            if (!appBarOn) return;
            var r = shownRect;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            int dx = 0, dy = 0;
            if (AutoHide && slide < 1)
            {
                // distance from the window to the screen edge, plus the window itself
                int dist = Edge switch
                {
                    0 => r.Right - edgeMon.Left, 1 => r.Bottom - edgeMon.Top,
                    2 => edgeMon.Right - r.Left, _ => edgeMon.Bottom - r.Top
                } + 2;
                int off = (int)Math.Round(dist * (1 - slide));
                switch (Edge) { case 0: dx = -off; break; case 1: dy = -off; break; case 2: dx = off; break; default: dy = off; break; }
            }
            uint flags = Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | (sized ? 0 : Native.SWP_NOSIZE);
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, r.Left + dx, r.Top + dy, w, h, flags);
        }

        void SlideTo(double target)
        {
            if (Math.Abs(slideTarget - target) < 0.001 && (sliding || Math.Abs(slide - target) < 0.001)) return;
            slideTarget = target;
            if (target > 0) awayTicks = 0;
            if (!sliding)
            {
                sliding = true;
                slideLast = TimeSpan.Zero;
                CompositionTarget.Rendering += SlideFrame;
            }
        }

        void SlideFrame(object s, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            double dt = slideLast == TimeSpan.Zero ? 1.0 / 120 : (now - slideLast).TotalSeconds;
            if (dt <= 0) return;
            slideLast = now;
            double speed = slideTarget > slide ? 7.0 : 5.0;   // ~150 ms in, ~200 ms out
            slide = slideTarget > slide ? Math.Min(slideTarget, slide + dt * speed) : Math.Max(slideTarget, slide - dt * speed);
            ApplySlide();
            if (Math.Abs(slide - slideTarget) < 0.0001)
            {
                sliding = false;
                CompositionTarget.Rendering -= SlideFrame;
                if (slide <= 0) { HideHover(); HideThumb(); }
            }
        }

        /// Show the dock right away (e.g. a pop-up is about to open next to it).
        public void RevealNow()
        {
            if (!AutoHide || !appBarOn) return;
            awayTicks = 0;
            if (sliding) { sliding = false; CompositionTarget.Rendering -= SlideFrame; }
            slide = slideTarget = 1;
            ApplySlide();
        }

        public void Reveal()
        {
            if (AutoHide && appBarOn) SlideTo(1);
        }

        // ---------------------------------------------------------------
        // when to show / hide
        // ---------------------------------------------------------------
        /// Called from the low-level mouse hook for every mouse move.
        void OnMouseMoveHook(int x, int y)
        {
            if (!AutoHide || !appBarOn || slideTarget >= 1) return;
            var m = edgeMon;
            if (x < m.Left || x >= m.Right || y < m.Top || y >= m.Bottom) return;
            bool atEdge = Edge switch { 0 => x <= m.Left + 1, 1 => y <= m.Top + 1, 2 => x >= m.Right - 2, _ => y >= m.Bottom - 2 };
            if (!atEdge || FullscreenAppInFront()) return;
            Dispatcher.BeginInvoke(Reveal);
        }

        /// A game or video in real fullscreen: don't pop up over it.
        bool FullscreenAppInFront()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Hwnd) return false;
            var sb = new System.Text.StringBuilder(64);
            GetClassName(fg, sb, 64);
            var cls = sb.ToString();
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
            const int WS_CAPTION = 0x00C00000;
            if ((GetWindowLong(fg, -16) & WS_CAPTION) == WS_CAPTION) return false;   // normal windows
            if (IsZoomed(fg)) return false;   // maximized (Chrome, Opera… draw their own title bar)
            if (!Native.GetWindowRect(fg, out var r)) return false;
            var m = edgeMon;
            return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        }

        bool KeepShown()
        {
            if (drag?.Active == true || lengthDrag || resizing || moving || moveArmed) return true;
            if (flyouts.Any(f => f.IsVisible) || menusOpen > 0) return true;
            if (thumb != null && thumb.IsVisible) return true;
            if (Toast.Visibility == Visibility.Visible || TaskbarAsk.Visibility == Visibility.Visible) return true;
            if (GetForegroundWindow() == Hwnd && (SearchBox.IsKeyboardFocusWithin || TermBox.IsKeyboardFocusWithin)) return true;

            // the mouse is over the dock, or between it and the screen edge
            GetCursorPos(out var p);
            var r = shownRect;
            int pad = 8;
            int l = r.Left - pad, t = r.Top - pad, rt = r.Right + pad, b = r.Bottom + pad;
            switch (Edge) { case 0: l = edgeMon.Left; break; case 1: t = edgeMon.Top; break; case 2: rt = edgeMon.Right; break; default: b = edgeMon.Bottom; break; }
            return p.X >= l && p.X < rt && p.Y >= t && p.Y < b;
        }

        // ---------------------------------------------------------------
        // Windows key: 1st press shows the dock, 2nd opens Start, 3rd hides it all
        // ---------------------------------------------------------------
        bool keyRevealed;
        IntPtr fgAtKey;

        void WinKeyPress()
        {
            if (!AutoHide || !appBarOn) { ToggleStart(); return; }
            bool startOpen = startFly?.IsVisible == true;
            bool dockUp = slideTarget >= 1;
            if (startOpen)
            {
                startFly.Hide();
                CloseFlyoutsExcept(null);
                keyRevealed = false;
                SlideTo(0);
            }
            else if (!dockUp)
            {
                keyRevealed = true;
                fgAtKey = GetForegroundWindow();
                SlideTo(1);
            }
            else ToggleStart();
        }

        void CheckHide()
        {
            if (!AutoHide || !appBarOn || slideTarget <= 0) return;
            if (keyRevealed)
            {
                // shown with the Windows key: stays until the next press or until you switch to another program
                var fg = GetForegroundWindow();
                if (fg == fgAtKey || IsOwnWindow(fg)) { awayTicks = 0; return; }
                keyRevealed = false;
            }
            if (KeepShown()) { awayTicks = 0; return; }
            // a little grace time so it doesn't flicker when the mouse brushes past
            if (++awayTicks >= 3) SlideTo(0);
        }
    }
}
