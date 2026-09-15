using Dnn.Mcp.WebApi;
using Dnn.Mcp.WebApi.Models;
using Dnn.Mcp.WebApi.Services;
using DotNetNuke.Entities.Modules;
using DotNetNuke.Entities.Modules.Prompt;
using DotNetNuke.Entities.Portals;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Satrabel.AIChat.Tools;
using Satrabel.OpenContent.Components;
using Satrabel.OpenContent.Components.Datasource;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Satrabel.OpenContentMcp.Tools
{
    public class ManageTemplatesTool : IMcpProvider
    {
        public void Register(IMcpRegistry registry)
        {

            // Register list-opencontent-templates tool
            registry.RegisterTool(new ToolDefinition
            {
                Name = "list-opencontent-templates",
                Title = "List OpenContent templates",
                Description = "List all available OpenContent templates in the portal",
                Category = "Open Content",
                Parameters = new List<ToolParameter>(),
                ReadOnly = true,
                Handler = (arguments) =>
                {
                    try
                    {
                        var templates = ListTemplates();

                        return new CallToolResult
                        {
                            Content = new List<ContentBlock>
                            {
                                new TextContentBlock
                                {
                                    Text = JsonConvert.SerializeObject(templates, Formatting.Indented)
                                }
                            }
                        };
                    }
                    catch (Exception ex)
                    {
                        return new CallToolResult
                        {
                            IsError = true,
                            Content = new List<ContentBlock>
                            {
                                new TextContentBlock
                                {
                                    Text = $"Error listing templates: {ex.Message}"
                                }
                            }
                        };
                    }
                }
            });

            // Register get-opencontent-template tool
            registry.RegisterTool(new ToolDefinition
            {
                Name = "get-opencontent-template",
                Title = "Get OpenContent Template",
                Description = "Get details of a specific OpenContent Template",
                Category = "Open Content",
                ReadOnly = true,
                Parameters = new List<ToolParameter>
                {
                    new ToolParameter
                    {
                        Name = "name",
                        Description = "The name of the OpenContent template",
                        Required = true,
                        Type = "string"
                    }
                },
                Handler = (arguments) =>
                {
                    try
                    {
                        var templatePath = arguments["name"].ToString();
                        var templateDetails = GetTemplate(templatePath);

                        return new CallToolResult
                        {
                            Content = new List<ContentBlock>
                            {
                                new TextContentBlock
                                {
                                    Text = JsonConvert.SerializeObject(templateDetails, Formatting.Indented)
                                }
                            }
                        };
                    }
                    catch (Exception ex)
                    {
                        return new CallToolResult
                        {
                            IsError = true,
                            Content = new List<ContentBlock>
                            {
                                new TextContentBlock
                                {
                                    Text = $"Error getting template: {ex.Message}"
                                }
                            }
                        };
                    }
                }
            });

            // Register create-opencontent-template tool
            registry.RegisterTool(new ToolDefinition
            {
                Name = "save-opencontent-template",
                Title = "Save OpenContent Template",
                Description = "Save a new OpenContent template",
                Category = "Open Content",
                Parameters = new List<ToolParameter>
                {
                    new ToolParameter
                    {
                        Name = "name",
                        Description = "The name of the template",
                        Required = true,
                        Type = "string"
                    },
                    new ToolParameter
                    {
                        Name = "template",
                        Description = "The content of the template (HTML/Handlebars)",
                        Required = false,
                        Type = "string"
                    },
                    new ToolParameter
                    {
                        Name = "schema",
                        Description = "The content of the schema",
                        Required = false,
                        Type = "string"
                    },
                    new ToolParameter
                    {
                        Name = "options",
                        Description = "The content of the options",
                        Required = false,
                        Type = "string"
                    },
                    new ToolParameter
                    {
                        Name = "data",
                        Description = "The content of the data",
                        Required = false,
                        Type = "string"
                    },
                    new ToolParameter
                    {
                        Name = "css",
                        Description = "The content of the CSS",
                        Required = false,
                        Type = "string"
                    },
                    new ToolParameter
                    {
                        Name = "js",
                        Description = "The content of the JS",
                        Required = false,
                        Type = "string"
                    }
                },
                Handler = (arguments) =>
                {
                    try
                    {
                        var name = arguments["name"].ToString();
                        var template = arguments.ContainsKey("template") ? arguments["template"].ToString() : null;
                        var schema = arguments.ContainsKey("schema") ? arguments["schema"].ToString() : null;
                        var options = arguments.ContainsKey("options") ? arguments["options"].ToString() : null;
                        var data = arguments.ContainsKey("data") ? arguments["data"].ToString() : null;
                        var css = arguments.ContainsKey("css") ? arguments["css"].ToString() : null;
                        var js = arguments.ContainsKey("js") ? arguments["js"].ToString() : null;

                        var result = SaveTemplate(name, template, schema, options, data, css, js);

                        return new CallToolResult
                        {
                            Content = new List<ContentBlock>
                            {
                                new TextContentBlock
                                {
                                    Text = result
                                }
                            }
                        };
                    }
                    catch (Exception ex)
                    {
                        return new CallToolResult
                        {
                            IsError = true,
                            Content = new List<ContentBlock>
                            {
                                new TextContentBlock
                                {
                                    Text = $"Error saving template: {ex.Message}"
                                }
                            }
                        };
                    }
                }
            });

        }
        private List<string> ListTemplates()
        {
            var portalId = PortalSettings.Current.PortalId;
            var portalPath = PortalSettings.Current.HomeDirectoryMapPath;
            var openContentPath = Path.Combine(portalPath, "OpenContent", "Templates");

            if (!Directory.Exists(openContentPath))
            {
                return new List<string>();
            }

            var templates = new List<string>();
            var templateDirs = Directory.GetDirectories(openContentPath);

            foreach (var dir in templateDirs)
            {
                var dirInfo = new DirectoryInfo(dir);
                var templateFiles = Directory.GetFiles(dir, "*.hbs").Concat(Directory.GetFiles(dir, "*.chtml")).ToList();

                templates.Add(dirInfo.Name);
            }

            return templates;
        }

        private TemplateInfo GetTemplate(string name)
        {
            var portalId = PortalSettings.Current.PortalId;
            var portalPath = PortalSettings.Current.HomeDirectoryMapPath;
            var fullPath = Path.Combine(portalPath, "OpenContent", "Templates", name);

            // Check for associated schema and options files
            var dataPath = Path.Combine(fullPath, "data.json");
            var schemaPath = Path.Combine(fullPath, "schema.json");
            var optionsPath = Path.Combine(fullPath, "options.json");
            var templatePath = Path.Combine(fullPath, "template.hbs");
            var cssPath = Path.Combine(fullPath, "template.css");
            var jsPath = Path.Combine(fullPath, "template.js");

            string schemaContent = null;
            string optionsContent = null;
            string templateContent = null;
            string cssContent = null;
            string jsContent = null;

            string dataContent = null;
            if (File.Exists(dataPath))
            {
                dataContent = File.ReadAllText(dataPath);
            }
            if (File.Exists(schemaPath))
            {
                schemaContent = File.ReadAllText(schemaPath);
            }

            if (File.Exists(optionsPath))
            {
                optionsContent = File.ReadAllText(optionsPath);
            }
            if (File.Exists(templatePath))
            {
                templateContent = File.ReadAllText(templatePath);
            }
            if (File.Exists(cssPath))
            {
                cssContent = File.ReadAllText(cssPath);
            }
            if (File.Exists(jsPath))
            {
                jsContent = File.ReadAllText(jsPath);
            }

            return new TemplateInfo
            {
                Name = name,
                //FullPath = fullPath,
                Data = dataContent,
                Schema = schemaContent,
                Options = optionsContent,
                Template = templateContent,
                CSS = cssContent,
                JS = jsContent
            };
        }

        private string SaveTemplate(string name, string template, string schema, string options, string data, string css, string js)
        {
            var portalPath = PortalSettings.Current.HomeDirectoryMapPath;
            var openContentPath = Path.Combine(portalPath, "OpenContent", "Templates", name);

            if (!Directory.Exists(openContentPath))
            {
                Directory.CreateDirectory(openContentPath);
            }


            var templatePath = Path.Combine(openContentPath, "template.hbs");
            var cssPath = Path.Combine(openContentPath, "template.css");
            var jsPath = Path.Combine(openContentPath, "template.js");
            var dataPath = Path.Combine(openContentPath, "data.json");
            var schemaPath = Path.Combine(openContentPath, "schema.json");
            var optionsPath = Path.Combine(openContentPath, "options.json");

            File.WriteAllText(templatePath, template);
            File.WriteAllText(cssPath, css);
            File.WriteAllText(jsPath, js);
            File.WriteAllText(dataPath, data);
            File.WriteAllText(schemaPath, schema);
            File.WriteAllText(optionsPath, options);
            return $"Template '{name}' saved successfully";
        }

        private string DeleteTemplate(string name)
        {
            var portalPath = PortalSettings.Current.HomeDirectoryMapPath;
            var openContentPath = Path.Combine(portalPath, "OpenContent", "Templates", name);

            if (Directory.Exists(openContentPath))
            {
                Directory.Delete(openContentPath, true);
            }

            return $"Template '{name}' deleted successfully";
        }
    }
}
