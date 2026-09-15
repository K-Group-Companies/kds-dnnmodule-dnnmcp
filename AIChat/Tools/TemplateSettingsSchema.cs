using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Hosting;
using DotNetNuke.Entities.Modules;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Satrabel.AIChat.Tools
{
    /// <summary>
    /// The set of template settings an OpenContent template declares, read from its
    /// <c>template-schema.json</c>.
    /// </summary>
    /// <remarks>
    /// This is what makes routing settings correctly possible without hardcoding names.
    /// A template folder carries four JSON files: <c>schema.json</c> / <c>options.json</c>
    /// describe the module's *content*, and <c>template-schema.json</c> /
    /// <c>template-options.json</c> describe its *settings*. The settings schema gives both
    /// the valid names and their types, e.g.
    ///
    /// <code>
    /// { "Rounded": { "type": "boolean" },
    ///   "AlignItems": { "type": "string", "enum": ["start","end","center","baseline","stretch"] } }
    /// </code>
    ///
    /// Types matter as much as names: a Razor template evaluating
    /// <c>Model.Settings.Rounded ? … : …</c> needs a real JSON boolean. The string
    /// <c>"true"</c> would fail the bool conversion at render time and take the module down,
    /// so values are coerced to the declared type rather than stored verbatim.
    /// </remarks>
    internal class TemplateSettingsSchema
    {
        private readonly IDictionary<string, JObject> properties;

        private TemplateSettingsSchema(string source, IDictionary<string, JObject> properties)
        {
            this.Source = source;
            this.properties = properties;
        }

        /// <summary>Where the schema came from, or why there isn't one.</summary>
        public string Source { get; }

        /// <summary>
        /// Loads the settings schema for a module's assigned template. Never returns null;
        /// an unreadable or absent schema yields an empty one, so callers get a consistent
        /// "this name is not declared" answer.
        /// </summary>
        public static TemplateSettingsSchema ForModule(ModuleInfo module)
        {
            var templateSetting = module?.ModuleSettings?["template"] as string;

            if (string.IsNullOrWhiteSpace(templateSetting))
            {
                return new TemplateSettingsSchema(
                    "no template assigned to this module",
                    new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase));
            }

            var schemaFile = ResolveSchemaPath(templateSetting);

            if (schemaFile == null || !File.Exists(schemaFile))
            {
                return new TemplateSettingsSchema(
                    "the template ships no template-schema.json",
                    new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase));
            }

            var declared = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var root = JObject.Parse(File.ReadAllText(schemaFile));

                if (root["properties"] is JObject props)
                {
                    foreach (var property in props.Properties())
                    {
                        declared[property.Name] = property.Value as JObject ?? new JObject();
                    }
                }
            }
            catch (JsonException)
            {
                return new TemplateSettingsSchema(
                    "template-schema.json is not valid JSON", declared);
            }

            return new TemplateSettingsSchema("template-schema.json", declared);
        }

        /// <summary>True when the template declares a setting by this name.</summary>
        public bool Declares(string name)
        {
            return this.properties.ContainsKey(name);
        }

        /// <summary>Describes the schema source for an error message.</summary>
        public string Describe()
        {
            return this.Source;
        }

        /// <summary>Lists the declared setting names for an error message.</summary>
        public string NamesForMessage()
        {
            return this.properties.Count == 0
                ? "(none — " + this.Source + ")"
                : string.Join(", ", this.properties.Keys.OrderBy(k => k));
        }

        /// <summary>
        /// Coerces a supplied value to the type the schema declares, and validates it against
        /// any enum.
        /// </summary>
        public bool TryCoerce(JProperty property, out JToken coerced, out string reason)
        {
            coerced = null;
            reason = null;

            var definition = this.properties[property.Name];
            var declaredType = definition["type"]?.Value<string>();
            var text = property.Value?.Type == JTokenType.Null
                ? null
                : property.Value?.ToString();

            switch (declaredType)
            {
                case "boolean":
                    if (!TryParseBool(text, out var flag))
                    {
                        reason = "expected a boolean, got '" + text + "'";

                        return false;
                    }

                    coerced = new JValue(flag);
                    break;

                case "integer":
                    if (!int.TryParse(text, out var whole))
                    {
                        reason = "expected an integer, got '" + text + "'";

                        return false;
                    }

                    coerced = new JValue(whole);
                    break;

                case "number":
                    if (!double.TryParse(text, out var number))
                    {
                        reason = "expected a number, got '" + text + "'";

                        return false;
                    }

                    coerced = new JValue(number);
                    break;

                default:
                    coerced = new JValue(text ?? string.Empty);
                    break;
            }

            if (definition["enum"] is JArray allowed && allowed.Any())
            {
                var permitted = allowed.Select(v => v.ToString()).ToList();
                var candidate = coerced.ToString();

                if (!permitted.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    reason = "'" + candidate + "' is not one of " + string.Join(", ", permitted);

                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Maps the module's <c>template</c> setting — a portal-relative path to the template
        /// file — to the sibling <c>template-schema.json</c> on disk.
        /// </summary>
        private static string ResolveSchemaPath(string templateSetting)
        {
            try
            {
                var relative = templateSetting.Replace('\\', '/').TrimStart('/', '~');
                var mapped = HostingEnvironment.MapPath("~/" + relative);

                if (string.IsNullOrEmpty(mapped))
                {
                    return null;
                }

                var directory = Path.GetDirectoryName(mapped);

                return directory == null ? null : Path.Combine(directory, "template-schema.json");
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool TryParseBool(string text, out bool value)
        {
            value = false;

            if (text == null)
            {
                return false;
            }

            switch (text.Trim().ToLowerInvariant())
            {
                case "true":
                case "1":
                case "yes":
                    value = true;

                    return true;
                case "false":
                case "0":
                case "no":
                    value = false;

                    return true;
                default:
                    return false;
            }
        }
    }
}
