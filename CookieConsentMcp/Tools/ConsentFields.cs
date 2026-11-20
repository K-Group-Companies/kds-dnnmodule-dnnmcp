using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace KGroup.CookieConsentMcp.Tools
{
    /// <summary>
    /// The writable columns of <c>EasyDNNCookieConsent_Settings</c>, keyed by the camelCase
    /// names the vendor's own JSON export uses, with the values the vendor accepts.
    /// </summary>
    /// <remarks>
    /// This list is an allowlist: only these columns are ever read back to a caller or
    /// written, so the encrypted OpenAI key and the identity/audit columns can never be
    /// touched through the tools. Allowed values were taken from the vendor assembly
    /// (Cookie Consent 3.0), which validates the same lists in its settings page.
    /// </remarks>
    internal static class ConsentFields
    {
        internal enum Kind
        {
            Bool,
            Int,
            Text,
        }

        internal sealed class Field
        {
            public Field(string name, Kind kind, bool nullable = false, bool siteSpecific = false, string[] allowed = null, int min = int.MinValue, int max = int.MaxValue, int maxLength = 0)
            {
                Name = name;
                Column = char.ToUpperInvariant(name[0]) + name.Substring(1);
                Kind = kind;
                Nullable = nullable;
                SiteSpecific = siteSpecific;
                Allowed = allowed;
                Min = min;
                Max = max;
                MaxLength = maxLength;
            }

            /// <summary>camelCase name used in tool arguments and in the vendor export.</summary>
            public string Name { get; }

            /// <summary>Column name in the settings table (the PascalCase form of <see cref="Name"/>).</summary>
            public string Column { get; }

            public Kind Kind { get; }

            public bool Nullable { get; }

            /// <summary>
            /// Values that identify one site (tracking IDs, page IDs, policy URL). Export omits
            /// them and import never copies them; set them with <c>set-cookie-consent</c>.
            /// </summary>
            public bool SiteSpecific { get; }

            public string[] Allowed { get; }

            public int Min { get; }

            public int Max { get; }

            /// <summary>Column length for text fields; 0 means nvarchar(max).</summary>
            public int MaxLength { get; }
        }

        private static readonly string[] Locations = { "head", "footer" };

        public static readonly IReadOnlyList<Field> All = new List<Field>
        {
            new Field("enabled", Kind.Bool),
            new Field("consentVersion", Kind.Text, maxLength: 50),
            new Field("cookieName", Kind.Text, maxLength: 100),
            new Field("cookieExpiryDays", Kind.Int, min: 1, max: 3650),
            new Field("bannerTitle", Kind.Text, maxLength: 500),
            new Field("bannerText", Kind.Text),
            new Field("acceptAllLabel", Kind.Text, maxLength: 100),
            new Field("rejectAllLabel", Kind.Text, maxLength: 100),
            new Field("managePreferencesLabel", Kind.Text, maxLength: 100),
            new Field("savePreferencesLabel", Kind.Text, maxLength: 100),
            new Field("cookieSettingsLabel", Kind.Text, maxLength: 100),
            new Field("policyUrl", Kind.Text, nullable: true, siteSpecific: true, maxLength: 500),
            new Field("privacyPolicyTabId", Kind.Int, nullable: true, siteSpecific: true),
            new Field("cookiePolicyTabId", Kind.Int, nullable: true, siteSpecific: true),
            new Field("consentLogRetentionDays", Kind.Int, min: 1, max: 3650),
            new Field("complianceMode", Kind.Text, allowed: new[] { "eu", "us", "strict-global" }),
            new Field("frontendTheme", Kind.Text, allowed: new[] { "modern-light", "modern-light-compact", "modern-dark", "modern-dark-compact" }),
            new Field("showReopenButton", Kind.Bool),
            new Field("reopenButtonPosition", Kind.Text, allowed: new[] { "bottom-left", "bottom-right", "top-left", "top-right" }),
            new Field("autoBlockKnownEmbedsEnabled", Kind.Bool),
            new Field("googleConsentModeEnabled", Kind.Bool),
            new Field("googleConsentModeType", Kind.Text, allowed: new[] { "basic", "advanced" }),
            new Field("googleConsentDebugEnabled", Kind.Bool),
            new Field("googleConsentUrlPassthroughEnabled", Kind.Bool),
            new Field("googleConsentAdsDataRedactionEnabled", Kind.Bool),
            new Field("googleConsentWaitForUpdateMilliseconds", Kind.Int, min: 0, max: 10000),
            new Field("googleTagManagerEnabled", Kind.Bool),
            new Field("googleTagManagerContainerId", Kind.Text, nullable: true, siteSpecific: true, maxLength: 50),
            new Field("googleTagManagerLocation", Kind.Text, allowed: Locations),
            new Field("googleTagManagerConsentWarningDismissed", Kind.Bool),
            new Field("googleAnalyticsEnabled", Kind.Bool),
            new Field("googleAnalyticsMeasurementId", Kind.Text, nullable: true, siteSpecific: true, maxLength: 50),
            new Field("googleAnalyticsLocation", Kind.Text, allowed: Locations),
            new Field("googleAdSenseEnabled", Kind.Bool),
            new Field("googleAdSensePublisherId", Kind.Text, nullable: true, siteSpecific: true, maxLength: 64),
            new Field("googleAdSenseAutoBlockingEnabled", Kind.Bool),
            new Field("googleAdSenseShowPlaceholder", Kind.Bool),
            new Field("googleAdSensePlaceholderText", Kind.Text, nullable: true, maxLength: 500),
        };

        private static readonly Dictionary<string, Field> ByName =
            All.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        private static readonly Regex GtmId = new Regex("^GTM-[A-Z0-9]{4,}$");
        private static readonly Regex Ga4Id = new Regex("^G-[A-Z0-9]{4,}$");
        private static readonly Regex AdSenseId = new Regex("^ca-pub-[0-9]{6,}$");

        public static Field Find(string name)
        {
            return name != null && ByName.TryGetValue(name, out var field) ? field : null;
        }

        /// <summary>
        /// Converts a JSON value to the column's CLR value, enforcing type, allowed values,
        /// range and length. Returns false with an error naming the field otherwise.
        /// </summary>
        public static bool TryConvert(Field field, JToken token, out object value, out string error)
        {
            value = null;
            error = null;

            if (token == null || token.Type == JTokenType.Null || (token.Type == JTokenType.String && string.IsNullOrWhiteSpace((string)token)))
            {
                if (field.Nullable)
                {
                    return true;
                }

                error = field.Name + " cannot be empty.";
                return false;
            }

            // ToString() would serialize an object or array into JSON text and store it as the value.
            if (token.Type == JTokenType.Object || token.Type == JTokenType.Array)
            {
                error = field.Name + " must be a single value, not an object or list.";
                return false;
            }

            switch (field.Kind)
            {
                case Kind.Bool:
                    if (token.Type == JTokenType.Boolean)
                    {
                        value = (bool)token;
                        return true;
                    }

                    if (bool.TryParse(token.ToString(), out var b))
                    {
                        value = b;
                        return true;
                    }

                    error = field.Name + " must be true or false.";
                    return false;

                case Kind.Int:
                    if (!int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    {
                        error = field.Name + " must be a whole number.";
                        return false;
                    }

                    if (i < field.Min || i > field.Max)
                    {
                        error = field.Name + " must be between " + field.Min + " and " + field.Max + ".";
                        return false;
                    }

                    value = i;
                    return true;

                default:
                    var text = token.ToString().Trim();
                    if (field.Allowed != null && !field.Allowed.Contains(text))
                    {
                        error = field.Name + " must be one of: " + string.Join(", ", field.Allowed) + ".";
                        return false;
                    }

                    if (field.MaxLength > 0 && text.Length > field.MaxLength)
                    {
                        error = field.Name + " is longer than " + field.MaxLength + " characters.";
                        return false;
                    }

                    if (field.Name == "googleTagManagerContainerId" && !GtmId.IsMatch(text))
                    {
                        error = "googleTagManagerContainerId must look like GTM-XXXXXXX.";
                        return false;
                    }

                    if (field.Name == "googleAnalyticsMeasurementId" && !Ga4Id.IsMatch(text))
                    {
                        error = "googleAnalyticsMeasurementId must be a GA4 measurement ID (G-XXXXXXXXXX).";
                        return false;
                    }

                    if (field.Name == "googleAdSensePublisherId" && !AdSenseId.IsMatch(text))
                    {
                        error = "googleAdSensePublisherId must look like ca-pub-0000000000000000.";
                        return false;
                    }

                    value = text;
                    return true;
            }
        }

        /// <summary>
        /// Checks the combination of settings as they will be after a write, so a tool never
        /// leaves the banner loading a tag it has no ID for, or loading two Google tags.
        /// </summary>
        public static List<string> CrossCheck(IDictionary<string, object> merged)
        {
            var errors = new List<string>();

            bool On(string name) => merged.TryGetValue(name, out var v) && v is bool flag && flag;
            bool Blank(string name) => !merged.TryGetValue(name, out var v) || v == null || string.IsNullOrWhiteSpace(v.ToString());

            if (On("googleTagManagerEnabled") && Blank("googleTagManagerContainerId"))
            {
                errors.Add("googleTagManagerEnabled needs googleTagManagerContainerId.");
            }

            if (On("googleAnalyticsEnabled") && Blank("googleAnalyticsMeasurementId"))
            {
                errors.Add("googleAnalyticsEnabled needs googleAnalyticsMeasurementId.");
            }

            if (On("googleAdSenseEnabled") && Blank("googleAdSensePublisherId"))
            {
                errors.Add("googleAdSenseEnabled needs googleAdSensePublisherId.");
            }

            if (On("googleTagManagerEnabled") && On("googleAnalyticsEnabled"))
            {
                errors.Add(
                    "googleAnalyticsEnabled and googleTagManagerEnabled are both on, which loads GA4 twice. "
                    + "Configure GA4 inside the GTM container and leave googleAnalyticsEnabled off.");
            }

            return errors;
        }

        /// <summary>Shows only the prefix and last two characters of a tracking ID.</summary>
        public static string Mask(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return id;
            }

            var dash = id.LastIndexOf('-');
            var prefix = dash >= 0 ? id.Substring(0, dash + 1) : string.Empty;
            var tail = id.Length - prefix.Length > 2 ? id.Substring(id.Length - 2) : string.Empty;

            return prefix + new string('*', Math.Max(3, id.Length - prefix.Length - tail.Length)) + tail;
        }
    }
}
