// <copyright file="ProviderIdSetTest.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Test
{
    using System.Linq;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Model.Entities;

    [TestClass]
    public class ProviderIdSetTest
    {
        [TestMethod]
        public void ForDouban_ShouldMatchSearchResultProviderIds()
        {
            var providerIds = ProviderIdSet.ForDouban("subject-1");

            CollectionAssert.AreEqual(
                new[] { BaseProvider.DoubanProviderId, MetaSharkPlugin.ProviderId },
                providerIds.Keys.ToArray());
            Assert.AreEqual("subject-1", providerIds[BaseProvider.DoubanProviderId]);
            Assert.AreEqual("Douban_subject-1", providerIds[MetaSharkPlugin.ProviderId]);
            Assert.IsFalse(providerIds.ContainsKey("MetaSharkTmdbID"));
        }

        [TestMethod]
        public void ForTmdb_ShouldMatchSearchResultProviderIds()
        {
            var providerIds = ProviderIdSet.ForTmdb(1234);

            CollectionAssert.AreEqual(
                new[] { MetadataProvider.Tmdb.ToString(), MetaSharkPlugin.ProviderId },
                providerIds.Keys.ToArray());
            Assert.AreEqual("1234", providerIds[MetadataProvider.Tmdb.ToString()]);
            Assert.AreEqual("Tmdb_1234", providerIds[MetaSharkPlugin.ProviderId]);
            Assert.IsFalse(providerIds.ContainsKey("MetaSharkTmdbID"));
        }
    }
}
