// <copyright file="BaseProvider.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Web;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using TMDbLib.Objects.General;
    using TMDbLib.Objects.Languages;
    using TMDbLib.Objects.TvShows;

    public abstract class BaseProvider
    {
        /// <summary>
        /// Gets the provider name.
        /// </summary>
        public const string DoubanProviderName = "Douban";

        /// <summary>
        /// Gets the provider id.
        /// </summary>
        public const string DoubanProviderId = "DoubanID";

        /// <summary>
        /// Name of the provider.
        /// </summary>
        public const string TmdbProviderName = "TheMovieDb";

        private static readonly Action<ILogger, string, Exception?> LogMetaSharkInfo =
            LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, nameof(Log)), "[MetaShark] {Message}");

        private readonly ILogger logger;
        private readonly IHttpClientFactory httpClientFactory;
        private readonly DoubanApi doubanApi;
        private readonly TmdbApi tmdbApi;
        private readonly OmdbApi omdbApi;
        private readonly ImdbApi imdbApi;
        private readonly ILibraryManager libraryManager;
        private readonly IHttpContextAccessor httpContextAccessor;

        private readonly Regex regMetaSourcePrefix = new Regex(@"^\[.+\]", RegexOptions.Compiled);
        private readonly Regex regSeasonNameSuffix = new Regex(@"\s第[0-9一二三四五六七八九十]+?季$|\sSeason\s\d+?$|(?<![0-9a-zA-Z])\d$", RegexOptions.Compiled);
        private readonly Regex regDoubanIdAttribute = new Regex(@"\[(?:douban|doubanid)-(\d+?)\]", RegexOptions.Compiled);
        private readonly Regex regTmdbIdAttribute = new Regex(@"\[(?:tmdb|tmdbid)-(\d+?)\]", RegexOptions.Compiled);

        protected BaseProvider(IHttpClientFactory httpClientFactory, ILogger logger, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi)
        {
            this.doubanApi = doubanApi;
            this.tmdbApi = tmdbApi;
            this.omdbApi = omdbApi;
            this.imdbApi = imdbApi;
            this.libraryManager = libraryManager;
            this.logger = logger;
            this.httpClientFactory = httpClientFactory;
            this.httpContextAccessor = httpContextAccessor;
        }

        protected static PluginConfiguration Config => MetaSharkPlugin.Instance?.Configuration ?? new PluginConfiguration();

        protected ILogger Logger => this.logger;

        protected IHttpClientFactory HttpClientFactory => this.httpClientFactory;

        protected DoubanApi DoubanApi => this.doubanApi;

        protected TmdbApi TmdbApi => this.tmdbApi;

        protected OmdbApi OmdbApi => this.omdbApi;

        protected ImdbApi ImdbApi => this.imdbApi;

        protected ILibraryManager LibraryManager => this.libraryManager;

        protected IHttpContextAccessor HttpContextAccessor => this.httpContextAccessor;

        protected Regex RegMetaSourcePrefix => this.regMetaSourcePrefix;

        protected Regex RegSeasonNameSuffix => this.regSeasonNameSuffix;

        protected Regex RegDoubanIdAttribute => this.regDoubanIdAttribute;

        protected Regex RegTmdbIdAttribute => this.regTmdbIdAttribute;

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(url);
            return this.GetImageResponse(new Uri(url, UriKind.RelativeOrAbsolute), cancellationToken);
        }

        /// <inheritdoc />
        public async Task<HttpResponseMessage> GetImageResponse(Uri url, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(url);
            var urlString = url.ToString();
            if (urlString.Contains("doubanio.com", StringComparison.OrdinalIgnoreCase))
            {
                // 相对链接补全
                if (!urlString.StartsWith("http", StringComparison.OrdinalIgnoreCase) && MetaSharkPlugin.Instance != null)
                {
                    urlString = MetaSharkPlugin.Instance.GetLocalApiBaseUrl().ToString().TrimEnd('/') + urlString;
                }

                // 包含了代理地址的话，从url解析出原始豆瓣图片地址
                if (urlString.Contains("/proxy/image", StringComparison.Ordinal))
                {
                    var uri = new UriBuilder(urlString);
                    var originalUrl = HttpUtility.ParseQueryString(uri.Query).Get("url");
                    if (!string.IsNullOrEmpty(originalUrl))
                    {
                        urlString = originalUrl;
                    }
                }

                this.Log("GetImageResponse url: {0}", urlString);

                // 豆瓣图，带referer下载
                using (var requestMessage = new HttpRequestMessage(HttpMethod.Get, urlString))
                {
                    requestMessage.Headers.Add("User-Agent", DoubanApi.HTTPUSERAGENT);
                    requestMessage.Headers.Add("Referer", DoubanApi.HTTPREFERER);
                    using var client = this.HttpClientFactory.CreateClient();
                    return await client.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                this.Log("GetImageResponse url: {0}", urlString);
                using var client = this.HttpClientFactory.CreateClient();
                return await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<string?> GuestDoubanSeasonByYearAsync(string seriesName, int? year, int? seasonNumber, CancellationToken cancellationToken)
        {
            if (year == null || year == 0)
            {
                return null;
            }

            this.Log($"GuestDoubanSeasonByYear of [name]: {seriesName} [year]: {year}");

            // 先通过suggest接口查找，减少搜索页访问次数，避免封禁（suggest没法区分电影或电视剧，排序也比搜索页差些）
            if (Config.EnableDoubanAvoidRiskControl)
            {
                var suggestResult = await this.DoubanApi.SearchBySuggestAsync(seriesName, cancellationToken).ConfigureAwait(false);
                var suggestItem = suggestResult.Where(x => x.Year == year && x.Name == seriesName).FirstOrDefault();
                if (suggestItem != null)
                {
                    this.Log($"Found douban [id]: {suggestItem.Name}({suggestItem.Sid}) (suggest)");
                    return suggestItem.Sid;
                }

                suggestItem = suggestResult.Where(x => x.Year == year).FirstOrDefault();
                if (suggestItem != null)
                {
                    this.Log($"Found douban [id]: {suggestItem.Name}({suggestItem.Sid}) (suggest)");
                    return suggestItem.Sid;
                }
            }

            // 通过搜索页面查找
            var result = await this.DoubanApi.SearchAsync(seriesName, cancellationToken).ConfigureAwait(false);
            var item = result.Where(x => x.Category == "电视剧" && x.Year == year).FirstOrDefault();
            if (item != null && !string.IsNullOrEmpty(item.Sid))
            {
                // 判断名称中是否有第X季，有的话和seasonNumber比较，用于修正多季都在同一年时，每次都是错误取第一个的情况
                var nameIndexNumber = ParseChineseSeasonNumberByName(item.Name);
                if (nameIndexNumber.HasValue && seasonNumber.HasValue && nameIndexNumber != seasonNumber)
                {
                    this.Log("GuestDoubanSeasonByYear not found!");
                    return null;
                }

                this.Log($"Found douban [id]: {item.Name}({item.Sid})");
                return item.Sid;
            }

            this.Log("GuestDoubanSeasonByYear not found!");
            return null;
        }

        public async Task<string?> GuestDoubanSeasonBySeasonNameAsync(string name, int? seasonNumber, CancellationToken cancellationToken)
        {
            if (seasonNumber is null or 0)
            {
                return null;
            }

            var chineseSeasonNumber = Utils.ToChineseNumber(seasonNumber);
            if (string.IsNullOrEmpty(chineseSeasonNumber))
            {
                return null;
            }

            var seasonName = $"{name}{seasonNumber}";
            var chineseSeasonName = $"{name} 第{chineseSeasonNumber}季";
            if (seasonNumber == 1)
            {
                seasonName = name;
            }

            this.Log($"GuestDoubanSeasonBySeasonNameAsync of [name]: {seasonName} 或 {chineseSeasonName}");

            // 通过名称精确匹配
            var result = await this.DoubanApi.SearchAsync(name, cancellationToken).ConfigureAwait(false);
            var item = result.Where(x => x.Category == "电视剧" && x.Rating > 0 && (x.Name == seasonName || x.Name == chineseSeasonName)).FirstOrDefault();
            if (item != null && !string.IsNullOrEmpty(item.Sid))
            {
                this.Log($"Found douban [id]: {item.Name}({item.Sid})");
                return item.Sid;
            }

            this.Log("GuestDoubanSeasonBySeasonNameAsync not found!");
            return null;
        }

        public int? GuessSeasonNumberByDirectoryName(string path)
        {
            // TODO: 有时 series name 中会带有季信息
            // 当没有 season 级目录时，或 season 文件夹特殊不规范命名时，会解析不到 seasonNumber，这时 path 为空，直接返回
            if (string.IsNullOrEmpty(path))
            {
                this.Log($"Season path is empty!");
                return null;
            }

            var fileName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            // 中文季名
            var regSeason = new Regex(@"第([0-9零一二三四五六七八九]+?)(季|部)", RegexOptions.Compiled);
            var match = regSeason.Match(fileName);
            if (match.Success && match.Groups.Count > 1)
            {
                var seasonNumber = match.Groups[1].Value.ToInt();
                if (seasonNumber <= 0)
                {
                    seasonNumber = Utils.ChineseNumberToInt(match.Groups[1].Value) ?? 0;
                }

                if (seasonNumber > 0)
                {
                    this.Log($"Found season number of filename: {fileName} seasonNumber: {seasonNumber}");
                    return seasonNumber;
                }
            }

            // SXX 季名
            regSeason = new Regex(@"(?<![a-z])S(\d\d?)(?![0-9a-z])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            match = regSeason.Match(fileName);
            if (match.Success && match.Groups.Count > 1)
            {
                var seasonNumber = match.Groups[1].Value.ToInt();
                if (seasonNumber > 0)
                {
                    this.Log($"Found season number of filename: {fileName} seasonNumber: {seasonNumber}");
                    return seasonNumber;
                }
            }

            // 动漫季特殊命名
            var seasonNameMap = new Dictionary<string, int>()
            {
                { @"[ ._](I|1st)[ ._]", 1 },
                { @"[ ._](II|2nd)[ ._]", 2 },
                { @"[ ._](III|3rd)[ ._]", 3 },
                { @"[ ._](IIII|4th)[ ._]", 3 },
            };

            foreach (var entry in seasonNameMap)
            {
                if (Regex.IsMatch(fileName, entry.Key))
                {
                    this.Log($"Found season number of filename: {fileName} seasonNumber: {entry.Value}");
                    return entry.Value;
                }
            }

            // // 带数字末尾的
            // match = Regex.Match(fileName, @"[ ._](\d{1,2})$");
            // if (match.Success && match.Groups.Count > 1)
            // {
            //     var seasonNumber = match.Groups[1].Value.ToInt();
            //     if (seasonNumber > 0)
            //     {
            //         this.Log($"Found season number of filename: {fileName} seasonNumber: {seasonNumber}");
            //         return seasonNumber;
            //     }
            // }
            return null;
        }

        protected static Uri GetLocalProxyImageUrl(Uri url)
        {
            ArgumentNullException.ThrowIfNull(url);
            var baseUrl = MetaSharkPlugin.Instance?.GetLocalApiBaseUrl().ToString() ?? string.Empty;
            var proxyBaseUrl = Config.DoubanImageProxyBaseUrl;
            if (!string.IsNullOrWhiteSpace(proxyBaseUrl))
            {
                baseUrl = proxyBaseUrl.TrimEnd('/');
            }

            var encodedUrl = HttpUtility.UrlEncode(url.ToString());
            return new Uri($"{baseUrl}/plugin/metashark/proxy/image/?url={encodedUrl}", UriKind.Absolute);
        }

        protected static bool IsDoubanAllowed(DefaultScraperSemantic semantic)
        {
            return DefaultScraperPolicy.IsDoubanAllowed(Config, semantic);
        }

        /// <summary>
         /// Adjusts the image's language code preferring the 5 letter language code eg. en-US.
         /// </summary>
        /// <param name="imageLanguage">The image's actual language code.</param>
        /// <param name="requestLanguage">The requested language code.</param>
        /// <returns>The language code.</returns>
        protected static string AdjustImageLanguage(string imageLanguage, string requestLanguage)
        {
            if (string.Equals(imageLanguage, "zh", StringComparison.OrdinalIgnoreCase)
                && ChineseLocalePolicy.IsChineseRequest(requestLanguage)
                && !string.Equals(requestLanguage, "zh", StringComparison.OrdinalIgnoreCase))
            {
                return imageLanguage;
            }

            if (!string.IsNullOrEmpty(imageLanguage)
                && !string.IsNullOrEmpty(requestLanguage)
                && requestLanguage.Length > 2
                && imageLanguage.Length == 2
                && requestLanguage.StartsWith(imageLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return requestLanguage;
            }

            return imageLanguage;
        }

        // 把第一个备选图片语言设为空，提高图片优先级，保证备选语言图片优先级比英文高
        protected static Collection<RemoteImageInfo> AdjustImageLanguagePriority(IList<RemoteImageInfo> images, string preferLanguage, string alternativeLanguage)
        {
            var imagesOrdered = images.OrderByLanguageDescending(preferLanguage, alternativeLanguage).ToList();

            // 不存在默认语言图片，且备选语言是日语
            if (alternativeLanguage == "ja" && !imagesOrdered.Any(x => x.Language == preferLanguage))
            {
                var idx = imagesOrdered.FindIndex(x => x.Language == alternativeLanguage);
                if (idx >= 0)
                {
                    imagesOrdered[idx].Language = null;
                }
            }

            return new Collection<RemoteImageInfo>(imagesOrdered);
        }

        /// <summary>
        /// Maps the TMDB provided roles for crew members to Jellyfin roles.
        /// </summary>
        /// <param name="crew">Crew member to map against the Jellyfin person types.</param>
        /// <returns>The Jellyfin person type.</returns>
        [SuppressMessage("Microsoft.Maintainability", "CA1309: Use ordinal StringComparison", Justification = "AFAIK we WANT InvariantCulture comparisons here and not Ordinal")]
        protected static string MapCrewToPersonType(Crew crew)
        {
            ArgumentNullException.ThrowIfNull(crew);
            if (crew.Department.Equals("production", StringComparison.InvariantCultureIgnoreCase)
                && crew.Job.Contains("director", StringComparison.InvariantCultureIgnoreCase))
            {
                return PersonType.Director;
            }

            if (crew.Department.Equals("production", StringComparison.InvariantCultureIgnoreCase)
                && crew.Job.Contains("producer", StringComparison.InvariantCultureIgnoreCase))
            {
                return PersonType.Producer;
            }

            if (crew.Department.Equals("writing", StringComparison.InvariantCultureIgnoreCase))
            {
                return PersonType.Writer;
            }

            return string.Empty;
        }

        protected static string GetOriginalFileName(ItemLookupInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);
            if (string.IsNullOrEmpty(info.Path))
            {
                return info.Name;
            }

            switch (info)
            {
                case MovieInfo:
                    // 当movie放在文件夹中并只有一部影片时, info.name是根据文件夹名解析的，但info.Path是影片的路径名
                    // 当movie放在文件夹中并有多部影片时，info.Name和info.Path都是具体的影片
                    var directoryName = Path.GetFileName(Path.GetDirectoryName(info.Path));
                    if (!string.IsNullOrEmpty(directoryName) && !string.IsNullOrEmpty(info.Name) && directoryName.Contains(info.Name, StringComparison.Ordinal))
                    {
                        return directoryName;
                    }

                    return Path.GetFileNameWithoutExtension(info.Path) ?? info.Name;
                case EpisodeInfo:
                    return Path.GetFileNameWithoutExtension(info.Path) ?? info.Name;
                default:
                    // series和season的info.Path是文件夹路径
                    return Path.GetFileName(info.Path) ?? info.Name;
            }
        }

        protected DefaultScraperSemantic ResolveMetadataSemantic(ItemLookupInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);

            if (this.IsManualMatchRequest())
            {
                return DefaultScraperSemantic.ManualMatch;
            }

            return info.IsAutomated ? DefaultScraperSemantic.AutomaticRefresh : DefaultScraperSemantic.UserRefresh;
        }

        protected DefaultScraperSemantic ResolveImageSemantic()
        {
            return this.IsManualImageRequest()
                ? DefaultScraperSemantic.ManualSearch
                : DefaultScraperSemantic.AutomaticRefresh;
        }

        protected bool IsManualMatchRequest()
        {
            var request = this.HttpContextAccessor.HttpContext?.Request;
            var path = request?.Path.Value;
            return request != null
                && HttpMethods.IsPost(request.Method)
                && !string.IsNullOrEmpty(path)
                && path.StartsWith("/Items/RemoteSearch/Apply", StringComparison.OrdinalIgnoreCase);
        }

        protected bool IsManualImageRequest()
        {
            var request = this.HttpContextAccessor.HttpContext?.Request;
            var path = request?.Path.Value;
            return request != null
                && HttpMethods.IsGet(request.Method)
                && !string.IsNullOrEmpty(path)
                && path.Contains("/RemoteImages", StringComparison.OrdinalIgnoreCase);
        }

        protected async Task<TvEpisode?> GetEpisodeAsync(int seriesTmdbId, int? seasonNumber, int? episodeNumber, string displayOrder, string? language, string? imageLanguages, CancellationToken cancellationToken)
        {
            var resolvedEpisodeRequest = await this.ResolveEpisodeRequestAsync(seriesTmdbId, seasonNumber, episodeNumber, displayOrder, language, imageLanguages, cancellationToken)
                .ConfigureAwait(false);
            if (resolvedEpisodeRequest == null)
            {
                return null;
            }

            var normalizedLanguage = language ?? string.Empty;
            var normalizedImageLanguages = imageLanguages ?? string.Empty;

            return await this.TmdbApi
                .GetEpisodeAsync(seriesTmdbId, resolvedEpisodeRequest.Value.SeasonNumber, resolvedEpisodeRequest.Value.EpisodeNumber, normalizedLanguage, normalizedImageLanguages, cancellationToken)
                .ConfigureAwait(false);
        }

        protected async Task<EpisodeLocalizedValue?> GetEpisodeTranslationTitleAsync(int seriesTmdbId, int? seasonNumber, int? episodeNumber, string displayOrder, string? language, string? imageLanguages, CancellationToken cancellationToken)
        {
            var resolvedEpisodeRequest = await this.ResolveEpisodeRequestAsync(seriesTmdbId, seasonNumber, episodeNumber, displayOrder, language, imageLanguages, cancellationToken)
                .ConfigureAwait(false);
            if (resolvedEpisodeRequest == null)
            {
                return null;
            }

            return await this.TmdbApi
                .GetEpisodeTranslationTitleAsync(seriesTmdbId, resolvedEpisodeRequest.Value.SeasonNumber, resolvedEpisodeRequest.Value.EpisodeNumber, language, cancellationToken)
                .ConfigureAwait(false);
        }

        protected async Task<EpisodeLocalizedValue?> GetEpisodeTranslationOverviewAsync(int seriesTmdbId, int? seasonNumber, int? episodeNumber, string displayOrder, string? language, string? imageLanguages, CancellationToken cancellationToken)
        {
            var resolvedEpisodeRequest = await this.ResolveEpisodeRequestAsync(seriesTmdbId, seasonNumber, episodeNumber, displayOrder, language, imageLanguages, cancellationToken)
                .ConfigureAwait(false);
            if (resolvedEpisodeRequest == null)
            {
                return null;
            }

            return await this.TmdbApi
                .GetEpisodeTranslationOverviewAsync(seriesTmdbId, resolvedEpisodeRequest.Value.SeasonNumber, resolvedEpisodeRequest.Value.EpisodeNumber, language, cancellationToken)
                .ConfigureAwait(false);
        }

        protected async Task<(int SeasonNumber, int EpisodeNumber)?> ResolveEpisodeRequestAsync(int seriesTmdbId, int? seasonNumber, int? episodeNumber, string displayOrder, string? language, string? imageLanguages, CancellationToken cancellationToken)
        {
            if (!seasonNumber.HasValue || !episodeNumber.HasValue)
            {
                return null;
            }

            var normalizedLanguage = language ?? string.Empty;
            var normalizedImageLanguages = imageLanguages ?? string.Empty;
            var resolvedSeasonNumber = seasonNumber.Value;
            var resolvedEpisodeNumber = episodeNumber.Value;
            var resolvedByEpisodeGroupMapping = false;
            var seriesId = seriesTmdbId.ToString(CultureInfo.InvariantCulture);
            if (TmdbEpisodeGroupMapping.TryGetGroupId(Config.TmdbEpisodeGroupMap, seriesId, out var groupId))
            {
                this.Log("TMDb episode group mapping hit: seriesId={0} groupId={1} season={2} episode={3}", seriesId, groupId, seasonNumber, episodeNumber);
                var group = await this.TmdbApi
                    .GetEpisodeGroupByIdAsync(groupId, normalizedLanguage, cancellationToken)
                    .ConfigureAwait(false);
                if (group != null)
                {
                    var season = group.Groups.Find(s => s.Order == seasonNumber);

                    // Episode order starts at 0
                    var ep = season?.Episodes.Find(e => e.Order == episodeNumber - 1);
                    if (ep is not null)
                    {
                        this.Log("TMDb episode group mapping resolved: seriesId={0} groupId={1} season={2} episode={3} -> S{4}E{5}", seriesId, groupId, seasonNumber, episodeNumber, ep.SeasonNumber, ep.EpisodeNumber);
                        resolvedSeasonNumber = ep.SeasonNumber;
                        resolvedEpisodeNumber = ep.EpisodeNumber;
                        resolvedByEpisodeGroupMapping = true;
                    }
                }
            }

            // 根据剧集组获取对应的剧集信息
            if (!resolvedByEpisodeGroupMapping && !string.IsNullOrWhiteSpace(displayOrder))
            {
                var group = await this.TmdbApi
                    .GetSeriesGroupAsync(seriesTmdbId, displayOrder, normalizedLanguage, normalizedImageLanguages, cancellationToken)
                    .ConfigureAwait(false);
                if (group != null)
                {
                    var season = group.Groups.Find(s => s.Order == seasonNumber);

                    // Episode order starts at 0
                    var ep = season?.Episodes.Find(e => e.Order == episodeNumber - 1);
                    if (ep is not null)
                    {
                        this.Log("TMDb display order mapping resolved: seriesId={0} displayOrder={1} season={2} episode={3} -> S{4}E{5}", seriesId, displayOrder, seasonNumber, episodeNumber, ep.SeasonNumber, ep.EpisodeNumber);
                        resolvedSeasonNumber = ep.SeasonNumber;
                        resolvedEpisodeNumber = ep.EpisodeNumber;
                    }
                }
            }

            return (resolvedSeasonNumber, resolvedEpisodeNumber);
        }

        protected async Task<string?> GuessByDoubanAsync(ItemLookupInfo info, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);
            var fileName = GetOriginalFileName(info);

            // 从文件名属性格式获取，如[douban-12345]或[doubanid-12345]
            var doubanId = this.RegDoubanIdAttribute.FirstMatchGroup(fileName);
            if (!string.IsNullOrWhiteSpace(doubanId))
            {
                this.Log($"Found douban [id] by attr: {doubanId}");
                return doubanId;
            }

            var parseResult = NameParser.Parse(fileName);
            var searchName = !string.IsNullOrEmpty(parseResult.ChineseName) ? parseResult.ChineseName : parseResult.Name;
            info.Year = parseResult.Year;  // 默认parser对anime年份会解析出错，以anitomy为准

            this.Log($"GuessByDouban of [name]: {info.Name} [file_name]: {fileName} [year]: {info.Year} [search name]: {searchName}");
            List<DoubanSubject> result;
            DoubanSubject? item;

            // 假如存在年份，先通过suggest接口查找，减少搜索页访问次数，避免封禁（suggest没法区分电影或电视剧，排序也比搜索页差些）
            if (Config.EnableDoubanAvoidRiskControl)
            {
                if (info.Year != null && info.Year > 0)
                {
                    result = await this.DoubanApi.SearchBySuggestAsync(searchName, cancellationToken).ConfigureAwait(false);
                    item = result.Where(x => x.Year == info.Year && x.Name == searchName).FirstOrDefault();
                    if (item != null)
                    {
                        this.Log($"Found douban [id]: {item.Name}({item.Sid}) (suggest)");
                        return item.Sid;
                    }

                    item = result.Where(x => x.Year == info.Year).FirstOrDefault();
                    if (item != null)
                    {
                        this.Log($"Found douban [id]: {item.Name}({item.Sid}) (suggest)");
                        return item.Sid;
                    }
                }
            }

            // 通过搜索页面查找
            result = await this.DoubanApi.SearchAsync(searchName, cancellationToken).ConfigureAwait(false);
            var cat = info is MovieInfo ? "电影" : "电视剧";

            // 存在年份时，返回对应年份的电影
            if (info.Year != null && info.Year > 0)
            {
                item = result.Where(x => x.Category == cat && x.Year == info.Year).FirstOrDefault();
                if (item != null)
                {
                    this.Log($"Found douban [id]: {item.Name}({item.Sid})");
                    return item.Sid;
                }
                else
                {
                    // TODO: 有年份找不到，直接返回，由其他插件接手查找（还是返回第一个好？？？？）
                    return null;
                }
            }

            //// 不存在年份，计算相似度，返回相似度大于0.8的第一个（可能出现冷门资源名称更相同的情况。。。）
            // var jw = new JaroWinkler();
            // item = result.Where(x => x.Category == cat && x.Rating > 5).OrderByDescending(x => Math.Max(jw.Similarity(searchName, x.Name), jw.Similarity(searchName, x.OriginalName))).FirstOrDefault();
            // if (item != null && Math.Max(jw.Similarity(searchName, item.Name), jw.Similarity(searchName, item.OriginalName)) > 0.8)
            // {
            //     return item.Sid;
            // }

            // 不存在年份时，返回豆瓣结果第一个
            item = result.Where(x => x.Category == cat).FirstOrDefault();
            if (item != null)
            {
                this.Log($"Found douban [id] by first match: {item.Name}({item.Sid})");
                return item.Sid;
            }

            return null;
        }

        protected async Task<string?> GuestByTmdbAsync(ItemLookupInfo info, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);
            var fileName = GetOriginalFileName(info);

            // 从文件名属性格式获取，如[tmdb-12345]或{tmdb-12345}
            var tmdbId = this.RegTmdbIdAttribute.FirstMatchGroup(fileName);
            if (!string.IsNullOrWhiteSpace(tmdbId))
            {
                this.Log($"Found tmdb [id] by attr: {tmdbId}");
                return tmdbId;
            }

            var parseResult = NameParser.Parse(fileName);
            var searchName = !string.IsNullOrEmpty(parseResult.ChineseName) ? parseResult.ChineseName : parseResult.Name;
            info.Year = parseResult.Year;  // 默认parser对anime年份会解析出错，以anitomy为准

            return await this.GuestByTmdbAsync(searchName, info.Year, info, cancellationToken).ConfigureAwait(false);
        }

        protected async Task<string?> GuestByTmdbAsync(string name, int? year, ItemLookupInfo info, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);
            var fileName = GetOriginalFileName(info);

            this.Log($"GuestByTmdb of [name]: {name} [year]: {year}");
            switch (info)
            {
                case MovieInfo:
                    var movieResults = await this.TmdbApi.SearchMovieAsync(name, year ?? 0, info.MetadataLanguage, cancellationToken).ConfigureAwait(false);

                    // 结果可能多个，优先取名称完全相同的
                    var movieItem = FindFirst(movieResults, x => x.Title == name || x.OriginalTitle == name);
                    if (movieItem != null)
                    {
                        this.Log($"Found tmdb [id]: {movieItem.Title}({movieItem.Id})");
                        return movieItem.Id.ToString(CultureInfo.InvariantCulture);
                    }

                    movieItem = movieResults.Count > 0 ? movieResults[0] : null;
                    if (movieItem != null)
                    {
                        // bt种子都是英文名，但电影是中日韩泰印法地区时，都不适用相似匹配，去掉限制
                        this.Log($"Found tmdb [id]: {movieItem.Title}({movieItem.Id})");
                        return movieItem.Id.ToString(CultureInfo.InvariantCulture);
                    }

                    break;
                case SeriesInfo:
                    var seriesResults = await this.TmdbApi.SearchSeriesAsync(name, info.MetadataLanguage, cancellationToken).ConfigureAwait(false);

                    // 年份在豆瓣可能匹配到第三季，但tmdb年份都是第一季的，可能匹配不上（例如：脱口秀大会）
                    // 优先年份和名称同时匹配
                    var seriesItem = FindFirst(seriesResults, x => (x.Name == name || x.OriginalName == name) && x.FirstAirDate?.Year == year);
                    if (seriesItem != null)
                    {
                        this.Log($"Found tmdb [id]: -> {seriesItem.Name}({seriesItem.Id})");
                        return seriesItem.Id.ToString(CultureInfo.InvariantCulture);
                    }

                    // 年份匹配
                    seriesItem = FindFirst(seriesResults, x => x.FirstAirDate?.Year == year);
                    if (seriesItem != null)
                    {
                        this.Log($"Found tmdb [id]: -> {seriesItem.Name}({seriesItem.Id})");
                        return seriesItem.Id.ToString(CultureInfo.InvariantCulture);
                    }

                    // 取名称完全相同的，可能综艺会有纯享版等非标准版本(例如：一年一度喜剧大赛)
                    seriesItem = FindFirst(seriesResults, x => x.Name == name || x.OriginalName == name);
                    if (seriesItem != null)
                    {
                        this.Log($"Found tmdb [id]: -> {seriesItem.Name}({seriesItem.Id})");
                        return seriesItem.Id.ToString(CultureInfo.InvariantCulture);
                    }

                    seriesItem = seriesResults.Count > 0 ? seriesResults[0] : null;
                    if (seriesItem != null)
                    {
                        // bt种子都是英文名，但电影是中日韩泰印法地区时，都不适用相似匹配，去掉限制
                        this.Log($"Found tmdb [id]: -> {seriesItem.Name}({seriesItem.Id})");
                        return seriesItem.Id.ToString(CultureInfo.InvariantCulture);
                    }

                    break;
            }

            this.Log($"Not found tmdb id by [name]: {name} [year]: {year}");
            return null;
        }

        protected async Task<string?> GetTmdbIdByImdbAsync(string imdb, string language, ItemLookupInfo info, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(imdb))
            {
                return null;
            }

            // 通过imdb获取tmdbId
            var findResult = await this.TmdbApi.FindByExternalIdAsync(imdb, TMDbLib.Objects.Find.FindExternalSource.Imdb, language, cancellationToken).ConfigureAwait(false);

            switch (info)
            {
                case MovieInfo:
                    if (findResult?.MovieResults != null && findResult.MovieResults.Count > 0)
                    {
                        var tmdbId = findResult.MovieResults[0].Id;
                        this.Log($"Found tmdb [id]: {tmdbId} by imdb id: {imdb}");
                        return $"{tmdbId}";
                    }

                    break;
                case SeriesInfo:
                    if (findResult?.TvResults != null && findResult.TvResults.Count > 0)
                    {
                        var tmdbId = findResult.TvResults[0].Id;
                        this.Log($"Found tmdb [id]: {tmdbId} by imdb id: {imdb}");
                        return $"{tmdbId}";
                    }

                    if (findResult?.TvEpisode != null && findResult.TvEpisode.Count > 0)
                    {
                        var tmdbId = findResult.TvEpisode[0].ShowId;
                        this.Log($"Found tmdb [id]: {tmdbId} by imdb id: {imdb}");
                        return $"{tmdbId}";
                    }

                    if (findResult?.TvSeason != null && findResult.TvSeason.Count > 0)
                    {
                        var tmdbId = findResult.TvSeason[0].ShowId;
                        this.Log($"Found tmdb [id]: {tmdbId} by imdb id: {imdb}");
                        return $"{tmdbId}";
                    }

                    break;
                default:
                    break;
            }

            this.Log($"Not found tmdb id by imdb id: {imdb}");
            return null;
        }

        /// <summary>
        /// 豆瓣的imdb id可能是旧的，需要先从omdb接口获取最新的imdb id.
        /// </summary>
        /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
        protected async Task<string> CheckNewImdbID(string imdb, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(imdb))
            {
                return imdb;
            }

            var omdbItem = await this.OmdbApi.GetByImdbID(imdb, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(omdbItem?.ImdbID))
            {
                imdb = omdbItem.ImdbID;
            }

            return imdb;
        }

        protected Uri GetProxyImageUrl(Uri url)
        {
            ArgumentNullException.ThrowIfNull(url);
            var baseUrl = this.GetBaseUrl();
            var encodedUrl = HttpUtility.UrlEncode(url.ToString());
            return new Uri($"{baseUrl}/plugin/metashark/proxy/image/?url={encodedUrl}", UriKind.Absolute);
        }

        protected void Log(string? message, params object?[] args)
        {
            var format = message ?? string.Empty;
            var formatted = string.Format(CultureInfo.InvariantCulture, format, args);
            LogMetaSharkInfo(this.Logger, formatted, null);
        }

        protected string GetDoubanPoster(DoubanSubject subject)
        {
            ArgumentNullException.ThrowIfNull(subject);
            if (string.IsNullOrEmpty(subject.Img))
            {
                return string.Empty;
            }

            var url = Config.EnableDoubanLargePoster ? subject.ImgLarge : subject.ImgMiddle;
            return this.GetProxyImageUrl(new Uri(url, UriKind.Absolute)).ToString();
        }

        protected string? GetOriginalSeasonPath(EpisodeInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);
            if (info.Path == null)
            {
                return null;
            }

            var seasonPath = Path.GetDirectoryName(info.Path);
            if (string.IsNullOrEmpty(seasonPath))
            {
                return null;
            }

            var item = this.LibraryManager.FindByPath(seasonPath, true);

            // 没有季文件夹
            if (item is Series)
            {
                return null;
            }

            return seasonPath;
        }

        protected bool IsVirtualSeason(EpisodeInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);
            if (info.Path == null)
            {
                return false;
            }

            var seasonPath = Path.GetDirectoryName(info.Path);
            if (string.IsNullOrEmpty(seasonPath))
            {
                return false;
            }

            var parent = this.LibraryManager.FindByPath(seasonPath, true);

            // 没有季文件夹
            if (parent is Series)
            {
                return true;
            }

            var seriesPath = Path.GetDirectoryName(seasonPath);
            if (string.IsNullOrEmpty(seriesPath))
            {
                return false;
            }

            var series = this.LibraryManager.FindByPath(seriesPath, true);

            // 季文件夹不规范，没法识别
            if (series is Series && parent is not Season)
            {
                return true;
            }

            return false;
        }

        protected string RemoveSeasonSuffix(string name)
        {
            return this.RegSeasonNameSuffix.Replace(name, string.Empty);
        }

        private static string GetImageLanguageParam(string preferredLanguage, string? originalLanguage = null)
        {
            var languageCodeMap = new Dictionary<string, string>()
            {
                { "法语", "fr" },
                { "德语", "de" },
                { "日语", "ja" },
                { "俄语", "ru" },
                { "韩语", "ko" },
                { "泰语", "th" },
            };
            if (!string.IsNullOrEmpty(originalLanguage))
            {
                if (languageCodeMap.TryGetValue(originalLanguage, out var lang) && lang != preferredLanguage)
                {
                    return $"{preferredLanguage},{lang}";
                }
            }

            return preferredLanguage;
        }

        private static int? ParseChineseSeasonNumberByName(string name)
        {
            var regSeason = new Regex(@"\s第([0-9零一二三四五六七八九]+?)(季|部)", RegexOptions.Compiled);
            var match = regSeason.Match(name);
            if (match.Success && match.Groups.Count > 1)
            {
                var seasonNumber = match.Groups[1].Value.ToInt();
                if (seasonNumber <= 0)
                {
                    seasonNumber = Utils.ChineseNumberToInt(match.Groups[1].Value) ?? 0;
                }

                if (seasonNumber > 0)
                {
                    return seasonNumber;
                }
            }

            return null;
        }

        private static T? FindFirst<T>(IReadOnlyList<T> items, Func<T, bool> predicate)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (predicate(item))
                {
                    return item;
                }
            }

            return default;
        }

        private string GetBaseUrl()
        {
            // 配置优先
            var proxyBaseUrl = Config.DoubanImageProxyBaseUrl;
            if (!string.IsNullOrWhiteSpace(proxyBaseUrl))
            {
                return proxyBaseUrl.TrimEnd('/');
            }

            // TODO：http请求时，获取请求的host (nginx代理/docker中部署时，没配置透传host时，本方式会有问题)
            // 除自动扫描之外都会执行这里，修改图片功能图片是直接下载，不走插件图片代理处理函数，host拿不到就下载不了
            if (MetaSharkPlugin.Instance != null && this.HttpContextAccessor.HttpContext != null)
            {
                return MetaSharkPlugin.Instance.GetApiBaseUrl(this.HttpContextAccessor.HttpContext.Request).ToString();
            }

            // 自动扫描刷新时，直接使用本地地址(127.0.0.1)
            return MetaSharkPlugin.Instance?.GetLocalApiBaseUrl().ToString() ?? string.Empty;
        }
    }
}
