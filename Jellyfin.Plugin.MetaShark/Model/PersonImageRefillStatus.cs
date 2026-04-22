// <copyright file="PersonImageRefillStatus.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Model
{
    public enum PersonImageRefillStatus
    {
        Unknown = 0,
        Pending = 1,
        Retryable = 2,
        Completed = 3,
    }
}
