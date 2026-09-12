// <copyright file="DoubanSuggest.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Model;

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MetaShark.Core;

public class DoubanSuggest
{
    private static readonly Regex SidRegex = new Regex(@"subject\/(\d+?)\/", RegexOptions.Compiled);

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public System.Uri? Url { get; set; }

    [JsonPropertyName("year")]
    public string Year { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    public string Sid
    {
        get
        {
            return (this.Url?.ToString() ?? string.Empty).GetMatchGroup(SidRegex);
        }
    }
}
