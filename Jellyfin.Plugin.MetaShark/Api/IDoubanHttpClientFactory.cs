// <copyright file="IDoubanHttpClientFactory.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using Microsoft.Extensions.Logging;

    internal interface IDoubanHttpClientFactory
    {
        DoubanHttpClientSet Create(ILogger logger);
    }
}
