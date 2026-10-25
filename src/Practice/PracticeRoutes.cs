using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace FIHGhost
{
    internal sealed class PracticeRoute
    {
        public int Id;
        public string Name;
        public bool HasStart;
        public Vector3 StartPosition;
        public Quaternion StartRotation = Quaternion.identity;
        public float StartCameraYaw;
        public bool HasEnd;
        public Vector3 EndCenter;
        public float EndSize = PracticeRoutes.DefaultEndSize;

        public bool IsReady => HasStart && HasEnd;
        public Bounds EndBounds => new Bounds(EndCenter, Vector3.one * EndSize);
    }

    /// <summary>
    /// The practice routes of one map, in Ghosts/Practice/&lt;map&gt;/routes.cfg: a start
    /// pose (phone position and rotation, camera yaw) and an end zone (a cube around a
    /// point). Ids are never reused, so a deleted route's PB files can't be picked up by a
    /// new one.
    /// </summary>
    internal sealed class PracticeRoutes
    {
        public const float DefaultEndSize = 4f;
        public const float MinEndSize = 1f;
        public const float MaxEndSize = 30f;

        private readonly List<PracticeRoute> _routes = new List<PracticeRoute>();
        private string _mapId;
        private int _nextId = 1;

        public IReadOnlyList<PracticeRoute> Routes => _routes;
        public string MapId => _mapId;

        public static string FolderFor(string mapId) => Path.Combine(GhostPlugin.RecordingsDir.Value, "Practice", Safe(mapId));
        private string FilePath => Path.Combine(FolderFor(_mapId), "routes.cfg");

        public PracticeRoute Find(int id) => _routes.FirstOrDefault(r => r.Id == id);

        public string NameOf(string id)
            => int.TryParse(id, out int n) && Find(n) is PracticeRoute r ? r.Name : "Route " + id;

        /// <summary>Loads the routes of a map (nothing to do when it is the one loaded).</summary>
        public void Load(string mapId)
        {
            if (mapId == _mapId) return;
            _mapId = mapId;
            _routes.Clear();
            _nextId = 1;
            if (!File.Exists(FilePath)) return;
            try
            {
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("next=")) _nextId = Math.Max(_nextId, int.Parse(line.Substring(5), CultureInfo.InvariantCulture));
                    else if (line.StartsWith("route|")) _routes.Add(ParseRoute(line));
                }
                if (_routes.Count > 0) _nextId = Math.Max(_nextId, _routes.Max(r => r.Id) + 1);
            }
            catch (Exception ex)
            {
                GhostPlugin.Logger.LogWarning($"[PRACTICE] Could not read {FilePath}: {ex.Message}");
            }
        }

        public PracticeRoute Add()
        {
            var route = new PracticeRoute { Id = _nextId++, };
            route.Name = "Route " + route.Id;
            _routes.Add(route);
            Save();
            return route;
        }

        public void Remove(PracticeRoute route)
        {
            if (_routes.Remove(route)) Save();
        }

        public void Save()
        {
            if (_mapId == null) return;
            var lines = new List<string>
            {
                "# FIH Ghost practice routes: route|id|name|hasStart|x;y;z;qx;qy;qz;qw;cameraYaw|hasEnd|x;y;z;size",
                "next=" + _nextId.ToString(CultureInfo.InvariantCulture),
            };
            foreach (var r in _routes)
            {
                lines.Add(string.Join("|", "route", r.Id.ToString(CultureInfo.InvariantCulture), r.Name,
                    r.HasStart ? "1" : "0",
                    Join(r.StartPosition.x, r.StartPosition.y, r.StartPosition.z,
                         r.StartRotation.x, r.StartRotation.y, r.StartRotation.z, r.StartRotation.w, r.StartCameraYaw),
                    r.HasEnd ? "1" : "0",
                    Join(r.EndCenter.x, r.EndCenter.y, r.EndCenter.z, r.EndSize)));
            }
            try
            {
                Directory.CreateDirectory(FolderFor(_mapId));
                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception ex)
            {
                GhostPlugin.Logger.LogError($"[PRACTICE] Could not save {FilePath}: {ex}");
            }
        }

        private static PracticeRoute ParseRoute(string line)
        {
            var f = line.Split('|');
            var s = f[4].Split(';').Select(Parse).ToArray();
            var e = f[6].Split(';').Select(Parse).ToArray();
            return new PracticeRoute
            {
                Id = int.Parse(f[1], CultureInfo.InvariantCulture),
                Name = f[2],
                HasStart = f[3] == "1",
                StartPosition = new Vector3(s[0], s[1], s[2]),
                StartRotation = new Quaternion(s[3], s[4], s[5], s[6]),
                StartCameraYaw = s[7],
                HasEnd = f[5] == "1",
                EndCenter = new Vector3(e[0], e[1], e[2]),
                EndSize = e[3],
            };
        }

        private static string Join(params float[] values)
            => string.Join(";", values.Select(v => v.ToString("0.#####", CultureInfo.InvariantCulture)));

        private static float Parse(string s) => float.Parse(s.Trim(), CultureInfo.InvariantCulture);

        private static string Safe(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
