using System;
using System.Collections.Generic;
using System.Linq;
using Dnn.Mcp.WebApi;
using Dnn.Mcp.WebApi.Models;
using Dnn.Mcp.WebApi.Services;
using DotNetNuke.Data;
using DotNetNuke.Entities.Modules;
using DotNetNuke.Entities.Portals;
using DotNetNuke.Entities.Tabs;
using DotNetNuke.Security.Permissions;
using Newtonsoft.Json;
using Satrabel.AIChat.Tools;

namespace KGroup.EasyDNNNewsMcp.Tools
{
    /// <summary>
    /// EasyDNNNews article tools: <c>get-news-articles</c>, <c>get-news-article</c>,
    /// <c>add-news-article</c> and <c>update-news-article</c>.
    /// </summary>
    /// <remarks>
    /// An article is one <c>EasyDNNNews</c> row plus side rows the module's own editor
    /// writes: category links, tag links, the <c>SocialSecurity</c> 'E' (everyone) key, a
    /// friendly-URL row in <c>EasyDNNnewsUrlNewProviderData</c>, a search-index row and a
    /// workflow log entry. <c>add-news-article</c> writes all of them in one transaction;
    /// an article missing its URL row or category link renders broken in the module.
    ///
    /// Bodies are stored HTML-encoded, as the module does, with a plain-text copy in
    /// <c>CleanArticleData</c> for search. Dates are UTC. The SEO/social templates
    /// (Open Graph, Twitter card, structured data, robots) are token templates, so new
    /// articles copy them from the portal's most recent article to keep the site's
    /// house settings; with no article to copy from, the column defaults apply.
    ///
    /// New articles are drafts (<c>Active</c>, <c>Approved</c> and <c>Published</c> off)
    /// unless <c>publish</c> is true. The status reported back is derived from those flags
    /// and the publish date: the module shows an article publicly when it is active and
    /// approved and its publish date has passed.
    ///
    /// Category membership decides where an article appears: a news module that does not
    /// show all categories lists only articles in its linked categories, so the tools warn
    /// when a new article would not be visible on its module.
    /// </remarks>
    public class NewsArticleTools : IMcpProvider
    {
        private const string Category = "EasyDNNNews";
        private const string DefaultRobots = "INDEX, FOLLOW, max-image-preview:large";
        private const int SummaryLength = 300;

        /// <inheritdoc />
        public void Register(IMcpRegistry registry)
        {
            registry.RegisterTool(new ToolDefinition
            {
                Name = "get-news-articles",
                Title = "Get news articles",
                Description =
                    "List EasyDNNNews articles on the current portal, newest first, with status "
                    + "(published, scheduled or draft), categories and URL. Filter by news module "
                    + "(articles in the categories it displays), category, status or a search "
                    + "term matched against the title and text.",
                Category = Category,
                ReadOnly = true,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("moduleId", "int", "Only articles shown by this news module.", false),
                    ToolKit.Param("categoryId", "int", "Only articles in this category.", false),
                    ToolKit.Param("status", "string", "Filter by status. scheduled is published with a future publishDate. Default all.", false, new[] { "all", "published", "scheduled", "draft" }),
                    ToolKit.Param("search", "string", "Text to find in the title or article text.", false),
                    ToolKit.Param("page", "int", "1-based page number. Default 1.", false),
                    ToolKit.Param("pageSize", "int", "Articles per page, 1-100. Default 20.", false),
                },
                Handler = ToolKit.Guard("get-news-articles", GetArticles),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "get-news-article",
                Title = "Get news article",
                Description =
                    "Get one EasyDNNNews article with its decoded HTML summary and content, "
                    + "categories, tags, main image, SEO fields, status and URL.",
                Category = Category,
                ReadOnly = true,
                Parameters = new List<ToolParameter>
                {
                    ToolKit.Param("articleId", "int", "Article ID.", true),
                },
                Handler = ToolKit.Guard("get-news-article", GetArticle),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "add-news-article",
                Title = "Add news article",
                Description =
                    "Create an EasyDNNNews article (blog post). Saved as a draft unless publish is "
                    + "true. content and summary are HTML. Categories and tags may be names or IDs; "
                    + "unknown tags are created, unknown categories are an error (use "
                    + "add-news-category first). The main image comes from imagePath (a file "
                    + "already in the portal) or imageUrl (a public image the server downloads). "
                    + "publishDate is ISO 8601; without an offset it is taken as UTC. Use this to "
                    + "migrate posts from another site (keep their original publishDate and "
                    + "slug) or to write a new post from a document.",
                Category = Category,
                ReadOnly = false,
                Parameters = ArticleParameters(true),
                Handler = ToolKit.Guard("add-news-article", AddArticle),
            });

            registry.RegisterTool(new ToolDefinition
            {
                Name = "update-news-article",
                Title = "Update news article",
                Description =
                    "Change an EasyDNNNews article. Only the fields passed are changed; pass an empty "
                    + "string to clear subTitle, summary, authorAlias, imageTitle or the SEO fields. "
                    + "publish true publishes it, false returns it to draft. categories and tags, "
                    + "when passed, replace the article's current ones. Changing slug updates the "
                    + "article's friendly URL.",
                Category = Category,
                ReadOnly = false,
                Parameters = ArticleParameters(false),
                Handler = ToolKit.Guard("update-news-article", UpdateArticle),
            });
        }

