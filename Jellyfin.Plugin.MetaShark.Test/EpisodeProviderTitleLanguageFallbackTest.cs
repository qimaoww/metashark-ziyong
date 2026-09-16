using System.Reflection;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using Jellyfin.Plugin.MetaShark.Test.EpisodeGroupMapping;
using Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test;

[TestClass]
[TestCategory("Stable")]
[DoNotParallelize]
public class EpisodeProviderTitleLanguageFallbackTest
{
    private const string SeriesTitle = "爱书的下克上：为了成为图书管理员不择手段！";
    private const string TraditionalSeriesTitle = "小書痴的下剋上 為了成為圖書管理員不擇手段！領主的養女";
    private const string SeasonTitle = "领主的养女";
    private const string EpisodeTitle = "星结仪式";

    [TestInitialize]
    public void Initialize()
    {
        ExplicitEpisodeGroupMappingTestHelper.ResetPluginConfiguration();
    }

    [TestCleanup]
    public void Cleanup()
    {
        ExplicitEpisodeGroupMappingTestHelper.ResetPluginConfiguration();
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("Episode 18")]
    [DataRow("第18集")]
    [DataRow("Starbinding Ceremony")]
    [DataRow(TraditionalSeriesTitle)]
    public async Task MissingSimplifiedTitle_UsesEpisodeNumberInsteadOfFilenameSeriesTitle(string? detailsTitle)
    {
        using var harness = new Harness(detailsTitle);

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.IsNotNull(result.Item);
        Assert.AreEqual("第 18 集", result.Item.Name);
        Assert.AreEqual(4, result.Item.ParentIndexNumber);
        Assert.AreEqual(18, result.Item.IndexNumber);
        Assert.IsNull(harness.Store.Peek(harness.Episode.Id));
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("   ")]
    [DataRow("Episode 18")]
    [DataRow("第18集")]
    [DataRow("Starbinding Ceremony")]
    [DataRow(TraditionalSeriesTitle)]
    [DataRow(SeriesTitle)]
    [DataRow(SeasonTitle)]
    public async Task UnusableDetailsTitle_TriesSameEpisodeSimplifiedTranslation(string? detailsTitle)
    {
        using var harness = new Harness(detailsTitle, translationTitle: EpisodeTitle);

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual(EpisodeTitle, result.Item?.Name);
        Assert.AreEqual("这一集的独立简介。", result.Item?.Overview);
        Assert.AreEqual(8.5f, result.Item?.CommunityRating);
    }

    [DataTestMethod]
    [DataRow("第 18 集")]
    [DataRow("第18集")]
    [DataRow("Episode 18")]
    [DataRow(TraditionalSeriesTitle)]
    public async Task SimplifiedTitleValidation_DoesNotDependOnOriginalTitleFormat(string originalTitle)
    {
        using var harness = new Harness(TraditionalSeriesTitle, originalTitle: originalTitle);

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual(originalTitle == TraditionalSeriesTitle ? "第 18 集" : originalTitle, result.Item?.Name);
    }

    [DataTestMethod]
    [DataRow(SeriesTitle)]
    [DataRow(" 爱书的下克上 为了成为图书管理员不择手段 ")]
    [DataRow(SeasonTitle)]
    public async Task ParentTitleInDetails_IsNotAnEpisodeTitle(string detailsTitle)
    {
        using var harness = new Harness(detailsTitle, originalTitle: "第 18 集");

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual("第 18 集", result.Item?.Name);
    }

    [DataTestMethod]
    [DataRow(SeriesTitle)]
    [DataRow(SeasonTitle)]
    [DataRow(TraditionalSeriesTitle)]
    public async Task ParentTitleInTranslations_IsNotAnEpisodeTitle(string translationTitle)
    {
        using var harness = new Harness("Episode 18", translationTitle: translationTitle);

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual("第 18 集", result.Item?.Name);
    }

