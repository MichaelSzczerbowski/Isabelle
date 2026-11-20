using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace IslandPlayer
{
    public sealed class Playlist
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public List<string> Paths { get; set; } = new List<string>();
        public DateTime Created { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// polubione utwory i playlisty biblioteki (ścieżki plików), zapisane w playlists.json.
    /// </summary>
    public static class PlaylistStore
    {
        private sealed class Data
        {
            public List<string> Liked { get; set; } = new List<string>();
            public List<Playlist> Playlists { get; set; } = new List<Playlist>();
        }

        private static Data _data;
        private static HashSet<string> _liked;
        private static string FilePath => Path.Combine(Settings.Folder, "playlists.json");

        /// <summary>Coś się zmieniło (polubienie, playlisty) - do odświeżenia widoków.</summary>
        public static event Action Changed;

        private static void Ensure()
        {
            if (_data != null) return;
            try
            {
                if (File.Exists(FilePath)) _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
            }
            catch (Exception ex) { Log.Error(ex); }
            _data ??= new Data();
            _data.Liked ??= new List<string>();
            _data.Playlists ??= new List<Playlist>();
            _liked = new HashSet<string>(_data.Liked, StringComparer.OrdinalIgnoreCase);
        }

        private static void Save()
        {
            try
            {
                _data.Liked = _liked.ToList();
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Log.Error(ex); }
            Changed?.Invoke();
        }

        // ---- polubione ----
        public static bool IsLiked(string path) { Ensure(); return path != null && _liked.Contains(path); }
        public static int LikedCount { get { Ensure(); return _liked.Count; } }

        public static void ToggleLike(string path)
        {
            Ensure();
            if (string.IsNullOrEmpty(path)) return;
            if (!_liked.Remove(path)) _liked.Add(path);
            Save();
        }

        // ---- playlisty ----
        public static IReadOnlyList<Playlist> Playlists { get { Ensure(); return _data.Playlists; } }

        public static bool InAnyPlaylist(string path)
        {
            Ensure();
            return path != null && _data.Playlists.Any(p => p.Paths.Contains(path, StringComparer.OrdinalIgnoreCase));
        }

        public static Playlist Find(string id) { Ensure(); return _data.Playlists.FirstOrDefault(p => p.Id == id); }

        public static Playlist Create(string name, IEnumerable<string> paths = null)
        {
            Ensure();
            var p = new Playlist { Name = string.IsNullOrWhiteSpace(name) ? L.T("Nowa playlista", "New playlist") : name.Trim() };
            if (paths != null) p.Paths.AddRange(paths.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase));
            _data.Playlists.Add(p);
            Save();
            return p;
        }

        public static void Rename(string id, string name)
        {
            var p = Find(id);
            if (p == null || string.IsNullOrWhiteSpace(name)) return;
            p.Name = name.Trim();
            Save();
        }

        public static void Delete(string id)
        {
            Ensure();
            _data.Playlists.RemoveAll(p => p.Id == id);
            Save();
        }

        /// <summary>Dodaje utwór na koniec playlisty (bez duplikatów). False = już tam był.</summary>
        public static bool Add(string id, string path)
        {
            var p = Find(id);
            if (p == null || string.IsNullOrEmpty(path) || p.Paths.Contains(path, StringComparer.OrdinalIgnoreCase)) return false;
            p.Paths.Add(path);
            Save();
            return true;
        }

        public static void Remove(string id, string path)
        {
            var p = Find(id);
            if (p == null) return;
            p.Paths.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }
}
