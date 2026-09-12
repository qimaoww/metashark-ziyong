using Jellyfin.Plugin.MetaShark.Model;
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
        var reference = MetaSharkSimilarItemsProviderBase.TryCreateReference(new DoubanRecommendation
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
        Assert.IsNull(MetaSharkSimilarItemsProviderBase.TryCreateReference(null));
        Assert.IsNull(MetaSharkSimilarItemsProviderBase.TryCreateReference(new DoubanRecommendation { Id = string.Empty, Title = "x" }));
        Assert.IsNull(MetaSharkSimilarItemsProviderBase.TryCreateReference(new DoubanRecommendation { Id = "1", Title = "  " }));
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void TryCreateReference_OmitsScoreWhenRatingMissing()
    {
        var reference = MetaSharkSimilarItemsProviderBase.TryCreateReference(new DoubanRecommendation { Id = "1", Title = "t" });

        Assert.IsNotNull(reference);
        Assert.IsNull(reference!.Score);
    }
}
