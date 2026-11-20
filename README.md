<p align="center"><img src="assets/logo.png" width="128" alt="Isabelle logo"></p>

<h1 align="center">Isabelle</h1>

**Dynamic Island for your music — on Windows.**
A tiny black pill at the top of your screen that shows what's playing and grows into a full player when you hover over it.

> 🇵🇱 **Wyspa w stylu iPhone'a dla Twojej muzyki na Windowsie.** Mała czarna wyspa u góry ekranu pokazuje, co gra, a po najechaniu myszką zamienia się w pełny odtwarzacz.

---

## ✨ Features / Funkcje

| | English | Polski |
|---|---|---|
| 🎵 | Works with **any app** that shows up in the Windows media flyout — Spotify, YouTube (Chrome/Edge/Firefox), Apple Music, VLC… | Działa z **każdą aplikacją** widoczną w systemowym okienku multimediów |
| 🖼️ | Real album art (Spotify logo bar removed automatically) | Prawdziwe okładki (pasek z logo Spotify wycinany automatycznie) |
| 🌈 | Animated blurred album background and glow in the cover's color, like Apple Music | Ruchome, rozmyte tło z okładki i poświata w jej kolorze |
| 🎤 | **Synced lyrics** with smooth scrolling (LRCLIB, free) | **Tekst piosenki** zsynchronizowany z muzyką |
| 📊 | Animated bars and cover that react to the music | Słupki i okładka reagujące na muzykę |
| ⏩ | Instant seeking — drag the progress bar | Natychmiastowe przewijanie |
| ℹ️ | Click the cover for track info — album, year, genre, track number | Kliknij okładkę: album, rok, gatunek, numer utworu |
| 🖥️ | **Full-screen "Now Playing"** mode with big cover and live lyrics (`Ctrl+Alt+F`) | **Tryb pełnoekranowy** z dużą okładką i tekstem na żywo |
| 👆 | **Gestures** — flick the island sideways or swipe with two fingers on a touchpad to change tracks | **Gesty** — szybkie przesunięcie wyspy w bok (lub dwa palce na touchpadzie) zmienia utwór |
| 🔊 | Mouse-wheel volume + volume indicator in the island (replaces the Windows OSD) | Głośność kółkiem myszy i wskaźnik w wyspie |
| 🌙 | Smooth fade on pause / resume | Płynne ściszanie przy pauzie |
| 🎚️ | Optional volume leveling, smooth track transitions and a music screensaver | Opcjonalnie: wyrównywanie głośności, płynne przejścia i wygaszacz z muzyką |
| ⌨️ | Global hotkeys: `Ctrl+Alt+P`, `Ctrl+Alt+→`, `Ctrl+Alt+←` | Skróty klawiszowe |
| 📍 | Drag the island to any screen edge (top, bottom, left, right) | Przeciągnij wyspę na dowolną krawędź ekranu |
| 📺 | **OBS overlay** for streamers – cover, title and live lyrics on your stream | Nakładka OBS dla streamerów |
| 💬 | **Discord status** with the line being sung, live | Status na Discordzie z tekstem piosenki na żywo |
| 🎙️ | Lowers or pauses music during calls (mic in use) | Ścisza muzykę podczas rozmów |
| 🖼️ | Wallpaper in the colors of the current album cover | Tapeta w kolorach aktualnej okładki |
| 🔒 | Current cover on the lock screen (optional) | Okładka na ekranie blokady (opcja) |
| ⏸️ | Auto-pause when another app starts playing, per-player volume memory | Auto-pauza, gdy gra coś innego; osobna głośność dla każdego odtwarzacza |
| 🎮 | Hides automatically in fullscreen games and videos | Chowa się w grach i filmach na pełnym ekranie |
| 🔀 | Switch between multiple players | Przełączanie między odtwarzaczami |
| 🌍 | English and Polish (follows your Windows language) | Polski i angielski (wg języka Windows) |
| ⬆️ | Automatic updates from GitHub Releases | Automatyczne aktualizacje |

## 📥 Install / Instalacja

1. Download **`Isabelle-Setup.exe`** from **Releases** and run it (no admin rights, no .NET required).
2. A welcome screen explains the basics.
3. Settings: right-click the island or use the tray icon. The app updates itself when a new release is published.

> Pobierz **`Isabelle-Setup.exe`** z zakładki **Releases** i uruchom. Program sam się aktualizuje.

**Requirements:** Windows 10 (1809+) or Windows 11.

## 🛠️ Build from source / Kompilacja

```bat
build.bat            :: installs .NET 8 SDK if needed, builds and runs
publish.bat          :: single self-contained Isabelle.exe in /publish
build_installer.bat  :: installer\Isabelle-Setup.exe (installer, updater and uninstaller in one file)
```

### Releasing a new version

1. Set your repository in `UpdateService.cs` → `Repo = "your-login/Isabelle"` (once).
2. Bump the version in `IslandPlayer.csproj` (`<Version>`).
3. Run `build_installer.bat` and attach **both** `installer/Isabelle-Setup.exe` and `installer/Isabelle-Setup.exe.sig` to a GitHub release tagged e.g. `v1.2.0`.

Updates are verified with an RSA signature: installed copies only accept an installer signed with the author's private key (`keys/private.xml`, never committed).

Installed copies will offer the update automatically.

or manually:

```bash
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## 🧩 Tech

- C# / .NET 8 / WPF — GPU-accelerated rendering, spring animations
- `Windows.Media.Control` (GSMTC) — media sessions, covers, seeking
- [NAudio](https://github.com/naudio/NAudio) — system volume and audio levels
- [LRCLIB](https://lrclib.net) — synced lyrics

Settings are stored in `%APPDATA%\Isabelle\settings.json`.

## 📄 License

© 2026 Michael Szczerbowski. All rights reserved — see [LICENSE](LICENSE).
The source code is public for transparency; copying, modifying or redistributing it requires the author's permission.
