using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PiScreen.Video
{
    /// <summary>
    /// Direct binding to Cisco's OpenH264 encoder (C ABI, x64 only). The DLL only imports KERNEL32,
    /// so it runs on stock Windows and under Wine with no VC++ runtime. Struct layouts follow
    /// codec_app_def.h; SLayerBSInfo grew a trailing float rPsnr[3] in 2.6.0, which changes the
    /// SFrameBSInfo array stride, so the layout is chosen from the DLL's reported version.
    /// </summary>
    public sealed unsafe class OpenH264Encoder : IDisposable
    {
        // ---- exports ----
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateFn(out IntPtr encoder);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyFn(IntPtr encoder);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VersionExFn(out OpenH264Version v);

        // ---- ISVCEncoder vtable (slot order from codec_api.h; virtual dtor is last) ----
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeFn(IntPtr self, ref SEncParamBase p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UninitializeFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EncodeFrameFn(IntPtr self, SSourcePicture* pic, byte* bsInfo);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ForceIntraFrameFn(IntPtr self, byte idr, int layerId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetOptionFn(IntPtr self, int option, void* value);

        private const int SlotInitialize = 0, SlotUninitialize = 3, SlotEncodeFrame = 4, SlotForceIntra = 6, SlotSetOption = 7;

        // codec_app_def.h / codec_def.h constants
        private const int VideoFormatI420 = 23;
        private const int ScreenContentRealTime = 1;  // EUsageType
        private const int RcBitrateMode = 1;          // RC_MODES
        private const int OptDataFormat = 0, OptIdrInterval = 1, OptFrameRate = 4, OptBitrate = 5,
                          OptMaxBitrate = 6, OptRcFrameSkip = 9, OptTraceLevel = 25;
        private const int FrameTypeIDR = 1, FrameTypeI = 2, FrameTypeSkip = 4;
        private const int MaxLayers = 128;            // MAX_LAYER_NUM_OF_FRAME

        [StructLayout(LayoutKind.Sequential)]
        private struct OpenH264Version { public uint Major, Minor, Revision, Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SBitrateInfo { public int iLayer; public int iBitrate; } // iLayer: LAYER_NUM

        private const int SpatialLayerAll = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct SEncParamBase
        {
            public int iUsageType;
            public int iPicWidth;
            public int iPicHeight;
            public int iTargetBitrate;
            public int iRCMode;
            public float fMaxFrameRate;
        }

        // x64 layout (identical on Windows LLP64 and Linux LP64: no C 'long' involved).
        [StructLayout(LayoutKind.Explicit, Size = 80)]
        private struct SSourcePicture
        {
            [FieldOffset(0)] public int iColorFormat;
            [FieldOffset(4)] public int iStride0;
            [FieldOffset(8)] public int iStride1;
            [FieldOffset(12)] public int iStride2;
            [FieldOffset(16)] public int iStride3;
            [FieldOffset(24)] public IntPtr pData0;
            [FieldOffset(32)] public IntPtr pData1;
            [FieldOffset(40)] public IntPtr pData2;
            [FieldOffset(48)] public IntPtr pData3;
            [FieldOffset(56)] public int iPicWidth;
            [FieldOffset(60)] public int iPicHeight;
            [FieldOffset(64)] public long uiTimeStamp;
            // 72..74: bPsnrY/U/V (2.6.0+), left false; older versions never read past 72.
        }

        // SLayerBSInfo field offsets (x64)
        private const int LayerFrameType = 4, LayerNalCount = 16, LayerNalLengths = 24, LayerBsBuf = 32;

        private readonly IntPtr lib;
        private readonly IntPtr encoder;
        private readonly DestroyFn destroy;
        private readonly UninitializeFn uninitialize;
        private readonly EncodeFrameFn encodeFrame;
        private readonly ForceIntraFrameFn forceIntra;
        private readonly SetOptionFn setOption;
        private readonly int layerStride;
        private readonly int frameTypeOffset;
        private readonly byte[] bsInfo;
        private readonly I420Frame yuv;
        private byte[] output = new byte[256 * 1024];
        private bool disposed;

        public int Width { get; }
        public int Height { get; }
        public string Version { get; }

        public OpenH264Encoder(string libraryPath, int width, int height, int bitrateBps, float maxFps)
        {
            if (IntPtr.Size != 8) throw new PlatformNotSupportedException("OpenH264 binding is x64-only");
            if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("I420 needs even dimensions");
            Width = width;
            Height = height;

            lib = NativeLib.Load(libraryPath);
            var create = Fn<CreateFn>("WelsCreateSVCEncoder");
            destroy = Fn<DestroyFn>("WelsDestroySVCEncoder");
            Fn<VersionExFn>("WelsGetCodecVersionEx")(out var v);
            Version = $"{v.Major}.{v.Minor}.{v.Revision}";

            bool hasPsnr = v.Major > 2 || (v.Major == 2 && v.Minor >= 6);
            layerStride = hasPsnr ? 56 : 40;
            frameTypeOffset = 8 + MaxLayers * layerStride;
            bsInfo = new byte[frameTypeOffset + 24];

            if (create(out encoder) != 0 || encoder == IntPtr.Zero)
                throw new InvalidOperationException("WelsCreateSVCEncoder failed");

            IntPtr vtbl = Marshal.ReadIntPtr(encoder);
            var initialize = Slot<InitializeFn>(vtbl, SlotInitialize);
            uninitialize = Slot<UninitializeFn>(vtbl, SlotUninitialize);
            encodeFrame = Slot<EncodeFrameFn>(vtbl, SlotEncodeFrame);
            forceIntra = Slot<ForceIntraFrameFn>(vtbl, SlotForceIntra);
            setOption = Slot<SetOptionFn>(vtbl, SlotSetOption);

            int trace = 1; // WELS_LOG_ERROR
            setOption(encoder, OptTraceLevel, &trace);

            var p = new SEncParamBase
            {
                iUsageType = ScreenContentRealTime,
                iPicWidth = width,
                iPicHeight = height,
                iTargetBitrate = bitrateBps,
                iRCMode = RcBitrateMode,
                fMaxFrameRate = maxFps,
            };
            int rc = initialize(encoder, ref p);
            if (rc != 0) throw new InvalidOperationException("OpenH264 Initialize failed: " + rc);

            int fmt = VideoFormatI420;
            setOption(encoder, OptDataFormat, &fmt);
            // Frames arrive irregularly (SimHub only renders changes): never let RC drop one, and
            // leave IDR timing to the caller (time-based + on PLI) instead of a frame count.
            byte noSkip = 0;
            setOption(encoder, OptRcFrameSkip, &noSkip);
            int idr = 0;
            setOption(encoder, OptIdrInterval, &idr);
            var maxBitrate = new SBitrateInfo { iLayer = SpatialLayerAll, iBitrate = bitrateBps * 2 };
            setOption(encoder, OptMaxBitrate, &maxBitrate);

            yuv = new I420Frame(width, height);
        }

        public void SetBitrate(int bps)
        {
            var b = new SBitrateInfo { iLayer = SpatialLayerAll, iBitrate = bps };
            setOption(encoder, OptBitrate, &b);
            var m = new SBitrateInfo { iLayer = SpatialLayerAll, iBitrate = bps * 2 };
            setOption(encoder, OptMaxBitrate, &m);
        }

        public void SetFrameRate(float fps)
        {
            float f = fps;
            setOption(encoder, OptFrameRate, &f);
        }

        public void ForceKeyFrame() => forceIntra(encoder, 1, -1);

        /// <summary>
        /// Encodes one tightly packed BGRA frame. Returns the Annex-B access unit (valid until the next
        /// call) or null when the encoder skipped the frame.
        /// </summary>
        public ArraySegment<byte>? EncodeBgra(byte[] bgra, long timestampMs, out bool keyFrame)
        {
            ColorConvert.BgraToI420(bgra, Width, Height, Width * 4, yuv);
            return EncodeI420(yuv, timestampMs, out keyFrame);
        }

        public ArraySegment<byte>? EncodeI420(I420Frame f, long timestampMs, out bool keyFrame)
        {
            keyFrame = false;
            fixed (byte* y = f.Y, u = f.U, vv = f.V, bs = bsInfo)
            {
                var pic = new SSourcePicture
                {
                    iColorFormat = VideoFormatI420,
                    iStride0 = f.StrideY,
                    iStride1 = f.StrideC,
                    iStride2 = f.StrideC,
                    pData0 = (IntPtr)y,
                    pData1 = (IntPtr)u,
                    pData2 = (IntPtr)vv,
                    iPicWidth = Width,
                    iPicHeight = Height,
                    uiTimeStamp = timestampMs,
                };
                Array.Clear(bsInfo, 0, bsInfo.Length);
                int rc = encodeFrame(encoder, &pic, bs);
                if (rc != 0) throw new InvalidOperationException("OpenH264 EncodeFrame failed: " + rc);

                int frameType = *(int*)(bs + frameTypeOffset);
                if (frameType == FrameTypeSkip) return null;
                keyFrame = frameType == FrameTypeIDR || frameType == FrameTypeI;

                int layers = *(int*)bs;
                int total = 0;
                for (int l = 0; l < layers; l++)
                {
                    byte* layer = bs + 8 + l * layerStride;
                    int nals = *(int*)(layer + LayerNalCount);
                    int* lens = *(int**)(layer + LayerNalLengths);
                    for (int n = 0; n < nals; n++) total += lens[n];
                }
                if (output.Length < total) output = new byte[total * 2];

                int pos = 0;
                for (int l = 0; l < layers; l++)
                {
                    byte* layer = bs + 8 + l * layerStride;
                    int nals = *(int*)(layer + LayerNalCount);
                    int* lens = *(int**)(layer + LayerNalLengths);
                    byte* buf = *(byte**)(layer + LayerBsBuf);
                    int layerBytes = 0;
                    for (int n = 0; n < nals; n++) layerBytes += lens[n];
                    Marshal.Copy((IntPtr)buf, output, pos, layerBytes); // NALs already carry 00 00 00 01 start codes
                    pos += layerBytes;
                }
                return pos == 0 ? (ArraySegment<byte>?)null : new ArraySegment<byte>(output, 0, pos);
            }
        }

        private T Fn<T>(string name) where T : Delegate =>
            (T)Marshal.GetDelegateForFunctionPointer(NativeLib.Symbol(lib, name), typeof(T));

        private static T Slot<T>(IntPtr vtbl, int index) where T : Delegate =>
            (T)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, index * IntPtr.Size), typeof(T));

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            uninitialize(encoder);
            destroy(encoder);
            // The library stays loaded: cheap, and a second encoder instance may reuse it.
        }

        /// <summary>Finds the OpenH264 library next to the executable (any openh264*.dll / libopenh264*.so*).</summary>
        public static string FindLibrary(string directory)
        {
            bool windows = Path.DirectorySeparatorChar == '\\';
            string pattern = windows ? "openh264*.dll" : "libopenh264*.so*";
            var hits = Directory.GetFiles(directory, pattern);
            Array.Sort(hits, StringComparer.OrdinalIgnoreCase);
            return hits.Length > 0 ? hits[hits.Length - 1] : null;
        }
    }

    internal static class NativeLib
    {
#if NETFRAMEWORK
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        public static IntPtr Load(string path)
        {
            IntPtr h = LoadLibraryW(path);
            if (h == IntPtr.Zero) throw new DllNotFoundException(path + " (Win32 error " + Marshal.GetLastWin32Error() + ")");
            return h;
        }

        public static IntPtr Symbol(IntPtr module, string name)
        {
            IntPtr p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return p;
        }
#else
        public static IntPtr Load(string path) => NativeLibrary.Load(path);
        public static IntPtr Symbol(IntPtr module, string name) => NativeLibrary.GetExport(module, name);
#endif
    }
}
