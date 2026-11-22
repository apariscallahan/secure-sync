#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SecureSync.App.Dev;

/// <summary>Development aid (Debug builds only): renders the vector logo into a multi-size .ico. SecureSync.exe --make-icon path.ico</summary>
internal static class IconMaker
{
    public static void Write(string path)
    {
        var logo = (DrawingImage)Application.Current.FindResource("LogoImage");
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var images = sizes.Select(size => (size, png: RenderPng(logo, size))).ToList();

        using var output = new BinaryWriter(File.Create(path));
        output.Write((ushort)0);            // reserved
        output.Write((ushort)1);            // type: icon
        output.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            output.Write((byte)(size >= 256 ? 0 : size));
            output.Write((byte)(size >= 256 ? 0 : size));
            output.Write((byte)0);          // palette
            output.Write((byte)0);          // reserved
            output.Write((ushort)1);        // planes
            output.Write((ushort)32);       // bits per pixel
            output.Write(png.Length);
            output.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) output.Write(png);
    }

    private static byte[] RenderPng(DrawingImage logo, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // The logo is drawn on a 24-unit grid; scale it to fill the icon with a hairline margin.
            double scale = size / 24.0;
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawDrawing(logo.Drawing);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
#endif