    [TestMethod]
    public async Task MissingTitle_PreservesExistingEpisodeTitleDuringOverwriteRefresh()
    {
        using var harness = new Harness(null, currentTitle: "手动整理的单集标题");
        harness.HttpContextAccessor.HttpContext = LlmProviderFlowTestHelpers.CreateExplicitRefreshHttpContext(
            harness.Episode.Id.ToString("N"), replaceAllMetadata: true);

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual("手动整理的单集标题", result.Item?.Name);
        Assert.IsNull(harness.Store.Peek(harness.Episode.Id));
    }

    [DataTestMethod]
    [DataRow("Chinese (Simplified)", EpisodeTitle, false)]
    [DataRow("Chinese (Simplified)", EpisodeTitle, true)]
    [DataRow("zh-SG", "皇后回宫", false)]
    [DataRow("Chinese (Traditional)", "皇后回宮", false)]
    [DataRow("zh-HK", "皇后回宮", false)]
    public async Task MissingTitle_PreservesExistingLocalizedEpisodeTitleWhenItMatchesFileName(string language, string title, bool missingEpisode)
    {
        using var harness = new Harness(
            null,
            originalTitle: title,
            currentTitle: title,
            language: language,
            missingEpisode: missingEpisode,
            fileTitle: title);
        harness.HttpContextAccessor.HttpContext = LlmProviderFlowTestHelpers.CreateExplicitRefreshHttpContext(
            harness.Episode.Id.ToString("N"), replaceAllMetadata: true);

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual(title, result.Item?.Name, "已有符合目标中文语言的独立单集名不能仅因与文件名一致就被丢弃。");
    }

    [DataTestMethod]
    [DataRow(SeriesTitle)]
    [DataRow(SeasonTitle)]
    public async Task MissingTitle_DoesNotPreserveKnownParentTitleWhenItMatchesFileName(string title)
    {
        using var harness = new Harness(null, originalTitle: title, currentTitle: title, fileTitle: title);

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual("第 18 集", result.Item?.Name, "已知父级剧名和季名不能因符合目标中文语言就被保留。");
    }

    [TestMethod]
    public async Task MissingTitle_DoesNotPreservePreviouslyScrapedFilenameSeriesTitle()
    {
        using var harness = new Harness(null, currentTitle: TraditionalSeriesTitle);

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual("第 18 集", result.Item?.Name);
    }

    [TestMethod]
    public async Task MissingTitle_PreservesOriginalEpisodeTitleWithoutAFilePath()
    {
        using var harness = new Harness(null, originalTitle: "手动整理的单集标题");
        harness.Info.Path = null;

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual("手动整理的单集标题", result.Item?.Name);
    }

    [TestMethod]
    public async Task MissingTitle_DoesNotUseRawFilenameAsAnEpisodeTitle()
    {
        using var harness = new Harness(null, originalTitle: $"{TraditionalSeriesTitle} - S04E18");

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual("第 18 集", result.Item?.Name);
    }

    [TestMethod]
    public async Task MissingEpisodeDetails_DoesNotReturnFilenameSeriesTitle()
    {
        using var harness = new Harness(null, missingEpisode: true);

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.IsFalse(result.QueriedById);
        Assert.AreEqual("第 18 集", result.Item?.Name);
    }

    [TestMethod]
    public async Task MissingGroupMappedTitle_UsesLocalEpisodeNumberForFallback()
    {
        using var harness = new Harness(null, useGroupMapping: true);

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.IsTrue(result.QueriedById);
        Assert.AreEqual("第 18 集", result.Item?.Name);
        Assert.AreEqual(4, result.Item?.ParentIndexNumber);
        Assert.AreEqual(18, result.Item?.IndexNumber);
    }

    [TestMethod]
    public async Task MissingGroupMappedTitle_UsesMappedEpisodeForTranslation()
    {
        using var harness = new Harness(null, translationTitle: EpisodeTitle, useGroupMapping: true);

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual(EpisodeTitle, result.Item?.Name);
        Assert.AreEqual(4, result.Item?.ParentIndexNumber);
        Assert.AreEqual(18, result.Item?.IndexNumber);
    }

