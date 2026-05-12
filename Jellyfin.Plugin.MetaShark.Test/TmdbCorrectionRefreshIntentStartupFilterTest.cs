using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class TmdbCorrectionRefreshIntentStartupFilterTest
    {
        [TestMethod]
        public async Task Configure_WhenRefreshItemHasStablePath_SavesIntentWithItemIdAndPath()
        {
            var itemId = Guid.NewGuid();
            const string itemPath = "/library/Shows/ReZero";
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemById(itemId)).Returns(new Folder { Id = itemId, Path = itemPath });
            var store = new Mock<ITmdbCorrectionRefreshIntentStore>();
            var filter = new TmdbCorrectionRefreshIntentStartupFilter(libraryManager.Object, store.Object);
            var context = CreateSearchMissingRefreshContext(itemId);

            var appBuilder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
            filter.Configure(_ => { })(appBuilder);
            var pipeline = appBuilder.Build();

            await pipeline(context);

            store.Verify(x => x.Save(itemId, itemPath), Times.Once);
        }

        [TestMethod]
        public async Task Configure_WhenRefreshItemPathIsUnavailable_SavesIntentWithItemIdOnly()
        {
            var itemId = Guid.NewGuid();
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemById(itemId)).Returns((BaseItem?)null);
            var store = new Mock<ITmdbCorrectionRefreshIntentStore>();
            var filter = new TmdbCorrectionRefreshIntentStartupFilter(libraryManager.Object, store.Object);
            var context = CreateSearchMissingRefreshContext(itemId);

            var appBuilder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
            filter.Configure(_ => { })(appBuilder);
            var pipeline = appBuilder.Build();

            await pipeline(context);

            store.Verify(x => x.Save(itemId, null), Times.Once);
        }

        private static DefaultHttpContext CreateSearchMissingRefreshContext(Guid itemId)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = $"/Items/{itemId:D}/Refresh";
            context.Request.QueryString = new QueryString("?metadataRefreshMode=FullRefresh&replaceAllMetadata=false");
            return context;
        }
    }
}
