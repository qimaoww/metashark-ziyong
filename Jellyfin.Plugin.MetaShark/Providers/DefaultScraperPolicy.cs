// <copyright file="DefaultScraperPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using Jellyfin.Plugin.MetaShark.Configuration;

    /// <summary>
    /// Explicit host semantics for default scraper policy decisions.
    /// </summary>
    public enum DefaultScraperSemantic
    {
        ManualSearch,
        ManualMatch,
        UserRefresh,
        AutomaticRefresh,
    }

    /// <summary>
    /// Centralized policy for deciding whether Douban is allowed.
    /// </summary>
    public static class DefaultScraperPolicy
    {
        /// <summary>
        /// Determines whether Douban is allowed for the supplied semantic.
        /// </summary>
        /// <param name="configuration">Plugin configuration.</param>
        /// <param name="semantic">Explicit provider call semantic.</param>
        /// <returns><see langword="true"/> when Douban is allowed; otherwise <see langword="false"/>.</returns>
        public static bool IsDoubanAllowed(PluginConfiguration? configuration, DefaultScraperSemantic semantic)
        {
            var mode = configuration?.DefaultScraperMode ?? PluginConfiguration.DefaultScraperModeDefault;

            return mode switch
            {
                PluginConfiguration.DefaultScraperModeTmdbOnly => false,
                _ => true,
            };
        }
    }
}
