// <copyright file="ITvImageRefillOutcomeReporter.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using MediaBrowser.Controller.Entities;

    public interface ITvImageRefillOutcomeReporter
    {
        void ReportHardMiss(BaseItem item, string reason);

        void ReportSuccess(BaseItem item);

        void ReportTransientFailure(BaseItem item, string reason);
    }
}
