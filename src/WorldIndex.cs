using System;
using System.Collections.Generic;
using System.Linq;
using EHS;
using EHS.Interactables;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FIHGhost
{
    /// <summary>
    /// Where things are in the loaded level, for the menu's teleports and for naming ghosts:
    /// the start of every area and every NPC with its challenge.
    ///
    /// An area starts where its progress zone starts: each area has ChangeLevelProgress
    /// zones (by height or along a spline) that tell the game how far the player got, and
    /// their start point is on the path the level is climbed from. A boss area is the
    /// exception: landing inside it skips the door that starts the fight (the game then
    /// forces a fail-safe boss respawn), so its target, and that of any NPC inside it, is
    /// the end of the previous area's path, in front of that door. That point is above the
    /// door's roof, so the target is the first floor under it. Every computed target is
    /// put on the floor below it, so the phone doesn't land falling. The spots the level
    /// data gets wrong are placed by hand (FixedTargets).
    /// </summary>
    internal sealed class WorldIndex
    {
        public sealed class Area
        {
            public int Id;
            public string Name;
            public bool HasStart;
            public Vector3 Position;
            public Quaternion Rotation;
            // The camera yaw to land with; null: face the phone's heading.
            public float? CameraYaw;
            public string Key => RunSession.TeleportAreaPrefix + Id;
        }

        public sealed class Npc
        {
            public string Name;
            public string ChallengeId;   // null for NPCs without a challenge
            public Vector3 Position;
            public Quaternion Rotation;
            public float? CameraYaw;
            public string Key => "npc:" + Name;
        }

        // A zone's path from its start to its end, and the volume its trigger covers.
        private struct Zone
        {
            public int Area;
            public Vector3 Start, StartAhead, End, EndBehind;
            public Bounds Bounds;
            public bool HasBounds;
        }

        // The phone rests this far above the floor it lands on: lying flat it is ~0.31 m
        // thick (its BoundingBox), so its centre sits just above the floor.
        private const float RestHeight = 0.2f;
        // How far above a target the floor search starts, and how far down it looks.
        private const float SearchAbove = 1.5f;
        private const float SearchDepth = 12f;
        // Under a roof the floor can be further down than in the open.
        private const float RoofSearchDepth = 30f;
        // How far in front of an NPC the phone lands.
        private const float NpcDistance = 2.5f;
        // Everything but the local player (layer 6, F11 dump), so the search never hits the
        // phone itself wherever it stands.
        private const int GroundMask = ~(1 << 6);

        // The story map's spots the level data gets wrong, placed by hand in the game on
        // 25-oct-2026 with the phone resting where a run of that level starts: rigidbody
        // position and rotation, and the camera's orbit yaw. L10 and Narrator are in front
        // of the boss door, under its roof; landing past it starts the fight's fail-safe.
        private const string StoryMapId = "OfficialMap_TrashlandsStory";
        private static readonly Dictionary<string, (Vector3 pos, Quaternion rot, float cameraYaw)> FixedTargets =
            new Dictionary<string, (Vector3, Quaternion, float)>
        {
            ["area:2"] = (new Vector3(18.607f, -14.475f, 262.544f), new Quaternion(0f, 0.03078f, 0f, 0.99953f), 4.3f),
            ["area:3"] = (new Vector3(-4.285f, 20.226f, 89.658f), new Quaternion(0f, 0.99721f, 0f, 0.07462f), -5.9f),
            ["area:4"] = (new Vector3(-36.564f, 374.906f, -15.619f), new Quaternion(0f, 0.24265f, 0f, 0.97011f), 24.1f),
            ["area:7"] = (new Vector3(-2.203f, 2288.446f, -39.797f), new Quaternion(0f, 0.99859f, 0f, -0.05301f), 19.9f),
            ["area:8"] = (new Vector3(40.449f, 2663.528f, -170.198f), new Quaternion(-0.00732f, 0.91112f, -0.0125f, 0.41188f), 132f),
            ["area:9"] = (new Vector3(107.616f, 3132.23f, -181.135f), new Quaternion(0f, -0.51351f, 0f, 0.85809f), -67.4f),
            ["area:10"] = (new Vector3(-171.424f, 3511.64f, -79.893f), new Quaternion(0.71287f, 0f, 0.7013f, 0f), -76f),
            ["area:11"] = (new Vector3(-314.231f, 3583.05f, 195.891f), new Quaternion(0f, -0.71351f, 0f, 0.70064f), 140.2f),
            ["npc:Webby"] = (new Vector3(-88.997f, 986.947f, 176.111f), new Quaternion(-0.25796f, 0.28094f, 0.91968f, 0.0933f), -57.7f),
            ["npc:Narrator"] = (new Vector3(-170.635f, 3511.64f, -79.624f), new Quaternion(0.70557f, 0f, 0.70864f, 0f), -87.8f),
        };
        // Interactable NPCs left out of the teleport list (not characters to visit).
        private static readonly string[] HiddenNpcs = { "Microwaver", "Phone" };

        private List<Area> _areas = new List<Area>();
        private List<Npc> _npcs = new List<Npc>();
        private readonly Dictionary<string, string> _challengeNames = new Dictionary<string, string>();
        private IntPtr _builtFor;

        public IReadOnlyList<Area> Areas => _areas;
        public IReadOnlyList<Npc> Npcs => _npcs;

        /// <summary>Rebuilds the index when the level (its LevelsInfoManager) changed, or when forced.</summary>
        public void Refresh(PlayerRef player, bool force = false)
        {
            var levels = player?.GameRefs?.LevelsInfoManager;
            if (levels == null) return;
            if (!force && levels.Pointer == _builtFor) return;
            _builtFor = levels.Pointer;

            string mapId = player.GameRefs?.WorldSaveManager?.GetCurrentProgress()?.MapId ?? "";
            bool story = mapId == StoryMapId;
            var zones = FindZones();
            var infos = GameAccess.AsList(levels.LevelsInfo);
            var areas = new List<Area>();
            for (int i = 0; infos != null && i < infos.Count; i++)
                areas.Add(new Area { Id = infos[i].Id, Name = CleanAreaName(infos[i].name) });

            var doors = new Dictionary<int, (Vector3 pos, Quaternion rot)>();
            foreach (var area in areas)
            {
                var own = zones.Where(z => z.Area == area.Id).ToList();
                bool isBoss = area.Name.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0;
                var before = zones.Where(z => z.Area < area.Id).ToList();
                if (isBoss && before.Count > 0)
                {
                    // Highest end among the areas below: the end of the climb up to the door.
                    var last = before.OrderByDescending(z => z.End.y).First();
                    doors[area.Id] = (UnderRoof(last.End), Facing(last.EndBehind, last.End));
                    (area.Position, area.Rotation) = doors[area.Id];
                    area.HasStart = true;
                }
                else if (own.Count > 0)
                {
                    // Lowest start: areas are climbed upwards, so that is where it begins.
                    var first = own.OrderBy(z => z.Start.y).First();
                    area.Position = OnFloor(first.Start);
                    area.Rotation = Facing(first.Start, first.StartAhead);
                    area.HasStart = true;
                }
                if (story && FixedTargets.TryGetValue(area.Key, out var fixedTarget))
                {
                    (area.Position, area.Rotation, float yaw) = fixedTarget;
                    area.CameraYaw = yaw;
                    area.HasStart = true;
                }
            }
            _areas = areas;
            _npcs = FindNpcs(zones, doors, story);
            GhostPlugin.Logger.LogInfo($"[WORLD] {areas.Count(a => a.HasStart)}/{areas.Count} area starts, {_npcs.Count} NPCs " +
                                       $"({_npcs.Count(n => n.ChallengeId != null)} with challenges): " +
                                       string.Join(", ", _npcs.Select(n => n.Name)));
        }

        public Area FindArea(int id) => _areas.FirstOrDefault(a => a.Id == id);

        /// <summary>The NPC that gives a challenge, or a short id when it isn't known.</summary>
        public string ChallengeName(string challengeId)
        {
            if (challengeId != null && _challengeNames.TryGetValue(challengeId, out var name)) return name;
            return challengeId != null && challengeId.Length > 8 ? challengeId.Substring(0, 6) : challengeId;
        }

        private static List<Zone> FindZones()
        {
            var zones = new List<Zone>();
            foreach (var zone in Object.FindObjectsOfType<ChangeLevelProgressByHeight>(true))
            {
                int id = ZoneArea(zone);
                if (id < 0 || zone.startPosT == null) continue;
                Vector3 start = zone.startPosT.position;
                Vector3 end = zone.finishPosT != null ? zone.finishPosT.position : start;
                zones.Add(WithBounds(zone, new Zone { Area = id, Start = start, StartAhead = end, End = end, EndBehind = start }));
            }
            foreach (var zone in Object.FindObjectsOfType<ChangeLevelProgressBySpline>(true))
            {
                int id = ZoneArea(zone);
                if (id < 0) continue;
                var path = zone.cachedPositions;
                if (path != null && path.Length > 0)
                {
                    int n = path.Length;
                    zones.Add(WithBounds(zone, new Zone
                    {
                        Area = id, Start = path[0], StartAhead = path[Math.Min(5, n - 1)],
                        End = path[n - 1], EndBehind = path[Math.Max(0, n - 6)],
                    }));
                    continue;
                }
                // The zone caches its path lazily; evaluate the spline itself until it has.
                var spline = zone.splineContainer;
                if (spline == null) continue;
                zones.Add(WithBounds(zone, new Zone
                {
                    Area = id, Start = ToVector(spline.EvaluatePosition(0f)), StartAhead = ToVector(spline.EvaluatePosition(0.02f)),
                    End = ToVector(spline.EvaluatePosition(1f)), EndBehind = ToVector(spline.EvaluatePosition(0.98f)),
                }));
            }
            return zones;
        }

        // The volume of the zone's trigger, to tell which area an NPC stands in.
        private static Zone WithBounds(ChangeLevelProgressBase zone, Zone z)
        {
            var detector = zone.triggerDetectorComponent;
            if (detector == null) return z;
            foreach (var col in detector.GetComponentsInChildren<Collider>(true))
            {
                if (!z.HasBounds) z.Bounds = col.bounds;
                else z.Bounds.Encapsulate(col.bounds);
                z.HasBounds = true;
            }
            return z;
        }

        private static int ZoneArea(ChangeLevelProgressBase zone)
        {
            var info = zone.levelInfoHolder?.LevelInfo;
            return info != null ? info.Id : -1;
        }

        private List<Npc> FindNpcs(List<Zone> zones, Dictionary<int, (Vector3 pos, Quaternion rot)> doors, bool story)
        {
            _challengeNames.Clear();
            var npcs = new List<Npc>();
            foreach (var npc in Object.FindObjectsOfType<InteractableNpc>(true))
            {
                var t = npc.transform;
                string name = CleanNpcName(npc.gameObject.name);
                string challengeId = npc.ChallengeController != null ? npc.ChallengeController.PersistentId : null;
                if (challengeId != null) _challengeNames[challengeId] = name;
                if (HiddenNpcs.Any(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase))) continue;

                Vector3 front = t.forward;
                front.y = 0;
                if (front.sqrMagnitude < 0.01f) front = Vector3.forward;
                front.Normalize();
                var entry = new Npc
                {
                    Name = name,
                    ChallengeId = challengeId,
                    Position = OnFloor(t.position + front * NpcDistance),
                    // Facing the NPC, ready to talk.
                    Rotation = Quaternion.LookRotation(-front),
                };
                // Inside a boss area: in front of its door, like the boss itself.
                foreach (var z in zones)
                {
                    if (!z.HasBounds || !z.Bounds.Contains(t.position) || !doors.TryGetValue(z.Area, out var door)) continue;
                    (entry.Position, entry.Rotation) = door;
                    break;
                }
                if (story && FixedTargets.TryGetValue(entry.Key, out var fixedTarget))
                {
                    (entry.Position, entry.Rotation, float yaw) = fixedTarget;
                    entry.CameraYaw = yaw;
                }
                npcs.Add(entry);
            }
            // Bottom to top, the order they are met in.
            npcs.Sort((a, b) => a.Position.y.CompareTo(b.Position.y));
            return npcs;
        }

        // The floor under (or just above) a point, with the phone resting on it; the point
        // itself when there is none within reach.
        public static Vector3 OnFloor(Vector3 point)
        {
            return Physics.Raycast(point + Vector3.up * SearchAbove, Vector3.down, out RaycastHit hit,
                       SearchAbove + SearchDepth, GroundMask, QueryTriggerInteraction.Ignore)
                ? hit.point + Vector3.up * RestHeight
                : point;
        }

        // The first floor under a roof: the search goes on from just below the first surface
        // it hits. Started inside the roof, it can't hit the roof's underside (colliders are
        // only hit from outside), so the next hit is the floor beneath.
        private static Vector3 UnderRoof(Vector3 point)
        {
            if (!Physics.Raycast(point + Vector3.up * SearchAbove, Vector3.down, out RaycastHit roof,
                    SearchAbove + SearchDepth, GroundMask, QueryTriggerInteraction.Ignore))
                return point;
            if (Physics.Raycast(roof.point + Vector3.down * 0.05f, Vector3.down, out RaycastHit floor,
                    RoofSearchDepth, GroundMask, QueryTriggerInteraction.Ignore))
                return floor.point + Vector3.up * RestHeight;
            return roof.point + Vector3.up * RestHeight;
        }

        private static Quaternion Facing(Vector3 from, Vector3 to)
        {
            Vector3 dir = to - from;
            dir.y = 0;
            return dir.sqrMagnitude > 0.01f ? Quaternion.LookRotation(dir) : Quaternion.identity;
        }

        private static Vector3 ToVector(Unity.Mathematics.float3 v) => new Vector3(v.x, v.y, v.z);

        // "LevelInfo_L5_ToyChest" -> "L5 ToyChest".
        private static string CleanAreaName(string assetName)
        {
            string name = assetName ?? "";
            if (name.StartsWith("LevelInfo_")) name = name.Substring("LevelInfo_".Length);
            return name.Replace('_', ' ');
        }

        // "InteractableNPC_MsElephant(Clone)" -> "MsElephant".
        private static string CleanNpcName(string goName)
        {
            string name = (goName ?? "").Replace("(Clone)", "").Trim();
            const string prefix = "InteractableNPC";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) name = name.Substring(prefix.Length);
            return name.Trim('_', ' ', '-').Replace('_', ' ');
        }
    }
}
