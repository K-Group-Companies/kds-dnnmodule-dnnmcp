using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using DotNetNuke.Services.FileSystem;

namespace KGroup.EasyDNNNewsMcp.Tools
{
    /// <summary>
    /// Fetches an article's main image and stores it where EasyDNNNews expects it.
    /// </summary>
    /// <remarks>
    /// The module keeps an article's images in <c>Portals/&lt;pid&gt;/EasyDNNNews/&lt;ArticleID&gt;/</c>
    /// and stores only the file name in <c>EasyDNNNews.ArticleImage</c>; the resized
    /// <c>images/</c> variants are generated on first render. The file is added through
    /// <see cref="FileManager"/> so it is registered in DNN's file table like an upload.
    ///
    /// <c>imageUrl</c> is downloaded by the web server, so it is guarded against SSRF:
    /// http(s) only, every hop's host must resolve to public addresses (redirects are
    /// followed manually so each one is checked), at most 10 MB, and the response must
    /// be an image. DNS can still change between the check and the connect; the guard
    /// stops casual and accidental internal fetches, not a determined rebinding attack.
    /// </remarks>
    internal static class NewsImages
    {
        private const int MaxBytes = 10 * 1024 * 1024;
        private const int MaxRedirects = 3;

        private static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

        internal class ImageData
        {
            public string FileName { get; set; }

            public byte[] Bytes { get; set; }

            public string ContentType { get; set; }
        }

        /// <summary>Reads an existing portal file, e.g. <c>Images/blog/hero.jpg</c>.</summary>
        public static ImageData FromPortalFile(int portalId, string path, out string error)
        {
            error = null;
            var file = FileManager.Instance.GetFile(portalId, path.Replace('\\', '/').TrimStart('/'));
            if (file == null)
            {
                error = "imagePath '" + path + "' is not a file in this portal's file system.";
                return null;
            }

            if (!Extensions.Contains("." + (file.Extension ?? string.Empty).ToLowerInvariant()))
            {
                error = "imagePath must be a jpg, jpeg, png, gif or webp file.";
                return null;
            }

            using (var stream = FileManager.Instance.GetFileContent(file))
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return new ImageData { FileName = SafeName(file.FileName, file.Extension), Bytes = ms.ToArray(), ContentType = file.ContentType };
            }
        }

        /// <summary>Downloads a public image URL (see the SSRF notes on the class).</summary>
        public static ImageData FromUrl(string url, out string error)
        {
            error = null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                error = "imageUrl is not an absolute URL.";
                return null;
            }

            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) })
            {
                for (var hop = 0; hop <= MaxRedirects; hop++)
                {
                    if (!IsPublicHttp(uri, out error))
                    {
                        return null;
                    }

                    using (var response = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                    {
                        var status = (int)response.StatusCode;
                        if (status >= 300 && status < 400 && response.Headers.Location != null)
                        {
                            uri = new Uri(uri, response.Headers.Location);
                            continue;
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            error = "imageUrl returned HTTP " + status + ".";
                            return null;
                        }

                        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        {
                            error = "imageUrl did not return an image (Content-Type '" + contentType + "').";
                            return null;
                        }

                        if (response.Content.Headers.ContentLength > MaxBytes)
                        {
                            error = "imageUrl is larger than 10 MB.";
                            return null;
                        }

                        var bytes = ReadCapped(response.Content.ReadAsStreamAsync().GetAwaiter().GetResult());
                        if (bytes == null)
                        {
                            error = "imageUrl is larger than 10 MB.";
                            return null;
                        }

                        var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
                        var ext = Path.GetExtension(name).ToLowerInvariant();
                        if (!Extensions.Contains(ext))
                        {
                            ext = ExtensionFor(contentType);
                            if (ext == null)
                            {
                                error = "imageUrl must be a jpg, png, gif or webp image (got '" + contentType + "').";
                                return null;
                            }

                            name = Path.GetFileNameWithoutExtension(name) + ext;
                        }

                        return new ImageData { FileName = SafeName(name, ext.TrimStart('.')), Bytes = bytes, ContentType = contentType };
                    }
                }
            }

            error = "imageUrl redirected more than " + MaxRedirects + " times.";
            return null;
        }

        /// <summary>
        /// Writes the image into the article's folder, creating <c>EasyDNNNews/&lt;id&gt;/</c>
        /// if needed, and returns the stored file name.
        /// </summary>
        public static string Save(int portalId, int articleId, ImageData image)
        {
            var folders = FolderManager.Instance;
            var root = folders.GetFolder(portalId, "EasyDNNNews/") ?? folders.AddFolder(portalId, "EasyDNNNews/");
            var path = root.FolderPath + articleId + "/";
            var folder = folders.GetFolder(portalId, path) ?? folders.AddFolder(portalId, path);

            using (var ms = new MemoryStream(image.Bytes))
            {
                FileManager.Instance.AddFile(folder, image.FileName, ms, true, false, image.ContentType);
            }

            return image.FileName;
        }

        private static bool IsPublicHttp(Uri uri, out string error)
        {
            error = null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                error = "imageUrl must use http or https.";
                return false;
            }

            IPAddress[] addresses;
            try
            {
                addresses = Dns.GetHostAddresses(uri.DnsSafeHost);
            }
            catch (SocketException)
            {
                error = "imageUrl host '" + uri.Host + "' does not resolve.";
                return false;
            }

            if (addresses.Length == 0 || addresses.Any(IsPrivate))
            {
                error = "imageUrl must point to a public internet host; '" + uri.Host
                    + "' resolves to a local or private address. Upload the file to the portal and pass imagePath instead.";
                return false;
            }

            return true;
        }

        private static bool IsPrivate(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            {
                return true;
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var first = ip.GetAddressBytes()[0];
                return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (first & 0xFE) == 0xFC;
            }

            var b = ip.GetAddressBytes();
            return b[0] == 10
                || b[0] == 127
                || b[0] == 0
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        private static byte[] ReadCapped(Stream stream)
        {
            using (stream)
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (ms.Length + read > MaxBytes)
                    {
                        return null;
                    }

                    ms.Write(buffer, 0, read);
                }

                return ms.ToArray();
            }
        }

        private static string ExtensionFor(string contentType)
        {
            switch (contentType.ToLowerInvariant())
            {
                case "image/jpeg":
                case "image/jpg":
                    return ".jpg";
                case "image/png":
                    return ".png";
                case "image/gif":
                    return ".gif";
                case "image/webp":
                    return ".webp";
                default:
                    return null;
            }
        }

        private static string SafeName(string fileName, string extension)
        {
            var stem = Regex.Replace(Path.GetFileNameWithoutExtension(fileName) ?? string.Empty, "[^A-Za-z0-9_-]+", "-").Trim('-');
            if (stem.Length == 0)
            {
                stem = "image";
            }

            if (stem.Length > 100)
            {
                stem = stem.Substring(0, 100);
            }

            return stem + "." + extension.TrimStart('.').ToLowerInvariant();
        }
    }
}
