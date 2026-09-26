# Jellyfin Cable TV

Cable-style, always-on channels for Jellyfin, built from your own library.

The project has two parts:

- **The plugin (this repo)** is the brain. It holds the channels, content pools, scheduling, commercials and
  settings, and publishes the channels to Jellyfin Live TV with a guide and a continuous stream, so every Live TV
  client can watch them.
- **A Wholphin fork (planned, separate repo)** is the TV. It reads the plugin's schedule API and tunes in
  instantly by direct-playing the original file at the right offset, with preloading and a static overlay.

It's based on the feasibility study "Cable-TV Experience for Jellyfin — Plugin + Wholphin Fork Feasibility".
The work is built from scratch; no NostalgiaTV code or assets are used.

## Install

Requires **Jellyfin 12.x**. The installable package is [`dist/cabletv_0.4.1.0.zip`](dist/).

**Option A: drop it in (works today).**

1. Stop Jellyfin.
2. Unzip `cabletv_0.4.1.0.zip` into a new folder `<jellyfin config>/plugins/Cable TV_0.4.1.0/`
   (Docker: `/config/plugins/…`; Linux packages: `/var/lib/jellyfin/plugins/…`;
   Windows: `%ProgramData%\Jellyfin\Server\plugins\…`).
3. Start Jellyfin. **Dashboard → Plugins** lists *Cable TV 0.4.1.0*.

**Option B: plugin repository (installs and updates from the dashboard).**

Add the manifest URL under **Dashboard → Plugins → Repositories**, then install *Cable TV* from the catalog and
restart. The manifest in `dist/manifest.json` points at
`https://raw.githubusercontent.com/franticg33k/jellyfin-cable-tv/main/dist/`, so it works once this repository is
public and the files are on `main`. For a private repository, host `dist/` anywhere your server can reach and
rebuild the manifest with `python3 scripts/package.py --base-url <that URL>`.

## Set up channels

Open **Dashboard → Plugins → Cable TV**. The page is a form:

- **General**: the time zone that time slots, restricted hours, seasons and premieres use.
- **Live TV stream** and global **Commercials** sources.
- **Channels**: pick a channel on the left, edit it on the right. Sources use pickers for your libraries,
  collections and playlists, with genre suggestions.
- **Add channels**: *Suggest channels* proposes one channel per TV network, TV and movie genre, decade and
  collection, plus kids and holiday channels, counted from your library. *Quick channel* makes a channel from one
  genre, network, collection, playlist, library, year range, tag, rating, title keyword or list of titles.
- **Preview** shows what the selected channel would air over the next 6 hours using the unsaved settings.
- **Save** rebuilds the channels and refreshes the Live TV guide. The channels then appear under **Live TV**.
- **Advanced** holds the same settings as JSON, plus the ids on your server.

### Import and export

**Import and export** on the same page reads three kinds of file. *Check* reports, for each channel, which titles
your library has; *Import* saves the result.

- **Lineup CSV**, one row per show or movie:

  ```csv
  Channel Number,Channel Name,Title,Release Year
  2,Toon Town,Dexter's Laboratory,1996
  2,Toon Town,"Ed, Edd n Eddy",1999
  ```

  Titles are matched by name, ignoring case, accents, punctuation, "&"/"and" and a leading "The". The year tells
  remakes apart; titles the library doesn't have simply don't air. *Merge* adds new channels and replaces the titles
  of channels with the same name, keeping their settings. *Replace* makes the file the whole lineup. *Seasonal* turns
  the file into a holiday lineup (name, dates, replace or mix) on the existing channels, which is how Christmas or
  Halloween lineups are imported.
- **Episodes CSV** (`Show Title,Episode Title`) pins holiday specials into a seasonal lineup on every channel that
  carries the show, or on the channel you pick.
- **Channel pack** (JSON, from *Export channels*): every setting. Libraries, collections, playlists and items are
  found by name on the importing server, so packs can be shared.

*Export lineup (CSV)* lists what each channel airs, so a lineup can be edited in a spreadsheet and imported back.

### Logos

Each channel's logo is, in order: its own **Logo** (an image URL, or `studio:Network Name`); the image at
`{Logo pack URL}/{channel_name}.png` when a logo pack URL is set (`Cartoon Network` → `cartoon_network.png`,
`HBO+` → `hbo_plus.png`); the logo Jellyfin has for a studio or TV network with the channel's name (install the
**Studio Images** plugin to download network logos); otherwise a generated logo with the channel's name. No
third-party logos ship with the plugin; a logo pack is any folder of PNGs your server can reach, for example a
GitHub repository of your own.

### Channel JSON

The JSON form of a channel, for reference:

```json
[
  {
    "Id": "ch-0412",
    "Number": "0412",
    "Name": "Sitcoms",
    "Enabled": true,
    "Sorting": "RoundRobin",
    "ItemTypes": ["Episode"],
    "Sources": [{ "Type": "Genre", "Ids": [], "Values": ["Comedy"], "Weight": 1 }],
    "CommercialsEnabled": true,
    "GridMinutes": 30,
    "MidBreak": "Halfway",
    "CommercialSources": []
  }
]
```

