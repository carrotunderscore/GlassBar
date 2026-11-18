using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GlassBar
{
    /// Round gauge: a coloured arc for 0–100 with the number and a small label in the middle.
    public class RingGauge : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(Brush), typeof(RingGauge),
            new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(RingGauge),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

        public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public Brush Color { get => (Brush)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
        public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
        public double Thickness { get; set; } = 4.5;

        static readonly Brush Track = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
        static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        static readonly Typeface Small = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static Brush Freeze(Brush b) { b.Freeze(); return b; }

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0) return;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            double r = size / 2 - Thickness / 2 - 0.5;
            dc.DrawEllipse(null, new Pen(Track, Thickness), c, r, r);

            double v = Math.Clamp(Value, 0, 100) / 100;
            if (v > 0.001)
            {
                var pen = new Pen(Color, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (v >= 0.999) dc.DrawEllipse(null, pen, c, r, r);
                else
                {
                    double a0 = -Math.PI / 2, a1 = a0 + v * 2 * Math.PI;
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), false, false);
                        ctx.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(null, pen, g);
                }
            }

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var num = new FormattedText($"{Value:0}%", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size * 0.27, Brushes.White, dpi);
            num.SetFontSize(size * 0.18, num.Text.Length - 1, 1);   // smaller % sign
            var lab = new FormattedText(Label ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Small, size * 0.16,
                (Brush)Application.Current.Resources["TextSecondary"], dpi);
            double total = num.Height + lab.Height * 0.8;
            double top = c.Y - total / 2;
            dc.DrawText(lab, new Point(c.X - lab.Width / 2, top - lab.Height * 0.05));
            dc.DrawText(num, new Point(c.X - num.Width / 2, top + lab.Height * 0.72));
        }
    }

    /// One dot per day of the month: past days bright, today in the accent colour, the rest dim.
    public class DotCalendar : FrameworkElement
    {
        public int Columns { get; set; } = 11;
        public double Dot { get; set; } = 7;
        public double Gap { get; set; } = 3.4;
        DateTime day = DateTime.Today;

        static readonly Brush Past = Make(0xE6, 0xEE, 0xF3, 0xFB), Future = Make(0x30, 0xFF, 0xFF, 0xFF);
        static Brush Make(byte a, byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }

        public void SetDay(DateTime d)
        {
            if (d.Date == day && IsMeasureValid) return;
            day = d.Date;
            InvalidateMeasure();
            InvalidateVisual();
        }

        int Days => DateTime.DaysInMonth(day.Year, day.Month);
        int Rows => (Days + Columns - 1) / Columns;

        protected override Size MeasureOverride(Size available) =>
            new(Columns * Dot + (Columns - 1) * Gap, Rows * Dot + (Rows - 1) * Gap);

        protected override void OnRender(DrawingContext dc)
        {
            var accent = (Brush)Application.Current.Resources["Accent"];
            double gx = Columns > 1 ? Math.Clamp((ActualWidth - Columns * Dot) / (Columns - 1), Gap, Dot * 2.4) : Gap;
            for (int i = 0; i < Days; i++)
            {
                int col = i % Columns, row = i / Columns;
                var center = new Point(col * (Dot + gx) + Dot / 2, row * (Dot + Gap) + Dot / 2);
                var b = i + 1 < day.Day ? Past : i + 1 == day.Day ? accent : Future;
                dc.DrawEllipse(b, null, center, Dot / 2, Dot / 2);
            }
        }
    }
}
