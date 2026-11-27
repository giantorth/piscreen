using System;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PiScreen.Net
{
    [DataContract]
    public sealed class PiInfo
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "width")] public int Width { get; set; }
        [DataMember(Name = "height")] public int Height { get; set; }
        [DataMember(Name = "touch")] public bool Touch { get; set; }
        [DataMember(Name = "version")] public string Version { get; set; }
    }

    /// <summary>
    /// Signaling against the Pi receiver: GET /info (display size) and a WHIP-style POST /offer
    /// (body application/sdp, 201 + answer SDP). No trickle ICE: both sides send complete SDP.
    /// </summary>
    public sealed class PiClient : IDisposable
    {
        private readonly HttpClient http;

        public string BaseUrl { get; }

        public PiClient(string host, int port)
        {
            BaseUrl = "http://" + (host.Contains(":") && !host.StartsWith("[") ? "[" + host + "]" : host) + ":" + port;
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        public async Task<PiInfo> GetInfoAsync(CancellationToken ct)
        {
            using (var resp = await http.GetAsync(BaseUrl + "/info", ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return Json.Read<PiInfo>(bytes);
            }
        }

        public async Task<string> PostOfferAsync(string offerSdp, CancellationToken ct)
        {
            var content = new StringContent(offerSdp, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/sdp");
            using (var resp = await http.PostAsync(BaseUrl + "/offer", content, ct).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"POST /offer: {(int)resp.StatusCode} {body.Trim()}");
                return body;
            }
        }

        public void Dispose() => http.Dispose();
    }

    public static class Json
    {
        public static T Read<T>(byte[] utf8)
        {
            using (var ms = new MemoryStream(utf8))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(ms);
        }

        public static T Read<T>(string s) => Read<T>(Encoding.UTF8.GetBytes(s));

        public static string Write<T>(T value)
        {
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(ms, value);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }
}
