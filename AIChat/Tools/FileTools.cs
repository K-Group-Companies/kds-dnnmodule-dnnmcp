using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dnn.Mcp.WebApi;
using Dnn.Mcp.WebApi.Models;
using Dnn.Mcp.WebApi.Services;
using DotNetNuke.Entities.Portals;
using DotNetNuke.Services.FileSystem;

namespace Satrabel.AIChat.Tools
{
    /// <summary>
    /// Portal file-system tools: <c>write-file</c> and <c>delete-file</c>.
    /// </summary>
    /// <remarks>
    /// <c>write-file</c> supersedes an earlier text-only version that also refused to
    /// overwrite, so images could not be uploaded at all — the workaround was to write a
    /// placeholder through the API and copy real bytes over it out of band, which left the
    /// Files table recording the placeholder's size. Both limits are lifted here.
    ///
    /// Files go through <see cref="FileManager"/> rather than being written to disk
    /// directly, so DNN's Folders/Files records stay consistent. A raw disk copy leaves the
    /// folder unregistered and <c>get-files</c> then reports it as missing.
    /// </remarks>
    public class FileTools : IMcpProvider
    {
        /// <summary>
        /// Extensions that must never be written through the portal file system: executable
        /// or configuration content that DNN or IIS would load from a portal folder.
        /// </summary>
        private static readonly string[] RestrictedExtensions =
            { ".exe", ".dll", ".config", ".asax", ".ashx", ".aspx", ".cs" };

        /// <inheritdoc />
        public void Register(IMcpRegistry registry)
        {
            registry.RegisterTool(new ToolDefinition
            {
                Name = "write-file",
                Title = "Write file",
                Description =
                    "Write a file into the portal's file system, registered through DNN's file "
                    + "APIs. Supports text or base64 content and can overwrite existing files. "
                    + "Paths are relative to the portal home directory, e.g. "
                    + "OpenContent/Files/1051/photo.jpg.",
                Category = "Files",
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param(
                        "path",
                        "string",
                        "Portal-relative path including file name, e.g. OpenContent/Files/1051/photo.jpg.",
                        true),
                    ToolKit.Param("content", "string", "File content, interpreted per 'encoding'.", true),
                    ToolKit.Param(
                        "encoding",
                        "string",
                        "How to interpret 'content'. 'utf8' for text (default), 'base64' for "
                        + "binary such as images.",
                        false,
                        new[] { "utf8", "base64" }),
                    ToolKit.Param(
                        "overwrite",
                        "bool",
                        "Overwrite the file when it already exists. Defaults to true.",
                        false),
                },
                Handler = ToolKit.Guard("write-file", WriteFile),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "delete-file",
                Title = "Delete file",
                Description =
                    "Delete a file from the portal's file system, removing both the physical "
                    + "file and its DNN Files record.",
                Category = "Files",
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param(
                        "path",
                        "string",
                        "Portal-relative path including file name, e.g. OpenContent/Files/1051/old.txt.",
                        true),
                },
                Handler = ToolKit.Guard("delete-file", DeleteFile),
            });
        }

        private static CallToolResult WriteFile(Dictionary<string, object> args)
        {
            var path = ToolKit.Str(args, "path");
            if (path == null)
            {
                return ToolKit.Fail("path is required.");
            }

            var content = ToolKit.Str(args, "content") ?? string.Empty;
            var encoding = (ToolKit.Str(args, "encoding") ?? "utf8").Trim().ToLowerInvariant();
            var overwrite = ToolKit.Bool(args, "overwrite", true);

            byte[] bytes;
            if (encoding == "base64")
            {
                try
                {
                    bytes = Convert.FromBase64String(content);
                }
                catch (FormatException)
                {
                    return ToolKit.Fail(
                        "content is not valid base64. Send raw text with encoding 'utf8', or "
                        + "correct the base64 payload.");
                }
            }
            else if (encoding == "utf8")
            {
                bytes = new UTF8Encoding(false).GetBytes(content);
            }
            else
            {
                return ToolKit.Fail("encoding must be 'utf8' or 'base64'; received '" + encoding + "'.");
            }

            var portalId = PortalSettings.Current.PortalId;
            if (!TrySplitPath(path, out var folderPath, out var fileName))
            {
                return ToolKit.Fail("path must include a file name, e.g. OpenContent/Files/1051/photo.jpg.");
            }

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (Array.IndexOf(RestrictedExtensions, extension) >= 0)
            {
                return ToolKit.Fail(
                    "Writing files with extension '" + extension + "' is not allowed.");
            }

            var folder = FolderManager.Instance.GetFolder(portalId, folderPath)
                         ?? FolderManager.Instance.AddFolder(portalId, folderPath);

            if (folder == null)
            {
                return ToolKit.Fail("Could not resolve or create folder '" + folderPath + "'.");
            }

            using (var stream = new MemoryStream(bytes))
            {
                var file = FileManager.Instance.AddFile(folder, fileName, stream, overwrite);

                return ToolKit.Ok(
                    "Wrote " + bytes.Length + " bytes to '" + path + "' (FileId " + file.FileId
                    + ", encoding " + encoding + ", overwrite " + overwrite + ").");
            }
        }

        private static CallToolResult DeleteFile(Dictionary<string, object> args)
        {
            var path = ToolKit.Str(args, "path");
            if (path == null)
            {
                return ToolKit.Fail("path is required.");
            }

            var portalId = PortalSettings.Current.PortalId;
            if (!TrySplitPath(path, out var folderPath, out var fileName))
            {
                return ToolKit.Fail("path must include a file name.");
            }

            var folder = FolderManager.Instance.GetFolder(portalId, folderPath);
            if (folder == null)
            {
                return ToolKit.Fail("Folder '" + folderPath + "' was not found.");
            }

            var file = FileManager.Instance.GetFile(folder, fileName);
            if (file == null)
            {
                return ToolKit.Fail("File '" + path + "' was not found.");
            }

            var fileId = file.FileId;
            FileManager.Instance.DeleteFile(file);

            return ToolKit.Ok("Deleted '" + path + "' (FileId " + fileId + ").");
        }

        /// <summary>
        /// Splits a portal-relative path into DNN's folder path (trailing slash, or empty for
        /// the portal root) and file name.
        /// </summary>
        private static bool TrySplitPath(string path, out string folderPath, out string fileName)
        {
            folderPath = string.Empty;
            fileName = null;

            var normalised = path.Replace('\\', '/').TrimStart('/');
            var slash = normalised.LastIndexOf('/');

            if (slash < 0)
            {
                fileName = normalised;
            }
            else
            {
                folderPath = normalised.Substring(0, slash + 1);
                fileName = normalised.Substring(slash + 1);
            }

            return !string.IsNullOrWhiteSpace(fileName);
        }
    }
}