        private static List<ToolParameter> ArticleParameters(bool adding)
        {
            var list = new List<ToolParameter>();
            if (adding)
            {
                list.Add(ToolKit.Param("moduleId", "int", "News module the article belongs to. Optional when the portal has only one.", false));
            }
            else
            {
                list.Add(ToolKit.Param("articleId", "int", "Article ID.", true));
            }

            list.Add(ToolKit.Param("title", "string", "Article title.", adding));
            list.Add(ToolKit.Param("content", "string", "Article body as HTML.", adding));
            list.Add(ToolKit.Param("summary", "string", "Summary HTML shown in lists. Default: the first " + SummaryLength + " characters of the content.", false));
            list.Add(ToolKit.Param("subTitle", "string", "Subtitle.", false));
            list.Add(ToolKit.Param("publishDate", "string", "Publish date, ISO 8601. Default now. A future date schedules the article.", false));
            list.Add(ToolKit.Param("authorAlias", "string", "Author name to display instead of the posting user.", false));
            list.Add(ToolKit.Param("slug", "string", "URL slug. Default: made from the title.", false));
            list.Add(ToolKit.Param("categories", "array", "Category names or IDs." + (adding ? " Default: the module's single category, if it has exactly one." : string.Empty), false));
            list.Add(ToolKit.Param("tags", "array", "Tag names. Missing tags are created.", false));
            list.Add(ToolKit.Param("imagePath", "string", "Main image: portal-relative path of an existing file, e.g. Images/blog/hero.jpg.", false));
            list.Add(ToolKit.Param("imageUrl", "string", "Main image: public http(s) URL to download (jpg, png or gif, 10 MB max; WebP is refused because the module cannot thumbnail it).", false));
            list.Add(ToolKit.Param("imageTitle", "string", "Main image title / alt text.", false));
            list.Add(ToolKit.Param("showMainImage", "bool", "Show the main image on the article (detail) view. Default: true when an image is set.", false));
            list.Add(ToolKit.Param("metaDescription", "string", "Meta description.", false));
            list.Add(ToolKit.Param("metaKeywords", "string", "Meta keywords.", false));
            list.Add(ToolKit.Param("titleTag", "string", "HTML <title> override.", false));
            list.Add(ToolKit.Param("featured", "bool", "Mark as featured.", false));
            list.Add(ToolKit.Param("allowComments", "bool", "Allow comments.", false));
            list.Add(ToolKit.Param("publish", "bool", adding ? "Publish now instead of saving a draft. Default false." : "true publishes, false returns to draft.", false));

            return list;
        }

        internal class ArticleRow
        {
            public int ArticleID { get; set; }

            public string Title { get; set; }

            public string SubTitle { get; set; }

            public string Summary { get; set; }

            public string Article { get; set; }

            public string ArticleImage { get; set; }

            public string MainImageTitle { get; set; }

            public DateTime PublishDate { get; set; }

            public DateTime LastModified { get; set; }

            public string TitleLink { get; set; }

            public string AuthorAliasName { get; set; }

            public string MetaDecription { get; set; }

            public string MetaKeywords { get; set; }

            public string TitleTag { get; set; }

            public bool Featured { get; set; }

            public bool AllowComments { get; set; }

            public bool Active { get; set; }

            public bool Approved { get; set; }

            public bool Published { get; set; }

            public int? UrlTabID { get; set; }

            public int? UrlModuleID { get; set; }

            public int Total { get; set; }
        }

        internal class ArticleNameRow
        {
            public int ArticleID { get; set; }

            public int Id { get; set; }

            public string Name { get; set; }
        }

        internal class HouseTemplates
        {
            public string OpenGraphMetaTags { get; set; }

            public string TwitterCardMetaTags { get; set; }

            public string StructuredDataJSON { get; set; }

            public string AddRobotsFollowTag { get; set; }
        }

        private const string ArticleColumns =
            "a.ArticleID, a.Title, a.SubTitle, a.Summary, a.Article, a.ArticleImage, a.MainImageTitle, a.PublishDate, a.LastModified, "
            + "a.TitleLink, a.AuthorAliasName, a.MetaDecription, a.MetaKeywords, a.TitleTag, a.Featured, a.AllowComments, "
            + "a.Active, a.Approved, a.Published, "
            + "(SELECT TOP 1 u.TabID FROM {databaseOwner}[{objectQualifier}EasyDNNnewsUrlNewProviderData] u WHERE u.ArticleID = a.ArticleID AND u.LinkType = 0 ORDER BY u.ID) AS UrlTabID, "
            + "(SELECT TOP 1 u.ModuleID FROM {databaseOwner}[{objectQualifier}EasyDNNnewsUrlNewProviderData] u WHERE u.ArticleID = a.ArticleID AND u.LinkType = 0 ORDER BY u.ID) AS UrlModuleID";

