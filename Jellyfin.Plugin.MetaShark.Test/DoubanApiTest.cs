using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class DoubanApiTest
    {
        private TestContext? testContextInstance;

        /// <summary>
        /// Gets or sets the test context which provides
        /// information about and functionality for the current test run.
        /// </summary>
        public TestContext TestContext
        {
            get { return testContextInstance!; }
            set { testContextInstance = value; }
        }

        ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
                builder.AddSimpleConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.SingleLine = true;
                    options.TimestampFormat = "hh:mm:ss ";
                }));



        [TestMethod]
        public void TestSearch()
        {
            var keyword = "声生不息";
            var api = new DoubanApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var result = await api.SearchAsync(keyword, CancellationToken.None);
                    TestContext.WriteLine(result?.ToJson() ?? string.Empty);
                }
                catch (Exception ex)
                {
                    TestContext.WriteLine(ex.Message);
                }
            }).GetAwaiter().GetResult();
        }


        [TestMethod]
        public void TestSearchBySuggest()
        {
            var keyword = "重返少年时";
            var api = new DoubanApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var result = await api.SearchBySuggestAsync(keyword, CancellationToken.None);
                    TestContext.WriteLine(result?.ToJson() ?? string.Empty);
                }
                catch (Exception ex)
                {
                    TestContext.WriteLine(ex.Message);
                }
            }).GetAwaiter().GetResult();
        }


        [TestMethod]
        public void TestGetVideoBySidAsync()
        {
            var sid = "26654184";

            var api = new DoubanApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetMovieAsync(sid, CancellationToken.None);
                    TestContext.WriteLine(result?.ToJson() ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestFixGetImage()
        {
            // 演员入驻了豆瓣, 下载的不是演员的头像#5
            var sid = "35460157";

            var api = new DoubanApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetMovieAsync(sid, CancellationToken.None);
                    if (result is null)
                    {
                        Assert.Fail("GetMovieAsync 返回 null");
                        return;
                    }

                    Assert.AreEqual<string>("https://img2.doubanio.com/view/celebrity/raw/public/p1598199472.61.jpg", result.Celebrities.First(x => x.Name == "刘陆").Img);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetCelebritiesBySidAsync()
        {
            var sid = "26654184";

            var api = new DoubanApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetCelebritiesBySidAsync(sid, CancellationToken.None);
                    TestContext.WriteLine(result?.ToJson() ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetCelebritiesByCidAsync()
        {
            var cid = "1340364";

            var api = new DoubanApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetCelebrityAsync(cid, CancellationToken.None);
                    TestContext.WriteLine(result?.ToJson() ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetCelebrityPhotosAsync()
        {
            var cid = "1322205";

            var api = new DoubanApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetCelebrityPhotosAsync(cid, CancellationToken.None);
                    TestContext.WriteLine(result?.ToJson() ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }).GetAwaiter().GetResult();
        }



        [TestMethod]
        public void TestParseCelebrityName()
        {

            var api = new DoubanApi(loggerFactory);


            var name = "佩吉·陆 Peggy Lu";
            var result = DoubanApi.ParseCelebrityName(name);
            Assert.AreEqual<string>(result, "佩吉·陆");

            name = "Antony Coleman Antony Coleman";
            result = DoubanApi.ParseCelebrityName(name);
            Assert.AreEqual<string>(result, "Antony Coleman");

            name = "Dick Cook";
            result = DoubanApi.ParseCelebrityName(name);
            Assert.AreEqual<string>(result, "Dick Cook");

            name = "李凡秀";
            result = DoubanApi.ParseCelebrityName(name);
            Assert.AreEqual<string>(result, "李凡秀");

        }

        [TestMethod]
        public void TestGetMovieAsync()
        {
            var sid = "26654184";

            var api = new DoubanApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetMovieAsync(sid, CancellationToken.None);
                    if (result != null)
                    {
                        Assert.AreEqual(sid, result.Sid, "Sid 不匹配");
                    }
                }
                catch (Exception ex)
                {
                    Assert.Fail($"调用 GetMovieAsync 时出现异常: {ex.Message}");
                }
            }).GetAwaiter().GetResult();
        }

    }
}
