using System;
using System.Collections.Generic;
using System.IO;

namespace FIHGhost
{
    /// <summary>
    /// The best ghost per map, difficulty, category and id, one file each under
    /// Ghosts/PB/&lt;map&gt;/&lt;difficulty&gt;/. A split only replaces the file when its
    /// time is better. Times are cached after the first read of each file.
    /// </summary>
    internal sealed class PersonalBests
    {
        // Path -> FinalTime of the file there, or null when there is none.
        private readonly Dictionary<string, double?> _times = new Dictionary<string, double?>();

        public static string Folder => Path.Combine(GhostPlugin.RecordingsDir.Value, "PB");

        public static string PathFor(string mapId, string difficulty, GhostCategory category, string id)
        {
            string name = category switch
            {
                GhostCategory.FullRun => "fullrun",
                GhostCategory.Area => "area_" + id,
                GhostCategory.Challenge => "challenge_" + id,
                GhostCategory.Route => "route_" + id,
                _ => throw new ArgumentException($"{category} ghosts have no PB"),
            };
            return Path.Combine(Folder, Safe(mapId), Safe(difficulty), Safe(name) + GhostRecording.Extension);
        }

        public double? TimeOf(string mapId, string difficulty, GhostCategory category, string id)
        {
            string path = PathFor(mapId, difficulty, category, id);
            if (_times.TryGetValue(path, out var cached)) return cached;

            double? time = null;
            if (File.Exists(path))
            {
                try
                {
                    time = GhostRecording.LoadSummary(path, out _).FinalTime;
                }
                catch (Exception ex)
                {
                    GhostPlugin.Logger.LogWarning($"[PB] Ignoring unreadable {path}: {ex.Message}");
                }
            }
            _times[path] = time;
            return time;
        }

        /// <summary>
        /// Saves the split if it beats the stored PB (or there is none). Returns the PB time it
        /// was compared against (null when there was none); <paramref name="improved"/> says
        /// whether the split was saved as the new PB.
        /// </summary>
        public double? Submit(GhostRecording split, out bool improved)
        {
            double? previous = TimeOf(split.MapId, split.Difficulty, split.Category, split.CategoryId);
            improved = previous == null || split.FinalTime < previous.Value;
            if (!improved) return previous;

            string path = PathFor(split.MapId, split.Difficulty, split.Category, split.CategoryId);
            try
            {
                split.Save(path);
                _times[path] = split.FinalTime;
                GhostPlugin.Logger.LogInfo($"[PB] New {split.Category} '{split.CategoryId}' {split.FinalTime:0.000} s " +
                                           $"(was {(previous.HasValue ? previous.Value.ToString("0.000") : "none")}): {path}");
            }
            catch (Exception ex)
            {
                improved = false;
                GhostPlugin.Logger.LogError($"[PB] Could not save {path}: {ex}");
            }
            return previous;
        }

        /// <summary>The PB ghost, or null when there is none or it can't be read.</summary>
        public GhostRecording Load(string mapId, string difficulty, GhostCategory category, string id)
        {
            string path = PathFor(mapId, difficulty, category, id);
            if (!File.Exists(path)) return null;
            try
            {
                return GhostRecording.Load(path);
            }
            catch (Exception ex)
            {
                GhostPlugin.Logger.LogWarning($"[PB] Could not load {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>How many area and challenge PBs exist for a map and difficulty.</summary>
        public void Count(string mapId, string difficulty, out int areas, out int challenges)
        {
            areas = challenges = 0;
            string dir = Path.Combine(Folder, Safe(mapId), Safe(difficulty));
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*" + GhostRecording.Extension))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("area_")) areas++;
                else if (name.StartsWith("challenge_")) challenges++;
            }
        }

        private static string Safe(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
