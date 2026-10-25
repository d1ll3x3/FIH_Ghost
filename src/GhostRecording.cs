using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace FIHGhost
{
    internal enum MarkerKind : byte
    {
        // Id: LevelInfoSO.Id. Value: playtime on entering.
        AreaEnter = 1,
        // Id: NpcChallengeController.PersistentId. Value: ElapsedTime when first seen active
        // (above 0 when a save restored an active challenge).
        ChallengeStart = 2,
        // Value: the game's completion time for the challenge (LastCompletedTime).
        ChallengeComplete = 3,
        // Value: ElapsedTime last seen before the challenge stopped.
        ChallengeCancel = 4,
        // Value: playtime when the game reported the run finished.
        GameEnd = 5,
        // The area the phone already was in when this recording (or session) began: not a
        // real entry, so it never starts an area split. Id and Value as AreaEnter.
        AreaAtStart = 6,
        // A teleport from the mod's menu. Id: RunSession.TeleportAreaPrefix + area id for an
        // area start, "npc:<name>" for an NPC. Value: playtime.
        Teleport = 7,
        // After a teleport to an area start, the first input: the area split starts here,
        // like the game's timer. Id: area id. Value: playtime.
        PracticeStart = 8,
    }

    // What a recording is the ghost of; PersonalBests keeps one file per category and id.
    internal enum GhostCategory : byte
    {
        Free = 0,        // recorded by hand
        FullRun = 1,     // CategoryId empty; FinalTime = playtime at the end of the game
        Area = 2,        // CategoryId = LevelInfoSO.Id; FinalTime = time from reaching it to the next
        Challenge = 3,   // CategoryId = PersistentId; FinalTime = the game's completion time
        Route = 4,       // CategoryId = PracticeRoute.Id; FinalTime = start (first input) to end zone
    }

    internal struct GhostSample
    {
        // Run clock: the playtime when the recording (or session) began plus the physics
        // time since, so it advances one fixedDeltaTime per sample and continues across
        // sessions of the same run.
        public double Time;
        public Vector3 Position;
        public Quaternion Rotation;
        // The phone jumped here (teleport, checkpoint, respawn): playback must not
        // interpolate from the previous sample to this one.
        public bool Cut;
    }

    internal struct GhostMarker
    {
        public double Time;
        public MarkerKind Kind;
        public string Id;
        public double Value;
    }

    /// <summary>
    /// The phone pose every physics tick plus markers for the events the per-area and
    /// per-challenge ghosts are cut from (NOTAS_FASE1.md, "Diseño").
    ///
    /// File (.fihghost, little endian): "FIHG" version:i32, then mapId:str difficulty:str
    /// startPlaytime:f64 recordedAtUtcTicks:i64 checkpointsUsed:i32, since v2
    /// category:u8 categoryId:str finalTime:f64 worldGuid:str,
    /// samples:i32 × (time:f64 pos:f32×3 rot:f32×4 cut:u8),
    /// markers:i32 × (time:f64 kind:u8 id:str value:f64). str = BinaryWriter string.
    /// </summary>
    internal sealed class GhostRecording
    {
        public const string Extension = ".fihghost";
        private const int FormatVersion = 2;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FIHG");
        // Bytes per sample as Save writes it: time f64, pos 3×f32, rot 4×f32, cut u8.
        private const int SampleSize = 8 + 3 * 4 + 4 * 4 + 1;

        public string MapId = "";
        public string Difficulty = "";
        public double StartPlaytime;
        public DateTime RecordedAtUtc;
        public int CheckpointsUsed;
        public GhostCategory Category;
        public string CategoryId = "";
        public double FinalTime;
        public string WorldGuid = "";
        public readonly List<GhostSample> Samples = new List<GhostSample>();
        public readonly List<GhostMarker> Markers = new List<GhostMarker>();

        public double StartTime => Samples.Count == 0 ? 0 : Samples[0].Time;
        public double EndTime => Samples.Count == 0 ? 0 : Samples[Samples.Count - 1].Time;
        public double Duration => EndTime - StartTime;

        /// <summary>
        /// Samples [from, to] (inclusive) with the markers inside their time span, as a new
        /// recording of the given category. Times are kept, not rebased.
        /// </summary>
        public GhostRecording Slice(int from, int to, GhostCategory category, string categoryId, double finalTime)
        {
            var slice = new GhostRecording
            {
                MapId = MapId,
                Difficulty = Difficulty,
                StartPlaytime = Samples[from].Time,
                RecordedAtUtc = DateTime.UtcNow,
                Category = category,
                CategoryId = categoryId ?? "",
                FinalTime = finalTime,
                WorldGuid = WorldGuid,
            };
            slice.Samples.AddRange(Samples.GetRange(from, to - from + 1));
            double start = Samples[from].Time, end = Samples[to].Time;
            foreach (var m in Markers)
                if (m.Time >= start && m.Time <= end) slice.Markers.Add(m);
            return slice;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // Written next to the target and swapped in, so a crash mid-write never leaves a
            // truncated PB or run file behind.
            string temp = path + ".tmp";
            using (var writer = new BinaryWriter(File.Create(temp)))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(MapId ?? "");
                writer.Write(Difficulty ?? "");
                writer.Write(StartPlaytime);
                writer.Write(RecordedAtUtc.Ticks);
                writer.Write(CheckpointsUsed);
                writer.Write((byte)Category);
                writer.Write(CategoryId ?? "");
                writer.Write(FinalTime);
                writer.Write(WorldGuid ?? "");

                writer.Write(Samples.Count);
                foreach (var s in Samples)
                {
                    writer.Write(s.Time);
                    writer.Write(s.Position.x);
                    writer.Write(s.Position.y);
                    writer.Write(s.Position.z);
                    writer.Write(s.Rotation.x);
                    writer.Write(s.Rotation.y);
                    writer.Write(s.Rotation.z);
                    writer.Write(s.Rotation.w);
                    writer.Write(s.Cut);
                }

                writer.Write(Markers.Count);
                foreach (var m in Markers)
                {
                    writer.Write(m.Time);
                    writer.Write((byte)m.Kind);
                    writer.Write(m.Id ?? "");
                    writer.Write(m.Value);
                }
            }
            File.Move(temp, path, true);
        }

        /// <summary>Reads a .fihghost file; throws InvalidDataException if it is not one.</summary>
        public static GhostRecording Load(string path)
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            var rec = ReadHeader(reader, path);

            int samples = reader.ReadInt32();
            rec.Samples.Capacity = samples;
            for (int i = 0; i < samples; i++)
            {
                rec.Samples.Add(new GhostSample
                {
                    Time = reader.ReadDouble(),
                    Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Cut = reader.ReadBoolean(),
                });
            }

            int markers = reader.ReadInt32();
            for (int i = 0; i < markers; i++)
            {
                rec.Markers.Add(new GhostMarker
                {
                    Time = reader.ReadDouble(),
                    Kind = (MarkerKind)reader.ReadByte(),
                    Id = reader.ReadString(),
                    Value = reader.ReadDouble(),
                });
            }
            return rec;
        }

        /// <summary>
        /// The header and duration only, for listing files without reading every sample:
        /// samples have a fixed size, so the first and last times are a seek away. The
        /// returned recording has no samples or markers.
        /// </summary>
        public static GhostRecording LoadSummary(string path, out double duration)
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            var rec = ReadHeader(reader, path);
            int samples = reader.ReadInt32();
            duration = 0;
            if (samples > 1)
            {
                long first = reader.BaseStream.Position;
                double start = reader.ReadDouble();
                reader.BaseStream.Seek(first + (long)(samples - 1) * SampleSize, SeekOrigin.Begin);
                duration = reader.ReadDouble() - start;
            }
            return rec;
        }

        private static GhostRecording ReadHeader(BinaryReader reader, string path)
        {
            byte[] magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || Encoding.ASCII.GetString(magic) != "FIHG")
                throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a ghost file");
            int version = reader.ReadInt32();
            if (version < 1 || version > FormatVersion)
                throw new InvalidDataException($"'{Path.GetFileName(path)}' has format v{version}, this mod reads up to v{FormatVersion}");

            var rec = new GhostRecording
            {
                MapId = reader.ReadString(),
                Difficulty = reader.ReadString(),
                StartPlaytime = reader.ReadDouble(),
                RecordedAtUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
                CheckpointsUsed = reader.ReadInt32(),
            };
            // v1 files are the hand-made recordings from before categories existed.
            if (version >= 2)
            {
                rec.Category = (GhostCategory)reader.ReadByte();
                rec.CategoryId = reader.ReadString();
                rec.FinalTime = reader.ReadDouble();
                rec.WorldGuid = reader.ReadString();
            }
            return rec;
        }
    }
}
