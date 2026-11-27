using System;
using System.Runtime.InteropServices;
using System.Text;

// SimHub "MMF V3" screen protocol. Layout copied from the sample driver SimHub 9.13.7 ships in
// SharedDrivers/VirtualExternalDisplayDriverSampleCSharp (newer than MMFVisualizerV4: it adds
// Capabilities, ConfigurationRequestCount and OwnerWindowHandle). Field order and sizes must not
// change: SimHub maps the same bytes. See docs/simhub-mmf.md.
namespace PiScreen.Mmf
{
    public enum TouchMode
    {
        Unchanged = 0,
        ForceLeftRight = 1,
        ForceAdvanced = 2,
    }

    public enum ScreenAction
    {
        Unset = 0,
        ActionA = 1,
        ActionB = 2,
        ActionC = 4,
        ActionD = 5,
        PreviousScreen = 6,
        NextScreen = 7,
        FirstScreen = 8,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct RenderBuffer
    {
        public const int MaxBytes = 1920 * 1080 * 4;
        public fixed byte Buffer[MaxBytes];
        public int UpdateCount;
        public fixed byte RenderBufferExpansion[128];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct LedsRenderBuffer
    {
        public fixed byte Buffer[2048 * 3];
        public int UpdateCount;
        public fixed byte RenderBufferExpansion[128];
    }

    // Deliberately NOT Pack=1: matches SimHub's own declaration (default sequential layout).
    public unsafe struct ActionCommand
    {
        public bool RequestActive;
        public int RequestId;
        public ScreenAction Action;
        public fixed byte ActionCommandExpansion[32];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MMFTouchPoint
    {
        public bool CursorPressed;
        public int CursorCoordinatesX;
        public int CursorCoordinatesY;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct DeviceEntry
    {
        public fixed byte DeviceID[64];
        public int MMFIdentifier1;
        public int MMFIdentifier2;
        public fixed byte DeviceInfo[256];
        public fixed char Expansion[128];
        public bool Available;
        public fixed char Expansion2[128];

        public string GetDeviceID() { fixed (byte* p = DeviceID) return Ascii.Read(p, 64); }
        public void SetDeviceID(string v) { fixed (byte* p = DeviceID) Ascii.Write(p, 64, v); }
        public string GetDeviceInfo() { fixed (byte* p = DeviceInfo) return Ascii.Read(p, 256); }
        public void SetDeviceInfo(string v) { fixed (byte* p = DeviceInfo) Ascii.Write(p, 256, v); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MMFDeviceIndex
    {
        public const int MaxDevices = 10;
        public DeviceEntry Device01, Device02, Device03, Device04, Device05,
                           Device06, Device07, Device08, Device09, Device10;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct MMFHeaderV3
    {
        // ---- Request area, written by the driver ----
        public int StructVersion;                 // 3
        public int RequestedRenderWidthPixels;
        public int RequestedRenderHeightPixels;
        public bool RequestDoubleBuffer;
        public fixed byte StartupDashboard[128];  // ASCII dashboard folder name
        public TouchMode RequestedTouchMode;
        public bool EnableTouchTraces;
        public int ReadTimeout;                   // seconds without ReadCount change before SimHub disconnects (0 = 5 s)
        public fixed char Requester[64];
        public int ReadCount;                     // driver keep-alive: increment on every poll
        public bool RequestActive;                // set last, once the request fields are filled
        public int Capabilities;                  // bit 0: driver has a configuration UI
        public fixed byte RequestHeaderExpansion[225];

        // ---- Touch, written by the driver (Cursor4 is the only slot SimHub < 9.11.18 reads) ----
        public MMFTouchPoint Cursor1;
        public MMFTouchPoint Cursor2;
        public MMFTouchPoint Cursor3;
        public MMFTouchPoint Cursor4;

        public ActionCommand DisplayActionCommand1;
        public ActionCommand DisplayActionCommand2;
        public ActionCommand DisplayActionCommand3;
        public ActionCommand DisplayActionCommand4;

        // ---- Render area, written by SimHub ----
        public bool SimHubConnected;              // stays true if SimHub is killed: use write counters for liveness
        public fixed byte ExternalLedsHardwareID[512];
        public int GlobalWriteCount;
        public int DisplayBufferCurrentlyWritten; // 0 none, else 1/2
        public int DisplayLastFilledBuffer;       // 0 none yet, else 1/2
        public int DisplayWriteCount;
        public int DisplayRenderedDataSize;
        public int DisplayRenderedWidthPixels;
        public int DisplayRenderedHeightPixels;
        public int DisplayBytesPerRow;
        public int DisplayBrightness;             // 0..100
        public int ConfigurationRequestCount;     // SimHub bumps it when the user clicks "configure"
        public long OwnerWindowHandle;            // SimHub window to parent the configuration UI on
        public fixed byte DisplayRenderingHeaderExpansion[116];

        public RenderBuffer DisplayRenderBuffer1;
        public RenderBuffer DisplayRenderBuffer2;

        public int LedsBufferCurrentlyWritten;
        public int LedsLastFilledBuffer;
        public int LedsWriteCount;
        public int WrittenLedsCount;
        public fixed byte LedsRenderingHeaderExpansion[128];
        public LedsRenderBuffer LedsRenderBuffer1;
        public LedsRenderBuffer LedsRenderBuffer2;
    }

    public static class MmfNames
    {
        public const string NotificationEvent = @"Global\SHMMFNotification";

        public static string Index(string familyId) => "SimHubDashIndexV3" + familyId;

        public static string Render(string familyId, int id1, int id2) =>
            "SimHubDashRenderV3" + familyId + "_" + id1 + "_" + id2;

        /// <summary>SimHub passes the full index MMF name; the family id is what follows the prefix.</summary>
        public static string FamilyFromIndexName(string indexName) =>
            indexName.StartsWith("SimHubDashIndexV3", StringComparison.Ordinal)
                ? indexName.Substring("SimHubDashIndexV3".Length)
                : indexName;
    }

    public static unsafe class Ascii
    {
        public static string Read(byte* buffer, int maxLength)
        {
            int len = 0;
            while (len < maxLength && buffer[len] != 0) len++;
            return Encoding.ASCII.GetString(buffer, len);
        }

        public static void Write(byte* buffer, int maxLength, string value)
        {
            for (int i = 0; i < maxLength; i++) buffer[i] = 0;
            if (string.IsNullOrEmpty(value)) return;
            int count = Math.Min(value.Length, maxLength - 1);
            for (int i = 0; i < count; i++) buffer[i] = (byte)(value[i] < 128 ? value[i] : '?');
        }
    }
}
