using System;
using System.Threading.Tasks;

namespace PiScreen.Video
{
    public sealed class I420Frame
    {
        public readonly int Width, Height, StrideY, StrideC;
        public readonly byte[] Y, U, V;

        public I420Frame(int width, int height)
        {
            Width = width;
            Height = height;
            StrideY = width;
            StrideC = width / 2;
            Y = new byte[width * height];
            U = new byte[StrideC * (height / 2)];
            V = new byte[StrideC * (height / 2)];
        }
    }

    public static unsafe class ColorConvert
    {
        /// <summary>
        /// BGRA → I420, BT.601 limited range (what H.264 decoders assume when the stream has no VUI
        /// colour info, which OpenH264 doesn't write). Chroma is the 2x2 average. Alpha is ignored:
        /// SimHub's buffer is opaque premultiplied BGRA.
        /// </summary>
        public static void BgraToI420(byte[] bgra, int width, int height, int stride, I420Frame dst)
        {
            int chromaRows = height / 2;
            // Split by chroma row pairs; ~4 bands keeps it well under 1 ms at 800x480 without much overhead.
            int bands = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
            int perBand = (chromaRows + bands - 1) / bands;
            Parallel.For(0, bands, b =>
            {
                int start = b * perBand, end = Math.Min(chromaRows, start + perBand);
                fixed (byte* src = bgra, py = dst.Y, pu = dst.U, pv = dst.V)
                {
                    for (int cy = start; cy < end; cy++)
                        ConvertRowPair(src, stride, width, cy, py, dst.StrideY, pu, pv, dst.StrideC);
                }
            });
        }

        private static void ConvertRowPair(byte* src, int stride, int width, int cy,
                                           byte* py, int strideY, byte* pu, byte* pv, int strideC)
        {
            byte* r0 = src + (2 * cy) * stride;
            byte* r1 = r0 + stride;
            byte* y0 = py + (2 * cy) * strideY;
            byte* y1 = y0 + strideY;
            byte* u = pu + cy * strideC;
            byte* v = pv + cy * strideC;

            for (int x = 0; x < width; x += 2)
            {
                int i = x * 4;
                int b00 = r0[i], g00 = r0[i + 1], r00 = r0[i + 2];
                int b01 = r0[i + 4], g01 = r0[i + 5], r01 = r0[i + 6];
                int b10 = r1[i], g10 = r1[i + 1], r10 = r1[i + 2];
                int b11 = r1[i + 4], g11 = r1[i + 5], r11 = r1[i + 6];

                y0[x] = (byte)(((66 * r00 + 129 * g00 + 25 * b00 + 128) >> 8) + 16);
                y0[x + 1] = (byte)(((66 * r01 + 129 * g01 + 25 * b01 + 128) >> 8) + 16);
                y1[x] = (byte)(((66 * r10 + 129 * g10 + 25 * b10 + 128) >> 8) + 16);
                y1[x + 1] = (byte)(((66 * r11 + 129 * g11 + 25 * b11 + 128) >> 8) + 16);

                int r = (r00 + r01 + r10 + r11 + 2) >> 2;
                int g = (g00 + g01 + g10 + g11 + 2) >> 2;
                int b = (b00 + b01 + b10 + b11 + 2) >> 2;
                u[x >> 1] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                v[x >> 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
            }
        }
    }
}
