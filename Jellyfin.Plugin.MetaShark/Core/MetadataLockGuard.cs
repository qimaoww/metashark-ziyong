// <copyright file="MetadataLockGuard.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using System.Linq;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Model.Entities;

    internal static class MetadataLockGuard
    {
        public static bool IsItemLocked(BaseItem item)
        {
            return item.IsLocked;
        }

        public static bool CanWriteField(BaseItem item, MetadataField field)
        {
            return !item.IsLocked
                && item.LockedFields?.Contains(field) != true;
        }

        public static bool CanWriteNameLikeField(BaseItem item)
        {
            return CanWriteField(item, MetadataField.Name);
        }
    }
}
