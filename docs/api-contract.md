# Plugin ↔ client API contract (v1 draft)

The plugin decides what airs when; a client that wants instant, transcode-free tune-in (the planned Wholphin
fork, or web) plays it. This API is the only thing the two share. All endpoints need a normal Jellyfin
user token (`Authorization: MediaBrowser ... Token="..."`) and return camelCase JSON regardless of the
server's naming policy. Times are UTC ISO 8601.

| Endpoint | Purpose |
|---|---|
| `GET /CableTv/Channels` | Channel list: id, number, name, `logoUrl` (absolute URL of `/CableTv/Logo/{id}`), `category`, `scheduleVersion`, `poolSize` |
| `GET /CableTv/Logo/{channelId}` | Anonymous: the channel's logo image (or a redirect to it) |
| `GET /CableTv/Schedule?channelIds=&from=&to=` | Resolved slots for a window, with `serverTime` |
| `GET /CableTv/Now?channelId=&next=3` | Airing slot, the offset to start at, and the next slots to preload |
| `GET /CableTv/Presentation` | Server-set branding |
| `POST /CableTv/Rebuild` | Admin only: re-read pools from the library and refresh the Live TV guide |
| `POST /CableTv/Preview?hours=6` | Admin only: body is a channel definition (unsaved); returns the coming slots |
| `GET /CableTv/Suggestions?minTitles=3` | Admin only: channels the library could fill (networks, genres, decades, kids, holidays, collections), as ready-to-add channel definitions |
| `POST /CableTv/Import` | Admin only: body `{ content, mode: Merge\|Replace\|Season, seasonName, from, to, seasonMode, targetChannelId, apply }`; returns a per-channel report of matched and missing titles; saves only with `apply: true` |
| `GET /CableTv/Export?format=json\|csv&channelIds=` | Admin only: a JSON channel pack or a lineup CSV |
| `GET /CableTv/Stream/{channelId}?key=` | Internal: the continuous MPEG-TS stream Jellyfin's Live TV reads; authenticated by the plugin's stream key, not for clients |

## `GET /CableTv/Schedule`

- `channelIds`: comma-separated; all channels when omitted.
- `from`: defaults to now. `to`: defaults to `from` + 6 h, and is clamped to `from` + 3 days.
- Returns every slot that *overlaps* the window, so the first slot may start before `from`.

```json
{
  "serverTime": "2026-09-26T20:15:03.120Z",
  "from": "2026-09-26T20:15:03.120Z",
  "to": "2026-09-27T02:15:03.120Z",
  "scheduleVersion": "c7a1e93b02d4",
  "channels": [{
    "channelId": "ch-0412",
    "number": "0412",
    "scheduleVersion": "b8dc77f35a3d",
    "slots": [{
      "slotId": "s-652549c8d5c5",
      "kind": "program",
      "start": "2026-09-26T20:00:00Z",
      "end": "2026-09-26T20:22:14Z",
      "itemId": "5b1c…",
      "mediaSourceId": "5b1c…",
      "inPointMs": 0,
      "outPointMs": 1334000,
      "title": "Example Show",
      "episode": "S02E05",
      "episodeTitle": "The One With the Example",
      "guideGroup": "g-652549c8d5c5"
    }]
  }]
}
```

## `GET /CableTv/Now`

```json
{
  "serverTime": "2026-09-26T20:15:03.120Z",
  "channelId": "ch-0412",
  "scheduleVersion": "b8dc77f35a3d",
  "current": { "slotId": "s-…", "kind": "program", "...": "..." },
  "offsetMs": 903120,
  "next": [ { "slotId": "s-…" } ]
}
```

`offsetMs` is where to seek inside `current.itemId`: `inPointMs` plus the time since the slot started.

## Rules

- `kind` is one of `program`, `commercial`, `bumper`, `filler`, `stream` (outside HLS/TS URL) or
  `generated` (for example weather). The plugin currently emits `program`, `commercial` and `filler`;
  clients must skip kinds they don't know.
- `filler` has no item: render static or black for its duration (it pads a break to the grid, or fills a
  stretch where the channel is off air).
- `premiere: true` marks an airing of a newly added item; `lineup` names the time slot or seasonal lineup a
  slot belongs to. Both are omitted otherwise.
- `year`, `rating` (official rating, e.g. `TV-PG`) and `movie: true` describe the item, for guide colours and
  badges. Anything else (overview, images, media details) comes from Jellyfin's item API when a client needs it.
- Fields that are null are omitted from the JSON (Jellyfin's serializer), for example `itemId` and `title`
  on filler and `episode` on movies.
- `guideGroup` ties breaks to their programme, so a guide shows one block and the player sees every item.
- A mid-show break splits a programme into two `program` slots on the same `itemId` with different in/out points.
- Clients compute "now" as `serverTime` plus elapsed device time, never from the device clock alone.
- `scheduleVersion` changes whenever a channel's timeline changes (channel settings or its pool changed on a
  rebuild). When it changes, discard cached slots for that channel and refetch.
- Play `itemId` / `mediaSourceId` with Jellyfin's normal direct-play endpoint
  (`/Videos/{itemId}/stream?Static=true&MediaSourceId=…`) and seek to the offset.

## How the schedule is computed

Each pool item airs as a *block*: the programme (split at the halfway point or the chapter nearest it when
mid-breaks are on), its breaks, and filler up to the grid (`GridMinutes`), or fixed `BreakSeconds` breaks when
there is no grid. A block's length depends only on its item; which commercials fill a break is drawn from a
generator seeded by the block id. Each block is one Live TV guide entry.

Each channel's timeline is a pure function of wall-clock time: time since the anchor
(`ScheduleAnchorUtc`, or the channel's own `AnchorUtc`) is cut into cycles as long as the whole pool. Each
cycle plays every item once, in an order set by `Sorting`: `Random` (a shuffle seeded by the channel id and
cycle number; a source's `Weight` makes its items air that many times per cycle, never back to back when
avoidable), `Cyclic` (canonical order), `RoundRobin` (one episode per series in turn), `Block`
(`BlockSize` episodes per series in turn) or `Marathon` (series back to back, series order reshuffled
each cycle). Nothing is stored, so restarts and multiple servers agree, and Live TV's guide
matches what the API returns.

Channels with time slots, restricted hours (a source's `AirHours`), seasonal lineups or premieres are planned
one local day at a time (in the configured time zone): the day is cut at every rule boundary, and each piece is
packed with whole blocks from its pool so slots start on time; minutes left before a boundary become
commercials or filler. Where each piece starts in its pool's sequence is estimated from the airtime that pool
has had since the anchor, so ordered pools carry on from day to day without stored state.

Pools are re-read from the library only on a rebuild (saving the configuration, the daily
"Rebuild Cable TV channels" task, or `POST /CableTv/Rebuild`). A rebuild that changes a pool reshuffles
that channel from the anchor. Keeping the airing show stable across pool changes is planned for phase 5.
