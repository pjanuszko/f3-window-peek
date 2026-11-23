using System;
using System.Drawing;

internal static class PreviewLayoutTests
{
    private static void Assert(bool valid, string message) { if (!valid) throw new Exception(message); }
    private static void Main()
    {
        int scenarios = 0;
        foreach (Size screen in new[] { new Size(800, 600), new Size(1920, 1080), new Size(2560, 1440), new Size(1080, 1920), new Size(3840, 2160) })
        foreach (int count in new[] { 0, 1, 2, 3, 5, 8, 12, 20, 40, 80 })
        {
            Rectangle[] cards = PreviewLayout.Arrange(count, screen);
            Assert(cards.Length == count, "Missing card");
            for (int i = 0; i < cards.Length; i++)
            {
                Assert(new Rectangle(Point.Empty, screen).Contains(cards[i]), "Card outside screen");
                Assert(cards[i].Width > 0 && cards[i].Height > 0, "Empty card");
                for (int j = i + 1; j < cards.Length; j++) Assert(!cards[i].IntersectsWith(cards[j]), "Cards overlap");
            }
            scenarios++;
        }
        var available = new Rectangle(50, 100, 700, 400);
        foreach (Size source in new[] { new Size(1920, 1080), new Size(1080, 1920), new Size(1, 1), new Size(0, 0) })
        {
            Rectangle fit = PreviewLayout.Fit(source, available);
            Assert(available.Contains(fit), "Thumbnail outside its card");
        }
        var from = new Rectangle(-300, 80, 1920, 1080);
        var to = new Rectangle(400, 160, 600, 338);
        Assert(PreviewLayout.Interpolate(from, to, 0) == from, "Animation must start at actual window");
        Assert(PreviewLayout.Interpolate(from, to, 1) == to, "Animation must finish at thumbnail");
        Assert(PreviewLayout.Interpolate(from, to, -1) == from && PreviewLayout.Interpolate(from, to, 2) == to, "Animation must clamp progress");
        for (int i = 0; i <= 100; i++)
        {
            Rectangle rect = PreviewLayout.Interpolate(from, to, i / 100.0);
            Assert(rect.Width > 0 && rect.Height > 0, "Animation collapsed a window");
        }
        Assert(PreviewLayout.EaseOut(0) == 0 && PreviewLayout.EaseOut(1) == 1, "Easing endpoints");
        double previous = 0;
        for (int i = 1; i <= 100; i++)
        {
            double next = PreviewLayout.EaseOut(i / 100.0);
            Assert(next >= previous && next <= 1, "Easing must be monotonic without overshoot");
            previous = next;
        }
        Assert(PreviewLayout.EaseOut(0.25) > 0.65, "Transition should move immediately");
        Assert(PreviewLayout.EaseOut(0.5) - PreviewLayout.EaseOut(0.4)
            > PreviewLayout.EaseOut(0.9) - PreviewLayout.EaseOut(0.8), "Transition must slow towards its destination");
        Console.WriteLine("PASS: " + scenarios + " grid scenarios, source aspect ratios and animation endpoints.");
    }
}
