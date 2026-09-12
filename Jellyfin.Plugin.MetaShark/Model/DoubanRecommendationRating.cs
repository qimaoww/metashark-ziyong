// <copyright file="DoubanRecommendationRating.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Model
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// 豆瓣相似项目条目的评分对象。
    /// </summary>
    public class DoubanRecommendationRating
    {
        [JsonPropertyName("value")]
        public float Value { get; set; }
    }
}