        private static CallToolResult GetArticles(Dictionary<string, object> args)
        {
            var portalId = PortalSettings.Current.PortalId;
            var denied = NewsDb.CheckReadAccess(portalId);
            if (denied != null)
            {
                return ToolKit.Fail(denied);
            }

            var where = new List<string> { "a.PortalID = @0" };
            var sqlArgs = new List<object> { portalId };

            if (ToolKit.TryInt(args, "moduleId", out var moduleId))
            {
                var module = NewsDb.ResolveNewsModule(portalId, moduleId, out var error);
                if (module == null)
                {
                    return ToolKit.Fail(error);
                }

                if (!ModulePermissionController.CanEditModuleContent(module))
                {
                    return ToolKit.Fail("You do not have edit rights on news module " + module.ModuleID + ".");
                }

                if (!NewsCategoryTools.ShowAllCategories(new[] { moduleId })[moduleId])
                {
                    where.Add("EXISTS (SELECT 1 FROM {databaseOwner}[{objectQualifier}EasyDNNNewsCategories] c "
                        + "JOIN {databaseOwner}[{objectQualifier}EasyDNNNewsModuleCategoryItems] m ON m.CategoryID = c.CategoryID "
                        + "WHERE c.ArticleID = a.ArticleID AND m.ModuleID = @" + sqlArgs.Count + ")");
                    sqlArgs.Add(moduleId);
                }
            }

            if (ToolKit.TryInt(args, "categoryId", out var categoryId))
            {
                where.Add("EXISTS (SELECT 1 FROM {databaseOwner}[{objectQualifier}EasyDNNNewsCategories] c WHERE c.ArticleID = a.ArticleID AND c.CategoryID = @" + sqlArgs.Count + ")");
                sqlArgs.Add(categoryId);
            }

            var status = (ToolKit.Str(args, "status") ?? "all").ToLowerInvariant();
            if (status == "published")
            {
                // PublishDate is stored in UTC (see Status), so future-dated posts are "scheduled".
                where.Add("a.Active = 1 AND a.Approved = 1 AND a.PublishDate <= GETUTCDATE()");
            }
            else if (status == "scheduled")
            {
                where.Add("a.Active = 1 AND a.Approved = 1 AND a.PublishDate > GETUTCDATE()");
            }
            else if (status == "draft")
            {
                where.Add("NOT (a.Active = 1 AND a.Approved = 1)");
            }
            else if (status != "all")
            {
                return ToolKit.Fail("status must be all, published, scheduled or draft.");
            }

            var search = ToolKit.Str(args, "search");
            if (search != null)
            {
                var like = "%" + search.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
                where.Add("(a.Title LIKE @" + sqlArgs.Count + " OR a.CleanArticleData LIKE @" + sqlArgs.Count + ")");
                sqlArgs.Add(like);
            }

            var page = ToolKit.TryInt(args, "page", out var p) && p > 0 ? p : 1;
            var pageSize = ToolKit.TryInt(args, "pageSize", out var s) ? Math.Max(1, Math.Min(100, s)) : 20;

            var rows = NewsDb.Query<ArticleRow>(
                "SELECT " + ArticleColumns + ", COUNT(*) OVER () AS Total "
                + "FROM {databaseOwner}[{objectQualifier}EasyDNNNews] a WHERE " + string.Join(" AND ", where)
                + " ORDER BY a.PublishDate DESC, a.ArticleID DESC OFFSET " + ((page - 1) * pageSize) + " ROWS FETCH NEXT " + pageSize + " ROWS ONLY",
                sqlArgs.ToArray());

            // COUNT(*) OVER () rides on the page's rows, so a page past the end needs its own count.
            var total = rows.Count > 0 ? rows[0].Total
                : page == 1 ? 0
                : NewsDb.Scalar<int>("SELECT COUNT(*) FROM {databaseOwner}[{objectQualifier}EasyDNNNews] a WHERE " + string.Join(" AND ", where), sqlArgs.ToArray());

            var categories = ArticleCategories(rows.Select(r => r.ArticleID).ToList());
            var result = new
            {
                total,
                page,
                pageSize,
                articles = rows.Select(r => new
                {
                    id = r.ArticleID,
                    title = r.Title,
                    publishDate = r.PublishDate.ToString("u"),
                    status = Status(r),
                    featured = r.Featured,
                    categories = categories.Where(c => c.ArticleID == r.ArticleID).Select(c => c.Name),
                    slug = r.TitleLink,
                    url = ArticleUrl(r.UrlTabID, r.UrlModuleID, r.ArticleID, r.TitleLink),
                }),
            };

            return ToolKit.Ok(JsonConvert.SerializeObject(result, Formatting.Indented));
        }

        private static CallToolResult GetArticle(Dictionary<string, object> args)
        {
            if (!ToolKit.TryInt(args, "articleId", out var articleId))
            {
                return ToolKit.Fail("articleId is required and must be an integer.");
            }

            var portalId = PortalSettings.Current.PortalId;
            var denied = NewsDb.CheckReadAccess(portalId);
            if (denied != null)
            {
                return ToolKit.Fail(denied);
            }

            var row = LoadArticle(portalId, articleId);
            if (row == null)
            {
                return ToolKit.Fail("Article " + articleId + " does not exist on this portal.");
            }

            var ids = new List<int> { articleId };
            var result = new
            {
                id = row.ArticleID,
                title = row.Title,
                subTitle = row.SubTitle,
                status = Status(row),
                flags = new { active = row.Active, approved = row.Approved, published = row.Published },
                publishDate = row.PublishDate.ToString("u"),
                lastModified = row.LastModified.ToString("u"),
                authorAlias = row.AuthorAliasName,
                slug = row.TitleLink,
                url = ArticleUrl(row.UrlTabID, row.UrlModuleID, row.ArticleID, row.TitleLink),
                featured = row.Featured,
                allowComments = row.AllowComments,
                categories = ArticleCategories(ids).Select(c => new { id = c.Id, name = c.Name }),
                tags = ArticleTags(ids).Select(t => t.Name),
                image = string.IsNullOrEmpty(row.ArticleImage)
                    ? null
                    : new { path = "EasyDNNNews/" + row.ArticleID + "/" + row.ArticleImage, title = row.MainImageTitle },
                metaDescription = row.MetaDecription,
                metaKeywords = row.MetaKeywords,
                titleTag = row.TitleTag,
                summary = NewsDb.Decode(row.Summary),
                content = NewsDb.Decode(row.Article),
            };

            return ToolKit.Ok(JsonConvert.SerializeObject(result, Formatting.Indented));
        }

