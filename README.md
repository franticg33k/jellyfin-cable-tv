# Jellyfin Cable TV

Cable-style, always-on channels for Jellyfin, built from your own library.

The project has two parts:

- **The plugin (this repo)** is the brain. It holds the channels, content pools, scheduling and settings, and
  publishes the channels to Jellyfin Live TV, so every Live TV client gets channels and a guide.
- **A Wholphin fork (planned, separate repo)** is the TV. It reads the plugin's schedule API and tunes in
  instantly by direct-playing the original file at the right offset, with preloading and a static overlay.

It's based on the feasibility study "Cable-TV Experience for Jellyfin — Plugin + Wholphin Fork Feasibility".
The work is built from scratch; no NostalgiaTV code or assets are used.

## Status

| Phase | Scope | State |
|---|---|---|
| 0. Foundations | Plugin scaffold for Jellyfin 12.x, local test server | Done |
| 1. Plugin MVP | Channels, content pools, random/cyclic schedule, `Schedule`/`Now` API, Live TV registration | Done |
| 2. Fork MVP | Guide, direct play at offset, preloading, static overlay | Next (Wholphin fork) |
| 3. Fallback stream | Continuous copy-video / re-encode-audio stream, shared HLS, ts offset | Planned |
| 4. Commercials | Break planning, mid-breaks, fill-to-grid | Planned |
| 5. Scheduling depth | Weighting, time slots, premieres, marathons, dashboard pages | Planned |
| 6–7. Presentation, extras | Branding, trailer pools, stream channels, web TV mode | Planned |

What phase 1 does and doesn't do yet:

- Channels and a guide show up in any Live TV client. Tuning in plays the file that's airing now, **from its
  start**, and stops at its end. Joining mid-show and running continuously across programmes arrive in phase 3.
- The instant, mid-show tune-in experience is for clients using the [API](docs/api-contract.md).
- Watched filters, parental filtering per user, commercials and advanced scheduling come later.

## Layout

```
src/Jellyfin.Plugin.CableTv/
  Scheduling/     Deterministic timeline engine (no Jellyfin dependencies)
  Content/        Library pool resolution, channel store, guide refresh
  LiveTv/         ILiveTvService: channels, guide, phase 1 stream
  Api/            /CableTv REST API (the client contract)
  Tasks/          Daily "Rebuild Cable TV channels" task
  Configuration/  Settings model and dashboard page
tests/            xUnit tests for the engine and wire format
docs/             API contract
dev/              Local Jellyfin 12 test server
```

## Build

Needs the .NET 10 SDK.

```sh
dotnet build
dotnet test
dotnet publish src/Jellyfin.Plugin.CableTv -c Release -o artifacts/plugin
```

Install by copying `artifacts/plugin/Jellyfin.Plugin.CableTv.dll` into
`<jellyfin config>/plugins/CableTv_0.1.0.0/` and restarting Jellyfin 12.x.

## Try it locally

```sh
dotnet publish src/Jellyfin.Plugin.CableTv -c Release -o artifacts/plugin
mkdir -p dev/media   # put a few shows/movies here
podman compose -f dev/compose.yaml up   # or docker compose
```

Open `http://localhost:8096`, finish the wizard, add libraries from `/media`, then go to
**Dashboard → Plugins → Cable TV**. The page lists your library, collection and playlist ids. Add channels as
JSON, for example:

```json
[
  {
    "Id": "ch-0412",
    "Number": "0412",
    "Name": "Sitcoms",
    "Enabled": true,
    "Sorting": "Random",
    "ItemTypes": ["Episode"],
    "AllowSpecials": false,
    "Sources": [{ "Type": "Genre", "Ids": [], "Values": ["Comedy"] }]
  }
]
```

Saving rebuilds the channels and queues Jellyfin's Refresh Guide task. The channels then appear under Live TV.

Source types: `Library`, `Collection`, `Playlist` and `Items` take `Ids`; `Genre` takes genre names and
`Decade` takes start years (`"1990"`) in `Values`. A channel's pool is the union of its sources.

Keep a channel's `Id` fixed: it seeds the shuffle, so changing it reshuffles the channel.
