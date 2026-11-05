using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Dnn.Mcp.WebApi;
using Dnn.Mcp.WebApi.Models;
using Dnn.Mcp.WebApi.Services;
using DotNetNuke.Common.Utilities;
using DotNetNuke.Entities.Modules;
using DotNetNuke.Entities.Portals;
using DotNetNuke.Entities.Tabs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Satrabel.AIChat.Tools;

namespace KGroup.LiveFormsMcp.Tools
{
    /// <summary>
    /// Live Forms write tools: <c>copy-liveform</c> and <c>add-liveform</c>.
    /// </summary>
    /// <remarks>
    /// Both write into an existing Live Forms module (add one with <c>add-module</c>
    /// first) and refuse a target that already has submissions: submission values are
    /// keyed by field GUID, so replacing the form would orphan them.
    /// </remarks>
    public class FormWriteTools : IMcpProvider
    {
        private const string Category = "LiveForms";

        /// <summary>The form style every form on the reference site uses.</summary>
        private const string DefaultStyle =
            "{\"Theme\":\"Default\",\"BackgroundColor\":\"#FFFFFF\",\"FormWidth\":0,\"MobileUnder\":768,"
            + "\"Submissions\":true,\"ShowNavigation\":false,\"Bullets\":false,\"Numbering\":false,"
            + "\"PageTitles\":false,\"Percentage\":false,\"XofY\":false,"
            + "\"Font\":{\"Name\":\"Theme\",\"Color\":\"\",\"Size\":0},"
            + "\"Label\":{\"Align\":\"Top\",\"Text\":\"Click to edit\",\"Width\":\"0\"}}";

        private static readonly string[] FieldTypes =
        {
            "textbox", "email", "phone", "textarea", "dropdown", "radio", "checkbox", "fullname", "heading", "html",
        };