        private static CallToolResult AddArticle(Dictionary<string, object> args)
        {
            var settings = PortalSettings.Current;
            var portalId = settings.PortalId;

            int? requestedModule = null;
            if (ToolKit.TryInt(args, "moduleId", out var mid))
            {
                requestedModule = mid;
            }

            var module = NewsDb.ResolveNewsModule(portalId, requestedModule, out var moduleError);
            if (module == null)
            {
                return ToolKit.Fail(moduleError);
            }

            if (!ModulePermissionController.CanEditModuleContent(module))
            {
                return ToolKit.Fail("You do not have edit rights on news module " + module.ModuleID + ".");
            }

            var title = ToolKit.Str(args, "title")?.Trim();
            var content = ToolKit.Str(args, "content");
            if (title == null || content == null)
            {
                return ToolKit.Fail("title and content are required.");
            }

            var fields = ReadFields(args, false, out var fieldError);
            if (fieldError != null)
            {
                return ToolKit.Fail(fieldError);
            }

            var categoryIds = ResolveCategories(portalId, module, args, true, out var categoryError);
            if (categoryError != null)
            {
                return ToolKit.Fail(categoryError);
            }

            var tags = ReadTags(args, out var tagError);
            if (tagError != null)
            {
                return ToolKit.Fail(tagError);
            }

            var image = ReadImage(portalId, args, out var imageError);
            if (imageError != null)
            {
                return ToolKit.Fail(imageError);
            }

            var summaryHtml = ToolKit.Str(args, "summary") ?? "<p>" + NewsDb.Encode(NewsDb.Truncate(NewsDb.PlainText(content), SummaryLength)) + "</p>";
            var encodedSummary = NewsDb.Encode(summaryHtml);
            if (encodedSummary.Length > 4000)
            {
                return ToolKit.Fail("summary is too long: it must fit in 4,000 characters once HTML-encoded.");
            }

            if (title.Length > 800)
            {
                return ToolKit.Fail("title must be 800 characters or fewer.");
            }

            var publish = ToolKit.Bool(args, "publish", false);
            var publishDate = NewsDb.TryDate(args, "publishDate", out var pd) ? pd : DateTime.UtcNow;
            var house = NewsDb.Query<HouseTemplates>(
                "SELECT TOP 1 OpenGraphMetaTags, TwitterCardMetaTags, StructuredDataJSON, AddRobotsFollowTag "
                + "FROM {databaseOwner}[{objectQualifier}EasyDNNNews] WHERE PortalID = @0 ORDER BY ArticleID DESC",
                portalId).FirstOrDefault();

            var columns = new List<KeyValuePair<string, object>>
            {
                Col("PortalID", portalId),
                Col("UserID", settings.UserInfo.UserID),
                Col("Title", title),
                Col("SubTitle", fields.SubTitle),
                Col("Summary", encodedSummary),
                Col("Article", NewsDb.Encode(content)),
                Col("CleanArticleData", NewsDb.PlainText(content)),
                Col("PublishDate", publishDate),
                Col("ExpireDate", publishDate.AddYears(1000)),
                Col("NumberOfViews", 0),
                Col("RatingValue", 0),
                Col("RatingCount", 0),
                Col("DetailType", "Text"),
                Col("DetailTypeData", string.Empty),
                Col("DetailsTemplate", "DEFAULT"),
                Col("DetailsTheme", "DEFAULT"),
                Col("GalleryPosition", "bottom"),
                Col("GalleryDisplayType", "chameleon"),
                Col("CommentsTheme", "DEFAULT"),
                Col("ArticleImageFolder", "EasyDNNNews"),
                Col("NumberOfComments", 0),
                Col("MetaDecription", fields.MetaDescription),
                Col("MetaKeywords", fields.MetaKeywords),
                Col("DisplayStyle", "DEFAULT"),
                Col("DetailTarget", "_self"),
                Col("DetailMediaType", "Image"),
                Col("ListMediaType", "Image"),
                Col("AuthorAliasName", fields.AuthorAlias),
                Col("MainImageTitle", fields.ImageTitle),
                Col("Featured", fields.Featured ?? false),
                Col("AllowComments", fields.AllowComments ?? false),
                Col("Active", publish),
                Col("Approved", publish),
                Col("Published", publish),
                Col("ShowMainImage", fields.ShowMainImage ?? image != null),
                Col("ShowMainImageFront", true),
                Col("ArticleImageSet", false),
                Col("TitleTag", fields.TitleTag),
                Col("WorkflowId", 1),
                Col("AddRobotsFollowTag", house?.AddRobotsFollowTag ?? DefaultRobots),
            };

            // Omitted when there is nothing to copy, so the NOT NULL column defaults apply.
            if (house?.OpenGraphMetaTags != null)
            {
                columns.Add(Col("OpenGraphMetaTags", house.OpenGraphMetaTags));
            }

            if (house?.TwitterCardMetaTags != null)
            {
                columns.Add(Col("TwitterCardMetaTags", house.TwitterCardMetaTags));
            }

            if (house?.StructuredDataJSON != null)
            {
                columns.Add(Col("StructuredDataJSON", house.StructuredDataJSON));
            }

            var userId = settings.UserInfo.UserID;
            var slug = string.Empty;
            var articleId = NewsDb.InTransaction(ctx =>
            {
                // TitleLink has a non-empty CHECK constraint, so the slug is resolved before
                // the INSERT. ArticleID 0 never exists, so the uniqueness check excludes no row.
                slug = UniqueSlug(ctx, portalId, 0, fields.Slug ?? NewsDb.Slug(title));
                columns.Add(Col("TitleLink", slug));

                var id = NewsDb.Scalar<int>(
                    ctx,
                    "INSERT INTO {databaseOwner}[{objectQualifier}EasyDNNNews] (" + string.Join(", ", columns.Select(c => "[" + c.Key + "]")) + ") "
                    + "VALUES (" + string.Join(", ", columns.Select((c, i) => "@" + i)) + "); SELECT CAST(SCOPE_IDENTITY() AS int);",
                    columns.Select(c => c.Value).ToArray());

                InsertUrlRow(ctx, portalId, module, id, slug);
                WriteCategories(ctx, id, categoryIds);
                WriteTags(ctx, portalId, id, tags);
                NewsDb.Exec(ctx, "INSERT INTO {databaseOwner}[{objectQualifier}EasyDNNNewsSocialSecurity] (ArticleID, SecurityKey) VALUES (@0, N'E')", id);
                NewsDb.Exec(ctx, "INSERT INTO {databaseOwner}[{objectQualifier}EasyDNNNewsDNNSearchItems] (ModuleID, ArticleID) VALUES (@0, @1)", module.ModuleID, id);
                NewsDb.Exec(
                    ctx,
                    "INSERT INTO {databaseOwner}[{objectQualifier}EasyDNNNewsWorkflowStateArticleLog] (StateId, ArticleId, UserId, EditStatus, DateFinished) VALUES (1, @0, @1, 1, GETUTCDATE())",
                    id,
                    userId);

                return id;
            });

            var imageNote = SaveImage(portalId, articleId, image);
            var visibility = VisibilityWarning(module, categoryIds);

            return ToolKit.Ok(
                "Created article " + articleId + " '" + title + "' as " + (publish ? (publishDate > DateTime.UtcNow ? "scheduled for " + publishDate.ToString("u") : "published") : "a draft")
                + " in news module " + module.ModuleID + ". URL: " + ArticleUrl(module.TabID, module.ModuleID, articleId, slug)
                + imageNote + visibility);
        }

