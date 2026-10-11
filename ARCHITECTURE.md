# Parche architecture

`Parche.Core` (net8.0, no UI) holds everything that does not depend on Android or Windows: title catalog, a client for addons compatible with the Stremio addon protocol, source ranking, subtitles, settings and library. The MAUI app (`Parche.App`) consumes it through the `ParcheEngine` facade.

```
AppSettings ─► ParcheEngine ─┬─ ICatalogProvider (Composite → TMDB | Cinemeta)
                              ├─ AddonClient + StreamParser ─► StreamRanker ─► StreamCandidate[]
                              │      ▲ addresses: AppSettings.StreamAddons + IStreamSourceProvider (plugins)
                              └─ SubtitlesClient (all subtitle addons) ─► SubtitleTrack[]
```

All HTTP goes through `Net/JsonHttp` (HttpClient + per-host `RateLimiter` + `ResponseCache` + System.Text.Json). Polite limits: TMDB 250 ms between requests, Cinemeta/addons 300 ms; retries with exponential backoff on 429/503. Addon sources are cached in memory for 5 minutes (going back to the list does not repeat the query).

## Catalog

Titles are identified by **IMDb id** (`tt…`), which is what addons understand. `MediaItem.Id` may be empty in TMDB listings (only `TmdbId`); the IMDb id is resolved in `GetDetailAsync` via `external_ids` (or `find/{imdb}` in the reverse direction).

- **CinemetaCatalog** (no key, English only): `v3-cinemeta.strem.io/catalog/{movie|series}/top[/genre=G][&skip=N].json` (pages of 50), search `top/search={q}.json`, detail at `cinemeta-live.strem.io/meta/{type}/{id}.json`. The `videos[]` are grouped by season (season 0, specials, goes last). Genres come from `manifest.json` (the `top` catalog).
- **TmdbCatalog** (requires a key): accepts a v3 key (`api_key` in the URL) or a v4 token (`Bearer` header), detected by format. The app language (`es-ES`, `es-MX`, `en-US`, `pt-BR`) is sent as `language`. Posters `w342`, backdrops `w1280`. For series the detail carries the season list and the episodes of the first one; the rest are requested with `GetSeasonAsync`.
- **CompositeCatalog**: TMDB if there is a key, otherwise Cinemeta; if TMDB answers 401 it falls back to Cinemeta and remembers it.

This product uses the TMDB API but is not endorsed or certified by TMDB.

## Addon protocol (sources)

`AddonClient.GetStreamsAsync(base, "movie"|"series", id)` does `GET {base}/stream/{type}/{id}.json`, with `id = tt123` or `tt123:{season}:{episode}`. `StreamParser` turns each element of `streams[]` into a `StreamCandidate`:

- quality (2160p/1080p/720p/480p/SD) from name and title; size (`💾 2.1 GB`) and availability (`👤 120`), following the usual addon format;
- languages: flags (🇪🇸 es, 🇲🇽/🇦🇷… es-419, 🇬🇧/🇺🇸 en, 🇧🇷/🇵🇹 pt) and words (SPANISH, CASTELLANO, LATINO, ENG…); `DUAL/MULTI` marks `multi`; with no information `en` is assumed;
- a direct `url` (with the headers from `behaviorHints.proxyHeaders.request`) or, as protocol fields, `infoHash` and `fileIdx` together with `sources`, file name and `bingeGroup`;
- `IsDirect` (there is a `url`) and `IsInstant` (the `[XX+]` tag in the name means it is ready instantly).

The engine queries all addons in parallel, merges and deduplicates by `infoHash+fileIdx` (or by URL). A down addon does not break the others; if all fail, the error is propagated. With no addon configured the list comes out empty without making any requests.

## Ranking

`StreamRanker.Rank` sorts without discarding anything: (1) instantly-ready sources first, (2) higher availability, (3) language position in the preference list (default `es-419`, `es`, `en`; `es` also accepts `es-419`; `multi` goes after the preferred ones), (4) quality: the highest one not exceeding `PreferredMaxQuality` (default 1080p), then those that exceed it, from lowest to highest, and (5) size. `DropOtherYears` discards movies whose file declares a different year.

## Plugins: sources that live outside this repository

Parche works as a neutral addon client. Anyone who wants to offer additional sources builds them separately and links them with a props file; without it, the app builds and works the same, only with the addons the user adds.

