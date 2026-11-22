# Unsplash Wallpaper: Project Plan

Status: **V1 implemented** — see `README.md` for build, verification, and launch instructions. Live Unsplash fetching and the real desktop wallpaper change still need manual acceptance with an authorized source.

### Implementation decisions

- Product name: **Frame Shift v1.20**. C# / .NET 10 / WinForms, tray-only UI.
- Free-text topic, configurable refresh (hourly default), optional Start with Windows, primary-screen sizing; multi-monitor and favorites remain v2.
- Unsplash adapter retained for separately authorized use. Current Unsplash guidelines exclude wallpaper apps, so this is not ready for public distribution with that source.
- Added a local-folder source so the product can operate without online credentials. Local topics match filenames; an empty topic selects from the entire folder.
- Access key comes from `UNSPLASH_ACCESS_KEY`, a Git-ignored `.env.local` file, or a masked session-only setup dialog, never source code or settings.
- Runtime data lives under `%LocalAppData%\FrameShift\`, with settings and a ten-image cache.
- Automated checks cover the API contract, state preservation on failure, concurrency, cancellation, cache retention, settings recovery, and native image preparation. Real wallpaper application and a full-hour timer still require manual acceptance.

## 1. Goal

A small Windows tray app that fetches photos from Unsplash and sets them as the desktop wallpaper, like a "theme" that refreshes itself. The user picks a topic, and every following wallpaper matches that topic.

### Non-goals (for now)

- No clock, greeting, weather, or widgets (unlike Bonjourr)
- No live/animated wallpapers
- No main window; the tray menu is the whole UI

## 2. Features

### Must have (v1)

- [x] Fetch a random landscape photo from Unsplash (adapter implemented; live acceptance requires authorized credentials)
- [x] Size the image to the screen resolution
- [x] Set it as the desktop wallpaper (Windows API implemented; real desktop acceptance pending)
- [x] Change automatically on a timer (default: every 1 hour; full-hour acceptance pending)
- [x] Tray menu: **Next wallpaper**
- [x] Tray menu: **Change topic…** (search term, saved, used for all later wallpapers)
- [x] Keep the previous topic if the new one returns no photos
- [x] Photographer credit link (required by Unsplash)
- [x] Call `download_location` on every download (required by Unsplash)
- [x] Keep only the 10 most recent downloaded images
- [x] Exit option

### Nice to have (v2)

- [x] Start with Windows (opt-in, current-user registry `Run` key)
- [x] Settings menu: Configure API, refresh interval (manual / 15 min / 30 min / 1 h / 3 h / 6 h / daily), and Start with Windows
- [ ] Dropdown of Unsplash's official topics instead of free text
- [x] Custom tray icon (green ring, also embedded in the executable)
- [x] Animated loading ring and quiet offline recovery with retry backoff
- [ ] Multi-monitor support (different image per screen via `IDesktopWallpaper`)
- [ ] Pause/resume
- [ ] Save/favorite the current wallpaper
- [x] Move the access key out of source code

## 3. Tech stack

| Item | Choice |
|---|---|
| Language | C# |
| Runtime | .NET 10 |
| UI | WinForms (tray icon + one small dialog) |
| HTTP | `HttpClient` |
| JSON | `System.Text.Json` |
| Wallpaper API | `SystemParametersInfo` (user32.dll) |

**Alternatives considered:** Tauri (web UI reuse, needs Rust), Electron (too heavy for a background app), Python (good for a prototype, weaker packaging).

## 4. Unsplash API notes

- Get an **Access Key** by registering an app at <https://unsplash.com/developers>
- Endpoint: `GET https://api.unsplash.com/photos/random?orientation=landscape&query=<topic>`
- Header: `Authorization: Client-ID <ACCESS_KEY>`
- Demo rate limit: about 50 requests/hour (enough for this app)
- Image size: use `urls.raw` plus `&w=<width>&h=<height>&fit=crop&q=85&fm=jpg`
- **Required by guidelines:**
  - Call `links.download_location` when a photo is used as a wallpaper
  - Show the photographer's name linked to their profile, with `utm_source` and `utm_medium=referral`
- A 404 from `/photos/random` with a query means no photos matched

## 5. Architecture

```
Tray icon ──► Menu ──► Next wallpaper ──┐
                  └──► Change topic ────┤
Timer (hourly) ─────────────────────────┤
                                        ▼
                                    Refresh()
                                        │
              1. GET /photos/random (topic from Settings)
              2. Build sized image URL
              3. Download JPG to cache folder
              4. Ping download_location
              5. Wallpaper.Set(path)
              6. Update credit link + tooltip
              7. Delete old cache files (keep 10)
```

## 6. Project structure

```
UnsplashWallpaper/
├── UnsplashWallpaper.csproj
├── Program.cs        # entry point only
├── TrayApp.cs        # tray icon, menu, timer, Refresh() / Unsplash fetch
├── Settings.cs       # load/save settings.json
├── TopicDialog.cs    # small popup to type a topic
├── Wallpaper.cs      # SystemParametersInfo wrapper
├── Extensions.cs     # Truncate() helper
└── (later) Autostart.cs, app.ico
```

## 7. Runtime data

```
%LocalAppData%\UnsplashWallpaper\
├── settings.json          # { "Topic": "nature" }
└── yyyyMMdd_HHmmss.jpg    # last 10 wallpapers
```

## 8. Settings schema

| Field | Type | Default | Notes |
|---|---|---|---|
| `Topic` | string | `"nature"` | Empty = random from any topic |
| `IntervalMinutes` | int | `60` | v2 |
| `StartWithWindows` | bool | `false` | v2 |

## 9. Milestones

1. **Prototype:** fetch, download, set wallpaper (console or minimal tray)
2. **Tray app:** Next wallpaper, hourly timer, credit link, cleanup
3. **Topic:** Change topic dialog, save to settings, handle "no results"
4. **Polish:** icon, autostart, interval choice
5. **Package:** single-file publish
   `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`

## 10. Risks and gotchas

| Risk | Mitigation |
|---|---|
| Access key leaked if repo goes public | Use env var or settings file; add to `.gitignore` |
| Tray tooltip max is 63 chars | `Truncate()` helper |
| No internet or API error | Catch exceptions, show a balloon tip, keep the current wallpaper |
| Rate limit hit | Keep the interval 15 minutes or longer; show an error on HTTP 403 |
| Topic has no results | Revert to the previous topic and notify |
| Multi-monitor shows the same image on all screens | Accept in v1; `IDesktopWallpaper` in v2 |
| Disk fills with images | Keep only the newest 10 |

## 11. Open questions (to decide before building)

1. Free-text topic, or a dropdown of official Unsplash topics, or both?
2. Default refresh interval: 1 hour, or something else?
3. Should the app start with Windows by default?
4. Multi-monitor in v1 or later?
5. Should the user be able to keep or favorite a wallpaper (save outside the 10-image cache)?
6. Where should the Access Key live: hardcoded, env var, or settings file?
7. Will this be open source or private? (Affects key handling and license.)
8. Does the user need a "previous wallpaper" action?

## 12. Definition of done (v1)

- Launching the app sets a wallpaper that matches the saved topic within a few seconds
- The wallpaper changes on schedule and from the tray menu
- Changing the topic persists across restarts
- Photographer credit is reachable from the tray menu
- The app survives network failures without crashing
