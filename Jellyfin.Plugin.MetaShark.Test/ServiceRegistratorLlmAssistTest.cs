using System;
using System.Linq;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
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
    public class ServiceRegistratorLlmAssistTest
    {
        [TestMethod]
        public void RegisterServices_ShouldResolveLlmSingletons()
        {
            using var serviceProvider = CreateServiceProvider();

            var llmApi = serviceProvider.GetRequiredService<ILlmApi>();
            var llmApiConcrete = serviceProvider.GetRequiredService<LlmApi>();
            var llmApiSecondResolve = serviceProvider.GetRequiredService<ILlmApi>();
            var assistService = serviceProvider.GetRequiredService<ILlmMetadataAssistService>();
            var assistServiceConcrete = serviceProvider.GetRequiredService<LlmMetadataAssistService>();
            var assistServiceSecondResolve = serviceProvider.GetRequiredService<ILlmMetadataAssistService>();
            var triggerPolicy = serviceProvider.GetRequiredService<LlmAssistTriggerPolicy>();
            var triggerPolicySecondResolve = serviceProvider.GetRequiredService<LlmAssistTriggerPolicy>();
            var requestLimiter = serviceProvider.GetRequiredService<ILlmRequestLimiter>();
            var requestLimiterSecondResolve = serviceProvider.GetRequiredService<ILlmRequestLimiter>();
            var contextBuilder = serviceProvider.GetRequiredService<LlmScrapeContextBuilder>();
            var contextBuilderSecondResolve = serviceProvider.GetRequiredService<LlmScrapeContextBuilder>();
            var validator = serviceProvider.GetRequiredService<LlmSuggestionValidator>();
            var validatorSecondResolve = serviceProvider.GetRequiredService<LlmSuggestionValidator>();
            var detector = serviceProvider.GetRequiredService<LlmScrapeMismatchDetector>();
            var detectorSecondResolve = serviceProvider.GetRequiredService<LlmScrapeMismatchDetector>();
            var mergePolicy = serviceProvider.GetRequiredService<LlmMetadataMergePolicy>();
            var mergePolicySecondResolve = serviceProvider.GetRequiredService<LlmMetadataMergePolicy>();
            var sanitizer = serviceProvider.GetRequiredService<LlmRelativePathSanitizer>();
            var sanitizerSecondResolve = serviceProvider.GetRequiredService<LlmRelativePathSanitizer>();
            var episodeGroupMappingAssist = serviceProvider.GetRequiredService<ILlmEpisodeGroupMappingAssistService>();
            var episodeGroupMappingAssistConcrete = serviceProvider.GetRequiredService<LlmEpisodeGroupMappingAssistService>();
            var episodeGroupMappingAssistSecondResolve = serviceProvider.GetRequiredService<ILlmEpisodeGroupMappingAssistService>();
            var externalIdValidator = serviceProvider.GetRequiredService<LlmExternalIdCandidateValidator>();
            var externalIdValidatorSecondResolve = serviceProvider.GetRequiredService<LlmExternalIdCandidateValidator>();
            var externalIdResolutionService = serviceProvider.GetRequiredService<ILlmExternalIdResolutionService>();
            var externalIdResolutionServiceConcrete = serviceProvider.GetRequiredService<LlmExternalIdResolutionService>();
            var externalIdResolutionServiceSecondResolve = serviceProvider.GetRequiredService<ILlmExternalIdResolutionService>();

            Assert.AreSame(llmApi, llmApiConcrete);
            Assert.AreSame(llmApi, llmApiSecondResolve);
            Assert.AreSame(assistService, assistServiceConcrete);
            Assert.AreSame(assistService, assistServiceSecondResolve);
            Assert.AreSame(triggerPolicy, triggerPolicySecondResolve);
            Assert.AreSame(requestLimiter, requestLimiterSecondResolve);
            Assert.AreEqual(1, requestLimiter.MaxConcurrency);
            Assert.AreSame(contextBuilder, contextBuilderSecondResolve);
            Assert.AreSame(validator, validatorSecondResolve);
            Assert.AreSame(detector, detectorSecondResolve);
            Assert.AreSame(mergePolicy, mergePolicySecondResolve);
            Assert.AreSame(sanitizer, sanitizerSecondResolve);
            Assert.AreSame(episodeGroupMappingAssist, episodeGroupMappingAssistConcrete);
            Assert.AreSame(episodeGroupMappingAssist, episodeGroupMappingAssistSecondResolve);
            Assert.AreSame(externalIdValidator, externalIdValidatorSecondResolve);
            Assert.AreSame(externalIdResolutionService, externalIdResolutionServiceConcrete);
            Assert.AreSame(externalIdResolutionService, externalIdResolutionServiceSecondResolve);
        }

        [TestMethod]
        public void RegisterServices_ShouldNotRegisterLlmTypesAsHostedServices()
        {
            using var serviceProvider = CreateServiceProvider();

            var hostedServices = serviceProvider.GetServices<IHostedService>().ToArray();

            Assert.IsFalse(hostedServices.Any(service => service.GetType().Name.Contains("Llm", StringComparison.OrdinalIgnoreCase)));
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
