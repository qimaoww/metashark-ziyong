using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.SimilarItems;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jellyfin.Plugin.MetaShark.Test;

[TestClass]
public class MetaSharkSimilarItemsProviderTest
{
    [TestMethod]
    [TestCategory("Stable")]
    public void ToScore_NormalizesDoubanRatingToUnitRange()
    {
        Assert.AreEqual(0.89f, MetaSharkSimilarItemsProviderBase.ToScore(8.9f)!.Value, 0.0001f);
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void ToScore_ReturnsNullWhenRatingMissingOrZero()
    {
        Assert.IsNull(MetaSharkSimilarItemsProviderBase.ToScore(null));
        Assert.IsNull(MetaSharkSimilarItemsProviderBase.ToScore(0f));
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void ToScore_ClampsOutOfRangeRating()
    {
        Assert.AreEqual(1f, MetaSharkSimilarItemsProviderBase.ToScore(15f)!.Value, 0.0001f);
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void TryCreateReference_MapsDoubanIdAndScore()
    {
        var reference = MetaSharkSimilarItemsProviderBase.TryCreateDoubanReference(new DoubanRecommendation
        {
            Id = "1296996",
            Title = "哈利·波特与密室",
            Rating = new DoubanRecommendationRating { Value = 8.5f },
        });

        Assert.IsNotNull(reference);
        Assert.AreEqual("DoubanID", reference!.ProviderName);
        Assert.AreEqual("1296996", reference.ProviderId);
        Assert.AreEqual(0.85f, reference.Score!.Value, 0.0001f);
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void TryCreateReference_ReturnsNullForIncompleteRecommendation()
    {
        Assert.IsNull(MetaSharkSimilarItemsProviderBase.TryCreateDoubanReference(null));
        Assert.IsNull(MetaSharkSimilarItemsProviderBase.TryCreateDoubanReference(new DoubanRecommendation { Id = string.Empty, Title = "x" }));
        Assert.IsNull(MetaSharkSimilarItemsProviderBase.TryCreateDoubanReference(new DoubanRecommendation { Id = "1", Title = "  " }));
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void TryCreateReference_OmitsScoreWhenRatingMissing()
    {
        var reference = MetaSharkSimilarItemsProviderBase.TryCreateDoubanReference(new DoubanRecommendation { Id = "1", Title = "t" });

        Assert.IsNotNull(reference);
        Assert.IsNull(reference!.Score);
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void TryCreateTmdbReference_MapsTmdbIdAndNormalizesVoteAverage()
    {
        var reference = MetaSharkSimilarItemsProviderBase.TryCreateTmdbReference(new TmdbSimilarItem
        {
            Id = 672,
            Title = "哈利·波特与密室",
            VoteAverage = 7.7,
        });

        Assert.IsNotNull(reference);
        Assert.AreEqual("Tmdb", reference!.ProviderName);
        Assert.AreEqual("672", reference.ProviderId);
        Assert.AreEqual(0.77f, reference.Score!.Value, 0.0001f);
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void TryCreateTmdbReference_ReturnsNullForInvalidIdOrTitle()
    {
        Assert.IsNull(MetaSharkSimilarItemsProviderBase.TryCreateTmdbReference(null));
        Assert.IsNull(MetaSharkSimilarItemsProviderBase.TryCreateTmdbReference(new TmdbSimilarItem { Id = 0, Title = "x" }));
        Assert.IsNull(MetaSharkSimilarItemsProviderBase.TryCreateTmdbReference(new TmdbSimilarItem { Id = 1, Title = " " }));
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void SimilarItemsConfiguration_DefaultsAndClamp()
    {
        var configuration = new PluginConfiguration();

        Assert.IsTrue(configuration.EnableDoubanSimilarItems, "相似项目开关默认应开启");
        Assert.AreEqual(7, configuration.DoubanSimilarItemsCacheDays, "缓存天数默认 7 天");

        configuration.DoubanSimilarItemsCacheDays = -5;
        Assert.AreEqual(0, configuration.DoubanSimilarItemsCacheDays, "负数应被夹到 0");

        configuration.DoubanSimilarItemsCacheDays = 999;
        Assert.AreEqual(90, configuration.DoubanSimilarItemsCacheDays, "超过上限应被夹到 90");
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void SourceRouting_FollowsDefaultScraperMode()
    {
        var defaultMode = new PluginConfiguration { DefaultScraperMode = PluginConfiguration.DefaultScraperModeDefault };
        var tmdbOnly = new PluginConfiguration { DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly };

        // default 模式：相似项目允许豆瓣来源，TMDb 作为兜底。
        Assert.IsTrue(DefaultScraperPolicy.IsDoubanAllowed(defaultMode, DefaultScraperSemantic.AutomaticRefresh));

        // tmdb-only 模式：后台语义下不允许豆瓣，相似项目只能走 TMDb。
        Assert.IsFalse(DefaultScraperPolicy.IsDoubanAllowed(tmdbOnly, DefaultScraperSemantic.AutomaticRefresh));
    }
}
