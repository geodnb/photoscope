using Microsoft.Data.Sqlite;
using PhotoScope.Models;

namespace PhotoScope.Services;

/// <summary>
/// The SQLite library database. One connection, serialised by a lock, so it is safe to call
/// from the UI thread (small updates) and from background import workers.
/// Stores metadata only; it never touches the image files themselves.
/// </summary>
public sealed class LibraryDb : IDisposable
{
    readonly SqliteConnection _conn;
    readonly object _gate = new();
    bool _disposed;

    public LibraryDb(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        _conn = new SqliteConnection(cs);
        _conn.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        Migrate();
    }

    void Migrate()
    {
        var version = Convert.ToInt64(Scalar("PRAGMA user_version"));
        if (version < 1)
        {
            using var tx = _conn.BeginTransaction();
            Execute("""
                CREATE TABLE IF NOT EXISTS photos(
                    id            INTEGER PRIMARY KEY,
                    path          TEXT    NOT NULL UNIQUE COLLATE NOCASE,
                    size          INTEGER NOT NULL,
                    modified_utc  INTEGER NOT NULL,
                    hash          TEXT    NOT NULL,
                    width         INTEGER NOT NULL DEFAULT 0,
                    height        INTEGER NOT NULL DEFAULT 0,
                    orientation   INTEGER NOT NULL DEFAULT 1,
                    date_taken    INTEGER NULL,
                    date_imported INTEGER NOT NULL,
                    camera        TEXT    NULL,
                    favourite     INTEGER NOT NULL DEFAULT 0,
                    rating        INTEGER NOT NULL DEFAULT 0,
                    rotation      INTEGER NOT NULL DEFAULT 0,
                    tags          TEXT    NOT NULL DEFAULT '',
                    missing       INTEGER NOT NULL DEFAULT 0,
                    decode_error  TEXT    NULL
                );
                CREATE INDEX IF NOT EXISTS ix_photos_hash ON photos(hash);
                CREATE TABLE IF NOT EXISTS playlists(
                    id          INTEGER PRIMARY KEY,
                    name        TEXT    NOT NULL,
                    created_utc INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS playlist_items(
                    playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
                    position    INTEGER NOT NULL,
                    photo_id    INTEGER NOT NULL REFERENCES photos(id) ON DELETE CASCADE,
                    PRIMARY KEY(playlist_id, position)
                );
                CREATE INDEX IF NOT EXISTS ix_playlist_items_photo ON playlist_items(photo_id);
                PRAGMA user_version = 1;
                """, tx);
            tx.Commit();
        }
    }

    // ---------------------------------------------------------------- photos

    const string PhotoColumns =
        "id, path, size, modified_utc, hash, width, height, orientation, date_taken, date_imported, camera, favourite, rating, rotation, tags, missing, decode_error";

