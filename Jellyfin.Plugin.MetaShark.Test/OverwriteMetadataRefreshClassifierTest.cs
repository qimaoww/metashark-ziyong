using System;
using System.Reflection;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class OverwriteMetadataRefreshClassifierTest
    {
        private const string ClassifierTypeName = "Jellyfin.Plugin.MetaShark.Providers.OverwriteMetadataRefreshClassifier";

        [DataTestMethod]
        [DataRow("POST", "/Items/11111111-1111-1111-1111-111111111111/Refresh", "?metadataRefreshMode=FullRefresh&replaceAllMetadata=true", "11111111-1111-1111-1111-111111111111", true)]
        [DataRow("POST", "/Items/11111111-1111-1111-1111-111111111111/Refresh", "?metadataRefreshMode=RefreshMetadata&replaceAllMetadata=true", "11111111-1111-1111-1111-111111111111", true)]
        [DataRow("POST", "/Items/11111111-1111-1111-1111-111111111111/Refresh", "?metadataRefreshMode=FullRefresh&ReplaceAllMetadata=true", "11111111-1111-1111-1111-111111111111", true)]
        [DataRow("POST", "/Items/11111111-1111-1111-1111-111111111111/Refresh", "?metadataRefreshMode=FullRefresh&replaceAllMetadata=false", "11111111-1111-1111-1111-111111111111", false)]
        [DataRow("GET", "/Items/11111111-1111-1111-1111-111111111111/Refresh", "?metadataRefreshMode=FullRefresh&replaceAllMetadata=true", "11111111-1111-1111-1111-111111111111", false)]
        [DataRow("POST", "/Items/RemoteSearch/Apply", "?metadataRefreshMode=FullRefresh&replaceAllMetadata=true", "11111111-1111-1111-1111-111111111111", false)]
        [DataRow("POST", "/Items/11111111-1111-1111-1111-111111111111/Refresh", "?metadataRefreshMode=FullRefresh&replaceAllMetadata=true", "22222222-2222-2222-2222-222222222222", false)]
        public void ClassifiesOverwriteRefreshRequestsByRouteAndQuery(string method, string path, string queryString, string expectedItemId, bool expected)
        {
            var context = CreateHttpContext(method, path, queryString);
            var result = InvokeIsOverwriteMetadataRefresh(context, Guid.Parse(expectedItemId));

            Assert.AreEqual(expected, result);
        }


        [DataTestMethod]
        [DataRow("POST", "/Items/RemoteSearch/Apply/11111111-1111-1111-1111-111111111111", "", true, DefaultScraperSemantic.ManualMatch)]
        [DataRow("POST", "/Items/11111111-1111-1111-1111-111111111111/Refresh", "?metadataRefreshMode=FullRefresh&replaceAllMetadata=true", true, DefaultScraperSemantic.OverwriteRefresh)]
        [DataRow("POST", "/Items/11111111-1111-1111-1111-111111111111/Refresh", "?metadataRefreshMode=RefreshMetadata&replaceAllMetadata=false", true, DefaultScraperSemantic.AutomaticRefresh)]
        [DataRow("POST", "/Items/11111111-1111-1111-1111-111111111111/Refresh", "", false, DefaultScraperSemantic.UserRefresh)]
        [DataRow("POST", "/Items/11111111-1111-1111-1111-111111111111/Refresh", "?metadataRefreshMode=RefreshMetadata&replaceAllMetadata=false", false, DefaultScraperSemantic.UserRefresh)]
        public void ResolveMetadataSemantic_HonorsExplicitRouteEvidenceBeforeAutomation(string method, string path, string queryString, bool isAutomated, DefaultScraperSemantic expected)
        {
            var context = CreateHttpContext(method, path, queryString);
            var provider = new BaseProviderProbe(new HttpContextAccessor { HttpContext = context });
            var info = new ItemLookupInfo { IsAutomated = isAutomated };

            var result = provider.InvokeResolveMetadataSemantic(info);

            Assert.AreEqual(expected, result);
        }

        private static bool InvokeIsOverwriteMetadataRefresh(HttpContext? context, Guid? expectedItemId)
        {
            var classifierType = GetClassifierType();
            var method = classifierType.GetMethod(
                "IsOverwriteMetadataRefresh",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(HttpContext), typeof(Guid?) },
                modifiers: null);

            Assert.IsNotNull(method, "OverwriteMetadataRefreshClassifier.IsOverwriteMetadataRefresh(HttpContext?, Guid?) 未定义。 ");

            var result = method.Invoke(null, new object?[] { context, expectedItemId });

            Assert.IsNotNull(result, "OverwriteMetadataRefreshClassifier.IsOverwriteMetadataRefresh 应返回布尔值结果。 ");
            Assert.IsInstanceOfType(result, typeof(bool));
            return (bool)result;
        }

        private static Type GetClassifierType()
        {
            var classifierType = typeof(PluginConfiguration).Assembly.GetType(ClassifierTypeName);
            Assert.IsNotNull(classifierType, "OverwriteMetadataRefreshClassifier 未定义。 ");
            return classifierType!;
        }

        private static DefaultHttpContext CreateHttpContext(string method, string path, string queryString)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;
            context.Request.QueryString = new QueryString(queryString);
            return context;
        }


        private sealed class BaseProviderProbe : BaseProvider
        {
            public BaseProviderProbe(IHttpContextAccessor httpContextAccessor)
                : base(null!, NullLogger.Instance, new Mock<ILibraryManager>().Object, httpContextAccessor, new DoubanApi(NullLoggerFactory.Instance), new TmdbApi(NullLoggerFactory.Instance), new OmdbApi(NullLoggerFactory.Instance), new ImdbApi(NullLoggerFactory.Instance))
            {
            }

            public DefaultScraperSemantic InvokeResolveMetadataSemantic(ItemLookupInfo info)
            {
                return this.ResolveMetadataSemantic(info);
            }
        }

    }
}
