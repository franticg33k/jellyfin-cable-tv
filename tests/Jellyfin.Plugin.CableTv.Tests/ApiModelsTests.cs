using System;
using System.Text.Json;
using Jellyfin.Plugin.CableTv.Api;
using Jellyfin.Plugin.CableTv.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class ApiModelsTests
{
    [Fact]
    public void Slot_SerializesToContractShape_WhateverTheNamingPolicy()
    {
        var item = new PoolItem(Guid.Parse("5b1c0000-0000-0000-0000-000000000001"), "5b1c0000000000000000000000000001", TimeSpan.FromMinutes(22).Ticks, "Example Show")
        {
            SeasonNumber = 2,
            EpisodeNumber = 5,
            EpisodeTitle = "Pilot",
        };
        var start = new DateTime(2026, 9, 26, 20, 0, 0, DateTimeKind.Utc);
        var slot = new ScheduledSlot("s-9f2", SlotKind.Program, start, start.AddMinutes(22), item, 0, item.DurationTicks, "g-77");

        // Jellyfin serializes with PascalCase unless the client asks otherwise; the contract must not depend on that.
        var json = JsonSerializer.Serialize(SlotDto.From(slot), new JsonSerializerOptions { PropertyNamingPolicy = null });
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("program", root.GetProperty("kind").GetString());
        Assert.Equal("5b1c0000000000000000000000000001", root.GetProperty("itemId").GetString());
        Assert.Equal(0, root.GetProperty("inPointMs").GetInt64());
        Assert.Equal(1_320_000, root.GetProperty("outPointMs").GetInt64());
        Assert.Equal("S02E05", root.GetProperty("episode").GetString());
        Assert.Equal("g-77", root.GetProperty("guideGroup").GetString());
        Assert.Equal(start, root.GetProperty("start").GetDateTime().ToUniversalTime());
    }
}