    public List<Photo> LoadPhotos()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {PhotoColumns} FROM photos";
            using var r = cmd.ExecuteReader();
            var list = new List<Photo>();
            while (r.Read())
            {
                list.Add(new Photo
                {
                    Id = r.GetInt64(0),
                    Path = r.GetString(1),
                    SizeBytes = r.GetInt64(2),
                    ModifiedUtc = new DateTime(r.GetInt64(3), DateTimeKind.Utc),
                    Hash = r.GetString(4),
                    PixelWidth = r.GetInt32(5),
                    PixelHeight = r.GetInt32(6),
                    Orientation = r.GetInt32(7),
                    DateTaken = r.IsDBNull(8) ? null : new DateTime(r.GetInt64(8), DateTimeKind.Unspecified),
                    DateImportedUtc = new DateTime(r.GetInt64(9), DateTimeKind.Utc),
                    CameraModel = r.IsDBNull(10) ? null : r.GetString(10),
                    IsFavourite = r.GetInt64(11) != 0,
                    Rating = r.GetInt32(12),
                    Rotation = r.GetInt32(13),
                    Tags = r.GetString(14),
                    IsMissing = r.GetInt64(15) != 0,
                    DecodeError = r.IsDBNull(16) ? null : r.GetString(16),
                });
            }
            return list;
        }
    }

    /// <summary>Inserts photos and assigns their ids. Photos whose path already exists are left with Id 0.</summary>
    public void InsertPhotos(IReadOnlyList<Photo> photos)
    {
        if (photos.Count == 0) return;
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO photos(path, size, modified_utc, hash, width, height, orientation, date_taken, date_imported, camera, favourite, rating, rotation, tags, missing, decode_error)
                VALUES($path, $size, $mod, $hash, $w, $h, $o, $taken, $imported, $camera, 0, 0, 0, '', 0, $err)
                ON CONFLICT(path) DO NOTHING
                RETURNING id
                """;
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
            var pMod = cmd.Parameters.Add("$mod", SqliteType.Integer);
            var pHash = cmd.Parameters.Add("$hash", SqliteType.Text);
            var pW = cmd.Parameters.Add("$w", SqliteType.Integer);
            var pH = cmd.Parameters.Add("$h", SqliteType.Integer);
            var pO = cmd.Parameters.Add("$o", SqliteType.Integer);
            var pTaken = cmd.Parameters.Add("$taken", SqliteType.Integer);
            var pImported = cmd.Parameters.Add("$imported", SqliteType.Integer);
            var pCamera = cmd.Parameters.Add("$camera", SqliteType.Text);
            var pErr = cmd.Parameters.Add("$err", SqliteType.Text);
            foreach (var p in photos)
            {
                pPath.Value = p.Path;
                pSize.Value = p.SizeBytes;
                pMod.Value = p.ModifiedUtc.Ticks;
                pHash.Value = p.Hash;
                pW.Value = p.PixelWidth;
                pH.Value = p.PixelHeight;
                pO.Value = p.Orientation;
                pTaken.Value = p.DateTaken is { } t ? t.Ticks : DBNull.Value;
                pImported.Value = p.DateImportedUtc.Ticks;
                pCamera.Value = (object?)p.CameraModel ?? DBNull.Value;
                pErr.Value = (object?)p.DecodeError ?? DBNull.Value;
                var id = cmd.ExecuteScalar();
                p.Id = id is null or DBNull ? 0 : Convert.ToInt64(id);
            }
            tx.Commit();
        }
    }

    public HashSet<string> GetAllHashes()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT hash FROM photos";
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
    }

    public HashSet<string> GetAllPaths()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT path FROM photos";
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
    }

    public bool PathExists(string path)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM photos WHERE path = $p LIMIT 1";
            cmd.Parameters.AddWithValue("$p", path);
            return cmd.ExecuteScalar() != null;
        }
    }

    public void SetFavourite(IEnumerable<long> ids, bool value) => UpdateMany("UPDATE photos SET favourite = $v WHERE id = $id", ids, value ? 1 : 0);
    public void SetRating(IEnumerable<long> ids, int value) => UpdateMany("UPDATE photos SET rating = $v WHERE id = $id", ids, Math.Clamp(value, 0, 5));
    public void SetRotation(long id, int value) => UpdateMany("UPDATE photos SET rotation = $v WHERE id = $id", [id], ((value % 360) + 360) % 360);
    public void SetTags(long id, string tags) => UpdateMany("UPDATE photos SET tags = $v WHERE id = $id", [id], tags);
    public void SetMissing(IEnumerable<long> ids, bool value) => UpdateMany("UPDATE photos SET missing = $v WHERE id = $id", ids, value ? 1 : 0);
    public void SetDecodeError(long id, string? error) => UpdateMany("UPDATE photos SET decode_error = $v WHERE id = $id", [id], (object?)error ?? DBNull.Value);
    public void SetPath(long id, string path) => UpdateMany("UPDATE photos SET path = $v WHERE id = $id", [id], path);

    /// <summary>Updates the file details after a relocate (or when the file changed on disk).</summary>
    public void UpdateFileDetails(Photo p)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE photos SET path=$path, size=$size, modified_utc=$mod, hash=$hash, width=$w, height=$h,
                       orientation=$o, date_taken=$taken, camera=$camera, missing=$missing, decode_error=$err
                WHERE id=$id
                """;
            cmd.Parameters.AddWithValue("$path", p.Path);
            cmd.Parameters.AddWithValue("$size", p.SizeBytes);
            cmd.Parameters.AddWithValue("$mod", p.ModifiedUtc.Ticks);
            cmd.Parameters.AddWithValue("$hash", p.Hash);
            cmd.Parameters.AddWithValue("$w", p.PixelWidth);
            cmd.Parameters.AddWithValue("$h", p.PixelHeight);
            cmd.Parameters.AddWithValue("$o", p.Orientation);
            cmd.Parameters.AddWithValue("$taken", p.DateTaken is { } t ? t.Ticks : DBNull.Value);
            cmd.Parameters.AddWithValue("$camera", (object?)p.CameraModel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$missing", p.IsMissing ? 1 : 0);
            cmd.Parameters.AddWithValue("$err", (object?)p.DecodeError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", p.Id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Removes library entries (and their playlist entries). Never touches files.</summary>
    public void DeletePhotos(IEnumerable<long> ids) => UpdateMany("DELETE FROM photos WHERE id = $id", ids, null);

    void UpdateMany(string sql, IEnumerable<long> ids, object? value)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            if (value != null) cmd.Parameters.AddWithValue("$v", value);
            var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
            foreach (var id in ids)
            {
                pId.Value = id;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ---------------------------------------------------------------- playlists

    public List<Playlist> LoadPlaylists()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT p.id, p.name, (SELECT COUNT(*) FROM playlist_items i WHERE i.playlist_id = p.id)
                FROM playlists p ORDER BY p.name COLLATE NOCASE, p.id
                """;
            using var r = cmd.ExecuteReader();
            var list = new List<Playlist>();
            while (r.Read())
                list.Add(new Playlist { Id = r.GetInt64(0), Name = r.GetString(1), Count = r.GetInt32(2) });
            return list;
        }
    }

    public long CreatePlaylist(string name)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO playlists(name, created_utc) VALUES($n, $c) RETURNING id";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.Ticks);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public void RenamePlaylist(long id, string name)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE playlists SET name = $n WHERE id = $id";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeletePlaylist(long id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM playlists WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public List<long> LoadPlaylistPhotoIds(long playlistId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT photo_id FROM playlist_items WHERE playlist_id = $id ORDER BY position";
            cmd.Parameters.AddWithValue("$id", playlistId);
            using var r = cmd.ExecuteReader();
            var list = new List<long>();
            while (r.Read()) list.Add(r.GetInt64(0));
            return list;
        }
    }

    /// <summary>Replaces the playlist's contents with <paramref name="photoIds"/> in order.</summary>
    public void SavePlaylistItems(long playlistId, IReadOnlyList<long> photoIds)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using (var del = _conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM playlist_items WHERE playlist_id = $id";
                del.Parameters.AddWithValue("$id", playlistId);
                del.ExecuteNonQuery();
            }
            using (var ins = _conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO playlist_items(playlist_id, position, photo_id) VALUES($pl, $pos, $ph)";
                ins.Parameters.AddWithValue("$pl", playlistId);
                var pPos = ins.Parameters.Add("$pos", SqliteType.Integer);
                var pPh = ins.Parameters.Add("$ph", SqliteType.Integer);
                for (var i = 0; i < photoIds.Count; i++)
                {
                    pPos.Value = i;
                    pPh.Value = photoIds[i];
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    // ---------------------------------------------------------------- helpers

    void Execute(string sql, SqliteTransaction? tx = null)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    object? Scalar(string sql)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "PRAGMA optimize;";
                cmd.ExecuteNonQuery();
            }
            catch { }
            _conn.Dispose();
        }
    }
}