        private static CallToolResult UpdateArticle(Dictionary<string, object> args)
        {
            if (!ToolKit.TryInt(args, "articleId", out var articleId))
            {
                return ToolKit.Fail("articleId is required and must be an integer.");
            }

            var settings = PortalSettings.Current;
            var portalId = settings.PortalId;
            var row = LoadArticle(portalId, articleId);
            if (row == null)
            {
                return ToolKit.Fail("Article " + articleId + " does not exist on this portal.");
            }

            var module = row.UrlModuleID.HasValue
                ? NewsDb.NewsModules(portalId).FirstOrDefault(m => m.ModuleID == row.UrlModuleID.Value)
                : null;
            module = module ?? NewsDb.ResolveNewsModule(portalId, null, out _);
            if (module == null)
            {
                return ToolKit.Fail("Cannot tell which news module article " + articleId + " belongs to.");
            }

            if (!ModulePermissionController.CanEditModuleContent(module))
            {
                return ToolKit.Fail("You do not have edit rights on news module " + module.ModuleID + ".");
            }

            var fields = ReadFields(args, true, out var fieldError);
            if (fieldError != null)
            {
                return ToolKit.Fail(fieldError);
            }

            List<int> categoryIds = null;
            if (args.ContainsKey("categories"))
            {
                categoryIds = ResolveCategories(portalId, module, args, false, out var categoryError);
                if (categoryError != null)
                {
                    return ToolKit.Fail(categoryError);
                }
            }

            List<string> tags = null;
            if (args.ContainsKey("tags"))
            {
                tags = ReadTags(args, out var tagError);
                if (tagError != null)
                {
                    return ToolKit.Fail(tagError);
                }
            }

            var image = ReadImage(portalId, args, out var imageError);
            if (imageError != null)
            {
                return ToolKit.Fail(imageError);
            }

            var sets = new List<KeyValuePair<string, object>>();
            var changed = new List<string>();

            void Set(string column, object value, string label)
            {
                sets.Add(Col(column, value));
                if (!changed.Contains(label))
                {
                    changed.Add(label);
                }
            }

            var title = ToolKit.Str(args, "title")?.Trim();
            if (title != null)
            {
                if (title.Length > 800)
                {
                    return ToolKit.Fail("title must be 800 characters or fewer.");
                }

                Set("Title", title, "title");
            }

            var content = ToolKit.Str(args, "content");
            if (content != null)
            {
                Set("Article", NewsDb.Encode(content), "content");
                Set("CleanArticleData", NewsDb.PlainText(content), "content");
            }

            var summary = Clearable(args, "summary", true);
            if (summary != null)
            {
                var encoded = NewsDb.Encode(summary);
                if (encoded.Length > 4000)
                {
                    return ToolKit.Fail("summary is too long: it must fit in 4,000 characters once HTML-encoded.");
                }

                Set("Summary", encoded, "summary");
            }

            if (fields.SubTitle != null)
            {
                Set("SubTitle", fields.SubTitle, "subTitle");
            }

            if (NewsDb.TryDate(args, "publishDate", out var publishDate))
            {
                Set("PublishDate", publishDate, "publishDate");
                Set("ExpireDate", publishDate.AddYears(1000), "publishDate");
            }

            if (fields.AuthorAlias != null)
            {
                Set("AuthorAliasName", fields.AuthorAlias, "authorAlias");
            }

            if (fields.MetaDescription != null)
            {
                Set("MetaDecription", fields.MetaDescription, "metaDescription");
            }

            if (fields.MetaKeywords != null)
            {
                Set("MetaKeywords", fields.MetaKeywords, "metaKeywords");
            }

            if (fields.TitleTag != null)
            {
                Set("TitleTag", fields.TitleTag, "titleTag");
            }

            if (fields.ImageTitle != null)
            {
                Set("MainImageTitle", fields.ImageTitle, "imageTitle");
            }

            if (fields.ShowMainImage.HasValue)
            {
                Set("ShowMainImage", fields.ShowMainImage.Value, "showMainImage");
            }
            else if (image != null)
            {
                // A new image is meant to be seen; without this the detail view hides it.
                Set("ShowMainImage", true, "showMainImage");
            }

            if (fields.Featured.HasValue)
            {
                Set("Featured", fields.Featured.Value, "featured");
            }

            if (fields.AllowComments.HasValue)
            {
                Set("AllowComments", fields.AllowComments.Value, "allowComments");
            }

            if (args.ContainsKey("publish"))
            {
                var publish = ToolKit.Bool(args, "publish", false);
                Set("Active", publish, publish ? "published" : "unpublished");
                Set("Approved", publish, publish ? "published" : "unpublished");
                Set("Published", publish, publish ? "published" : "unpublished");
            }

            if (sets.Count == 0 && fields.Slug == null && categoryIds == null && tags == null && image == null)
            {
                return ToolKit.Fail("Nothing to change: pass at least one field to update.");
            }

            var slug = row.TitleLink;
            NewsDb.InTransaction(ctx =>
            {
                if (sets.Count > 0)
                {
                    var values = sets.Select(c => c.Value).ToList();
                    values.Add(articleId);
                    NewsDb.Exec(
                        ctx,
                        "UPDATE {databaseOwner}[{objectQualifier}EasyDNNNews] SET "
                        + string.Join(", ", sets.Select((c, i) => "[" + c.Key + "] = @" + i))
                        + ", LastModified = GETUTCDATE() WHERE ArticleID = @" + sets.Count,
                        values.ToArray());
                }

                if (fields.Slug != null && fields.Slug != row.TitleLink)
                {
                    slug = UniqueSlug(ctx, portalId, articleId, fields.Slug);
                    NewsDb.Exec(ctx, "UPDATE {databaseOwner}[{objectQualifier}EasyDNNNews] SET TitleLink = @0, LastModified = GETUTCDATE() WHERE ArticleID = @1", slug, articleId);
                    var updated = NewsDb.Scalar<int>(
                        ctx,
                        "UPDATE {databaseOwner}[{objectQualifier}EasyDNNnewsUrlNewProviderData] "
                        + "SET LinkTitle = @0, ArticleName = @0, OriginalUrlString = '/artmid/' + CAST(ArtMID AS nvarchar(20)) + '/articleid/' + CAST(ArticleID AS nvarchar(20)) + '/' + @0, "
                        + "OriginURL = '/ArtMID/' + CAST(ArtMID AS nvarchar(20)) + '/ArticleID/' + CAST(ArticleID AS nvarchar(20)) + '//' + @0 + '//' "
                        + "WHERE ArticleID = @1 AND PortalID = @2 AND LinkType = 0; SELECT CAST(ROWCOUNT_BIG() AS int);", // not @@ROWCOUNT: PetaPoco unescapes @@ to @
                        slug,
                        articleId,
                        portalId);
                    if (updated == 0)
                    {
                        InsertUrlRow(ctx, portalId, module, articleId, slug);
                    }

                    changed.Add("slug");
                }

                if (categoryIds != null)
                {
                    NewsDb.Exec(ctx, "DELETE FROM {databaseOwner}[{objectQualifier}EasyDNNNewsCategories] WHERE ArticleID = @0", articleId);
                    WriteCategories(ctx, articleId, categoryIds);
                    changed.Add("categories");
                }

                if (tags != null)
                {
                    NewsDb.Exec(ctx, "DELETE FROM {databaseOwner}[{objectQualifier}EasyDNNNewsTagsItems] WHERE ArticleID = @0", articleId);
                    WriteTags(ctx, portalId, articleId, tags);
                    changed.Add("tags");
                }

                return 0;
            });

            if (changed.Contains("slug"))
            {
                // The module's URL provider caches slug-to-article lookups in the DNN cache, so
                // without this the new slug 301s to the old one until the app pool recycles.
                NewsDb.ClearModuleCache();
            }

            var imageNote = SaveImage(portalId, articleId, image);
            if (image != null && imageNote.Length == 0)
            {
                changed.Add("image");
            }

            return ToolKit.Ok(
                "Updated article " + articleId + ": " + string.Join(", ", changed) + ". URL: "
                + ArticleUrl(module.TabID, module.ModuleID, articleId, slug)
                + imageNote + (categoryIds == null ? string.Empty : VisibilityWarning(module, categoryIds)));
        }

