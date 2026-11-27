using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace PiScreen.Mmf
{
    /// <summary>A screen the driver offers to SimHub through the index MMF.</summary>
    public struct PublishedScreen
    {
        public string DeviceId;   // must be stable across runs: SimHub attaches device instances to it
        public string DeviceInfo; // shown as the model name in SimHub
        public int Identifier1;   // random, makes the render MMF name unique
        public int Identifier2;
        public bool Available;    // false in -detect mode (listing only)
    }

    /// <summary>Writes the "SimHubDashIndexV3&lt;family&gt;" MMF that SimHub scans for screens.</summary>
    public sealed unsafe class DeviceIndex : IDisposable
    {
        private readonly MemoryMappedFile mmf;
        private readonly MemoryMappedViewAccessor accessor;
        private byte* pointer;

        /// <summary>Clear the entries on Dispose. Off for -detect, whose listing SimHub reads after we exit.</summary>
        public bool ClearOnDispose { get; set; } = true;

        public DeviceIndex(string indexMmfName)
        {
            mmf = MemoryMappedFile.CreateOrOpen(indexMmfName, sizeof(MMFDeviceIndex), MemoryMappedFileAccess.ReadWrite);
            accessor = mmf.CreateViewAccessor(0, sizeof(MMFDeviceIndex), MemoryMappedFileAccess.ReadWrite);
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        }

        public void Publish(IList<PublishedScreen> screens)
        {
            DeviceEntry* first = (DeviceEntry*)pointer;
            for (int i = 0; i < MMFDeviceIndex.MaxDevices; i++)
            {
                DeviceEntry* d = first + i;
                // Clear Available before touching the other fields, set it last (same as SimHub's sample).
                d->Available = false;
                Thread.MemoryBarrier();
                if (i < screens.Count)
                {
                    d->SetDeviceID(screens[i].DeviceId);
                    d->SetDeviceInfo(screens[i].DeviceInfo);
                    d->MMFIdentifier1 = screens[i].Identifier1;
                    d->MMFIdentifier2 = screens[i].Identifier2;
                    Thread.MemoryBarrier();
                    d->Available = screens[i].Available;
                }
                else
                {
                    d->SetDeviceID(string.Empty);
                    d->SetDeviceInfo(string.Empty);
                    d->MMFIdentifier1 = 0;
                    d->MMFIdentifier2 = 0;
                }
            }
        }

        public void Dispose()
        {
            if (pointer != null)
            {
                if (ClearOnDispose) Publish(Array.Empty<PublishedScreen>());
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                pointer = null;
            }
            accessor.Dispose();
            mmf.Dispose();
        }
    }
}
