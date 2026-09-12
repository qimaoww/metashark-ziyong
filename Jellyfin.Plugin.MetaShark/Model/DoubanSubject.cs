// <copyright file="DoubanSubject.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Model
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Data;
    using System.Linq;
    using System.Text.Json.Serialization;

    public class DoubanSubject
    {
        private static readonly Dictionary<string, string> LanguageCodeMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "日语", "ja" },
            { "法语", "fr" },
            { "德语", "de" },
            { "俄语", "ru" },
            { "韩语", "ko" },
            { "泰语", "th" },
            { "泰米尔语", "ta" },
        };

        // "name": "哈利·波特与魔法石",
        public string Name { get; set; } = string.Empty;

        // "originalName": "Harry Potter and the Sorcerer's Stone",
        public string OriginalName { get; set; } = string.Empty;

        // "rating": "9.1",
        public float Rating { get; set; }

        // "img": "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p2614949805.webp",
        public string Img { get; set; } = string.Empty;

        // "sid": "1295038",
        public string Sid { get; set; } = string.Empty;

        // "year": "2001",
        public int Year { get; set; }

        // "director": "克里斯·哥伦布",
        public string Director { get; set; } = string.Empty;

        // "writer": "史蒂夫·克洛夫斯 / J·K·罗琳",
        public string Writer { get; set; } = string.Empty;

        // "actor": "丹尼尔·雷德克里夫 / 艾玛·沃森 / 鲁伯特·格林特 / 艾伦·瑞克曼 / 玛吉·史密斯 / 更多...",
        public string Actor { get; set; } = string.Empty;

        // "genre": "奇幻 / 冒险",
        public string Genre { get; set; } = string.Empty;

        // 电影/电视剧
        public string Category { get; set; } = string.Empty;

        // "site": "www.harrypotter.co.uk",
        public string Site { get; set; } = string.Empty;

        // "country": "美国 / 英国",
        public string Country { get; set; } = string.Empty;

        // "language": "英语",
        public string Language { get; set; } = string.Empty;

        // "screen": "2002-01-26(中国大陆) / 2020-08-14(中国大陆重映) / 2001-11-04(英国首映) / 2001-11-16(美国)",
        public string Screen { get; set; } = string.Empty;

        public DateTime? ScreenTime
        {
            get
            {
                if (string.IsNullOrEmpty(this.Screen))
                {
                    return null;
                }

                var items = this.Screen.Split("/");

                // 豆瓣 screen 形如 "2002-01-26(中国大陆)"，也可能带非日期前缀；
                // 解析失败必须返回 null，否则会把 DateTime.MinValue 当成首映日期。
                var item = items[0].Split("(")[0].Trim();
                return DateTime.TryParseExact(
                    item,
                    "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var result)
                    ? result
                    : null;
            }
        }

        // "duration": "152分钟 / 159分钟(加长版)",
        public string Duration { get; set; } = string.Empty;

        // "subname": "哈利波特1：神秘的魔法石(港/台) / 哈1 / Harry Potter and the Philosopher's Stone",
        public string Subname { get; set; } = string.Empty;

        // "imdb": "tt0241527"
        public string Imdb { get; set; } = string.Empty;

        public string Intro { get; set; } = string.Empty;

        public Collection<DoubanCelebrity> Celebrities { get; } = new();

        [JsonIgnore]
        public IReadOnlyList<DoubanCelebrity> LimitDirectorCelebrities
        {
            get
            {
                // 限制导演最多返回5个
                var limitCelebrities = new List<DoubanCelebrity>();
                if (this.Celebrities.Count == 0)
                {
                    return limitCelebrities;
                }

                limitCelebrities.AddRange(this.Celebrities.Where(x => x.RoleType == MediaBrowser.Model.Entities.PersonType.Director && !string.IsNullOrEmpty(x.Name)).Take(5));
                limitCelebrities.AddRange(this.Celebrities.Where(x => x.RoleType != MediaBrowser.Model.Entities.PersonType.Director && !string.IsNullOrEmpty(x.Name)));

                return limitCelebrities;
            }
        }

        [JsonIgnore]
        public string ImgMiddle
        {
            get
            {
                return this.Img.Replace("s_ratio_poster", "m", StringComparison.Ordinal);
            }
        }

        [JsonIgnore]
        public string ImgLarge
        {
            get
            {
                return this.Img.Replace("s_ratio_poster", "l", StringComparison.Ordinal);
            }
        }

        [JsonIgnore]
        public IReadOnlyList<string> Genres
        {
            get
            {
                return this.Genre.Split("/").Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
            }
        }

        [JsonIgnore]
        public string PrimaryLanguageCode
        {
            get
            {
                var primaryLanguage = this.Language.Split("/").Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).FirstOrDefault();
                if (!string.IsNullOrEmpty(primaryLanguage))
                {
                    if (LanguageCodeMap.TryGetValue(primaryLanguage, out var lang))
                    {
                        return lang;
                    }
                }

                return string.Empty;
            }
        }
    }
}
