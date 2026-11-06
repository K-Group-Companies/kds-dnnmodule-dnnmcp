using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace KGroup.LiveFormsMcp.Tools
{
    /// <summary>
    /// Reads and writes the Live Forms <c>Actions</c> column.
    /// </summary>
    /// <remarks>
    /// The column holds XmlSerializer output of the vendor's action list, declared
    /// <c>encoding="utf-16"</c>: an <c>ArrayOfConditionalActions</c> whose
    /// <c>ConditionalActions</c> each carry one or more <c>anyType</c> actions (an
    /// <c>xsi:type</c> of <c>Email</c>, <c>ShowMessage</c>, <c>Redirect</c>, ...), a
    /// <c>ConditionSet</c> and a <c>GUID</c>. Field values are referenced as
    /// <c>[FIELD-GUID]</c> tokens. The ShowMessage text element really is spelled
    /// <c>Messsage</c> (three s) by the vendor; the spelling must be kept or the message
    /// deserializes empty.
    /// </remarks>
    internal static class ActionsXml
    {
        private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
        private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

        public static XDocument Parse(string actions)
        {
            return string.IsNullOrWhiteSpace(actions) ? null : XDocument.Parse(actions);
        }

        /// <summary>
        /// A readable summary of the actions: field tokens shown by name, addresses
        /// masked, long bodies truncated.
        /// </summary>
        public static List<object> Summarize(string actions, JArray fields)
        {
            var doc = Parse(actions);
            if (doc == null)
            {
                return new List<object>();
            }

            string Text(XElement e, string name)
            {
                var value = (string)e.Element(name);
                return string.IsNullOrEmpty(value) ? null : FormsDb.MaskEmails(FormsDb.ReadableTokens(value, fields));
            }

            string Short(string value) => value == null || value.Length <= 300 ? value : value.Substring(0, 300) + "...";

            return doc.Root.Elements("ConditionalActions").Select(group => (object)new
            {
                guid = (string)group.Element("GUID"),
                enabled = (bool?)group.Element("Enable") ?? true,
                runs = (string)group.Element("ConditionSet")?.Element("ExecuteCondition"),
                conditions = group.Element("ConditionSet")?.Element("Conditions")?.Elements().Count() ?? 0,
                actions = group.Element("Actions")?.Elements("anyType").Select(a => new
                {
                    type = (string)a.Attribute(Xsi + "type"),
                    name = (string)a.Element("ActionName"),
                    to = Text(a, "ToEmail"),
                    cc = Text(a, "Cc"),
                    bcc = Text(a, "Bcc"),
                    fromName = Text(a, "FromName"),
                    from = Text(a, "FromEmail"),
                    replyTo = Text(a, "ReplyToEmail"),
                    subject = Text(a, "Subject"),
                    body = Short(Text(a, "Body")),
                    message = Short(Text(a, "Messsage")),
                    url = Text(a, "URL"),
                }).ToList(),
            }).ToList();
        }

        /// <summary>Distinct action types, for the form list.</summary>
        public static List<string> Types(string actions)
        {
            var doc = Parse(actions);

            return doc == null
                ? new List<string>()
                : ActionElements(doc).Select(a => (string)a.Attribute(Xsi + "type")).Distinct().ToList();
        }

        /// <summary>
        /// The actions themselves. Conditions are <c>anyType</c> elements too
        /// (<c>EqualsTo</c>, <c>IsEmpty</c>, ...), so <c>Descendants("anyType")</c> would mix
        /// them in.
        /// </summary>
        private static IEnumerable<XElement> ActionElements(XDocument doc)
        {
            return doc.Root.Elements("ConditionalActions").Elements("Actions").Elements("anyType");
        }

        /// <summary>
        /// Replaces the literal address in every Email action whose recipient is a fixed
        /// address. Actions sent to a field token (the submitter) are left alone.
        /// </summary>
        public static string ReplaceStaticRecipients(string actions, string toEmail, out int changed)
        {
            changed = 0;
            var doc = Parse(actions);
            if (doc == null)
            {
                return actions;
            }

            foreach (var to in ActionElements(doc)
                .Where(a => (string)a.Attribute(Xsi + "type") == "Email")
                .Select(a => a.Element("ToEmail"))
                .Where(e => e != null && e.Value.Contains("@") && !e.Value.Contains("[")))
            {
                to.Value = toEmail;
                changed++;
            }

            return Serialize(doc);
        }

        public static XElement Email(string name, string to, string fromEmail, string replyTo, string subject, string bodyHtml)
        {
            return new XElement(
                "anyType",
                new XAttribute(Xsi + "type", "Email"),
                new XElement("Body", bodyHtml),
                new XElement("ReplaceDNNStandardTokens", "true"),
                new XElement("FromName", "[Portal:PortalName]"),
                new XElement("FromEmail", fromEmail),
                new XElement("ReplyToEmail", replyTo ?? string.Empty),
                new XElement("Subject", subject),
                new XElement("ActionType", "ServerSide"),
                new XElement("ToEmail", to),
                new XElement("Cc"),
                new XElement("Bcc"),
                new XElement("RoleID", "0"),
                new XElement("ActionName", name),
                new XElement("Attachments"));
        }

        public static XElement ShowMessage(string html)
        {
            return new XElement(
                "anyType",
                new XAttribute(Xsi + "type", "ShowMessage"),
                new XElement("Messsage", html),
                new XElement("ActionType", "ServerSide"),
                new XElement("ActionName", "ThankYou"));
        }

        public static XElement Redirect(string url)
        {
            return new XElement(
                "anyType",
                new XAttribute(Xsi + "type", "Redirect"),
                new XElement("URL", url),
                new XElement("ActionType", "ServerSide"),
                new XElement("ActionName", "ThankYouPage"));
        }

        /// <summary>
        /// Builds the column value: one always-run <c>ConditionalActions</c> per action, the
        /// shape the vendor UI saves when each action is added on its own.
        /// </summary>
        public static string Build(IEnumerable<XElement> actions)
        {
            var root = new XElement(
                "ArrayOfConditionalActions",
                new XAttribute(XNamespace.Xmlns + "xsd", Xsd),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
                actions.Select(a => new XElement(
                    "ConditionalActions",
                    new XElement("Actions", a),
                    new XElement(
                        "ConditionSet",
                        new XElement("Conditions"),
                        new XElement("ExecuteCondition", "Always"),
                        new XElement("MatchCondition", "All"),
                        new XElement("ExecuteStatus", "0"),
                        new XElement("Groups")),
                    new XElement("GUID", Guid.NewGuid().ToString()),
                    new XElement("Enable", "true"))));

            return Serialize(new XDocument(root));
        }

        /// <summary>
        /// Serializes through a <see cref="StringWriter"/>, whose UTF-16 encoding produces the
        /// same <c>encoding="utf-16"</c> declaration the vendor writes.
        /// </summary>
        private static string Serialize(XDocument doc)
        {
            using (var sw = new StringWriter())
            {
                using (var xw = XmlWriter.Create(sw, new XmlWriterSettings { Indent = true }))
                {
                    doc.Save(xw);
                }

                return sw.ToString();
            }
        }
    }
}
