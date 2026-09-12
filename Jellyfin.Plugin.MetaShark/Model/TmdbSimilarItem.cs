// <copyright file="TmdbSimilarItem.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Model
{
    /// <summary>
    /// TMDb recommendations 接口返回的相似项目精简结果。
    /// </summary>
    public class TmdbSimilarItem
    {
        public int Id { get; set; }

        public string Title { get; set; } = string.Empty;

        public double VoteAverage { get; set; }
    }
}
