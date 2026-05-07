// <copyright file="LlmExternalIdProviderIdWrite.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;

    public sealed class LlmExternalIdProviderIdWrite
    {
        public LlmExternalIdProviderIdWrite(string providerIdKey, string provider, string providerIdValue, string mediaType, LlmExternalIdCandidate candidate)
        {
            this.ProviderIdKey = string.IsNullOrWhiteSpace(providerIdKey) ? throw new ArgumentException("ProviderId key is required.", nameof(providerIdKey)) : providerIdKey;
            this.Provider = string.IsNullOrWhiteSpace(provider) ? throw new ArgumentException("Provider is required.", nameof(provider)) : provider;
            this.ProviderIdValue = string.IsNullOrWhiteSpace(providerIdValue) ? throw new ArgumentException("ProviderId value is required.", nameof(providerIdValue)) : providerIdValue;
            this.MediaType = string.IsNullOrWhiteSpace(mediaType) ? throw new ArgumentException("Media type is required.", nameof(mediaType)) : mediaType;
            this.Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        }

        public string ProviderIdKey { get; }

        public string Provider { get; }

        public string ProviderIdValue { get; }

        public string MediaType { get; }

        public LlmExternalIdCandidate Candidate { get; }
    }
}
