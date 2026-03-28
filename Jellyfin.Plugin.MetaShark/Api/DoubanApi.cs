// <copyright file="DoubanApi.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Web;
    using AngleSharp;
    using AngleSharp.Dom;
    using ComposableAsync;
    using Jellyfin.Extensions.Json;
    using Jellyfin.Plugin.MetaShark.Api.Http;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.Logging;
    using RateLimiter;

    public class DoubanApi : IDisposable
    {
        public const string HTTPUSERAGENT = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/93.0.4577.63 Safari/537.36 Edg/93.0.961.44";
        public const string HTTPREFERER = "https://www.douban.com/";

        private static readonly Action<ILogger, string, Exception?> LogCookieAddFailed =
            LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1, nameof(LoadLoadDoubanCookie)), "Failed to add cookie: {ErrorMessage}");

        private static readonly Action<ILogger, string, HttpStatusCode, Exception?> LogSearchFailed =
            LoggerMessage.Define<string, HttpStatusCode>(LogLevel.Warning, new EventId(2, nameof(SearchAsync)), "douban搜索请求失败. keyword: {Keyword} statusCode: {StatusCode}");

        private static readonly Action<ILogger, string, Exception?> LogRiskControl =
            LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, nameof(SearchAsync)), "douban触发风控，可能ip被封，请到插件配置中打开防封禁功能。。。keyword: {Keyword}");

        private static readonly Action<ILogger, string, Exception?> LogSearchEmpty =
            LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, nameof(SearchAsync)), "douban搜索不到内容，这消息大量出现时，可能触发了爬虫风控。。。keyword: {Keyword}");

        private static readonly Action<ILogger, string, HttpStatusCode, Exception?> LogSuggestFailed =
            LoggerMessage.Define<string, HttpStatusCode>(LogLevel.Warning, new EventId(5, nameof(SearchBySuggestAsync)), "douban suggest请求失败. keyword: {Keyword} statusCode: {StatusCode}");

        private static readonly Action<ILogger, string, Exception?> LogSuggestError =
            LoggerMessage.Define<string>(LogLevel.Error, new EventId(6, nameof(SearchBySuggestAsync)), "SearchBySuggestAsync error. keyword: {Keyword}");

        private static readonly Action<ILogger, string, Exception?> LogPageMissing =
            LoggerMessage.Define<string>(LogLevel.Error, new EventId(7, nameof(GetMovieAsync)), "获取不到douban页面内容，可能触发douban防爬或页面结构已改变! url: {Url}");

        private static readonly Action<ILogger, string, Exception?> LogPageBlocked =
            LoggerMessage.Define<string>(LogLevel.Warning, new EventId(12, nameof(GetMovieAsync)), "douban页面被禁止访问或触发风控，已跳过刮削. url: {Url}");

        private static readonly Action<ILogger, string, Exception?> LogCelebrityPhotosError =
            LoggerMessage.Define<string>(LogLevel.Error, new EventId(8, nameof(GetCelebrityPhotosAsync)), "GetCelebrityPhotosAsync error. cid: {CelebrityId}");

        private static readonly Action<ILogger, string, Exception?> LogWallpaperError =
            LoggerMessage.Define<string>(LogLevel.Error, new EventId(9, nameof(GetWallpaperBySidAsync)), "GetWallpaperBySidAsync error. sid: {Sid}");

        private static readonly Action<ILogger, Exception?> LogCheckLoginError =
            LoggerMessage.Define(LogLevel.Error, new EventId(10, nameof(CheckLoginAsync)), "CheckLoginAsync error.");

        private static readonly Action<ILogger, Exception?> LogGetLoginInfoError =
            LoggerMessage.Define(LogLevel.Error, new EventId(11, nameof(GetLoginInfoAsync)), "GetLoginInfoAsync error.");

        private static readonly object Lock = new object();
        private readonly ILogger<DoubanApi> logger;
        private readonly HttpClient httpClient;
        private readonly HttpClientHandlerExtended httpClientHandler;
        private readonly DoubanSecHandler doubanHandler;
        private readonly MemoryCache memoryCache;
        private CookieContainer cookieContainer;
        private Regex regId = new Regex(@"/(\d+?)/", RegexOptions.Compiled);
        private Regex regSid = new Regex(@"sid: (\d+?),", RegexOptions.Compiled);
        private Regex regCat = new Regex(@"\[(.+?)\]", RegexOptions.Compiled);
        private Regex regYear = new Regex(@"([12][890][0-9][0-9])", RegexOptions.Compiled);
        private Regex regTitle = new Regex(@"<title>([\w\W]+?)</title>", RegexOptions.Compiled);
        private Regex regKeywordMeta = new Regex(@"<meta name=""keywords"" content=""(.+?)""", RegexOptions.Compiled);
        private Regex regOriginalName = new Regex(@"原名[:：](.+?)\s*?\/", RegexOptions.Compiled);
        private Regex regDirector = new Regex(@"导演: (.+?)\n", RegexOptions.Compiled);
        private Regex regWriter = new Regex(@"编剧: (.+?)\n", RegexOptions.Compiled);
        private Regex regActor = new Regex(@"主演: (.+?)\n", RegexOptions.Compiled);
        private Regex regGenre = new Regex(@"类型: (.+?)\n", RegexOptions.Compiled);
        private Regex regCountry = new Regex(@"制片国家/地区: (.+?)\n", RegexOptions.Compiled);
        private Regex regLanguage = new Regex(@"语言: (.+?)\n", RegexOptions.Compiled);
        private Regex regDuration = new Regex(@"片长: (.+?)\n", RegexOptions.Compiled);
        private Regex regScreen = new Regex(@"(上映日期|首播): (.+?)\n", RegexOptions.Compiled);
        private Regex regSubname = new Regex(@"又名: (.+?)\n", RegexOptions.Compiled);
        private Regex regImdb = new Regex(@"IMDb: (tt\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private Regex regSite = new Regex(@"官方网站: (.+?)\n", RegexOptions.Compiled);
        private Regex regRole = new Regex(@"\([饰|配]?\s*?(.+?)\)", RegexOptions.Compiled);
        private Regex regBackgroundImage = new Regex(@"url\(([^)]+?)\)$", RegexOptions.Compiled);
        private Regex regLifedate = new Regex(@"(.+?) 至 (.+)", RegexOptions.Compiled);
        private Regex regHtmlTag = new Regex(@"<.?>", RegexOptions.Compiled);
        private Regex regImgHost = new Regex(@"\/\/(img\d+?)\.", RegexOptions.Compiled);

        // 匹配除了换行符之外所有空白
        private Regex regOverviewSpace = new Regex(@"\n[^\S\n]+", RegexOptions.Compiled);
        private Regex regPhotoId = new Regex(@"/photo/(\d+?)/", RegexOptions.Compiled);
        private Regex regLoginName = new Regex(@"<div[^>]*?db-usr-profile[^>]*?>[\w\W]*?<h1>([^>]*?)<", RegexOptions.Compiled);

        // 默认200毫秒请求1次
        private TimeLimiter defaultTimeConstraint = TimeLimiter.GetFromMaxCountByInterval(1, TimeSpan.FromMilliseconds(200));

        // 未登录最多1分钟10次请求，不然5分钟后会被封ip
        private TimeLimiter guestTimeConstraint = TimeLimiter.Compose(new CountByIntervalAwaitableConstraint(10, TimeSpan.FromMinutes(1)), new CountByIntervalAwaitableConstraint(1, TimeSpan.FromMilliseconds(5000)));

        // 登录后最多1分钟20次请求，不然会触发机器人检验
        private TimeLimiter loginedTimeConstraint = TimeLimiter.Compose(new CountByIntervalAwaitableConstraint(20, TimeSpan.FromMinutes(1)), new CountByIntervalAwaitableConstraint(1, TimeSpan.FromMilliseconds(3000)));

        /// <summary>
        /// Initializes a new instance of the <see cref="DoubanApi"/> class.
        /// </summary>
        /// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
        public DoubanApi(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<DoubanApi>();
            this.memoryCache = new MemoryCache(new MemoryCacheOptions());

            this.httpClientHandler = new HttpClientHandlerExtended();
            this.httpClientHandler.CheckCertificateRevocationList = true;
            this.cookieContainer = this.httpClientHandler.CookieContainer;
            this.doubanHandler = new DoubanSecHandler(this.logger) { InnerHandler = this.httpClientHandler };
            this.httpClient = new HttpClient(this.doubanHandler, disposeHandler: false);
            this.httpClient.Timeout = TimeSpan.FromSeconds(20);
            this.httpClient.DefaultRequestHeaders.Add("User-Agent", HTTPUSERAGENT);
            this.httpClient.DefaultRequestHeaders.Add("Origin", "https://movie.douban.com");
            this.httpClient.DefaultRequestHeaders.Add("Referer", "https://movie.douban.com/");

            this.LoadLoadDoubanCookie();
            if (MetaSharkPlugin.Instance != null)
            {
                MetaSharkPlugin.Instance.ConfigurationChanged += (_, _) =>
                {
                    this.LoadLoadDoubanCookie();
                };
            }
        }

        public static string ParseCelebrityName(string nameString)
        {
            if (string.IsNullOrEmpty(nameString))
            {
                return string.Empty;
            }

            // 只有中文名情况
            var idx = nameString.IndexOf(' ', StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return nameString.Trim();
            }

            // 中英名混合情况
            var firstName = nameString.Substring(0, idx);
            if (firstName.HasChinese())
            {
                return firstName.Trim();
            }

            // 英文名重复两次的情况
            var nextIndex = nameString[idx..].IndexOf(firstName, StringComparison.OrdinalIgnoreCase);
            if (nextIndex >= 0)
            {
                nextIndex = idx + nextIndex;
                return nameString[..nextIndex].Trim();
            }

            // 只有英文名情况
            return nameString.Trim();
        }

        public async Task<List<DoubanSubject>> SearchMovieAsync(string keyword, CancellationToken cancellationToken)
        {
            var result = await this.SearchAsync(keyword, cancellationToken).ConfigureAwait(false);
            return result.Where(x => x.Category == "电影").ToList();
        }

        public async Task<List<DoubanSubject>> SearchTVAsync(string keyword, CancellationToken cancellationToken)
        {
            var result = await this.SearchAsync(keyword, cancellationToken).ConfigureAwait(false);
            return result.Where(x => x.Category == "电视剧").ToList();
        }

        public async Task<List<DoubanSubject>> SearchAsync(string keyword, CancellationToken cancellationToken)
        {
            var list = new List<DoubanSubject>();
            if (string.IsNullOrEmpty(keyword))
            {
                return list;
            }

            var cacheKey = $"search_{keyword}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) };
            List<DoubanSubject>? searchResult;
            if (this.memoryCache.TryGetValue(cacheKey, out searchResult) && searchResult != null)
            {
                return searchResult;
            }

            await this.LimitRequestFrequently().ConfigureAwait(false);

            var encodedKeyword = HttpUtility.UrlEncode(keyword);
            var url = $"https://www.douban.com/search?cat=1002&q={encodedKeyword}";
            var response = await this.httpClient.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogSearchFailed(this.logger, keyword, response.StatusCode, null);
                return list;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var context = BrowsingContext.New();
            var doc = await context.OpenAsync(req => req.Content(body), cancellationToken).ConfigureAwait(false);
            var movieElements = doc.QuerySelectorAll("div.result-list .result");

            foreach (var movieElement in movieElements)
            {
                var ratingStr = movieElement.GetText("div.rating-info") ?? string.Empty;
                if (ratingStr.Contains("尚未播出", StringComparison.Ordinal))
                {
                    continue;
                }

                var rating = movieElement.GetText("div.rating-info>.rating_nums") ?? "0";
                var img = movieElement.GetAttr("a.nbg>img", "src") ?? string.Empty;
                var oncick = movieElement.GetAttr("div.title a", "onclick") ?? string.Empty;
                var sid = oncick.GetMatchGroup(this.regSid);
                var name = movieElement.GetText("div.title a") ?? string.Empty;
                var titleStr = movieElement.GetText("div.title>h3>span") ?? string.Empty;
                var cat = titleStr.GetMatchGroup(this.regCat);
                var subjectStr = movieElement.GetText("div.rating-info>span:last-child") ?? string.Empty;
                var year = subjectStr.GetMatchGroup(this.regYear);
                var originalName = subjectStr.GetMatchGroup(this.regOriginalName);
                var desc = movieElement.GetText("div.content>p") ?? string.Empty;
                if (cat != "电影" && cat != "电视剧")
                {
                    continue;
                }

                var movie = new DoubanSubject();
                movie.Sid = sid;
                movie.Name = name;
                movie.OriginalName = !string.IsNullOrEmpty(originalName) ? originalName : name;
                movie.Genre = cat;
                movie.Category = cat;
                movie.Img = img;
                movie.Rating = rating.ToFloat();
                movie.Year = year.ToInt();
                movie.Intro = desc;
                list.Add(movie);
            }

            if (list.Count > 0)
            {
                this.memoryCache.Set<List<DoubanSubject>>(cacheKey, list, expiredOption);
            }
            else
            {
                if (body.Contains("sec.douban.com", StringComparison.OrdinalIgnoreCase))
                {
                    LogRiskControl(this.logger, keyword, null);
                }
                else
                {
                    LogSearchEmpty(this.logger, keyword, null);
                }
            }

            return list;
        }

        public async Task<List<DoubanSubject>> SearchBySuggestAsync(string keyword, CancellationToken cancellationToken)
        {
            var list = new List<DoubanSubject>();
            if (string.IsNullOrEmpty(keyword))
            {
                return list;
            }

            await this.LimitRequestFrequently().ConfigureAwait(false);

            try
            {
                var encodedKeyword = HttpUtility.UrlEncode(keyword);
                var url = $"https://www.douban.com/j/search_suggest?q={encodedKeyword}";

                using (var requestMessage = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    requestMessage.Headers.Add("Origin", "https://www.douban.com");
                    requestMessage.Headers.Add("Referer", "https://www.douban.com/");

                    var response = await this.httpClient.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        LogSuggestFailed(this.logger, keyword, response.StatusCode, null);
                        return list;
                    }

                    JsonSerializerOptions? serializeOptions = null;
                    var result = await response.Content.ReadFromJsonAsync<DoubanSuggestResult>(serializeOptions, cancellationToken).ConfigureAwait(false);

                    if (result != null && result.Cards != null)
                    {
                        foreach (var suggest in result.Cards)
                        {
                            if (suggest.Type != "movie")
                            {
                                continue;
                            }

                            var movie = new DoubanSubject();
                            movie.Sid = suggest.Sid;
                            movie.Name = suggest.Title;
                            movie.Year = suggest.Year.ToInt();
                            list.Add(movie);
                        }
                    }
                }
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogSuggestError(this.logger, keyword, ex);
            }
            catch (HttpRequestException ex)
            {
                LogSuggestError(this.logger, keyword, ex);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return list;
        }

        public async Task<DoubanSubject?> GetMovieAsync(string sid, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sid))
            {
                return null;
            }

            var cacheKey = $"movie_{sid}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
            DoubanSubject? movie;
            if (this.memoryCache.TryGetValue<DoubanSubject?>(cacheKey, out movie))
            {
                return movie;
            }

            await this.LimitRequestFrequently().ConfigureAwait(false);

            var url = $"https://movie.douban.com/subject/{sid}/";
            var response = await this.httpClient.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

            movie = new DoubanSubject();
            var body = await this.ReadBodyUnlessBlockedAsync(response, url, cancellationToken).ConfigureAwait(false);
            if (body == null)
            {
                return null;
            }

            var context = BrowsingContext.New();
            var doc = await context.OpenAsync(req => req.Content(body), cancellationToken).ConfigureAwait(false);
            var contentNode = doc.QuerySelector("#content");
            if (contentNode == null)
            {
                LogPageMissing(this.logger, url, null);
                return null;
            }

            var nameStr = contentNode.GetText("h1>span:first-child") ?? string.Empty;
            var name = this.GetTitle(body);
            var orginalName = nameStr.Replace(name, string.Empty, StringComparison.Ordinal).Trim();
            var yearStr = contentNode.GetText("h1>span.year") ?? string.Empty;
            var year = yearStr.GetMatchGroup(this.regYear);
            var rating = contentNode.GetText("div.rating_self strong.rating_num") ?? "0";
            var img = contentNode.GetAttr("a.nbgnbg>img", "src") ?? string.Empty;
            var category = contentNode.QuerySelector("div.episode_list") == null ? "电影" : "电视剧";
            var intro = contentNode.GetText("div#link-report-intra>span.all") ?? contentNode.GetText("div#link-report-intra>span") ?? string.Empty;
            intro = this.FormatOverview(intro);

            var info = contentNode.GetText("#info") ?? string.Empty;
            var director = info.GetMatchGroup(this.regDirector);
            var writer = info.GetMatchGroup(this.regWriter);
            var actor = info.GetMatchGroup(this.regActor);
            var genre = info.GetMatchGroup(this.regGenre);
            var country = info.GetMatchGroup(this.regCountry);
            var language = info.GetMatchGroup(this.regLanguage);
            var duration = info.GetMatchGroup(this.regDuration);
            var subname = info.GetMatchGroup(this.regSubname);
            var imdb = info.GetMatchGroup(this.regImdb);
            var site = info.GetMatchGroup(this.regSite);
            var matchs = this.regScreen.Match(info);
            var screen = matchs.Groups.Count > 2 ? matchs.Groups[2].Value : string.Empty;

            movie.Sid = sid;
            movie.Name = name;
            movie.OriginalName = orginalName;
            movie.Year = year.ToInt();
            movie.Rating = rating.ToFloat();
            movie.Img = img;
            movie.Intro = intro;
            movie.Subname = subname;
            movie.Director = director;
            movie.Genre = genre;
            movie.Category = category;
            movie.Country = country;
            movie.Language = language;
            movie.Duration = duration;
            movie.Screen = screen;
            movie.Site = site;
            movie.Actor = actor;
            movie.Writer = writer;
            movie.Imdb = imdb;

            movie.Celebrities.Clear();
            var celebrityNodes = contentNode.QuerySelectorAll("#celebrities li.celebrity");
            foreach (var node in celebrityNodes)
            {
                var celebrityIdStr = node.GetAttr("div.info a.name", "href") ?? string.Empty;
                var celebrityId = celebrityIdStr.GetMatchGroup(this.regId);
                var celebrityImgStr = node.GetAttr("div.avatar", "style") ?? string.Empty;
                var celebrityImg = celebrityImgStr.GetMatchGroup(this.regBackgroundImage);
                var celebrityName = node.GetText("div.info a.name") ?? string.Empty;
                var celebrityRole = node.GetText("div.info span.role") ?? string.Empty;
                var celebrityRoleType = string.Empty;

                var celebrity = new DoubanCelebrity();
                celebrity.Id = celebrityId;
                celebrity.Name = celebrityName;
                celebrity.Role = celebrityRole;
                celebrity.RoleType = celebrityRoleType;
                celebrity.Img = celebrityImg;
                movie.Celebrities.Add(celebrity);
            }

            this.memoryCache.Set<DoubanSubject?>(cacheKey, movie, expiredOption);
            return movie;
        }

        public async Task<List<DoubanCelebrity>> GetCelebritiesBySidAsync(string sid, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sid))
            {
                return new List<DoubanCelebrity>();
            }

            var cacheKey = $"celebrities_{sid}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
            List<DoubanCelebrity>? celebrities;
            if (this.memoryCache.TryGetValue(cacheKey, out celebrities) && celebrities != null)
            {
                return celebrities;
            }

            await this.LimitRequestFrequently().ConfigureAwait(false);

            var list = new List<DoubanCelebrity>();
            var url = $"https://movie.douban.com/subject/{sid}/celebrities";
            var response = await this.httpClient.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new List<DoubanCelebrity>();
            }

            var body = await this.ReadBodyUnlessBlockedAsync(response, url, cancellationToken).ConfigureAwait(false);
            if (body == null)
            {
                return list;
            }

            var context = BrowsingContext.New();
            var doc = await context.OpenAsync(req => req.Content(body), cancellationToken).ConfigureAwait(false);

            var celebritiesElements = doc.QuerySelectorAll("div#celebrities>.list-wrapper");
            foreach (var celebritiesNode in celebritiesElements)
            {
                var celebritiesTitle = celebritiesNode.GetText("h2") ?? string.Empty;
                if (!celebritiesTitle.Contains("导演", StringComparison.Ordinal) && !celebritiesTitle.Contains("演员", StringComparison.Ordinal))
                {
                    continue;
                }

                var celebrityElements = celebritiesNode.QuerySelectorAll("ul.celebrities-list li.celebrity");
                foreach (var node in celebrityElements)
                {
                    var celebrityIdStr = node.GetAttr("div.info a.name", "href") ?? string.Empty;
                    var celebrityId = celebrityIdStr.GetMatchGroup(this.regId);
                    var celebrityImgStr = node.GetAttr("div.avatar", "style") ?? string.Empty;
                    var celebrityImg = celebrityImgStr.GetMatchGroup(this.regBackgroundImage);
                    var celebrityNameStr = node.GetText("div.info a.name") ?? string.Empty;
                    var celebrityName = ParseCelebrityName(celebrityNameStr);

                    // 有时存在演员信息缺少名字的
                    if (string.IsNullOrEmpty(celebrityName))
                    {
                        continue;
                    }

                    var celebrityRoleStr = node.GetText("div.info span.role") ?? string.Empty;
                    var celebrityRole = celebrityRoleStr.GetMatchGroup(this.regRole);
                    var arrRole = celebrityRoleStr.Split(" ");
                    var celebrityRoleType = arrRole.Length > 1 ? arrRole[0] : string.Empty;
                    if (string.IsNullOrEmpty(celebrityRole))
                    {
                        celebrityRole = celebrityRoleType;
                    }

                    var celebrity = new DoubanCelebrity();
                    celebrity.Id = celebrityId;
                    celebrity.Name = celebrityName;
                    celebrity.Role = celebrityRole;
                    celebrity.RoleType = celebrityRoleType;
                    celebrity.Img = celebrityImg;

                    list.Add(celebrity);
                }
            }

            this.memoryCache.Set<List<DoubanCelebrity>>(cacheKey, list, expiredOption);
            return list;
        }

        public async Task<DoubanCelebrity?> GetCelebrityAsync(string id, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            var cacheKey = $"personage_{id}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
            DoubanCelebrity? celebrity;
            if (this.memoryCache.TryGetValue<DoubanCelebrity?>(cacheKey, out celebrity))
            {
                return celebrity;
            }

            // 兼容旧版 ID 处理
            var personageID = await this.CheckPersonageIDAsync(id, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(personageID))
            {
                id = personageID;
            }

            var url = $"https://www.douban.com/personage/{id}/";
            var response = await this.httpClient.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

            var body = await this.ReadBodyUnlessBlockedAsync(response, url, cancellationToken).ConfigureAwait(false);
            if (body == null)
            {
                return null;
            }

            celebrity = new DoubanCelebrity();
            var context = BrowsingContext.New();
            var doc = await context.OpenAsync(req => req.Content(body), cancellationToken).ConfigureAwait(false);
            var contentNode = doc.QuerySelector("#content");
            if (contentNode != null)
            {
                celebrity.Id = id;
                celebrity.Img = contentNode.GetAttr("img.avatar", "src") ?? string.Empty;
                var nameStr = contentNode.GetText("h1.subject-name") ?? string.Empty;
                celebrity.Name = ParseCelebrityName(nameStr);
                celebrity.EnglishName = nameStr.Replace(celebrity.Name, string.Empty, StringComparison.Ordinal).Trim();

                var family = string.Empty;
                var propertyNodes = contentNode.QuerySelectorAll("ul.subject-property>li");
                foreach (var li in propertyNodes)
                {
                    var label = li.GetText("span.label") ?? string.Empty;
                    var value = li.GetText("span.value") ?? string.Empty;
                    switch (label)
                    {
                        case "性别:":
                            celebrity.Gender = value;
                            break;
                        case "星座:":
                            celebrity.Constellation = value;
                            break;
                        case "出生日期:":
                            celebrity.Birthdate = value;
                            break;
                        case "去世日期:":
                            celebrity.Enddate = value;
                            break;
                        case "生卒日期:":
                            var match = this.regLifedate.Match(value);
                            if (match.Success && match.Groups.Count > 2)
                            {
                                celebrity.Birthdate = match.Groups[1].Value.Trim();
                                celebrity.Enddate = match.Groups[2].Value.Trim();
                            }

                            break;
                        case "出生地:":
                            celebrity.Birthplace = value;
                            break;
                        case "职业:":
                            celebrity.Role = value;
                            break;
                        case "更多外文名:":
                            celebrity.NickName = value;
                            break;
                        case "家庭成员:":
                            family = value;
                            break;
                        case "IMDb编号:":
                            celebrity.Imdb = value;
                            break;
                        default:
                            break;
                    }
                }

                // 保留段落关系，把段落替换为换行符
                var intro = contentNode.GetHtml("section.subject-intro div.content") ?? string.Empty;
                intro = this.regHtmlTag.Replace(intro.Replace("</p>", "\n", StringComparison.Ordinal), string.Empty);
                celebrity.Intro = this.FormatOverview(intro);
                this.memoryCache.Set<DoubanCelebrity?>(cacheKey, celebrity, expiredOption);
                return celebrity;
            }

            this.memoryCache.Set<DoubanCelebrity?>(cacheKey, null, expiredOption);
            return null;
        }

        public async Task<string?> CheckPersonageIDAsync(string id, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(id);
            if (id.Length != 7)
            {
                return null;
            }

            var url = $"https://movie.douban.com/celebrity/{id}/";
            using var handler = new HttpClientHandler()
            {
                AllowAutoRedirect = false,
                CheckCertificateRevocationList = true,
            };
            using (var noRedirectClient = new HttpClient(handler, disposeHandler: true))
            {
                var resp = await noRedirectClient.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
                if (resp.Headers.TryGetValues("Location", out var values))
                {
                    var location = values.First();
                    var newId = location.GetMatchGroup(this.regId);
                    if (!string.IsNullOrEmpty(newId))
                    {
                        return newId;
                    }
                }
            }

            return null;
        }

        public async Task<List<DoubanPhoto>> GetCelebrityPhotosAsync(string cid, CancellationToken cancellationToken)
        {
            var list = new List<DoubanPhoto>();
            if (string.IsNullOrEmpty(cid))
            {
                return list;
            }

            var cacheKey = $"personage_photo_{cid}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
            if (this.memoryCache.TryGetValue(cacheKey, out List<DoubanPhoto>? photos) && photos != null)
            {
                return photos;
            }

            await this.LimitRequestFrequently().ConfigureAwait(false);

            try
            {
                // 兼容旧版 ID 处理
                var personageID = await this.CheckPersonageIDAsync(cid, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(personageID))
                {
                    cid = personageID;
                }

                var url = $"https://www.douban.com/personage/{cid}/photos/";
                var response = await this.httpClient.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return list;
                }

                var body = await this.ReadBodyUnlessBlockedAsync(response, url, cancellationToken).ConfigureAwait(false);
                if (body == null)
                {
                    return list;
                }

                var context = BrowsingContext.New();
                var doc = await context.OpenAsync(req => req.Content(body), cancellationToken).ConfigureAwait(false);
                var elements = doc.QuerySelectorAll(".poster-col3>li");

                foreach (var node in elements)
                {
                    var href = node.QuerySelector("a")?.GetAttribute("href") ?? string.Empty;
                    var id = href.GetMatchGroup(this.regPhotoId);
                    var raw = node.QuerySelector("img")?.GetAttribute("src") ?? string.Empty;
                    var size = node.GetText("div.prop") ?? string.Empty;

                    var photo = new DoubanPhoto();
                    photo.Id = id;
                    photo.Size = size;
                    photo.Raw = raw;
                    if (!string.IsNullOrEmpty(size))
                    {
                        var arr = size.Split('x');
                        if (arr.Length == 2)
                        {
                            photo.Width = arr[0].ToInt();
                            photo.Height = arr[1].ToInt();
                        }
                    }

                    list.Add(photo);
                }

                this.memoryCache.Set<List<DoubanPhoto>>(cacheKey, list, expiredOption);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogCelebrityPhotosError(this.logger, cid, ex);
            }
            catch (HttpRequestException ex)
            {
                LogCelebrityPhotosError(this.logger, cid, ex);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return list;
        }

        public async Task<List<DoubanCelebrity>> SearchCelebrityAsync(string keyword, CancellationToken cancellationToken)
        {
            var list = new List<DoubanCelebrity>();
            if (string.IsNullOrEmpty(keyword))
            {
                return list;
            }

            var cacheKey = $"search_celebrity_{keyword}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
            if (this.memoryCache.TryGetValue(cacheKey, out List<DoubanCelebrity>? searchResult) && searchResult != null)
            {
                return searchResult;
            }

            keyword = HttpUtility.UrlEncode(keyword);
            var url = $"https://movie.douban.com/celebrities/search?search_text={keyword}";
            var response = await this.httpClient.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return list;
            }

            var body = await this.ReadBodyUnlessBlockedAsync(response, url, cancellationToken).ConfigureAwait(false);
            if (body == null)
            {
                return list;
            }

            var context = BrowsingContext.New();
            var doc = await context.OpenAsync(req => req.Content(body), cancellationToken).ConfigureAwait(false);
            var elements = doc.QuerySelectorAll("div.article .result");

            foreach (var el in elements)
            {
                var celebrity = new DoubanCelebrity();
                var img = el.GetAttr("div.pic img", "src") ?? string.Empty;
                var href = el.GetAttr("h3>a", "href") ?? string.Empty;
                var cid = href.GetMatchGroup(this.regId);
                var nameStr = el.GetText("h3>a") ?? string.Empty;
                var arr = nameStr.Split(" ");
                var name = arr.Length > 1 ? arr[0] : nameStr;

                celebrity.Name = name;
                celebrity.Img = img;
                celebrity.Id = cid;
                list.Add(celebrity);
            }

            this.memoryCache.Set<List<DoubanCelebrity>>(cacheKey, list, expiredOption);
            return list;
        }

        public async Task<List<DoubanPhoto>> GetWallpaperBySidAsync(string sid, CancellationToken cancellationToken)
        {
            var list = new List<DoubanPhoto>();
            if (string.IsNullOrEmpty(sid))
            {
                return list;
            }

            var cacheKey = $"photo_{sid}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
            if (this.memoryCache.TryGetValue(cacheKey, out List<DoubanPhoto>? photos) && photos != null)
            {
                return photos;
            }

            await this.LimitRequestFrequently().ConfigureAwait(false);

            try
            {
                var url = $"https://movie.douban.com/subject/{sid}/photos?type=W&start=0&sortby=size&size=a&subtype=a";
                var response = await this.httpClient.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return list;
                }

                var body = await this.ReadBodyUnlessBlockedAsync(response, url, cancellationToken).ConfigureAwait(false);
                if (body == null)
                {
                    return list;
                }

                var context = BrowsingContext.New();
                var doc = await context.OpenAsync(req => req.Content(body), cancellationToken).ConfigureAwait(false);
                var elements = doc.QuerySelectorAll(".poster-col3>li");

                foreach (var node in elements)
                {
                    var id = node.GetAttribute("data-id") ?? string.Empty;
                    var imgUrl = node.QuerySelector("img")?.GetAttribute("src") ?? string.Empty;
                    var imgHost = this.regImgHost.FirstMatchGroup(imgUrl, "img2");
                    var small = $"https://{imgHost}.doubanio.com/view/photo/s/public/p{id}.jpg";
                    var medium = $"https://{imgHost}.doubanio.com/view/photo/m/public/p{id}.jpg";
                    var large = $"https://{imgHost}.doubanio.com/view/photo/l/public/p{id}.jpg";
                    var raw = $"https://{imgHost}.doubanio.com/view/photo/raw/public/p{id}.jpg";
                    var size = node.GetText("div.prop") ?? string.Empty;

                    var photo = new DoubanPhoto();
                    photo.Id = id;
                    photo.Size = size;
                    photo.Small = small;
                    photo.Medium = medium;
                    photo.Large = large;
                    photo.Raw = raw;
                    if (!string.IsNullOrEmpty(size))
                    {
                        var arr = size.Split('x');
                        if (arr.Length == 2)
                        {
                            photo.Width = arr[0].ToInt();
                            photo.Height = arr[1].ToInt();
                        }
                    }

                    list.Add(photo);
                }

                this.memoryCache.Set<List<DoubanPhoto>>(cacheKey, list, expiredOption);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogWallpaperError(this.logger, sid, ex);
            }
            catch (HttpRequestException ex)
            {
                LogWallpaperError(this.logger, sid, ex);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return list;
        }

        public async Task<bool> CheckLoginAsync(CancellationToken cancellationToken)
        {
            try
            {
                var url = "https://www.douban.com/mine/";
                var response = await this.httpClient.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
                var requestUrl = response.RequestMessage?.RequestUri?.ToString();
                if (requestUrl == null
                    || requestUrl.Contains("accounts.douban.com", StringComparison.OrdinalIgnoreCase)
                    || requestUrl.Contains("login", StringComparison.OrdinalIgnoreCase)
                    || requestUrl.Contains("sec.douban.com", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                LogCheckLoginError(this.logger, ex);
            }
            catch (TaskCanceledException ex)
            {
                LogCheckLoginError(this.logger, ex);
            }

            return true;
        }

        public async Task<DoubanLoginInfo> GetLoginInfoAsync(CancellationToken cancellationToken)
        {
            var loginInfo = new DoubanLoginInfo();
            try
            {
                var url = "https://www.douban.com/mine/";
                var response = await this.httpClient.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
                var requestUrl = response.RequestMessage?.RequestUri?.ToString();
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var loginName = Match(body, this.regLoginName).Trim();
                loginInfo.Name = loginName;
                loginInfo.IsLogined = !(requestUrl == null
                    || requestUrl.Contains("accounts.douban.com", StringComparison.OrdinalIgnoreCase)
                    || requestUrl.Contains("login", StringComparison.OrdinalIgnoreCase)
                    || requestUrl.Contains("sec.douban.com", StringComparison.OrdinalIgnoreCase));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                LogGetLoginInfoError(this.logger, ex);
            }
            catch (TaskCanceledException ex)
            {
                LogGetLoginInfoError(this.logger, ex);
            }

            return loginInfo;
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.httpClient.Dispose();
                this.doubanHandler.Dispose();
                this.httpClientHandler.Dispose();
                this.memoryCache.Dispose();
            }
        }

        protected async Task LimitRequestFrequently()
        {
            if (IsEnableAvoidRiskControl())
            {
                var configCookie = MetaSharkPlugin.Instance?.Configuration.DoubanCookies.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(configCookie))
                {
                    await this.loginedTimeConstraint;
                }
                else
                {
                    await this.guestTimeConstraint;
                }
            }
            else
            {
                await this.defaultTimeConstraint;
            }
        }

        private static string? GetText(IElement el, string css)
        {
            var node = el.QuerySelector(css);
            if (node != null)
            {
                return node.Text();
            }

            return null;
        }

        private static string? GetAttr(IElement el, string css, string attr)
        {
            var node = el.QuerySelector(css);
            if (node != null)
            {
                return node.GetAttribute(attr);
            }

            return null;
        }

        private static string Match(string text, Regex reg)
        {
            var match = reg.Match(text);
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
            }

            return string.Empty;
        }

        private static bool IsEnableAvoidRiskControl()
        {
            return MetaSharkPlugin.Instance?.Configuration.EnableDoubanAvoidRiskControl ?? false;
        }

        private static bool IsBlockedPage(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return false;
            }

            return body.Contains("sec.douban.com", StringComparison.OrdinalIgnoreCase)
                || body.Contains("禁止访问豆瓣", StringComparison.OrdinalIgnoreCase)
                || body.Contains("检测到有异常请求", StringComparison.OrdinalIgnoreCase)
                || body.Contains("有异常请求从你的 IP 发出", StringComparison.OrdinalIgnoreCase)
                || body.Contains("有异常请求从这台机器发出", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string?> ReadBodyUnlessBlockedAsync(HttpResponseMessage response, string url, CancellationToken cancellationToken)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (IsBlockedPage(body))
            {
                LogPageBlocked(this.logger, url, null);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                response.EnsureSuccessStatusCode();
            }

            return body;
        }

        private string FormatOverview(string intro)
        {
            intro = intro.Replace("©豆瓣", string.Empty, StringComparison.Ordinal);
            return this.regOverviewSpace.Replace(intro, "\n").Trim();
        }

        private string GetTitle(string body)
        {
            var title = string.Empty;

            if (IsBlockedPage(body))
            {
                return string.Empty;
            }

            var keyword = Match(body, this.regKeywordMeta);
            if (!string.IsNullOrEmpty(keyword))
            {
                title = keyword.Split(",").FirstOrDefault();
                if (!string.IsNullOrEmpty(title))
                {
                    return title.Trim();
                }
            }

            title = Match(body, this.regTitle);
            return title.Replace("(豆瓣)", string.Empty, StringComparison.Ordinal).Trim();
        }

        private void LoadLoadDoubanCookie()
        {
            var configCookie = MetaSharkPlugin.Instance?.Configuration.DoubanCookies.Trim() ?? string.Empty;

            lock (Lock)
            {
                var container = this.httpClientHandler.CookieContainer ?? this.cookieContainer ?? new CookieContainer();
                try
                {
                    if (!ReferenceEquals(this.httpClientHandler.CookieContainer, container))
                    {
                        this.httpClientHandler.CookieContainer = container;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Handler already started; keep existing container to avoid failing config updates.
                    container = this.httpClientHandler.CookieContainer ?? container;
                }

                this.cookieContainer = container;
                if (!string.IsNullOrEmpty(configCookie))
                {
                    var cookieList = configCookie.Split(';');
                    foreach (var cookie in cookieList)
                    {
                        var cookieArr = cookie.Trim().Split('=');
                        if (cookieArr.Length < 2)
                        {
                            continue;
                        }

                        var key = cookieArr[0].Trim();
                        var value = cookieArr[1].Trim();
                        try
                        {
                            this.cookieContainer.Add(new Cookie(key, value, "/", ".douban.com"));
                        }
                        catch (CookieException ex)
                        {
                            LogCookieAddFailed(this.logger, ex.Message, ex);
                        }
                    }
                }
            }
        }
    }
}
