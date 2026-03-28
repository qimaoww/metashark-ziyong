// <copyright file="ITvImageRefillStateStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using Jellyfin.Plugin.MetaShark.Model;

    public interface ITvImageRefillStateStore
    {
        TvImageRefillState? Get(Guid itemId);

        void Save(TvImageRefillState state);

        void Remove(Guid itemId);
    }
}