| Setting | Meaning |
|---|---|
| `Id` | Stable id. It seeds the schedule, so changing it reshuffles the channel. |
| `Sources` | Union of sources. `Library`, `Collection`, `Playlist`, `Items` take `Ids`. In `Values`: `Genre`, `Studio` (TV networks), `Tag`, `Rating` take names; `Decade` start years (`"1990"`); `Years` years or ranges (`"1985-1994"`); `Keyword` words in the title; `Titles` show or movie titles, optionally `"Title (Year)"`; `Episodes` `"Show :: Episode title"`. `Exclude: true` removes a source's items from the channel. `Weight` (1–10) airs a source's items more often under `Random`. |
| `LogoUrl` | Image URL, `studio:Name`, or empty for automatic (see Logos). |
| `Category` | Guide group, for example `Kids`; returned by the API. |
| `Sorting` | `Random`, `Cyclic`, `RoundRobin` (one episode per series in turn), `Block` (`BlockSize` episodes per series), `Marathon` (whole series back to back, series order reshuffled each pass). |
| `CommercialsEnabled` | Plan breaks. Commercials come from `CommercialSources`, or the page's global *Commercial sources* when empty (for example a *Home videos* library of ads). |
| `GridMinutes` | Pad every programme to the grid (for example 30): a 22-minute episode gets 8 minutes of breaks, topped up with filler when no commercial fits. |
| `MidBreak` | `None`, `Halfway`, or `Chapter` (chapter marker nearest halfway). |
| `BreakSeconds` | Break length when `GridMinutes` is 0. |
| `TimeSlots` | Different content at set times: `Name`, `Start`/`End` (`"07:00"`; an end at or before the start runs past midnight), `Days` (`["Sat","Sun"]`, empty = daily), `Sorting`, `Sources`. Programmes are fitted so a slot starts on time. |
| `AirHours` (on a source) | Restricted hours, e.g. `"21:00-05:00"`: that source's items only air then. |
| `Seasons` | Seasonal or holiday lineups: `Name`, `From`/`To` (`"12-01"`, inclusive, may wrap the new year), `Mode` (`Mix` or `Replace`), `Sources`. |
| `Premieres` | `Enabled`, `Time` (`"20:00"`), `Days`, `WithinDays`: items added in the last N days air at that time, marked new in the guide. |

**Live TV stream** settings on the same page:

- *Auto* (default) copies video from items in the channel's main codec and resolution and transcodes the rest.
- *Copy* never transcodes video. *Transcode* always makes H.264 at the chosen height.
- Audio is always converted to AAC stereo, optionally with loudness levelling.

## How watching works

- **Any Live TV app** (Jellyfin web, Android TV, Fladder, Wholphin, Swiftfin…): tuning in joins the programme
  that's airing at the right point and plays on through breaks and following programmes. Each channel runs one
  ffmpeg process, shared by everyone watching it, and it stops ~20 s after the last viewer leaves.
- **Clients using the [API](docs/api-contract.md)** (the planned Wholphin fork) get the full slot list and direct-play
  the original files instead, with no server transcoding.

## Status

| Phase | Scope | State |
|---|---|---|
| 0. Foundations | Plugin scaffold for Jellyfin 12.x, local test server | Done |
| 1. Plugin MVP | Channels, pools, random/cyclic schedule, `Schedule`/`Now` API, Live TV registration | Done |
| 2. Fork MVP | Guide, direct play at offset, preloading, static overlay | Built and CI-green in the [Wholphin fork](https://github.com/franticg33k/Wholphin) (`cable-tv` branch); first device test pending |
| 3. Fallback stream | Continuous copy-video / AAC-audio stream, shared per channel, timestamps that never rewind | Done |
| 4. Commercials | Break planning, halfway and chapter mid-breaks, fill-to-grid, one guide entry per programme | Done (pre-converting the ad library is not done; Auto mode transcodes mismatched ads on the fly) |
| 5. Scheduling depth | Sorting modes, weights, time slots, restricted hours, seasonal lineups, premieres, settings form, preview | Done |
| 5b. Lineups | Suggestions, quick channels, CSV/JSON import and export, title/network/tag/rating/year/keyword sources, logos | Done (0.4.1.0) |
| 6–7. Presentation, extras | Branding, trailer pools, stream channels, web TV mode | Planned |

Known limits:

- In copy mode, tuning in mid-programme starts at the keyframe before the scheduled point (usually 1–5 s early).
- Jellyfin waits for a few HLS segments before playback starts, so tune-in takes a few seconds to ~20 s
  depending on the source's keyframe spacing.
- A rebuild that finds new or removed content reshuffles that channel.
- Jellyfin doesn't show channel categories in its own guide yet; they're available to API clients.
- With time slots or seasons, an ordered lineup's position is estimated from airtime, so an episode can
  occasionally be skipped or repeated when a slot starts.

## Build

Needs the .NET 10 SDK.

```sh
dotnet build
dotnet test
python3 scripts/package.py            # writes dist/cabletv_<version>.zip and dist/manifest.json
```

Pushing a tag like `v0.2.0.0` runs the release workflow, which attaches the zip to a GitHub release.

## Layout

```
src/Jellyfin.Plugin.CableTv/
  Scheduling/     Deterministic timeline engine: sorting, weights, blocks and breaks
  Library/        Library index and title matching
  Content/        Pool resolution, channel store, guide refresh
  Packs/          CSV and JSON import/export, channel suggestions
  Logos/          Channel logos (network images, logo packs, generated)
  Streaming/      Continuous MPEG-TS stream: ffmpeg arguments, per-channel broadcaster
  LiveTv/         ILiveTvService: channels, guide, stream source
  Api/            /CableTv REST API (the client contract)
  Tasks/          Daily "Rebuild Cable TV channels" task
  Configuration/  Settings model and dashboard page
tests/            xUnit tests
docs/             API contract
scripts/          Packaging
dist/             Installable zip and repository manifest
dev/              Local Jellyfin 12 test server
```

## Credits

Generated logos use the [Oswald](https://fonts.google.com/specimen/Oswald) font, embedded under the SIL Open Font
License (`src/Jellyfin.Plugin.CableTv/Logos/Oswald-OFL.txt`).