    [DataTestMethod]
    [DataRow("Chinese (Simplified)", EpisodeTitle)]
    [DataRow("zh-CN", "领主的养女与星结仪式")]
    [DataRow("zh-SG", EpisodeTitle)]
    [DataRow("Chinese (Traditional)", "領主的養女與星結儀式")]
    [DataRow("zh-HK", "領主的養女與星結儀式")]
    [DataRow("en", "Starbinding Ceremony")]
    public async Task ValidEpisodeTitle_StillUsesRequestedLanguage(string language, string title)
    {
        using var harness = new Harness(title, language: language, translationTitle: "不应采用的翻译");

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual(title, result.Item?.Name);
    }

    [TestMethod]
    public async Task SimplifiedRequest_DoesNotAcceptTranslationFromAnotherRegion()
    {
        using var harness = new Harness("Episode 18", translationTitle: "千里之外", translationSourceLanguage: "zh-TW");

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual("第 18 集", result.Item?.Name);
    }

    [TestMethod]
    public async Task MissingTitle_SearchMissingMetadataDoesNotQueueSeriesTitle()
    {
        using var harness = new Harness(SeriesTitle, originalTitle: "第 18 集", currentTitle: "第 18 集");
        harness.HttpContextAccessor.HttpContext = LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext(harness.Episode.Id.ToString("N"));

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual("第 18 集", result.Item?.Name);
        Assert.IsNull(harness.Store.Peek(harness.Episode.Id));
    }

    [TestMethod]
    public async Task ValidTranslation_SearchMissingMetadataStillQueuesEpisodeTitle()
    {
        using var harness = new Harness(null, translationTitle: EpisodeTitle, originalTitle: "第 18 集", currentTitle: "第 18 集");
        harness.HttpContextAccessor.HttpContext = LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext(harness.Episode.Id.ToString("N"));

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual(EpisodeTitle, result.Item?.Name);
        Assert.AreEqual(EpisodeTitle, harness.Store.Peek(harness.Episode.Id)?.CandidateTitle);
    }

    [DataTestMethod]
    [DataRow(SeriesTitle, "第 18 集")]
    [DataRow(SeasonTitle, "第 18 集")]
    [DataRow(TraditionalSeriesTitle, "第 18 集")]
    [DataRow(EpisodeTitle, EpisodeTitle)]
    public async Task LlmTitleCompletion_CannotReintroduceParentTitles(string suggestedTitle, string expectedTitle)
    {
        var llmService = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
        llmService.EnqueueResult(LlmScrapingAssistResult.Succeeded(
            new LlmPromptContext { MediaType = nameof(Episode) },
            new LlmScrapingSuggestion { Title = suggestedTitle, Confidence = 0.99 },
            new LlmSearchHints()));
        using var harness = new Harness(null, originalTitle: "第 18 集", llmService: llmService);
        var configuration = MetaSharkPlugin.Instance!.Configuration;
        configuration.EnableLlmAssist = true;
        configuration.LlmAllowTextCompletion = true;
        configuration.LlmBaseUrl = "https://llm.invalid/v1";
        configuration.LlmModel = "test-model";
        configuration.LlmApiKey = "test-key";
        harness.Info.IsAutomated = false;
        harness.HttpContextAccessor.HttpContext = LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext(harness.Episode.Id.ToString("N"));

        var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None);

