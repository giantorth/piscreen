using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace PiScreen.Net
{
    /// <summary>
    /// One WebRTC session to the Pi: a send-only H.264 track plus a "control" data channel.
    /// The driver is always the offerer. Events fire on SIPSorcery threads.
    /// </summary>
    public sealed class WebRtcLink : IDisposable
    {
        // Constrained Baseline 3.1 is what OpenH264 emits up to 1280x720@30 / 800x480@60; the Pi's
        // decoder takes it as-is. packetization-mode=1 lets SIPSorcery use FU-A for large NALs.
        private const string H264Fmtp = "packetization-mode=1;profile-level-id=42e01f;level-asymmetry-allowed=1";

        private readonly RTCPeerConnection pc;
        private RTCDataChannel control;
        private int closed;

        public event Action<string> MessageReceived;
        public event Action KeyFrameRequested;
        public event Action<string> Closed;
        /// <summary>The control channel is open: anything sent before this was dropped.</summary>
        public event Action ControlOpened;

        public bool MediaReady { get; private set; }
        public bool ControlOpen => control != null && control.readyState == RTCDataChannelState.open;

        public WebRtcLink()
        {
            var config = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>(), // LAN only: host candidates
                X_UseRtpFeedbackProfile = true,        // RTP/SAVPF so the Pi can send PLI/NACK feedback
            };
            pc = new RTCPeerConnection(config);

            var video = new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.H264, 96, 90000, H264Fmtp), MediaStreamStatusEnum.SendOnly);
            pc.addTrack(video);

            pc.onconnectionstatechange += state =>
            {
                Log.Info("WebRTC connection " + state);
                if (state == RTCPeerConnectionState.connected)
                {
                    MediaReady = true;
                    KeyFrameRequested?.Invoke();
                }
                else if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.closed ||
                         state == RTCPeerConnectionState.disconnected)
                {
                    RaiseClosed("connection " + state);
                }
            };

            pc.OnReceiveReport += (ep, media, report) =>
            {
                var fb = report?.Feedback;
                if (fb == null || fb.Header == null) return;
                if (fb.Header.PacketType == RTCPReportTypesEnum.PSFB &&
                    (fb.Header.PayloadFeedbackMessageType == PSFBFeedbackTypesEnum.PLI ||
                     fb.Header.PayloadFeedbackMessageType == PSFBFeedbackTypesEnum.FIR))
                {
                    Log.Info("Keyframe requested by the Pi (" + fb.Header.PayloadFeedbackMessageType + ")");
                    KeyFrameRequested?.Invoke();
                }
            };
        }

        /// <summary>Creates the offer, exchanges it with the Pi and waits for ICE/DTLS to finish.</summary>
        public async Task ConnectAsync(PiClient client, TimeSpan timeout, CancellationToken ct)
        {
            control = await pc.createDataChannel("control", new RTCDataChannelInit { ordered = true }).ConfigureAwait(false);
            control.onopen += () =>
            {
                Log.Info("Control channel open");
                ControlOpened?.Invoke();
            };
            control.onmessage += (dc, proto, data) =>
            {
                if (data != null) MessageReceived?.Invoke(Encoding.UTF8.GetString(data));
            };

            await WaitGatheringAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

            var offer = pc.createOffer();
            await pc.setLocalDescription(offer).ConfigureAwait(false);
            // localDescription has the gathered host candidates; offer.sdp may predate gathering.
            string offerSdp = AddKeyFrameFeedback(pc.localDescription?.sdp?.ToString() ?? offer.sdp);

            string answer = await client.PostOfferAsync(offerSdp, ct).ConfigureAwait(false);
            var result = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answer });
            if (result != SetDescriptionResultEnum.OK)
                throw new InvalidOperationException("Pi answer rejected: " + result);

            var deadline = DateTime.UtcNow + timeout;
            while (pc.connectionState != RTCPeerConnectionState.connected)
            {
                ct.ThrowIfCancellationRequested();
                if (closed != 0 || DateTime.UtcNow > deadline)
                    throw new TimeoutException("WebRTC did not connect (state " + pc.connectionState + ")");
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// SIPSorcery only advertises transport-cc. Without "nack pli"/"ccm fir" the receiver never
        /// asks for a keyframe after loss. Plain "nack" is left out: SIPSorcery doesn't retransmit, so
        /// NACKs would only make the Pi's jitter buffer wait for packets that never come.
        /// </summary>
        private static string AddKeyFrameFeedback(string sdp)
        {
            const string rtpmap = "a=rtpmap:96 H264/90000";
            int i = sdp.IndexOf(rtpmap, StringComparison.Ordinal);
            if (i < 0 || sdp.IndexOf("a=rtcp-fb:96 nack pli", StringComparison.Ordinal) >= 0) return sdp;
            int eol = sdp.IndexOf('\n', i);
            if (eol < 0) return sdp;
            string nl = sdp[eol - 1] == '\r' ? "\r\n" : "\n";
            return sdp.Insert(eol + 1, "a=rtcp-fb:96 nack pli" + nl + "a=rtcp-fb:96 ccm fir" + nl);
        }

        private async Task WaitGatheringAsync(TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (pc.iceGatheringState != RTCIceGatheringState.complete && DateTime.UtcNow < deadline)
                await Task.Delay(20, ct).ConfigureAwait(false);
        }

        /// <summary>Sends one Annex-B access unit. <paramref name="duration90k"/> advances the RTP clock.</summary>
        public void SendFrame(ArraySegment<byte> accessUnit, uint duration90k)
        {
            if (!MediaReady || closed != 0) return;
            var sample = new byte[accessUnit.Count];
            Buffer.BlockCopy(accessUnit.Array, accessUnit.Offset, sample, 0, accessUnit.Count);
            pc.SendVideo(duration90k, sample);
        }

        public void SendMessage(string json)
        {
            var c = control;
            if (c != null && c.readyState == RTCDataChannelState.open && closed == 0)
            {
                try { c.send(json); }
                catch (Exception ex) { Log.Warn("Control send: " + ex.Message); }
            }
        }

        private void RaiseClosed(string why)
        {
            if (Interlocked.Exchange(ref closed, 1) == 0)
            {
                MediaReady = false;
                Closed?.Invoke(why);
            }
        }

        public void Dispose()
        {
            RaiseClosed("disposed");
            try { pc.close(); } catch (Exception ex) { Log.Warn("WebRTC close: " + ex.Message); }
        }
    }
}
