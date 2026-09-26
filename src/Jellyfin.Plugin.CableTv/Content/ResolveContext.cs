using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Jellyfin.Plugin.CableTv.Library;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.CableTv.Content;

/// <summary>
/// Work shared by every channel in one rebuild: the library index and the episodes already fetched per series, so a
/// rebuild costs one library scan plus one query per series used, however many channels there are.
/// </summary>
public sealed class ResolveContext
{
    private readonly Lazy<LibraryIndex> _index;

    internal ResolveContext(Func<LibraryIndex> buildIndex)
    {
        _index = new Lazy<LibraryIndex>(buildIndex);
    }

    /// <summary>Gets the library index, built on first use.</summary>
    public LibraryIndex Index => _index.Value;

    // A shared context serves concurrent previews and import checks, so both collections are thread-safe.
    internal ConcurrentDictionary<Guid, IReadOnlyList<BaseItem>> Children { get; } = new();

    internal ConcurrentDictionary<Guid, byte> UsedItems { get; } = new();

    /// <summary>Gets or sets the library's trailers, fetched on first use.</summary>
    internal IReadOnlyList<BaseItem>? Trailers { get; set; }
}