        /// <inheritdoc />
        public void Register(IMcpRegistry registry)
        {
            registry.RegisterTool(new ToolDefinition
            {
                Name = "copy-liveform",
                Title = "Copy Live Form",
                Description =
                    "Copy a Live Forms form (fields, actions, settings) from one module to another "
                    + "Live Forms module on this portal. The target must have no submissions. "
                    + "Optionally replace the fixed recipient address of the copied email actions.",
                Category = Category,
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("sourceModuleId", "int", "Module ID of the form to copy.", true),
                    ToolKit.Param("targetModuleId", "int", "Live Forms module ID to copy into (add one with add-module, moduleName 'LiveForms').", true),
                    ToolKit.Param("notifyEmail", "string", "Replace the recipient of every email action sent to a fixed address (not to the submitter).", false),
                    ToolKit.Param("overwrite", "bool", "Replace a form the target already has. Default false.", false),
                },
                Handler = ToolKit.Guard("copy-liveform", CopyForm),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "add-liveform",
                Title = "Add Live Form",
                Description =
                    "Build a form in an existing Live Forms module with the usual actions: a "
                    + "notification email to notifyEmail (reply-to the submitter), an optional "
                    + "confirmation email to the submitter, and a thank-you message or redirect. "
                    + "Without fields it builds a contact form (Name, Email, Phone, Message). "
                    + "The target must have no submissions.",
                Category = Category,
                ReadOnly = false,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("moduleId", "int", "Live Forms module ID (add one with add-module, moduleName 'LiveForms').", true),
                    ToolKit.Param("notifyEmail", "string", "Address(es) that receive each submission; separate several with ';'.", true),
                    ToolKit.Param(
                        "fields",
                        "array",
                        "Fields in order. Each: {label, type: textbox|email|phone|textarea|dropdown|radio|checkbox|fullname|heading|html, "
                        + "required, options: [..] for dropdown/radio/checkbox, width: 1-12 grid columns (default 12; "
                        + "consecutive narrow fields share a row), token: name used in email tokens (default label), "
                        + "html: content for type html}. Omit for the standard contact form.",
                        false),
                    ToolKit.Param("intro", "string", "HTML shown above the fields.", false),
                    ToolKit.Param("subject", "string", "Notification subject. Default 'New <module title> submission'.", false),
                    ToolKit.Param("fromEmail", "string", "Sender address. Default the portal's email address.", false),
                    ToolKit.Param("emailContext", "string", "Sentence added after the opening line of the notification, e.g. 'They expressed interest in the managed IT case study.'", false),
                    ToolKit.Param("submissionType", "string", "Submission Type line of the notification. Default 'New inbound inquiry'.", false),
                    ToolKit.Param("thankYouMessage", "string", "HTML shown after submitting. Ignored when redirectUrl is set.", false),
                    ToolKit.Param("redirectUrl", "string", "Redirect here after submitting instead of showing a message.", false),
                    ToolKit.Param("confirmationMessage", "string", "HTML emailed to the submitter. Needs an email field. Omit for no confirmation.", false),
                    ToolKit.Param("confirmationSubject", "string", "Confirmation subject. Default 'Thank you for contacting [Portal:PortalName]'.", false),
                    ToolKit.Param("submitText", "string", "Submit button text. Default 'Submit'.", false),
                    ToolKit.Param("captcha", "bool", "Add a reCAPTCHA, reusing the keys of an existing form on this portal. Default false.", false),
                    ToolKit.Param("overwrite", "bool", "Replace a form the module already has. Default false.", false),
                },
                Handler = ToolKit.Guard("add-liveform", AddForm),
            });
        }

        private static CallToolResult CopyForm(Dictionary<string, object> args)
        {
            if (!ToolKit.TryInt(args, "sourceModuleId", out var sourceId) || !ToolKit.TryInt(args, "targetModuleId", out var targetId))
            {
                return ToolKit.Fail("sourceModuleId and targetModuleId are required.");
            }

            if (sourceId == targetId)
            {
                return ToolKit.Fail("sourceModuleId and targetModuleId must differ.");
            }

            var portalId = PortalSettings.Current.PortalId;
            if (FormsDb.ResolveForm(portalId, sourceId, out var error) == null)
            {
                return ToolKit.Fail(error);
            }

            var target = FormsDb.ResolveForm(portalId, targetId, out error);
            if (target == null)
            {
                return ToolKit.Fail(error);
            }

            var source = FormsDb.LoadForm(sourceId);
            if (source == null)
            {
                return ToolKit.Fail("Module " + sourceId + " has no form to copy.");
            }

            var blocked = CheckTarget(targetId, ToolKit.Bool(args, "overwrite", false));
            if (blocked != null)
            {
                return ToolKit.Fail(blocked);
            }

            var actions = source.Actions ?? string.Empty;
            var notify = ToolKit.Str(args, "notifyEmail")?.Trim();
            var replaced = 0;
            if (notify != null)
            {
                if (!FormsDb.LooksLikeEmailList(notify))
                {
                    return ToolKit.Fail("notifyEmail is not a valid address list.");
                }

                actions = ActionsXml.ReplaceStaticRecipients(actions, notify, out replaced);
            }

            var settings = FormsDb.LoadSettings(sourceId);
            FormsDb.InTransaction(ctx =>
            {
                Save(ctx, targetId, source.FormDefinition, actions, source.Localization);
                FormsDb.Exec(ctx, "DELETE FROM {databaseOwner}[{objectQualifier}LiveForms_Setting] WHERE ModuleID = @0", targetId);
                foreach (var s in settings)
                {
                    FormsDb.Exec(
                        ctx,
                        "INSERT INTO {databaseOwner}[{objectQualifier}LiveForms_Setting] (ModuleID, Identifier, Name, Value) VALUES (@0, @1, @2, @3)",
                        targetId,
                        s.Identifier,
                        s.Name,
                        s.Value ?? string.Empty);
                }
            });
            ClearCache(target);

            return ToolKit.Ok(
                "Copied form " + sourceId + " into module " + targetId + " on page " + target.TabID
                + " (" + FormsDb.InputFields(FormsDb.Fields(source.FormDefinition)).Count() + " input fields, "
                + settings.Count + " settings)."
                + (notify == null ? string.Empty : " Recipient replaced in " + replaced + " email action(s).")
                + " Review the actions with get-liveform.");
        }

        private static CallToolResult AddForm(Dictionary<string, object> args)
        {
            var settings = PortalSettings.Current;
            if (!ToolKit.TryInt(args, "moduleId", out var moduleId))
            {
                return ToolKit.Fail("moduleId is required.");
            }

            var module = FormsDb.ResolveForm(settings.PortalId, moduleId, out var error);
            if (module == null)
            {
                return ToolKit.Fail(error);
            }

            var notify = ToolKit.Str(args, "notifyEmail")?.Trim();
            if (!FormsDb.LooksLikeEmailList(notify))
            {
                return ToolKit.Fail("notifyEmail is required and must be an address or ';'-separated addresses.");
            }

            var from = ToolKit.Str(args, "fromEmail")?.Trim() ?? settings.Email;
            if (!FormsDb.LooksLikeEmailList(from) || from.Contains(";") || from.Contains(","))
            {
                return ToolKit.Fail("fromEmail must be one address (the portal has none set, so pass fromEmail).");
            }

            var blocked = CheckTarget(moduleId, ToolKit.Bool(args, "overwrite", false));
            if (blocked != null)
            {
                return ToolKit.Fail(blocked);
            }

            JArray specs;
            try
            {
                specs = FormsDb.JsonArray(args, "fields") ?? DefaultContactFields();
            }
            catch (JsonException ex)
            {
                return ToolKit.Fail("fields is not a valid JSON array: " + ex.Message);
            }

            var fields = new JArray();
            var intro = ToolKit.Str(args, "intro");
            if (intro != null)
            {
                fields.Add(HtmlField(intro, 12));
            }

            foreach (var spec in specs)
            {
                if (!(spec is JObject s))
                {
                    return ToolKit.Fail("Each entry in fields must be an object with at least a label.");
                }

                var field = BuildField(s, out error);
                if (field == null)
                {
                    return ToolKit.Fail(error);
                }

                if (fields.OfType<JObject>().Any(f => f["UniqueToken"] != null && string.Equals((string)f["UniqueToken"], (string)field["UniqueToken"], StringComparison.OrdinalIgnoreCase)))
                {
                    return ToolKit.Fail("Two fields share the token '" + field["UniqueToken"] + "'; set a distinct token on one.");
                }

                fields.Add(field);
            }

            var inputs = FormsDb.InputFields(fields).ToList();
            if (inputs.Count == 0)
            {
                return ToolKit.Fail("The form needs at least one input field.");
            }

            if (ToolKit.Bool(args, "captcha", false))
            {
                var captcha = CaptchaFromPortal(settings.PortalId);
                if (captcha == null)
                {
                    return ToolKit.Fail("captcha: no form on this portal has a reCAPTCHA to take the keys from. Add one in the Live Forms editor.");
                }

                fields.Add(captcha);
            }

            fields.Add(new JObject
            {
                ["IResponse"] = false,
                ["Type"] = "Submitbutton",
                ["DisplayName"] = "Submit Button",
                ["GUID"] = NewGuid(),
                ["Class"] = string.Empty,
                ["Align"] = "Left",
                ["ImageUrl"] = string.Empty,
                ["ShowPrintButton"] = false,
                ["ShowResetButton"] = false,
                ["Text"] = ToolKit.Str(args, "submitText") ?? "Submit",
                ["SubmitTitle"] = "Page Title",
                ["ColSpan"] = 12,
            });
            Layout(fields);

            var emailField = inputs.FirstOrDefault(f => (string)f["Type"] == "Email");
            var emailToken = emailField == null ? null : "[" + emailField["GUID"] + "]";
            var confirmation = ToolKit.Str(args, "confirmationMessage");
            if (confirmation != null && emailToken == null)
            {
                return ToolKit.Fail("confirmationMessage needs an email-type field to send to.");
            }

            var actions = new List<XElement>
            {
                ActionsXml.Email(
                    "SendToUs",
                    notify,
                    from,
                    emailToken,
                    ToolKit.Str(args, "subject") ?? "New " + (module.ModuleTitle ?? "form") + " submission",
                    SummaryBody(module.ModuleTitle, inputs, settings, module, ToolKit.Str(args, "emailContext"), ToolKit.Str(args, "submissionType"))),
            };

            if (confirmation != null)
            {
                actions.Add(ActionsXml.Email(
                    "SendToUser",
                    emailToken,
                    from,
                    from,
                    ToolKit.Str(args, "confirmationSubject") ?? "Thank you for contacting [Portal:PortalName]",
                    confirmation));
            }

            var redirect = ToolKit.Str(args, "redirectUrl");
            actions.Add(redirect != null
                ? ActionsXml.Redirect(redirect)
                : ActionsXml.ShowMessage(ToolKit.Str(args, "thankYouMessage") ?? "<p><strong>Thank you!</strong> We'll be in touch soon.</p>"));

            var definition = new JObject
            {
                ["Fields"] = fields.ToString(Formatting.None),
                ["Style"] = JObject.Parse(DefaultStyle),
            };

            FormsDb.InTransaction(ctx => Save(ctx, moduleId, definition.ToString(Formatting.None), ActionsXml.Build(actions), null));
            ClearCache(module);

            return ToolKit.Ok(
                "Built form in module " + moduleId + " on page " + module.TabID + ": "
                + string.Join(", ", inputs.Select(f => (string)f["UniqueToken"] + " (" + f["Type"] + ((bool?)f["IsRequired"] == true ? ", required" : string.Empty) + ")"))
                + ". Actions: notification email" + (confirmation != null ? ", confirmation email" : string.Empty)
                + (redirect != null ? ", redirect" : ", thank-you message") + ".");
        }

        /// <summary>Returns why the target cannot be written, or null when it can.</summary>
        private static string CheckTarget(int moduleId, bool overwrite)
        {
            var submissions = FormsDb.SubmissionStats(new[] { moduleId });
            if (submissions.TryGetValue(moduleId, out var s) && s.Total > 0)
            {
                return "Module " + moduleId + " has " + s.Total + " submissions; replacing its form would orphan them. Add a new Live Forms module instead.";
            }

            var existing = FormsDb.LoadForm(moduleId);
            if (!overwrite && existing != null && FormsDb.InputFields(FormsDb.Fields(existing.FormDefinition)).Any())
            {
                return "Module " + moduleId + " already has a form. Pass overwrite=true to replace it.";
            }

            return null;
        }

        private static void Save(DotNetNuke.Data.IDataContext ctx, int moduleId, string definition, string actions, string localization)
        {
            FormsDb.Exec(
                ctx,
                "IF EXISTS (SELECT 1 FROM {databaseOwner}[{objectQualifier}LiveForms] WHERE ModuleId = @0) "
                + "UPDATE {databaseOwner}[{objectQualifier}LiveForms] SET FormDefinition = @1, Actions = @2, Localization = @3 WHERE ModuleId = @0 "
                + "ELSE INSERT INTO {databaseOwner}[{objectQualifier}LiveForms] (ModuleId, FormDefinition, Actions, Localization) VALUES (@0, @1, @2, @3)",
                moduleId,
                definition,
                actions,
                localization);
        }

        /// <summary>
        /// Clears the page's output cache so the new form shows on the next request.
        /// </summary>
        /// <remarks>
        /// The <c>int</c> overload is obsolete from DNN 10.2.2 in favour of one taking
        /// <c>IHostSettings</c> (<c>DotNetNuke.Abstractions</c>, not referenced here), as with
        /// <c>FormsDb.Open</c>.
        /// </remarks>
        private static void ClearCache(ModuleInfo module)
        {
#pragma warning disable CS0618 // see remarks
            DataCache.ClearModuleCache(module.TabID);
#pragma warning restore CS0618
        }

        private static JArray DefaultContactFields()
        {
            return JArray.Parse(
                "[{\"label\":\"Name\",\"type\":\"textbox\",\"required\":true},"
                + "{\"label\":\"Email\",\"type\":\"email\",\"required\":true,\"width\":6},"
                + "{\"label\":\"Phone\",\"type\":\"phone\",\"width\":6},"
                + "{\"label\":\"Message\",\"type\":\"textarea\",\"required\":true}]");
        }

        private static string NewGuid()
        {
            return Guid.NewGuid().ToString().ToUpperInvariant();
        }

        private static JObject Labels(string label)
        {
            return new JObject
            {
                ["Label"] = new JObject { ["Align"] = "Top", ["Text"] = label, ["Width"] = "0" },
                ["SubLabel"] = new JObject { ["Text"] = string.Empty },
                ["Tooltip"] = new JObject { ["Text"] = string.Empty },
            };
        }

        private static JObject HtmlField(string html, int width)
        {
            return new JObject
            {
                ["IResponse"] = false,
                ["Type"] = "Text",
                ["DisplayName"] = "HTML",
                ["GUID"] = NewGuid(),
                ["Hide"] = false,
                ["Html"] = html,
                ["UniqueToken"] = "HTML",
                ["Class"] = string.Empty,
                ["ColSpan"] = width,
            };
        }

        /// <summary>
        /// Builds one field in the vendor's JSON shape, mirroring what its editor saves for
        /// each type.
        /// </summary>
        private static JObject BuildField(JObject spec, out string error)
        {
            error = null;
            var label = ((string)spec["label"])?.Trim();
            var type = (((string)spec["type"]) ?? "textbox").Trim().ToLowerInvariant();
            var width = (int?)spec["width"] ?? 12;
            if (width < 1 || width > 12)
            {
                error = "width must be 1-12 (field '" + label + "').";
                return null;
            }

            if (!FieldTypes.Contains(type))
            {
                error = "Unknown field type '" + type + "'. Use one of: " + string.Join(", ", FieldTypes) + ".";
                return null;
            }

            if (type == "html")
            {
                var html = (string)spec["html"] ?? (label == null ? null : "<p>" + WebUtility.HtmlEncode(label) + "</p>");
                if (html == null)
                {
                    error = "An html field needs html or label.";
                    return null;
                }

                return HtmlField(html, width);
            }

            if (string.IsNullOrEmpty(label))
            {
                error = "Every field needs a label.";
                return null;
            }

            if (type == "heading")
            {
                return new JObject
                {
                    ["IResponse"] = false,
                    ["Type"] = "Heading",
                    ["DisplayName"] = "Heading",
                    ["GUID"] = NewGuid(),
                    ["Hide"] = false,
                    ["Text"] = label,
                    ["SubHeading"] = string.Empty,
                    ["HeadingSize"] = "Default",
                    ["Class"] = string.Empty,
                    ["ColSpan"] = width,
                };
            }

            var field = new JObject
            {
                ["IResponse"] = true,
                ["GUID"] = NewGuid(),
                ["UniqueToken"] = ((string)spec["token"])?.Trim() ?? label,
                ["Hide"] = false,
                ["Class"] = string.Empty,
                ["IsRequired"] = (bool?)spec["required"] ?? false,
            };

            var options = (spec["options"] as JArray)?.Select(o => (string)o).Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
            var choice = type == "dropdown" || type == "radio" || type == "checkbox";
            if (choice && (options == null || options.Count == 0))
            {
                error = "Field '" + label + "' (" + type + ") needs options.";
                return null;
            }

            var optionMap = new JObject();
            if (options != null)
            {
                for (var i = 0; i < options.Count; i++)
                {
                    optionMap[i.ToString()] = options[i];
                }
            }

            var textValidation = new JObject
            {
                ["Watermark"] = new JObject { ["Text"] = (string)spec["placeholder"] ?? string.Empty },
                ["Validation"] = new JObject { ["RegEx"] = string.Empty, ["Validate"] = type == "email" ? "Email" : "None" },
            };

            switch (type)
            {
                case "textbox":
                case "email":
                    field["Type"] = type == "email" ? "Email" : "Textbox";
                    field["DisplayName"] = type == "email" ? "Email" : "Textbox";
                    field["DefaultValue"] = string.Empty;
                    field["Width"] = 0;
                    field["MaxLength"] = 0;
                    field["MaskValue"] = string.Empty;
                    field.Merge(textValidation);
                    break;
                case "phone":
                    field["Type"] = "Phone";
                    field["DisplayName"] = "Phone";
                    field["InputMask"] = false;
                    field["MaskValue"] = "(000) 000-0000";
                    field["AreaCodeText"] = "Area Code";
                    field["PhoneNumberText"] = string.Empty;
                    field["Watermark"] = textValidation["Watermark"];
                    break;
                case "textarea":
                    field["Type"] = "Textarea";
                    field["DisplayName"] = "Textarea";
                    field["Columns"] = 0;
                    field["Rows"] = "100";
                    field["MaxLength"] = 0;
                    field["DefaultValue"] = string.Empty;
                    field["EntryLimit"] = "None";
                    field.Merge(textValidation);
                    break;
                case "fullname":
                    field["Type"] = "Fullname";
                    field["DisplayName"] = "Fullname";
                    field["DefaultValue"] = string.Empty;
                    field["Width"] = 150;
                    field["MaxLength"] = 0;
                    field["Prefix"] = false;
                    field["Suffix"] = false;
                    field["MiddleName"] = false;
                    field["PrefixText"] = "Prefix";
                    field["FirstNameText"] = "First Name";
                    field["MiddleNameText"] = "Middle Name";
                    field["LastNameText"] = "Last Name";
                    field["SuffixText"] = "Suffix";
                    field.Merge(textValidation);
                    break;
                case "dropdown":
                    field["Type"] = "Dropdown";
                    field["DisplayName"] = "Dropdown";
                    field["DefaultValue"] = string.Empty;
                    field["SelectedOption"] = string.Empty;
                    field["Width"] = 0;
                    field["Size"] = 0;
                    field["Options"] = optionMap;
                    field["Special"] = "None";
                    field["SelectedList"] = string.Empty;
                    field["ListName"] = string.Empty;
                    break;
                default: // radio, checkbox
                    var radio = type == "radio";
                    field["Type"] = radio ? "Radiobutton" : "Checkbox";
                    field["DisplayName"] = radio ? "Radiobutton" : "Checkbox";
                    field["SelectedOption"] = radio ? (JToken)string.Empty : new JArray();
                    field["AllowOther"] = false;
                    field["Columns"] = 1;
                    field["Options"] = optionMap;
                    field["SelectedList"] = string.Empty;
                    field["ListName"] = string.Empty;
                    field["OtherText"] = "Other";
                    break;
            }

            field.Merge(Labels(label));
            field["ColSpan"] = width;

            return field;
        }

        /// <summary>
        /// Assigns <c>RowIndex</c>/<c>ColOffset</c>: consecutive fields share a row while their
        /// <c>ColSpan</c> totals 12 or less.
        /// </summary>
        private static void Layout(JArray fields)
        {
            var row = -1;
            var used = 12;
            foreach (var f in fields.OfType<JObject>())
            {
                var span = (int?)f["ColSpan"] ?? 12;
                if (used + span > 12)
                {
                    row++;
                    used = 0;
                }

                used += span;
                f["RowIndex"] = row;
                f["ColOffset"] = 0;
            }
        }

        /// <summary>
        /// A reCAPTCHA field with the keys of an existing form on the portal. The keys are
        /// registered per domain, so they are taken from this portal's forms only.
        /// </summary>
        private static JObject CaptchaFromPortal(int portalId)
        {
            var ids = FormsDb.FormModules(portalId).Select(m => m.ModuleID).ToList();
            var source = FormsDb.LoadForms(ids)
                .SelectMany(f => FormsDb.Fields(f.FormDefinition).OfType<JObject>())
                .FirstOrDefault(f => (string)f["Type"] == "Captcha" && !string.IsNullOrEmpty((string)f["SecretKey"]));
            if (source == null)
            {
                return null;
            }

            var captcha = (JObject)source.DeepClone();
            captcha["GUID"] = NewGuid();
            captcha["ColSpan"] = 12;

            return captcha;
        }

        /// <summary>
        /// The notification body: a prose sentence naming the submitter, the field values as
        /// "Label: value" lines, then a source block (site, form, page link, submission type
        /// and ID). Mail filters score a bare table of tokens as spam far more often than
        /// this shape, which reads like a written message.
        /// </summary>
        private static string SummaryBody(string title, IEnumerable<JObject> inputs, PortalSettings settings, ModuleInfo module, string context, string submissionType)
        {
            var list = inputs.ToList();
            string Token(JObject f) => f == null ? null : "[" + f["GUID"] + "]";
            string Label(JObject f) => WebUtility.HtmlEncode((string)f["Label"]?["Text"] ?? (string)f["UniqueToken"]);
            JObject Find(params string[] names) => list.FirstOrDefault(f => names.Any(n =>
                string.Equals(Normalize((string)f["UniqueToken"]), n, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Normalize((string)f["Label"]?["Text"]), n, StringComparison.OrdinalIgnoreCase)));

            var full = Find("name", "fullname", "yourname");
            var first = Find("firstname", "first");
            var last = Find("lastname", "last", "surname");
            var name = full != null ? Token(full)
                : first != null ? Token(first) + (last != null ? " " + Token(last) : string.Empty)
                : null;
            var email = Token(list.FirstOrDefault(f => (string)f["Type"] == "Email"));

            var formName = WebUtility.HtmlEncode(title ?? "website");
            var sb = new StringBuilder();
            sb.Append("<table style=\"width: 95%;margin:5px;\"><tbody><tr><td colspan=\"2\"><p>The following ")
                .Append(formName).Append("&nbsp;form was submitted at&nbsp;[DateTime:Now]");
            if (name != null)
            {
                sb.Append("&nbsp;by ").Append(name);
            }

            if (email != null)
            {
                sb.Append(name != null ? " they" : ". They").Append(" can be reached at ").Append(email);
            }

            sb.Append('.');
            if (!string.IsNullOrWhiteSpace(context))
            {
                sb.Append(' ').Append(WebUtility.HtmlEncode(context.Trim()));
            }

            sb.Append("</p><p><strong>Contact Information</strong></p>");
            sb.Append(string.Join("<br />", list.Select(f => Label(f) + ": " + Token(f))));
            sb.Append("</td></tr><tr><td width=\"35%\"> </td><td> </td></tr></tbody></table><hr />");

            var tab = TabController.Instance.GetTab(module.TabID, settings.PortalId, false);
            sb.Append("<p><strong>Source:</strong>&nbsp;").Append(WebUtility.HtmlEncode(settings.PortalName))
                .Append("&nbsp;website ").Append(formName).Append(" form");
            if (tab != null)
            {
                var pageName = string.IsNullOrWhiteSpace(tab.Title) ? tab.TabName : tab.Title;
                sb.Append("&nbsp;<a href=\"").Append(WebUtility.HtmlEncode(tab.FullUrl)).Append("\">")
                    .Append(WebUtility.HtmlEncode(pageName)).Append("</a>");
            }

            sb.Append(".<br /><strong>Submission Type:</strong> ").Append(WebUtility.HtmlEncode(submissionType ?? "New inbound inquiry"))
                .Append("<br /><strong>Submission ID:</strong>&nbsp;[SubmissionID]&nbsp;</p>");

            return sb.ToString();
        }

        private static string Normalize(string s) =>
            s == null ? null : new string(s.Where(char.IsLetterOrDigit).ToArray());
    }
}
