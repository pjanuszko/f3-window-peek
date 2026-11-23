using System;
using System.Drawing;

internal static class PreviewLayout
{
    // Fluent-like deceleration: immediate movement, then a long soft landing.
    // X is solved rather than treating the Bezier parameter as elapsed time.
    internal static double EaseOut(double time)
    {
        double x = Math.Max(0, Math.Min(1, time));
        if (x == 0 || x == 1) return x;
        double lower = 0, upper = 1, t = x;
        for (int i = 0; i < 15; i++)
        {
            double curveX = 3 * (1 - t) * (1 - t) * t * 0.16 + 3 * (1 - t) * t * t * 0.18 + t * t * t;
            if (curveX < x) lower = t; else upper = t;
            t = (lower + upper) / 2;
        }
        return 3 * (1 - t) * (1 - t) * t + 3 * (1 - t) * t * t + t * t * t;
    }
    internal static Rectangle Interpolate(Rectangle from, Rectangle to, double progress)
    {
        double p = Math.Max(0, Math.Min(1, progress));
        return new Rectangle((int)Math.Round(from.X + (to.X - from.X) * p),
            (int)Math.Round(from.Y + (to.Y - from.Y) * p),
            Math.Max(1, (int)Math.Round(from.Width + (to.Width - from.Width) * p)),
            Math.Max(1, (int)Math.Round(from.Height + (to.Height - from.Height) * p)));
    }
    internal static Rectangle[] Arrange(int count, Size screen)
    {
        if (count <= 0) return new Rectangle[0];
        int margin = Math.Max(12, Math.Min(44, screen.Width / 30));
        int top = Math.Min(104, screen.Height / 6);
        int bottom = Math.Min(76, screen.Height / 6);
        int gap = Math.Max(8, Math.Min(26, screen.Width / 60));
        int availableWidth = Math.Max(1, screen.Width - margin * 2);
        int availableHeight = Math.Max(1, screen.Height - top - bottom);
        int columns = 1;
        double best = -1;
        for (int c = 1; c <= count; c++)
        {
            int rows = (count + c - 1) / c;
            int width = (availableWidth - (c - 1) * gap) / c;
            int height = (availableHeight - (rows - 1) * gap) / rows;
            if (width <= 0 || height <= 0) continue;
            double imageWidth = Math.Min(width - 20, (height - 52) * 1.6);
            if (imageWidth > best) { best = imageWidth; columns = c; }
        }
        int rowCount = (count + columns - 1) / columns;
        int tileWidth = Math.Max(1, (availableWidth - (columns - 1) * gap) / columns);
        int tileHeight = Math.Max(1, (availableHeight - (rowCount - 1) * gap) / rowCount);
        // Avoid a single enormous thumbnail; keep the grid centered instead.
        tileWidth = Math.Min(tileWidth, 960);
        tileHeight = Math.Min(tileHeight, 650);
        var result = new Rectangle[count];
        int gridTop = top + Math.Max(0, (availableHeight - rowCount * tileHeight - (rowCount - 1) * gap) / 2);
        for (int i = 0; i < count; i++)
        {
            int row = i / columns;
            int inRow = Math.Min(columns, count - row * columns);
            int rowLeft = (screen.Width - inRow * tileWidth - (inRow - 1) * gap) / 2;
            result[i] = new Rectangle(rowLeft + (i % columns) * (tileWidth + gap),
                gridTop + row * (tileHeight + gap), tileWidth, tileHeight);
        }
        return result;
    }

    internal static Rectangle Fit(Size source, Rectangle available)
    {
        double scale = Math.Min((double)available.Width / Math.Max(1, source.Width),
            (double)available.Height / Math.Max(1, source.Height));
        int width = Math.Max(1, (int)(Math.Max(1, source.Width) * scale));
        int height = Math.Max(1, (int)(Math.Max(1, source.Height) * scale));
        return new Rectangle(available.X + (available.Width - width) / 2,
            available.Y + (available.Height - height) / 2, width, height);
    }
}
