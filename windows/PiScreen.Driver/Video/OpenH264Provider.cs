using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.BZip2;

namespace PiScreen.Video
{
    /// <summary>
    /// Locates Cisco's OpenH264 DLL, downloading it from Cisco on first run. Releases don't bundle
    /// it: per openh264.org, Cisco covers the H.264 patent royalties only for binaries downloaded
    /// from Cisco. The download is pinned to one version and verified by SHA-256 before it is ever
    /// loaded.
    /// </summary>
    public static class OpenH264Provider
    {
        public const string FileName = "openh264-2.6.0-win64.dll";
        private const string Sha256 = "2076cb5675ec6c1a4c70e7a2a322552f547b6eeed649d6dfcd9e02a543b24691";
        private static readonly string[] Urls =
        {
            "https://ciscobinary.openh264.org/" + FileName + ".bz2",
            "http://ciscobinary.openh264.org/" + FileName + ".bz2", // integrity comes from the pinned hash
        };

        /// <summary>Per-user cache: the driver folder under Program Files isn't writable.</summary>
        public static string CachedPath => Path.Combine(Settings.Directory, FileName);

        /// <summary>
        /// A DLL next to the executable (dev builds, manual installs) wins; otherwise the verified
        /// per-user download. Null when neither exists yet.
        /// </summary>
        public static string FindExisting(string exeDirectory)
        {
            string local = OpenH264Encoder.FindLibrary(exeDirectory);
            if (local != null) return local;
            try
            {
                if (File.Exists(CachedPath))
                {
                    if (HashOf(File.ReadAllBytes(CachedPath)) == Sha256) return CachedPath;
                    Log.Warn("Cached " + CachedPath + " fails its SHA-256 check; downloading again");
                    File.Delete(CachedPath);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("OpenH264 cache: " + ex.Message);
            }
            return null;
        }

        /// <summary>Downloads, verifies and caches the DLL. Returns its path, or null on failure.</summary>
        public static async Task<string> DownloadAsync()
        {
            Directory.CreateDirectory(Settings.Directory);
            foreach (string url in Urls)
            {
                try
                {
                    Log.Info("Downloading Cisco OpenH264 from " + url);
                    byte[] compressed;
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                        compressed = await http.GetByteArrayAsync(url).ConfigureAwait(false);

                    byte[] dll;
                    using (var input = new MemoryStream(compressed))
                    using (var bz = new BZip2InputStream(input))
                    using (var output = new MemoryStream())
                    {
                        bz.CopyTo(output);
                        dll = output.ToArray();
                    }

                    string hash = HashOf(dll);
                    if (hash != Sha256)
                        throw new InvalidDataException("SHA-256 mismatch (got " + hash + ")");

                    string tmp = CachedPath + ".tmp";
                    File.WriteAllBytes(tmp, dll);
                    if (File.Exists(CachedPath)) File.Delete(CachedPath);
                    File.Move(tmp, CachedPath);
                    Log.Info($"OpenH264 saved to {CachedPath} ({dll.Length / 1024} KiB, SHA-256 verified)");
                    return CachedPath;
                }
                catch (Exception ex)
                {
                    Log.Warn("OpenH264 download from " + url + " failed: " + (ex.InnerException?.Message ?? ex.Message));
                }
            }
            return null;
        }

        private static string HashOf(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
    }
}
