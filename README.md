# Parche

**Website: <https://fsoftt.github.io/Parche/>**

Media center for **Android (phone and Android TV)** and **Windows (desktop)** built with **.NET MAUI**. It plays content from the **addons you add** (compatible with the Stremio addon protocol) and shows a catalog with metadata from TMDB or Cinemeta. Parche neither includes nor hosts any content, and ships with no preconfigured source addons: with no addons added, the app works as a catalog.

> Parche is an independent project. It is not affiliated with Stremio or TMDB.

## How it works

```
Catalog (TMDB or Cinemeta)    ─► titles, genres, seasons
Source addons (yours)         ─► playback options
Subtitle addons               ─► external tracks (OpenSubtitles by default)
                    │
             Parche.Core ── ParcheEngine ──► Parche.App (MAUI)
                                              └─► ExoPlayer (Android) / LibVLC (Windows)
```

- **`src/Parche.Core`** (net8.0, no UI): catalog, addon client, source ranking, subtitles, settings and library. See [ARCHITECTURE.md](ARCHITECTURE.md).
- **`src/Parche.App`** (net10.0-android and net10.0-windows): video-service-style interface. Phone with bottom tabs; Android TV with a side menu and D-pad navigation; Windows with a resizable window, mouse and keyboard. Tabs: Home, Search, Categories, My list and Settings.

## Settings

From the *Settings* tab:

- **Catalog**: automatic (TMDB if there is a key, otherwise Cinemeta), TMDB or Cinemeta. You can get a TMDB key at themoviedb.org/settings/api.
- **Source addons**: editable list of addon URLs compatible with the Stremio addon protocol. With none, the app invites you to add one.
- **Subtitle addons**: editable list (OpenSubtitles by default). All of them are queried and the results are merged; any that fails is skipped.
- Preferred languages, preferred maximum quality, subtitle languages and WiFi-only downloads.

## Build

Requirements: .NET 10 SDK with the `maui-android` workload and Java 21.

```bash
dotnet workload install maui-android
dotnet build src/Parche.App/Parche.App.csproj -f net10.0-android -t:Run   # on a connected device/emulator
dotnet test tests/Parche.Core.Tests
```

GitHub Actions runs the tests on every push and pull request to `main` (`ci.yml`). Publishing to the `release` branch (or running it manually) builds an APK, kept as the `parche-android-apk` artifact (`android.yml`). The same APK works on phone and TV.

### Windows (desktop)

Requirements: Windows 10 1809 or later, .NET 10 SDK and the `maui-windows` workload. Visual Studio is not required.

```powershell
dotnet workload install maui-windows
dotnet build src/Parche.App/Parche.App.csproj -f net10.0-windows10.0.19041.0
src\Parche.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Parche.App.exe
```

The app runs as a loose `.exe` (no MSIX) and ships its own Windows App SDK runtime and LibVLC. The whole `win-x64` folder of the build output can be copied to another machine.

The Windows player uses **LibVLC** (opens MKV/MP4 with H.264/HEVC, multiple audio tracks and external subtitles). Shortcuts: `Space` pauses, `←`/`→` ±10 s, `↑`/`↓` volume, `M` mute, `F` or `F11` full screen, `Esc` leaves full screen (or goes back), double-click toggles full screen. Limitations: VLC only sends the `User-Agent` and `Referer` headers, downloads continue only while the window is open (there is no background service) and cards are operated with the mouse.

### Extensions

The app has extension points (`IStreamSourceProvider`, `IStreamResolver`, `IDownloadResolver`, `IPluginSettingsSection`) and an optional props file (`ParchePluginsProps`) to build external modules together with the app. See [ARCHITECTURE.md](ARCHITECTURE.md).

## Roku

The Roku version is a separate project, written in BrightScript, with the same idea: [fsoftt/ParcheRoku](https://github.com/fsoftt/ParcheRoku). Its README explains how to enable developer mode and install it.

## License

[MIT](LICENSE). Copyright (c) 2026 fsoftt.

### Third-party notices

- This product uses the TMDB API but is not endorsed or certified by TMDB.
- Parche is compatible with the Stremio addon protocol, but it is an independent project: it has no relationship with Stremio and is not affiliated with, sponsored or endorsed by Stremio. Stremio is a trademark of its respective owners.
- [LibVLCSharp](https://github.com/videolan/libvlcsharp) and LibVLC (LGPL-2.1) for Windows playback, used as libraries.
- [Media3 / ExoPlayer](https://github.com/androidx/media) (Apache-2.0) for Android playback.
- [.NET MAUI](https://github.com/dotnet/maui), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT) and the Open Sans font (SIL OFL 1.1).

## Disclaimer

Parche is a playback tool for personal use. It neither includes nor hosts protected content; users are responsible for making sure they have the right to access what they play and for complying with the laws and the terms of the services they configure.