        internal class ArticleFields
        {
            public string SubTitle { get; set; }

            public string AuthorAlias { get; set; }

            public string Slug { get; set; }

            public string ImageTitle { get; set; }

            public string MetaDescription { get; set; }

            public string MetaKeywords { get; set; }

            public string TitleTag { get; set; }

            public bool? ShowMainImage { get; set; }

            public bool? Featured { get; set; }

            public bool? AllowComments { get; set; }
        }

        private static ArticleFields ReadFields(Dictionary<string, object> args, bool updating, out string error)
        {
            error = null;
            var fields = new ArticleFields
            {
                SubTitle = Clearable(args, "subTitle", updating),
                AuthorAlias = Clearable(args, "authorAlias", updating),
                ImageTitle = Clearable(args, "imageTitle", updating),
                MetaDescription = Clearable(args, "metaDescription", updating),
                MetaKeywords = Clearable(args, "metaKeywords", updating),
                TitleTag = Clearable(args, "titleTag", updating),
                ShowMainImage = args.ContainsKey("showMainImage") ? ToolKit.Bool(args, "showMainImage", false) : (bool?)null,
                Featured = args.ContainsKey("featured") ? ToolKit.Bool(args, "featured", false) : (bool?)null,
                AllowComments = args.ContainsKey("allowComments") ? ToolKit.Bool(args, "allowComments", false) : (bool?)null,
            };

            var slug = ToolKit.Str(args, "slug");
            if (slug != null)
            {
                fields.Slug = NewsDb.Slug(slug);
            }

            if (args.ContainsKey("publishDate") && ToolKit.Str(args, "publishDate") != null && !NewsDb.TryDate(args, "publishDate", out _))
            {
                error = "publishDate is not a valid date; use ISO 8601, e.g. 2024-05-01T09:00:00Z.";
            }
            else if (fields.SubTitle?.Length > 4000)
            {
                error = "subTitle must be 4,000 characters or fewer.";
            }
            else if (fields.AuthorAlias?.Length > 100)
            {
                error = "authorAlias must be 100 characters or fewer.";
            }
            else if (fields.MetaDescription?.Length > 1000)
            {
                error = "metaDescription must be 1,000 characters or fewer.";
            }
            else if (fields.MetaKeywords?.Length > 500)
            {
                error = "metaKeywords must be 500 characters or fewer.";
            }
            else if (fields.TitleTag?.Length > 256)
            {
                error = "titleTag must be 256 characters or fewer.";
            }
            else if (fields.ImageTitle?.Length > 500)
            {
                error = "imageTitle must be 500 characters or fewer.";
            }

            return fields;
        }

