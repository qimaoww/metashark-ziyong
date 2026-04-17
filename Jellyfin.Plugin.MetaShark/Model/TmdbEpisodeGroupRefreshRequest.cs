// <copyright file="TmdbEpisodeGroupRefreshRequest.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Model;

using System.Text.Json.Serialization;

public class TmdbEpisodeGroupRefreshRequest
{
    [JsonPropertyName("oldMapping")]
    public string? OldMapping { get; set; }

    [JsonPropertyName("newMapping")]
    public string? NewMapping { get; set; }
}
