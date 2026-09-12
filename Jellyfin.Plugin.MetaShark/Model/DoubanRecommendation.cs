// <copyright file="DoubanRecommendation.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Model
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// 豆瓣 rexxar 接口返回的相似项目条目。
    /// </summary>
    public class DoubanRecommendation
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets 副标题，形如「2010 / 日本 / 动画 奇幻 冒险 / 米林宏昌 / 志田未来 神木隆之介」。
        /// </summary>
        [JsonPropertyName("card_subtitle")]
        public string CardSubtitle { get; set; } = string.Empty;

        [JsonPropertyName("rating")]
        public DoubanRecommendationRating? Rating { get; set; }
    }
}
