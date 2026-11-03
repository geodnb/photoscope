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

The app icon (`src/PhotoScope/Assets/PhotoScope.ico`, 16-256 px) is drawn by `tools/make-icon.ps1`; run
`powershell -STA -File tools\make-icon.ps1` to regenerate it.

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

**Viewer**: opens in a normal resizable window that remembers its size and position (and moves back on screen if a
monitor has been unplugged). F11 or the toolbar button toggles full screen; slideshows start full screen. Zoom and
pan, fit / 1:1, info overlay, slideshow with crossfade, shuffle, loop and an adjustable interval. Neighbouring
photos are preloaded.

**Opening photos from File Explorer**
- `PhotoScope.exe "C:\path\photo.jpg"` (which is what Windows runs when PhotoScope is the default or chosen with
  *Open with*) opens the photo straight in the viewer. The image appears first; the library loads behind it.
- The arrow keys move through the other images in the same folder, sorted by name like Explorer.
- The library button in the viewer's toolbar (or Ctrl+L) opens the main PhotoScope window, with the photo selected
  if it's in the library.
- Photos already in the library show their rating, favourite and tags. Others aren't imported: click
  **Add to library** to add one (in place, nothing is copied). Rating and favourites need the photo in the library;
  rotation works for the session.
- Only one copy of PhotoScope runs. Opening another photo while it's running hands the file to the running copy
  (over a named pipe that only your Windows account can use) instead of starting a second copy.
- Missing files, folders and unsupported file types get a short message instead of an error.

**Add to "Open with" (optional, per user)**: Settings > *Open photos from File Explorer* > **Add to Open with**.
This writes only under `HKEY_CURRENT_USER` (no admin rights, nothing system-wide) and never changes your default app:
- `Software\Classes\PhotoScope.Image` (file type and open command)
- `Software\Classes\Applications\PhotoScope.exe` (app entry and its supported types)
- a `PhotoScope.Image` value under `Software\Classes\<ext>\OpenWithProgids` for each supported extension
- `Software\PhotoScope\Capabilities` and a `PhotoScope` value in `Software\RegisteredApplications`, so it appears in
  Settings > Apps > Default apps

**Remove** in the same place deletes exactly those keys and values. To make PhotoScope the default for a type, use
*Open with > Choose another app* or Windows Settings.

**Playlists**: create, rename and delete in the sidebar. Drag photos from the gallery into the playlist strip (or onto
a playlist in the sidebar, or use right-click > Add to playlist), drag to reorder, remove, and Play as a slideshow.

**Management** (right-click a photo)
- Favourite, 0–5 star rating, tags, and rotate left/right. All of these are stored in PhotoScope's database;
  the image files are never rewritten.
- Rename (validated: no illegal or reserved names, no overwriting), Move to folder, Copy to folder.
- Open containing folder, Show in Explorer.
- Delete offers **Remove from library** (file untouched) or **Move to Recycle Bin**. There is no permanent delete:
  - First PhotoScope checks where the file really lives (through junctions, mount points and subst drives) and
    refuses network shares, removable drives, subst drives, drives whose Recycle Bin is set to *Remove files
    immediately when deleted*, files bigger than the bin, and a policy that turns the bin off.
  - Then it asks Windows (IFileOperation) to recycle the file silently. If Windows reports that the file wouldn't
    go to the Recycle Bin, the operation is aborted before anything happens. No "permanently delete?" prompt is
    ever shown.
  - Either way, a file that can't be recycled is left exactly where it was, and you're told why.
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
| Space | Start, pause or resume the slideshow (slideshows go full screen) |
| F11 | Toggle full screen |
| Ctrl+L | Open the library window (selects the photo if it's in the library); also a toolbar button when the photo was opened from Explorer |
| Delete | Delete (same choices as the gallery; files not in the library can only go to the Recycle Bin) |
| Esc | Leave full screen; closes the viewer when it's already windowed |

**Playlist strip**: Delete removes the selected photos from the playlist, Enter or double-click plays from there.

## Notes

- Importing from OneDrive folders with online-only files downloads them, because each file has to be read to hash it.
- Photos on network shares load through their own queue with timeouts. A share that stops responding is skipped for
  a minute at a time and never holds up local photos.
- The full viewer first decodes at screen size and loads full resolution (up to 8192 px on the long side) when you
  zoom in; larger images are shown slightly downscaled at 1:1.
- Photos that fail because a codec is missing are retried each time PhotoScope starts, so installing the WebP or
  HEIF extension fixes them without re-importing. Temporary errors (file in use, drive offline) are never remembered.
- The thumbnail cache defaults to 4 GB (about 100,000 photos); change it in Settings.
- Developer aids exist only in Debug builds: `PHOTOSCOPE_HOME=<folder>` (use a different data folder),
  `--debug-import <folder>` and `PHOTOSCOPE_OFFSCREEN=1` (automated UI checks). Release builds ignore them.
