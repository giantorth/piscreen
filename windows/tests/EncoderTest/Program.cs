using System;
using System.Diagnostics;
using System.IO;
using PiScreen.Video;

// Usage: EncoderTest <openh264 library> <out.h264> [width height frames]
// Encodes a synthetic dashboard-like animation (colour bars, moving box, gradient) and reports
// frame types/sizes. Decode the output with ffmpeg to check it.
class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: EncoderTest <libopenh264> <out.h264> [w h frames]"); return 2; }
        int w = args.Length > 2 ? int.Parse(args[2]) : 800;
        int h = args.Length > 3 ? int.Parse(args[3]) : 480;
        int frames = args.Length > 4 ? int.Parse(args[4]) : 120;

        using var enc = new OpenH264Encoder(args[0], w, h, 3_000_000, 60);
        Console.WriteLine($"OpenH264 {enc.Version}, {w}x{h}");
        var bgra = new byte[w * h * 4];
        using var outFile = File.Create(args[1]);
        var sw = new Stopwatch();
        long convTicks = 0, encTicks = 0, bytes = 0;
        int keyFrames = 0, skipped = 0;
        var yuv = new I420Frame(w, h);

        for (int f = 0; f < frames; f++)
        {
            Draw(bgra, w, h, f);
            if (f == frames / 2) enc.ForceKeyFrame();

            sw.Restart();
            ColorConvert.BgraToI420(bgra, w, h, w * 4, yuv);
            convTicks += sw.ElapsedTicks;
            sw.Restart();
            var au = enc.EncodeI420(yuv, f * 16, out bool key);
            encTicks += sw.ElapsedTicks;

            if (au == null) { skipped++; continue; }
            if (key) keyFrames++;
            bytes += au.Value.Count;
            outFile.Write(au.Value.Array, au.Value.Offset, au.Value.Count);
            if (f < 3 || f == frames / 2 || key)
                Console.WriteLine($"frame {f}: {au.Value.Count} bytes{(key ? " KEY" : "")} first NAL type {au.Value.Array[4] & 0x1f}");
        }
        double ms(long t) => t * 1000.0 / Stopwatch.Frequency / frames;
        Console.WriteLine($"frames={frames} key={keyFrames} skipped={skipped} avg={bytes / Math.Max(1, frames - skipped)} B " +
                          $"convert={ms(convTicks):F2} ms encode={ms(encTicks):F2} ms per frame");
        return keyFrames >= 2 && skipped == 0 ? 0 : 1;
    }

    static void Draw(byte[] p, int w, int h, int f)
    {
        // Background gradient, 8 colour bars in the top third, a white box moving left to right.
        byte[][] bars = { new byte[]{255,255,255}, new byte[]{0,255,255}, new byte[]{255,255,0}, new byte[]{0,255,0},
                          new byte[]{255,0,255}, new byte[]{0,0,255}, new byte[]{255,0,0}, new byte[]{0,0,0} }; // BGR
        int boxX = (f * 8) % (w - 80), boxY = h / 2;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            byte b, g, r;
            if (y < h / 3) { var c = bars[x * 8 / w]; b = c[0]; g = c[1]; r = c[2]; }
            else { b = (byte)(x * 255 / w); g = (byte)(y * 255 / h); r = 40; }
            if (x >= boxX && x < boxX + 80 && y >= boxY && y < boxY + 80) { b = g = r = 255; }
            p[i] = b; p[i + 1] = g; p[i + 2] = r; p[i + 3] = 255;
        }
    }
}
