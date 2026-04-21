// <copyright file="IPeopleRefreshStateStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using System;

    public interface IPeopleRefreshStateStore
    {
        PeopleRefreshState? GetState(Guid itemId);

        void Save(PeopleRefreshState state);

        void Remove(Guid itemId);
    }
}
