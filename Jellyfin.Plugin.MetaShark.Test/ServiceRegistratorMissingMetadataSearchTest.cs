using System;
using System.Linq;
using Jellyfin.Plugin.MetaShark.Workers;
using Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill;
using MediaBrowser.Controller;
using MediaBrowser.Controller.BaseItemManager;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class ServiceRegistratorMissingMetadataSearchTest
    {
        [TestMethod]
        public void RegisterServices_ShouldResolveMissingMetadataSearchSingletonAndExistingServices()
        {
            using var serviceProvider = CreateServiceProvider();

            var missingMetadataSearchService = serviceProvider.GetRequiredService<IMissingMetadataSearchService>();
            var missingMetadataSearchServiceSecondResolve = serviceProvider.GetRequiredService<IMissingMetadataSearchService>();
            var tvMissingImageRefillService = serviceProvider.GetRequiredService<ITvMissingImageRefillService>();
            var episodeTitleBackfillPostProcessService = serviceProvider.GetRequiredService<IEpisodeTitleBackfillPostProcessService>();
            var episodeOverviewCleanupPostProcessService = serviceProvider.GetRequiredService<IEpisodeOverviewCleanupPostProcessService>();

            Assert.IsInstanceOfType(missingMetadataSearchService, typeof(MissingMetadataSearchService));
            Assert.AreSame(missingMetadataSearchService, missingMetadataSearchServiceSecondResolve);
            Assert.IsInstanceOfType(tvMissingImageRefillService, typeof(TvMissingImageRefillService));
            Assert.IsInstanceOfType(episodeTitleBackfillPostProcessService, typeof(EpisodeTitleBackfillPostProcessService));
            Assert.IsInstanceOfType(episodeOverviewCleanupPostProcessService, typeof(EpisodeOverviewCleanupPostProcessService));
        }

        [TestMethod]
        public void RegisterServices_ShouldNotRegisterMissingMetadataSearchServiceAsHostedService()
        {
            using var serviceProvider = CreateServiceProvider();

            var hostedServices = serviceProvider.GetServices<IHostedService>().ToArray();

            Assert.IsFalse(hostedServices.Select(service => service.GetType()).Contains(typeof(MissingMetadataSearchService)));
        }

        private static ServiceProvider CreateServiceProvider()
        {
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddLogging();
            serviceCollection.AddSingleton(Mock.Of<ILibraryManager>());
            serviceCollection.AddSingleton(Mock.Of<IProviderManager>());
            serviceCollection.AddSingleton(Mock.Of<IBaseItemManager>());
            serviceCollection.AddSingleton(Mock.Of<IFileSystem>());
            serviceCollection.AddSingleton(Mock.Of<ICollectionManager>());

            new ServiceRegistrator().RegisterServices(serviceCollection, Mock.Of<IServerApplicationHost>());
            serviceCollection.AddSingleton(Mock.Of<IEpisodeTitleBackfillPersistence>());
            serviceCollection.AddSingleton(Mock.Of<IEpisodeOverviewCleanupPersistence>());

            return serviceCollection.BuildServiceProvider();
        }
    }
}
