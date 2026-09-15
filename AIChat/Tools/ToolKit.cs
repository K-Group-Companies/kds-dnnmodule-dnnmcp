using System;
using System.Collections.Generic;
using System.Globalization;
using Dnn.Mcp.WebApi.Models;

namespace Satrabel.AIChat.Tools
{
    /// <summary>
    /// Shared argument parsing and result construction for the KGroup tools.
    /// </summary>
    /// <remarks>
    /// The module's own tools declare every parameter as <c>type: "string"</c> — even
    /// integer ids and booleans — and set no <c>EnumValues</c>, so nothing validates the
    /// arguments a client sends. <see cref="McpHandler.ConvertParametersToJsonSchema"/>
    /// does honour <c>Type</c> and <c>EnumValues</c>, so tools declared here use real
    /// JSON Schema types and enumerate their allowed values. That pushes the first line of
    /// validation back into the schema instead of leaving it entirely to the caller.
    ///
    /// Arguments are still parsed defensively: a client that ignores the schema can send
    /// anything, and a bad value should produce a usable message rather than a
    /// <c>NullReferenceException</c> or a silent default.
    /// </remarks>
    public static class ToolKit
    {
        /// <summary>
    /// Declares a tool parameter.
    /// </summary>
    /// <param name="type">
    /// Must be one of the tokens <c>McpHandler.MapParameterType</c> recognises —
    /// <c>string</c>, <c>int</c>, <c>float</c>, <c>bool</c>, <c>array</c>, <c>object</c> —
    /// which it maps to the JSON Schema names <c>integer</c>, <c>number</c>,
    /// <c>boolean</c> and so on. Anything else silently falls back to <c>string</c>, so
    /// passing the JSON Schema name directly (e.g. "integer") does not work.
    /// </param>
        public static ToolParameter Param(
            string name,
            string type,
            string description,
            bool required = false,
            string[] enumValues = null)
        {
            return new ToolParameter
            {
                Name = name,
                Type = type,
                Description = description,
                Required = required,
                EnumValues = enumValues,
            };
        }

        /// <summary>Reads a string argument. Returns null when absent or blank.</summary>
        public static string Str(IDictionary<string, object> args, string name)
        {
            if (args == null || !args.TryGetValue(name, out var raw) || raw == null)
            {
                return null;
            }

            var text = raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture);

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        /// <summary>
        /// Reads an integer argument. Accepts a real number or a numeric string, since a
        /// client may send either regardless of the declared type.
        /// </summary>
        public static bool TryInt(IDictionary<string, object> args, string name, out int value)
        {
            value = 0;
            var text = Str(args, name);

            return text != null
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Reads a boolean argument, tolerating the string forms clients actually send.
        /// Returns <paramref name="fallback"/> when absent or unrecognised.
        /// </summary>
        public static bool Bool(IDictionary<string, object> args, string name, bool fallback = false)
        {
            var text = Str(args, name);
            if (text == null)
            {
                return fallback;
            }

            switch (text.Trim().ToLowerInvariant())
            {
                case "true":
                case "1":
                case "yes":
                case "y":
                    return true;
                case "false":
                case "0":
                case "no":
                case "n":
                    return false;
                default:
                    return fallback;
            }
        }

        /// <summary>Builds a successful result.</summary>
        public static CallToolResult Ok(string message)
        {
            return new CallToolResult
            {
                IsError = false,
                Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
            };
        }

        /// <summary>Builds an error result carrying an actionable message.</summary>
        public static CallToolResult Fail(string message)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
            };
        }

        /// <summary>
        /// Wraps a handler so an unhandled exception becomes a readable tool error rather
        /// than raw .NET exception text surfacing to the client.
        /// </summary>
        public static Func<Dictionary<string, object>, CallToolResult> Guard(
            string toolName,
            Func<Dictionary<string, object>, CallToolResult> handler)
        {
            return args =>
            {
                try
                {
                    return handler(args);
                }
                catch (Exception ex)
                {
                    return Fail(toolName + " failed: " + ex.Message);
                }
            };
        }
    }
}
