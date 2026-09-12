// <copyright file="MetaSharkOrdinaryItemLibraryCapabilityResolver.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using MediaBrowser.Controller.BaseItemManager;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Configuration;

    public sealed class MetaSharkOrdinaryItemLibraryCapabilityResolver
    {
        private readonly ILibraryManager libraryManager;
        private readonly IBaseItemManager? baseItemManager;

        public MetaSharkOrdinaryItemLibraryCapabilityResolver(ILibraryManager libraryManager, IBaseItemManager? baseItemManager = null)
        {
            ArgumentNullException.ThrowIfNull(libraryManager);
            this.libraryManager = libraryManager;
            this.baseItemManager = baseItemManager;
        }

        public MetaSharkLibraryCapabilityDecision Resolve(BaseItem item, MetaSharkLibraryCapability capability)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (!TryResolveItemType(item, out var itemType))
            {
                return CreateNoResolvedLibraryDecision(capability);
            }

            var typeOptions = ResolveTypeOptions(this.libraryManager.GetLibraryOptions(item), itemType);
            if (typeOptions == null)
            {
                // 与 Jellyfin 原生语义对齐：库没有该类型的选项时回退到全局 fetcher 配置（默认启用）。
                return this.baseItemManager != null
                    ? ResolveFromBaseItemManager(item, itemType, capability, this.baseItemManager)
                    : CreateNoResolvedLibraryDecision(capability);
            }

            var evidence = CreateResolvedLibraryEvidence(item, itemType, typeOptions);
            return MetaSharkLibraryCapabilityGate.Evaluate(
                MetaSharkLibraryCapabilityGateInput.ForOrdinaryItem(capability, evidence));
        }

        private static MetaSharkLibraryCapabilityDecision ResolveFromBaseItemManager(BaseItem item, string itemType, MetaSharkLibraryCapability capability, IBaseItemManager baseItemManager)
        {
            var library = item.GetTopParent() ?? item;
            var evidence = new MetaSharkResolvedLibraryCapabilityEvidence(
                library.Id,
                library.Name ?? string.Empty,
                library.Path ?? string.Empty,
                itemType,
                baseItemManager.IsMetadataFetcherEnabled(item, null, MetaSharkPlugin.PluginName),
                baseItemManager.IsImageFetcherEnabled(item, null, MetaSharkPlugin.PluginName));
            return MetaSharkLibraryCapabilityGate.Evaluate(
                MetaSharkLibraryCapabilityGateInput.ForOrdinaryItem(capability, evidence));
        }

        internal static TypeOptions? ResolveTypeOptions(LibraryOptions? libraryOptions, string itemType)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(itemType);

            return libraryOptions?.TypeOptions?.FirstOrDefault(x => string.Equals(x.Type, itemType, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool TryResolveItemType(BaseItem item, out string itemType)
        {
            ArgumentNullException.ThrowIfNull(item);

            switch (item)
            {
                case Movie:
                    itemType = nameof(Movie);
                    return true;
                case Series:
                    itemType = nameof(Series);
                    return true;
                case Season:
                    itemType = nameof(Season);
                    return true;
                case Episode:
                    itemType = nameof(Episode);
                    return true;
                default:
                    itemType = string.Empty;
                    return false;
            }
        }

        private static MetaSharkLibraryCapabilityDecision CreateNoResolvedLibraryDecision(MetaSharkLibraryCapability capability)
        {
            return MetaSharkLibraryCapabilityGate.Evaluate(
                MetaSharkLibraryCapabilityGateInput.ForNoResolvedLibrary(
                    capability,
                    MetaSharkLibraryCapabilityContextKind.OrdinaryItem));
        }

        private static MetaSharkResolvedLibraryCapabilityEvidence CreateResolvedLibraryEvidence(BaseItem item, string itemType, TypeOptions typeOptions)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(itemType);
            ArgumentNullException.ThrowIfNull(typeOptions);

            var library = item.GetTopParent() ?? item;
            return new MetaSharkResolvedLibraryCapabilityEvidence(
                library.Id,
                library.Name ?? string.Empty,
                library.Path ?? string.Empty,
                itemType,
                IsProviderEnabled(typeOptions.MetadataFetchers),
                IsProviderEnabled(typeOptions.ImageFetchers));
        }

        private static bool IsProviderEnabled(IEnumerable<string>? fetchers)
        {
            return fetchers?.Any(x => string.Equals(x, MetaSharkPlugin.PluginName, StringComparison.Ordinal)) ?? false;
        }
    }
}