        Assert.AreEqual(1, llmService.Requests.Count);
        Assert.AreEqual(expectedTitle, result.Item?.Name);
        Assert.AreEqual(expectedTitle == EpisodeTitle ? EpisodeTitle : null, harness.Store.Peek(harness.Episode.Id)?.CandidateTitle);
    }

    private sealed class Harness : IDisposable
    {
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });
        private readonly TmdbApi tmdbApi;

        public Harness(
            string? detailsTitle,
            string? translationTitle = null,
            string originalTitle = TraditionalSeriesTitle,
            string? currentTitle = null,
            string language = "Chinese (Simplified)",
            bool missingEpisode = false,
            bool useGroupMapping = false,
            string? translationSourceLanguage = null,
            ILlmMetadataAssistService? llmService = null,
            string fileTitle = TraditionalSeriesTitle)
        {
            var configuration = MetaSharkPlugin.Instance!.Configuration;
            configuration.EnableTmdb = true;
            configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;
            var resolvedLanguage = ChineseLocalePolicy.ResolveTmdbMetadataLanguage(language, null, "zh-CN")!;
            this.Info = new EpisodeInfo
            {
                Name = originalTitle,
                Path = $"/library/tv/Bookworm/Season 04/{fileTitle} - S04E18.mkv",
                ParentIndexNumber = 4,
                IndexNumber = 18,
                MetadataLanguage = language,
                SeriesProviderIds = new Dictionary<string, string> { [MetadataProvider.Tmdb.ToString()] = "123" },
                IsAutomated = true,
            };
            this.Episode = new Episode { Id = Guid.NewGuid(), Name = currentTitle, Path = this.Info.Path };
            this.HttpContextAccessor = new HttpContextAccessor();
            this.Store = new InMemoryEpisodeTitleBackfillCandidateStore();
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.FindByPath(this.Info.Path, false)).Returns(this.Episode);
            libraryManager.Setup(x => x.FindByPath("/library/tv/Bookworm", true))
                .Returns(new Series { Name = SeriesTitle, Path = "/library/tv/Bookworm" });
            libraryManager.Setup(x => x.FindByPath("/library/tv/Bookworm/Season 04", true))
                .Returns(new Season { Name = SeasonTitle, IndexNumber = 4, Path = "/library/tv/Bookworm/Season 04" });

            this.tmdbApi = new TmdbApi(this.loggerFactory);
            var seasonNumber = useGroupMapping ? 1 : 4;
            var episodeNumber = useGroupMapping ? 62 : 18;
            if (useGroupMapping)
            {
                configuration.TmdbEpisodeGroupMap = "123=bookworm-group";
                ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(
                    this.tmdbApi,
                    "bookworm-group",
                    resolvedLanguage,
                    ExplicitEpisodeGroupMappingTestHelper.CreateGroup(
                        4, "Season 4", ExplicitEpisodeGroupMappingTestHelper.CreateEpisode(17, seasonNumber, episodeNumber)));
            }

            var cacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var cache = (IMemoryCache)cacheField.GetValue(this.tmdbApi)!;
            cache.Set(
                $"episode-123-s{seasonNumber}e{episodeNumber}-{resolvedLanguage}-{resolvedLanguage}",
                missingEpisode ? null : new TvEpisode
                {
                    Name = detailsTitle,
                    Overview = "这一集的独立简介。",
                    StillPath = "/still.jpg",
                    VoteAverage = 8.5,
                });
            cache.Set(
                $"episode-translation-title-123-s{seasonNumber}e{episodeNumber}-{resolvedLanguage}",
                translationTitle == null ? null : new EpisodeLocalizedValue
                {
                    Value = translationTitle,
                    SourceLanguage = translationSourceLanguage ?? resolvedLanguage,
                });
            cache.Set(
                $"episode-translation-overview-123-s{seasonNumber}e{episodeNumber}-{resolvedLanguage}",
                (EpisodeLocalizedValue?)null);

            this.Provider = new EpisodeProvider(
                new DefaultHttpClientFactory(),
                this.loggerFactory,
                libraryManager.Object,
                this.HttpContextAccessor,
                new DoubanApi(this.loggerFactory),
                this.tmdbApi,
                new OmdbApi(this.loggerFactory),
                new ImdbApi(this.loggerFactory),
                new TvdbApi(this.loggerFactory),
                this.Store,
                llmService);
        }

        public EpisodeInfo Info { get; }

        public Episode Episode { get; }

        public HttpContextAccessor HttpContextAccessor { get; }

        public InMemoryEpisodeTitleBackfillCandidateStore Store { get; }

        public EpisodeProvider Provider { get; }

        public void Dispose()
        {
            this.Provider.Dispose();
            this.tmdbApi.Dispose();
            this.loggerFactory.Dispose();
        }
    }
}
