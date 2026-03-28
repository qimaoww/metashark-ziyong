// <copyright file="TvImageRefillStatus.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Model
{
    public enum TvImageRefillStatus
    {
        Unknown = 0,
        CoolingDown = 1,
        HardMiss = 2,
        Success = 3,
    }
}
