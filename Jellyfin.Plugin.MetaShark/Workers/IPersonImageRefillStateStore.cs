// <copyright file="IPersonImageRefillStateStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using Jellyfin.Plugin.MetaShark.Model;

    public interface IPersonImageRefillStateStore
    {
        PersonImageRefillState? GetState(Guid personId);

        void Save(PersonImageRefillState state);

        void Remove(Guid personId);
    }
}
