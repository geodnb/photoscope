# PhotoScope

A fast, simple photo viewer and manager for Windows 11, built with C# / .NET 8 and WPF.
It stays smooth with libraries of 10,000+ photos and never modifies your originals without asking.

## Build and run

Requirements: Windows 10/11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). No admin rights, installer or services are needed.

```powershell
git clone <this repo>
cd photoscope
dotnet run --project src/PhotoScope -c Release
```

To produce a standalone folder you can copy anywhere:

```powershell
dotnet publish src/PhotoScope -c Release -r win-x64 --self-contained false -o publish
.\publish\PhotoScope.exe
```

Run the tests with `dotnet test`.

## Where things are stored

Everything PhotoScope writes lives in `%LocalAppData%\PhotoScope`:

| Path | Contents |
|---|---|
| `library.db` | SQLite library: paths, dates, favourites, ratings, tags, viewing rotation, playlists |
| `thumbs\` | Thumbnail cache, named by content hash + modified time (safe to clear) |
| `logs\` | Daily log files (kept for 14 days) |
| `settings.json` | Window layout, last view and settings |

The only other place it writes is the library folder you choose for **Copy into library folder** imports
(default `Pictures\PhotoScope Library`).

## Features

**Library and gallery**
- All Photos, Favourites, Recently Imported (last 30 days), and Missing Files when any are missing.
- Virtualised thumbnail grid with a size slider (or Ctrl + mouse wheel), sorting by date taken, date imported,
  name or size, and search by file name or tag.
- Ctrl/Shift multi-select. Thumbnails load in the background, newest requests first, so scrolling never blocks.

**Import**
- Import files or folders (subfolders optional) from the Import button or by dropping them on the window.
- *Reference in place* (default) leaves files where they are; *Copy into library folder* copies them into
  `Year\Month` folders, never overwriting (name clashes become `photo (2).jpg`).
- Duplicates are detected by SHA-256 content hash and skipped if you choose.
- Runs in the background with progress and Cancel; photos appear in the gallery as they arrive.
- Date taken comes from EXIF where present, otherwise the file date.

**Formats**: JPEG, PNG, BMP, GIF, TIFF, ICO and JPEG XR always work. WebP, HEIC/HEIF and AVIF work when the
matching Windows codec extensions are installed from the Microsoft Store. Files that can't be decoded show a
placeholder with the reason instead of crashing.

**Details pane**: large preview, dimensions, file size, date taken, camera model, path, favourite, rating and tags.

**Full viewer**: fullscreen, zoom and pan, fit / 1:1, info overlay, slideshow with crossfade, shuffle, loop and
an adjustable interval. Neighbouring photos are preloaded.

**Playlists**: create, rename and delete in the sidebar. Drag photos from the gallery into the playlist strip (or onto
a playlist in the sidebar, or use right-click > Add to playlist), drag to reorder, remove, and Play as a slideshow.

**Management** (right-click a photo)
- Favourite, 0–5 star rating, tags, and rotate left/right. All of these are stored in PhotoScope's database;
  the image files are never rewritten.
- Rename (validated: no illegal or reserved names, no overwriting), Move to folder, Copy to folder.
- Open containing folder, Show in Explorer.
- Delete offers **Remove from library** (file untouched) or **Move to Recycle Bin**. There is no permanent delete.
  Before deleting, PhotoScope works out where the file really lives (through junctions, mount points and subst
  drives) and refuses, leaving the file alone, if Windows would delete it permanently: network shares, removable
  drives, drives whose Recycle Bin is set to *Remove files immediately when deleted*, files bigger than the bin,
  or a policy that disables the bin.
- Missing files (moved or deleted outside the app, or on a disconnected drive) are flagged on startup and can be
  relocated (pick the file, or let PhotoScope search a folder by name, size and content) or removed.

**Settings**: theme (follow Windows, Light or Dark), library copy folder, default import mode, slideshow interval,
shuffle and loop, thumbnail cache size with Clear cache.

## Keyboard shortcuts

**Gallery**

| Key | Action |
|---|---|
| Enter / double-click | Open in the viewer |
| F | Toggle favourite |
| 0–5 | Set rating (0 clears) |
| Delete | Delete (choose Remove from library or Recycle Bin) |
| F2 | Rename |
| Ctrl+A | Select all |
| Ctrl+F | Search |
| Ctrl+I | Import |
| Ctrl + mouse wheel | Thumbnail size |

**Viewer**

| Key / mouse | Action |
|---|---|
| ← → / mouse wheel / PageUp PageDown | Previous / next photo |
| Home / End | First / last photo |
| Ctrl + wheel, pinch, + / − | Zoom |
| Double-click | Toggle fit / 1:1 |
| Drag | Pan when zoomed |
| Ctrl+0 / Ctrl+1 | Fit / actual size |
| I | Info overlay |
| F | Toggle favourite |
| 0–5 | Set rating |
| R / Shift+R | Rotate right / left (viewing rotation only) |
| Space | Start, pause or resume the slideshow |
| Delete | Delete (same choices as the gallery) |
| Esc | Close |

**Playlist strip**: Delete removes the selected photos from the playlist, Enter or double-click plays from there.

## Notes

- Importing from OneDrive folders with online-only files downloads them, because each file has to be read to hash it.
- The full viewer first decodes at screen size and loads full resolution (up to 8192 px on the long side) when you
  zoom in; larger images are shown slightly downscaled at 1:1.
- Photos that fail because a codec is missing are retried each time PhotoScope starts, so installing the WebP or
  HEIF extension fixes them without re-importing. Temporary errors (file in use, drive offline) are never remembered.
- The thumbnail cache defaults to 4 GB (about 100,000 photos); change it in Settings.
- Developer aids exist only in Debug builds: `PHOTOSCOPE_HOME=<folder>` (use a different data folder),
  `--debug-import <folder>` and `PHOTOSCOPE_OFFSCREEN=1` (automated UI checks). Release builds ignore them.
