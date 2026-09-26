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

    [Fact]
    public void Slot_OmitsMediaSourceWhenItIsTheItem()
    {
        var id = Guid.NewGuid();
        var start = new DateTime(2026, 9, 26, 20, 0, 0, DateTimeKind.Utc);
        ScheduledSlot Slot(string source) => new("s", SlotKind.Program, start, start.AddMinutes(1), new PoolItem(id, source, 600_000_000, "x"), 0, 600_000_000, "g");

        Assert.Null(SlotDto.From(Slot(id.ToString("N"))).MediaSourceId);
        Assert.Equal("other-version", SlotDto.From(Slot("other-version")).MediaSourceId);
    }

    [Fact]
    public void GuideProgram_FoldsBreaksIntoOneEntry()
    {
        var item = new PoolItem(Guid.NewGuid(), "m", TimeSpan.FromMinutes(90).Ticks, "Starship Harbor") { IsMovie = true, ProductionYear = 1994, OfficialRating = "PG-13" };
        var start = new DateTime(2026, 9, 26, 20, 0, 0, DateTimeKind.Utc);
        var slots = new[]
        {
            new ScheduledSlot("s-1", SlotKind.Program, start, start.AddMinutes(90), item, 0, item.DurationTicks, "g-1"),
            new ScheduledSlot("s-1-1", SlotKind.Filler, start.AddMinutes(90), start.AddMinutes(120), null, 0, 0, "g-1"),
        };
        var dto = GuideProgramDto.From(new ScheduledBlock("1", item, start, start.AddMinutes(120), slots));

        Assert.Equal("g-1", dto.GuideGroup);
        Assert.Equal(start.AddMinutes(120), dto.End);
        Assert.Equal("Starship Harbor", dto.Title);
        Assert.True(dto.Movie);
        Assert.Equal(1994, dto.Year);
        Assert.Equal("PG-13", dto.Rating);
        Assert.Null(dto.Episode);
    }
}
