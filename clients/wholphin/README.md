# Wholphin fork: Cable TV mode

The Android TV client for this plugin lives in a fork of Wholphin:
**[franticg33k/Wholphin](https://github.com/franticg33k/Wholphin)**, branch `cable-tv`.

It adds a full-screen TV mode that tunes instantly by direct-playing the airing file at its scheduled offset, with the
next items preloaded, a static overlay, a channel banner, number-pad tuning and a guide. It talks to this plugin only
through the API in [`docs/api-contract.md`](../../docs/api-contract.md).

- How the TV mode works: `tvmode/README.md` in the fork.
- How the fork stays mergeable with upstream Wholphin: `FORK.md` in the fork.
- Debug APKs: the fork's *Cable TV build* workflow runs (Actions → run → Artifacts).
