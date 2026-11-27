using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace PiScreen.Mmf
{
    /// <summary>
    /// One render MMF ("SimHubDashRenderV3&lt;family&gt;_&lt;id1&gt;_&lt;id2&gt;"): posts the render request,
    /// keeps SimHub's read watchdog alive, pulls finished frames and pushes touch points.
    /// Not thread-safe: call everything from one polling thread.
    /// </summary>
    public sealed unsafe class ScreenChannel : IDisposable
    {
        public const int CapabilityConfiguration = 1;

        private readonly MemoryMappedFile mmf;
        private readonly MemoryMappedViewAccessor accessor;
        private byte* pointer;
        private int lastWriteCount = -1;
        private DateTime lastWrite;
        private int lastConfigRequests;

        public int Width { get; }
        public int Height { get; }
        public bool Connected { get; private set; }
        public int Brightness { get; private set; } = -1;
        public long TornFrames { get; private set; }

        private MMFHeaderV3* Header => (MMFHeaderV3*)pointer;

        public ScreenChannel(string familyId, int id1, int id2, int width, int height, int capabilities,
                             TouchMode touchMode = TouchMode.Unchanged, bool touchTraces = false)
        {
            Width = width;
            Height = height;
            mmf = MemoryMappedFile.CreateOrOpen(MmfNames.Render(familyId, id1, id2), sizeof(MMFHeaderV3), MemoryMappedFileAccess.ReadWrite);
            accessor = mmf.CreateViewAccessor(0, sizeof(MMFHeaderV3), MemoryMappedFileAccess.ReadWrite);
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);

            var h = Header;
            h->StructVersion = 3;
            h->RequestedRenderWidthPixels = width;
            h->RequestedRenderHeightPixels = height;
            h->RequestDoubleBuffer = true;
            h->RequestedTouchMode = touchMode;
            h->EnableTouchTraces = touchTraces; // SimHub draws where it sees each touch (debugging)
            h->ReadTimeout = 2;
            h->Capabilities = capabilities;
            lastConfigRequests = h->ConfigurationRequestCount;
            Thread.MemoryBarrier();
            h->RequestActive = true;
        }

        /// <summary>Wake SimHub so it scans for requests now instead of on its 5 s poll.</summary>
        public static void NotifySimHub()
        {
            try
            {
                using (var e = new EventWaitHandle(false, EventResetMode.ManualReset, MmfNames.NotificationEvent))
                    e.Set();
            }
            catch (Exception ex)
            {
                Log.Warn("SimHub notification event: " + ex.Message);
            }
        }

        /// <summary>True once per click on SimHub's "configure" button.</summary>
        public bool TakeConfigurationRequest(out IntPtr ownerWindow)
        {
            var h = Header;
            ownerWindow = new IntPtr(h->OwnerWindowHandle);
            int n = h->ConfigurationRequestCount;
            if (n == lastConfigRequests) return false;
            lastConfigRequests = n;
            return true;
        }

        /// <summary>
        /// Poll once. Returns true and fills <paramref name="bgra"/> (Width*Height*4, tightly packed)
        /// when SimHub finished a new frame since the last call.
        /// </summary>
        public bool Poll(byte[] bgra)
        {
            var h = Header;
            h->ReadCount++; // watchdog: SimHub disconnects after ReadTimeout seconds without a change

            if (!h->SimHubConnected || h->DisplayLastFilledBuffer == 0)
            {
                Connected = false;
                return false;
            }

            Brightness = Math.Max(0, Math.Min(100, h->DisplayBrightness));

            int writeCount = h->DisplayWriteCount;
            if (writeCount == lastWriteCount)
            {
                // SimHub only writes on change plus a ~1/s keep-alive; SimHubConnected stays true if it dies.
                Connected = (DateTime.UtcNow - lastWrite).TotalSeconds < 6;
                return false;
            }
            lastWriteCount = writeCount;
            lastWrite = DateTime.UtcNow;
            Connected = true;

            int buffer = h->DisplayLastFilledBuffer;
            if (h->DisplayBufferCurrentlyWritten == buffer)
            {
                TornFrames++;
                return false;
            }

            RenderBuffer* rb = buffer == 2 ? &h->DisplayRenderBuffer2 : &h->DisplayRenderBuffer1;
            int updateBefore = rb->UpdateCount;

            int srcW = h->DisplayRenderedWidthPixels, srcH = h->DisplayRenderedHeightPixels;
            int srcStride = h->DisplayBytesPerRow > 0 ? h->DisplayBytesPerRow : srcW * 4;
            if (srcW != Width || srcH != Height || srcStride < Width * 4 || (long)srcStride * Height > RenderBuffer.MaxBytes)
            {
                // SimHub renders at the size we requested; anything else is a transient during (re)connect.
                return false;
            }

            int rowBytes = Width * 4;
            fixed (byte* dst = bgra)
            {
                byte* src = rb->Buffer;
                if (srcStride == rowBytes)
                {
                    Buffer.MemoryCopy(src, dst, bgra.Length, (long)rowBytes * Height);
                }
                else
                {
                    for (int y = 0; y < Height; y++)
                        Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * rowBytes, rowBytes, rowBytes);
                }
            }

            // SimHub bumps UpdateCount when it *starts* drawing into a buffer: a change means we raced it.
            if (rb->UpdateCount != updateBefore)
            {
                TornFrames++;
                lastWriteCount = -1; // retry on the next poll
                return false;
            }
            return true;
        }

        /// <summary>
        /// Up to 4 contacts in render pixels. Cursor4 is filled first: SimHub before 9.11.18 only reads that slot.
        /// </summary>
        public void SetTouch(TouchContact[] contacts, int count)
        {
            var h = Header;
            MMFTouchPoint* slots = &h->Cursor1;
            int[] order = { 3, 0, 1, 2 };
            for (int i = 0; i < 4; i++)
            {
                MMFTouchPoint* s = slots + order[i];
                if (i < count)
                {
                    s->CursorCoordinatesX = Clamp(contacts[i].X, 0, Width - 1);
                    s->CursorCoordinatesY = Clamp(contacts[i].Y, 0, Height - 1);
                    Thread.MemoryBarrier(); // coordinates before the press flag
                    s->CursorPressed = true;
                }
                else
                {
                    s->CursorPressed = false;
                }
            }
        }

        public void EnqueueAction(ScreenAction action)
        {
            var h = Header;
            ActionCommand* cmds = &h->DisplayActionCommand1;
            for (int i = 0; i < 4; i++)
            {
                if (!cmds[i].RequestActive)
                {
                    cmds[i].Action = action;
                    cmds[i].RequestId = ++actionRequestId;
                    Thread.MemoryBarrier();
                    cmds[i].RequestActive = true;
                    return;
                }
            }
        }
        private int actionRequestId;

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        public void Dispose()
        {
            if (pointer != null)
            {
                Header->RequestActive = false;
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                pointer = null;
            }
            accessor.Dispose();
            mmf.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TouchContact
    {
        public int X;
        public int Y;
    }
}
