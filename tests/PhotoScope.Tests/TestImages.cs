using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoScope.Tests;

/// <summary>Creates small real image files (with EXIF where needed) for tests.</summary>
public static class TestImages
{
    public static BitmapSource Make(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        var pixels = new byte[w * h * 3];
        var (r, g, b) = ((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
        for (var i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = (byte)(b + i / 3 % w);
            pixels[i + 1] = g;
            pixels[i + 2] = (byte)(r + i / 3 / w);
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, pixels, w * 3);
        bmp.Freeze();
        return bmp;
    }

    public static string Jpeg(string path, int w = 64, int h = 48, int seed = 1, string? dateTaken = null,
        string? make = null, string? model = null, int orientation = 1)
    {
        var md = new BitmapMetadata("jpg");
        if (dateTaken != null) md.SetQuery("/app1/ifd/exif/{ushort=36867}", dateTaken);
        if (make != null) md.SetQuery("/app1/ifd/{ushort=271}", make);
        if (model != null) md.SetQuery("/app1/ifd/{ushort=272}", model);
        if (orientation > 1) md.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
        var enc = new JpegBitmapEncoder { QualityLevel = 90 };
        enc.Frames.Add(BitmapFrame.Create(Make(w, h, seed), null, md, null));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    public static string Png(string path, int w = 40, int h = 30, int seed = 2)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(Make(w, h, seed)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    public static string Encoded(BitmapEncoder enc, string path, int w = 40, int h = 30, int seed = 3)
    {
        enc.Frames.Add(BitmapFrame.Create(Make(w, h, seed)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    public static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s));
    }
}

/// <summary>A throwaway folder that is deleted afterwards (test data only).</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "photoscope-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}
