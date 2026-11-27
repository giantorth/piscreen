using System.Runtime.Serialization;

namespace PiScreen.Net
{
    // JSON messages on the "control" data channel. See docs/protocol.md.

    /// <summary>Envelope used to read the "type" field before decoding the rest.</summary>
    [DataContract]
    public sealed class ControlMessage
    {
        [DataMember(Name = "type")] public string Type { get; set; }
    }

    /// <summary>Pi → driver. Active contacts, normalized 0..1 over the video frame; empty = all released.</summary>
    [DataContract]
    public sealed class TouchMessage
    {
        [DataMember(Name = "type")] public string Type { get; set; } = "touch";
        [DataMember(Name = "points")] public TouchPointMessage[] Points { get; set; }
    }

    [DataContract]
    public sealed class TouchPointMessage
    {
        [DataMember(Name = "x")] public double X { get; set; }
        [DataMember(Name = "y")] public double Y { get; set; }
    }

    /// <summary>Driver → Pi. SimHub's brightness setting (0..100) for the panel backlight.</summary>
    [DataContract]
    public sealed class BrightnessMessage
    {
        [DataMember(Name = "type")] public string Type { get; set; } = "brightness";
        [DataMember(Name = "value")] public int Value { get; set; }
    }

    /// <summary>Driver → Pi. Whether SimHub is currently rendering to this screen.</summary>
    [DataContract]
    public sealed class StateMessage
    {
        [DataMember(Name = "type")] public string Type { get; set; } = "state";
        [DataMember(Name = "simhub")] public bool SimHub { get; set; }
        [DataMember(Name = "width")] public int Width { get; set; }
        [DataMember(Name = "height")] public int Height { get; set; }
    }
}