        private static List<int> ResolveCategories(int portalId, ModuleInfo module, Dictionary<string, object> args, bool adding, out string error)
        {
            error = null;
            var all = NewsCategoryTools.LoadCategories(portalId);
            var requested = NewsDb.StrList(args, "categories") ?? new List<string>();

            if (requested.Count == 0)
            {
                if (!adding)
                {
                    error = "categories cannot be empty: an article needs at least one category to appear in a news module.";
                    return null;
                }

                // A module bound to exactly one category is the common blog setup; use it.
                var linked = NewsDb.Query<int>(
                    "SELECT CategoryID FROM {databaseOwner}[{objectQualifier}EasyDNNNewsModuleCategoryItems] WHERE ModuleID = @0",
                    module.ModuleID);
                if (linked.Count == 1)
                {
                    return linked;
                }

                error = "categories is required for this module. Available: "
                    + string.Join(", ", all.Select(c => c.CategoryID + " '" + c.CategoryName + "'")) + ".";
                return null;
            }

            var ids = new List<int>();
            foreach (var item in requested)
            {
                var matches = int.TryParse(item, out var id)
                    ? all.Where(c => c.CategoryID == id).ToList()
                    : all.Where(c => string.Equals(c.CategoryName, item, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count > 1)
                {
                    // Names are not unique in EasyDNNNews (sites often carry two "Uncategorized").
                    error = "Category name '" + item + "' matches several categories: "
                        + string.Join(", ", matches.Select(c => c.CategoryID + (c.ParentCategory > 0 ? " (under " + c.ParentCategory + ")" : string.Empty)))
                        + ". Pass the category ID instead.";
                    return null;
                }

                var match = matches.FirstOrDefault();
                if (match == null)
                {
                    error = "Category '" + item + "' does not exist on this portal (create it with add-news-category). Available: "
                        + string.Join(", ", all.Select(c => c.CategoryID + " '" + c.CategoryName + "'")) + ".";
                    return null;
                }

                if (!ids.Contains(match.CategoryID))
                {
                    ids.Add(match.CategoryID);
                }
            }

            return ids;
        }

        private static List<string> ReadTags(Dictionary<string, object> args, out string error)
        {
            error = null;
            var tags = NewsDb.StrList(args, "tags") ?? new List<string>();
            var tooLong = tags.FirstOrDefault(t => t.Length > 50);
            if (tooLong != null)
            {
                error = "Tag '" + tooLong + "' is longer than 50 characters.";
            }

            return tags;
        }

        private static NewsImages.ImageData ReadImage(int portalId, Dictionary<string, object> args, out string error)
        {
            error = null;
            var path = ToolKit.Str(args, "imagePath");
            var url = ToolKit.Str(args, "imageUrl");
            if (path != null && url != null)
            {
                error = "Pass imagePath or imageUrl, not both.";
                return null;
            }

            if (path != null)
            {
                return NewsImages.FromPortalFile(portalId, path, out error);
            }

            return url == null ? null : NewsImages.FromUrl(url, out error);
        }

        /// <summary>
        /// Stores the image after the article rows are committed; the folder name needs the
        /// new ArticleID. A failure here leaves a valid article without an image, which is
        /// reported rather than rolled back.
        /// </summary>
        private static string SaveImage(int portalId, int articleId, NewsImages.ImageData image)
        {
            if (image == null)
            {
                return string.Empty;
            }

            try
            {
                var fileName = NewsImages.Save(portalId, articleId, image);
                NewsDb.Exec(
                    "UPDATE {databaseOwner}[{objectQualifier}EasyDNNNews] SET ArticleImage = @0, ArticleImageSet = 1, LastModified = GETUTCDATE() WHERE ArticleID = @1",
                    fileName,
                    articleId);

                return string.Empty;
            }
            catch (Exception ex)
            {
                return " WARNING: the article was saved but its image was not: " + ex.Message;
            }
        }

        private static string VisibilityWarning(ModuleInfo module, List<int> categoryIds)
        {
            if (NewsCategoryTools.ShowAllCategories(new[] { module.ModuleID })[module.ModuleID])
            {
                return string.Empty;
            }

            var linked = NewsDb.Query<int>(
                "SELECT CategoryID FROM {databaseOwner}[{objectQualifier}EasyDNNNewsModuleCategoryItems] WHERE ModuleID = @0",
                module.ModuleID);

            return categoryIds.Any(linked.Contains)
                ? string.Empty
                : " WARNING: none of its categories is shown by news module " + module.ModuleID
                    + ", so it will not appear in that module's list. Link a category with add-news-category (moduleIds) or pick a linked category.";
        }

        private static string UniqueSlug(IDataContext ctx, int portalId, int articleId, string slug)
        {
            var candidate = slug;
            for (var n = 2; ; n++)
            {
                var taken = NewsDb.Scalar<int>(
                    ctx,
                    "SELECT COUNT(*) FROM {databaseOwner}[{objectQualifier}EasyDNNNews] WHERE PortalID = @0 AND ArticleID <> @1 AND TitleLink = @2",
                    portalId,
                    articleId,
                    candidate)
                    + NewsDb.Scalar<int>(
                    ctx,
                    "SELECT COUNT(*) FROM {databaseOwner}[{objectQualifier}EasyDNNnewsUrlNewProviderData] WHERE PortalID = @0 AND ISNULL(ArticleID, -1) <> @1 AND LinkTitle = @2",
                    portalId,
                    articleId,
                    candidate);

                if (taken == 0)
                {
                    return candidate;
                }

                var suffix = "-" + n;
                candidate = NewsDb.Truncate(slug, 200 - suffix.Length).TrimEnd('-') + suffix;
            }
        }

        private static void InsertUrlRow(IDataContext ctx, int portalId, ModuleInfo module, int articleId, string slug)
        {
            NewsDb.Exec(
                ctx,
                "INSERT INTO {databaseOwner}[{objectQualifier}EasyDNNnewsUrlNewProviderData] "
                + "(PortalID, LinkTitle, OriginalUrlString, TabID, ModuleID, ArtMID, ArticleID, LinkType, ArticleName, OriginURL) "
                + "VALUES (@0, @1, @2, @3, @4, @4, @5, 0, @1, @6)",
                portalId,
                slug,
                "/artmid/" + module.ModuleID + "/articleid/" + articleId + "/" + slug,
                module.TabID,
                module.ModuleID,
                articleId,
                "/ArtMID/" + module.ModuleID + "/ArticleID/" + articleId + "//" + slug + "//");
        }

        private static void WriteCategories(IDataContext ctx, int articleId, IEnumerable<int> categoryIds)
        {
            foreach (var categoryId in categoryIds)
            {
                NewsDb.Exec(ctx, "INSERT INTO {databaseOwner}[{objectQualifier}EasyDNNNewsCategories] (ArticleID, CategoryID) VALUES (@0, @1)", articleId, categoryId);
            }
        }

        private static void WriteTags(IDataContext ctx, int portalId, int articleId, IEnumerable<string> tags)
        {
            foreach (var tag in tags)
            {
                var tagId = NewsDb.Scalar<int?>(
                    ctx,
                    "SELECT TOP 1 TagID FROM {databaseOwner}[{objectQualifier}EasyDNNNewsNewTags] WHERE PortalID = @0 AND Name = @1",
                    portalId,
                    tag)
                    ?? NewsDb.Scalar<int>(
                    ctx,
                    "INSERT INTO {databaseOwner}[{objectQualifier}EasyDNNNewsNewTags] (Name, PortalID, DateCreated) VALUES (@0, @1, GETUTCDATE()); SELECT CAST(SCOPE_IDENTITY() AS int);",
                    tag,
                    portalId);

                NewsDb.Exec(ctx, "INSERT INTO {databaseOwner}[{objectQualifier}EasyDNNNewsTagsItems] (ArticleID, TagID, DateAdded) VALUES (@0, @1, GETUTCDATE())", articleId, tagId);
            }
        }

        private static ArticleRow LoadArticle(int portalId, int articleId)
        {
            return NewsDb.Query<ArticleRow>(
                "SELECT " + ArticleColumns + ", 1 AS Total FROM {databaseOwner}[{objectQualifier}EasyDNNNews] a WHERE a.PortalID = @0 AND a.ArticleID = @1",
                portalId,
                articleId).FirstOrDefault();
        }

        private static List<ArticleNameRow> ArticleCategories(List<int> articleIds)
        {
            // Article IDs come from our own query, never from the caller, so inlining is safe.
            return articleIds.Count == 0
                ? new List<ArticleNameRow>()
                : NewsDb.Query<ArticleNameRow>(
                    "SELECT c.ArticleID, l.CategoryID AS Id, l.CategoryName AS Name "
                    + "FROM {databaseOwner}[{objectQualifier}EasyDNNNewsCategories] c "
                    + "JOIN {databaseOwner}[{objectQualifier}EasyDNNNewsCategoryList] l ON l.CategoryID = c.CategoryID "
                    + "WHERE c.ArticleID IN (" + string.Join(",", articleIds) + ") ORDER BY l.Position");
        }

        private static List<ArticleNameRow> ArticleTags(List<int> articleIds)
        {
            return NewsDb.Query<ArticleNameRow>(
                "SELECT i.ArticleID, t.TagID AS Id, t.Name "
                + "FROM {databaseOwner}[{objectQualifier}EasyDNNNewsTagsItems] i "
                + "JOIN {databaseOwner}[{objectQualifier}EasyDNNNewsNewTags] t ON t.TagID = i.TagID "
                + "WHERE i.ArticleID IN (" + string.Join(",", articleIds) + ") ORDER BY t.Name");
        }

        private static string Status(ArticleRow row)
        {
            if (!row.Active || !row.Approved)
            {
                return "draft";
            }

            return row.PublishDate > DateTime.UtcNow ? "scheduled" : "published";
        }

        private static string ArticleUrl(int? tabId, int? moduleId, int articleId, string slug)
        {
            if (!tabId.HasValue || !moduleId.HasValue)
            {
                return null;
            }

            var tab = TabController.Instance.GetTab(tabId.Value, PortalSettings.Current.PortalId);
            return tab == null
                ? null
                : tab.FullUrl.TrimEnd('/') + "/ArtMID/" + moduleId.Value + "/ArticleID/" + articleId + "/" + slug;
        }

        /// <summary>
        /// <see cref="ToolKit.Str"/>, except that on update a key passed as "" (or null) reads as
        /// "" so the field is cleared; ToolKit.Str alone treats it as not passed.
        /// </summary>
        private static string Clearable(Dictionary<string, object> args, string name, bool updating)
        {
            return ToolKit.Str(args, name) ?? (updating && args.ContainsKey(name) ? string.Empty : null);
        }

        private static KeyValuePair<string, object> Col(string name, object value)
        {
            return new KeyValuePair<string, object>(name, value);
        }
    }
}