- **Composition**: `MauiProgram` is a `static partial class` and declares `static partial void AddExternalSources(IServiceCollection)`. `Parche.App.csproj` imports `$(ParchePluginsProps)` (by default `..\..\..\Parche.Plugins.props`, or `-p:ParchePluginsProps=<path>`; with a value that does not exist, such as `none`, nothing is imported). That props file adds `ProjectReference`s and a `Compile` that implements the partial method and registers the pieces.
- **`IStreamSourceProvider`** (Core): `IEnumerable<string> GetAddonUrls(AppSettings)`. `EngineProvider` hands all providers to `ParcheEngine`, which builds `EffectiveStreamAddons()` = providers + `AppSettings.StreamAddons`, normalized and without duplicates.
- **`IStreamResolver`** (App): turns a candidate with no `Url` but with `InfoHash` (`StreamCandidate.NeedsResolver`) into a playable local URL (`ResolveAsync`, with `ResolveStatus` progress), and offers `PreloadAsync`, `PreloadAlternateAsync`, `CancelPreloadAsync` and `StopAsync`. `StreamResolvers` aggregates all registered ones. With none, `EngineProvider` discards those candidates (`CanPlay` filter). An expected failure is signaled with `StreamResolveException` and the retry loop in `PlayerViewModel` (generic and public) tries the next source in the list.
- **`IDownloadResolver`** (App): `CanDownload` and `DownloadAsync` for candidates without a direct link. `DownloadService` keeps the queue and the resumable HTTP download; if no resolver supports a candidate, downloading it is not offered.
- **`IPluginSettingsSection`** (App): an entry with a title in Settings that opens its own page (built in C#, so it can be linked as a loose file).
- **Plugin settings**: `AppSettings.Plugins` stores one JSON per key (`GetPlugin<T>` / `SetPlugin<T>`). `AppSettings.Extra` (`[JsonExtensionData]`) keeps unknown properties on load and save, so a plugin can migrate settings from earlier versions (`GetExtra<T>` / `RemoveExtra`).

## Subtitles

`SubtitlesClient` queries `{base}/subtitles/{type}/{id}.json`. `AppSettings.SubtitleAddons` (default `https://opensubtitles-v3.strem.io`) is a list: the engine queries all of them in parallel, merges the results, removes duplicates by URL and filters by `SubtitleLanguages` (default `spa`, `eng`). The addon uses 3-letter codes (`spa`, `eng`, `por`, `pob`); they are converted to ISO 639-1 (`es`, `en`, `pt`, `pt-BR`) with a readable label. Up to 5 per language are kept and repeated ones are numbered ("Español 2"). A failing addon is skipped; if all fail, playback goes ahead without subtitles and without an error. The old `SubtitlesAddonUrl` property is migrated into the list.

## Settings

`AppSettings` is a serializable POCO (`ToJson`/`FromJson`, enums as text) with `Validate()` (messages in Spanish). It has no source addons by default. Changing settings creates a new `ParcheEngine`.

## Application (Parche.App)

MAUI app (net10.0-android, a single APK for phone and Android TV, and net10.0-windows for desktop with LibVLC in `Platforms/Windows`) on top of `ParcheEngine`. MVVM with CommunityToolkit.Mvvm, compiled bindings (`x:DataType`) and localized texts (`{l:Translate Key}` / `LocalizationManager.T`, `Strings.resx` in Spanish + `.en` + `.pt`). Network, disk and JSON work never runs on the UI thread.

```
SettingsService ─► EngineProvider ─► ParcheEngine (rebuilt when settings are saved; cover cached 10 min)
       │                  └─ IStreamSourceProvider[] · StreamResolvers.CanPlay
       └──► StreamResolvers / DownloadResolvers (plugins)            ViewModels ─► Views
```

- **Services**: `SettingsService` stores `AppSettings` as JSON in Preferences (`app_settings`; `AppLanguage` follows the UI language: es→`es-ES`, en→`en-US`, pt→`pt-BR`) and notifies through `Changed`. `EngineProvider` creates the engine with a shared `HttpClient`. `MyListService` stores "My list" (`my_list_v2`).
- **Navigation**: tabs `Home · Search · Categories · My list · Settings` (bottom bar on phone, fixed side menu on TV; defined once in `ShellSetup.Tabs`). Routes: `detail` → `streams` → `player`.
- **Playback flow**: Detail (movie, or chosen episode) → `StreamPickerPage` requests sources and subtitles in parallel, shows the sorted list with badges (quality, languages, size, availability) and a language filter → `PlayerPage`:
  - candidate with `Url`: `ParcheEngine.CreatePlayback` → `NativeVideoView` (ExoPlayer with headers, external subtitles and HLS/DASH/progressive type; LibVLC on Windows);
  - candidate to resolve: `StreamResolvers.ResolveAsync` with a progress panel (phase, %, speed, connections, Cancel); once ready, the local URL is played. If the source does not respond, the next one in the list is tried automatically.
  - Errors (including the HTTP code from the native player) offer Retry, Choose another source and a copyable diagnostics panel.
- **No addons**: the source picker and the detail page show "Add a source addon in Settings" with a button to Settings (never an error), and downloading is not offered.
- **TV**: `TvCard` (border + zoom on focus, OK/Enter), the chips (`Chip`) and the fields are focusable with the D-pad.
- **Settings**: interface language, catalog (Auto/TMDB/Cinemeta) and TMDB key, editable list of source addons, preferred languages, maximum quality, subtitle languages and editable list of subtitle addons, WiFi-only downloads, plugin entries and "About"; `Validate()` shows problems before saving.

## Adding an addon

1. In Settings, add the addon URL (the manifest URL works: `/manifest.json` is removed; `stremio://` is converted to `https://`). No code is needed if the addon implements the `stream` resource for `movie`/`series` with IMDb ids and returns `url`.
2. If it uses its own title format, adjust `StreamParser`; add a fixture in `tests/Parche.Core.Tests/Fixtures` and a test.
3. For another subtitle addon (`subtitles` resource, `subtitles[] {id, url, lang}` format), add it to the subtitle addons list.

## Tests

`dotnet test tests/Parche.Core.Tests`: no network; a fake `HttpMessageHandler` serves trimmed Cinemeta/OpenSubtitles fixtures, plus TMDB and addon fixtures built from the documented format.
