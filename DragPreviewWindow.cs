using System;
using System.Drawing;
using System.Windows.Forms;

// A small, non-activating window keeps the live drag image smooth across monitors.
internal sealed class DragPreviewWindow : Form
{
    private readonly WindowCard card;
    private readonly Point grip;
    private readonly Rectangle image;
    private readonly Font labelFont = new Font("Segoe UI", 9);
    private IntPtr thumbnail;
    internal DragPreviewWindow(WindowCard card, Rectangle sourceImage, Point cursor)
    {
        this.card = card;
        Size fitted = PreviewLayout.Fit(sourceImage.Size, new Rectangle(0, 0, Math.Min(320, Math.Max(90, sourceImage.Width)), 230)).Size;
        int width = Math.Max(1, fitted.Width);
        int height = Math.Max(1, fitted.Height);
        image = new Rectangle(8, 8, width, height);
        grip = new Point(8 + (int)(width * Math.Max(0, Math.Min(1, (cursor.X - sourceImage.Left) / (double)Math.Max(1, sourceImage.Width)))),
            8 + (int)(height * Math.Max(0, Math.Min(1, (cursor.Y - sourceImage.Top) / (double)Math.Max(1, sourceImage.Height)))));
        Text = "F3 — przenoszenie okna";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(width + 16, height + 42);
        Location = new Point(cursor.X - grip.X, cursor.Y - grip.Y);
        BackColor = Color.FromArgb(30, 40, 56);
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        if (!card.Minimized && PreviewNative.DwmRegisterThumbnail(Handle, card.Window, out thumbnail) >= 0)
        {
            var properties = new PreviewNative.ThumbnailProperties
            {
                Flags = 1 | 4 | 8 | 16, Destination = new PreviewNative.Rect(image),
                Opacity = 240, Visible = true, SourceClientOnly = false
            };
            PreviewNative.DwmUpdateThumbnailProperties(thumbnail, ref properties);
        }
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { CreateParams value = base.CreateParams; value.ExStyle |= 0x80 | 0x08000000 | 0x20; return value; }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; }
        base.WndProc(ref message);
    }
    internal void Follow(Point cursor) { Location = new Point(cursor.X - grip.X, cursor.Y - grip.Y); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var border = new Pen(Color.FromArgb(156, 198, 249), 2)) e.Graphics.DrawRectangle(border, 1, 1, Width - 3, Height - 3);
        if (thumbnail == IntPtr.Zero && card.AppIcon != null)
            e.Graphics.DrawIcon(card.AppIcon, new Rectangle(image.Left + image.Width / 2 - 24, image.Top + image.Height / 2 - 24, 48, 48));
        TextRenderer.DrawText(e.Graphics, card.Title, labelFont,
            new Rectangle(10, image.Bottom + 7, image.Width - 4, 23), Color.White,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
    }
    protected override void Dispose(bool disposing)
    {
        if (thumbnail != IntPtr.Zero) { PreviewNative.DwmUnregisterThumbnail(thumbnail); thumbnail = IntPtr.Zero; }
        if (disposing) labelFont.Dispose();
        base.Dispose(disposing);
    }
}
