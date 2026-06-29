using System.Windows;
using System.Windows.Media;

namespace CommitBallBallShell;

public static class BasicBallRenderer
{
    private const double BubbleMargin = 8.0;
    private const double BubbleGap = 10.0;
    private const double BubblePaddingX = 12.0;
    private const double BubblePaddingY = 11.0;
    private const double BubbleMinWidth = 120.0;
    private const double BubbleMaxWidth = 360.0;

    public static readonly BallBubbleStyle DefaultBubbleStyle = new(
        Color.FromRgb(30, 37, 50),
        Color.FromArgb(80, 255, 255, 255),
        Colors.White,
        9.0);

    public static void Render(DrawingContext dc, Rect bounds, BallRuntimeState state, BallAnimationFrame frame)
    {
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var radius = Math.Min(bounds.Width, bounds.Height) * 0.34;
        var color = state.Mode switch
        {
            BallMode.Recording => Color.FromRgb(239, 68, 68),
            _ => Color.FromRgb(59, 130, 246)
        };

        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)), null, new Point(center.X + 2, center.Y + 5), radius * 1.04, radius * 1.04);
        dc.DrawEllipse(new SolidColorBrush(color), new Pen(Brushes.White, 2), center, radius, radius);
        RenderCenterSymbol(dc, center, radius, state);
    }

    public static void RenderCenterSymbol(DrawingContext dc, Point center, double radius, BallRuntimeState state)
    {
        if (state.Mode == BallMode.Idle)
        {
            RenderPauseSymbol(dc, center, radius);
            return;
        }

        RenderTextSymbol(dc, center, "▶", radius * 0.72);
    }

    private static void RenderPauseSymbol(DrawingContext dc, Point center, double radius)
    {
        var barWidth = radius * 0.18;
        var barHeight = radius * 0.82;
        var gap = radius * 0.18;
        var brush = Brushes.White;
        dc.DrawRoundedRectangle(
            brush,
            null,
            new Rect(center.X - gap / 2 - barWidth, center.Y - barHeight / 2, barWidth, barHeight),
            barWidth * 0.28,
            barWidth * 0.28);
        dc.DrawRoundedRectangle(
            brush,
            null,
            new Rect(center.X + gap / 2, center.Y - barHeight / 2, barWidth, barHeight),
            barWidth * 0.28,
            barWidth * 0.28);
    }

    private static void RenderTextSymbol(DrawingContext dc, Point center, string text, double fontSize)
    {
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Symbol"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            fontSize,
            Brushes.White,
            1.25);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    public static Rect CalculateBubbleBounds(Rect viewport, Rect anchor, double progress)
    {
        return CalculateBubbleBounds(viewport, anchor, progress, "", DefaultBubbleStyle);
    }

    public static Rect CalculateBubbleBounds(Rect viewport, Rect anchor, double progress, string text, BallBubbleStyle style)
    {
        progress = Math.Clamp(progress, 0.0, 1.0);
        if (viewport.Width <= BubbleMargin * 2 + 24.0 || viewport.Height <= BubbleMargin * 2 + 24.0)
        {
            return Rect.Empty;
        }

        var availableWidth = Math.Max(24.0, viewport.Width - BubbleMargin * 2);
        var maxTextWidth = Math.Max(24.0, Math.Min(BubbleMaxWidth, availableWidth) - BubblePaddingX * 2);
        var measured = MeasureBubbleText(text, maxTextWidth, style);
        var width = Math.Clamp(measured.Width + BubblePaddingX * 2, BubbleMinWidth, Math.Min(BubbleMaxWidth, availableWidth));
        var finalTextWidth = Math.Max(24.0, width - BubblePaddingX * 2);
        measured = MeasureBubbleText(text, finalTextWidth, style);
        var height = measured.Height + BubblePaddingY * 2;
        var maxHeight = Math.Max(24.0, viewport.Height - BubbleMargin * 2);
        if (height > maxHeight)
        {
            height = maxHeight;
        }

        var rightX = anchor.Right + BubbleGap;
        var leftX = anchor.Left - BubbleGap - width;
        double x;
        if (rightX + width <= viewport.Right - BubbleMargin)
        {
            x = rightX;
        }
        else if (leftX >= viewport.Left + BubbleMargin)
        {
            x = leftX;
        }
        else
        {
            var rightSpace = viewport.Right - anchor.Right;
            var leftSpace = anchor.Left - viewport.Left;
            x = rightSpace >= leftSpace ? rightX : leftX;
        }

        var y = anchor.Top + 16 - (1.0 - progress) * 6;
        x = ClampToRange(x, viewport.Left + BubbleMargin, viewport.Right - BubbleMargin - width);
        y = ClampToRange(y, viewport.Top + BubbleMargin, viewport.Bottom - BubbleMargin - height);
        return new Rect(x, y, width, height);
    }

    public static void RenderBubble(DrawingContext dc, Rect viewport, Rect anchor, string text, double progress, BallBubbleStyle style)
    {
        progress = Math.Clamp(progress, 0.0, 1.0);
        var box = CalculateBubbleBounds(viewport, anchor, progress, text, style);
        if (box.IsEmpty || box.Width <= 0 || box.Height <= 0)
        {
            return;
        }
        var brush = new SolidColorBrush(WithProgressAlpha(style.Background, 235, progress));
        var pen = new Pen(new SolidColorBrush(WithProgressAlpha(style.Border, style.Border.A, progress)), 1.0);
        var cornerRadius = Math.Clamp(style.CornerRadius, 4.0, 18.0);
        dc.DrawRoundedRectangle(brush, pen, box, cornerRadius, cornerRadius);

        var targetOnRight = box.Left >= anchor.Left + anchor.Width / 2;
        var target = new Point(targetOnRight ? anchor.Right : anchor.Left, anchor.Top + anchor.Height * 0.50);
        var tail = new StreamGeometry();
        using (var ctx = tail.Open())
        {
            if (targetOnRight)
            {
                ctx.BeginFigure(new Point(box.Left + 2, box.Bottom - 16), true, true);
                ctx.LineTo(target, true, false);
                ctx.LineTo(new Point(box.Left + 22, box.Bottom - 7), true, false);
            }
            else
            {
                ctx.BeginFigure(new Point(box.Right - 2, box.Bottom - 16), true, true);
                ctx.LineTo(target, true, false);
                ctx.LineTo(new Point(box.Right - 22, box.Bottom - 7), true, false);
            }
        }
        dc.DrawGeometry(brush, null, tail);

        var textBrush = new SolidColorBrush(WithProgressAlpha(style.Text, style.Text.A, progress));
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
            13,
            textBrush,
            1.25)
        {
            MaxTextWidth = Math.Max(24.0, box.Width - BubblePaddingX * 2),
            MaxTextHeight = Math.Max(12.0, box.Height - BubblePaddingY * 2),
            Trimming = TextTrimming.None
        };
        dc.DrawText(ft, new Point(box.Left + BubblePaddingX, box.Top + BubblePaddingY));
    }

    private static FormattedText MeasureBubbleText(string text, double maxTextWidth, BallBubbleStyle style)
    {
        var ft = new FormattedText(
            string.IsNullOrEmpty(text) ? " " : text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
            13,
            new SolidColorBrush(style.Text),
            1.25)
        {
            MaxTextWidth = Math.Max(24.0, maxTextWidth),
            Trimming = TextTrimming.None
        };
        return ft;
    }

    private static Color WithProgressAlpha(Color color, byte fallbackAlpha, double progress)
    {
        var alpha = color.A == 255 ? fallbackAlpha : color.A;
        return Color.FromArgb(
            (byte)Math.Clamp(alpha * progress, 0.0, 255.0),
            color.R,
            color.G,
            color.B);
    }

    private static double ClampToRange(double value, double min, double max)
    {
        if (max < min)
        {
            return min;
        }
        return Math.Clamp(value, min, max);
    }
}
