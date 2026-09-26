# Wholphin fork: Cable TV mode

Patches for the planned Wholphin fork (phase 2): a full-screen TV that tunes instantly by direct-playing the airing
file at its scheduled offset, with the next items preloaded, a static overlay, a channel banner, number-pad tuning and
a guide. Details are in the patch's `tvmode/README.md`.

They live here until the fork exists. To apply them to a fork of
[damontecres/Wholphin](https://github.com/damontecres/Wholphin):

```sh
git clone https://github.com/<you>/Wholphin.git && cd Wholphin
git checkout -b cable-tv-mode 365f8f8adacf4dcaf7d05317917830ed3a439668   # upstream commit the patches are based on
git am /path/to/jellyfin-cable-tv/clients/wholphin/*.patch
```

## Status

| Part | State |
|---|---|
| `:tvmode-core` (API client, server clock, schedule cache, tune-in and queue planning, channel navigation, guide) | Written, 21 unit tests passing, ktlint clean |
| `:tvmode` (screen, view model, host interface) and the app hooks | Written and ktlint clean, **not compiled yet**: this environment can't download the Android SDK or AndroidX (`dl.google.com` is blocked) |

Next: build and fix compile errors, run on an Android TV emulator against a server with the plugin, then tune the
static and banner timing on a real device.
