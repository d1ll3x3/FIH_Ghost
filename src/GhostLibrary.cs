using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FIHGhost
{
    /// <summary>
    /// Every ghost the player can watch: their PBs (Ghosts/PB), imported files
    /// (Ghosts/Imported) and hand-made recordings (Ghosts). The unfinished-run streams in
    /// Ghosts/Run are left out: they are working files, not ghosts.
    /// </summary>
    internal sealed class GhostLibrary
    {
        public enum Source { Pb, Imported, Manual }

        public sealed class Entry
        {
            public string Path;
            public string Name;
            public Source Source;
            public string Title;
            // Header fields only (GhostRecording.LoadSummary): no samples or markers.
            public GhostRecording Header;
            public double Duration;

            // The time the ghost stands for: the split time for PBs, else its length.
            public double Time => Header.Category != GhostCategory.Free && Header.FinalTime > 0 ? Header.FinalTime : Duration;
        }

        public static string ImportedFolder => Path.Combine(GhostPlugin.RecordingsDir.Value, "Imported");

        private readonly WorldIndex _world;
        private readonly PracticeRoutes _routes;
        private List<Entry> _entries = new List<Entry>();

        // The names ghosts are shown with come from the loaded level and its routes.
        public GhostLibrary(WorldIndex world, PracticeRoutes routes)
        {
            _world = world;
            _routes = routes;
        }

        public IReadOnlyList<Entry> Entries => _entries;
        public Entry Latest => _entries.Where(e => e.Source == Source.Manual).OrderByDescending(e => e.Header.RecordedAtUtc).FirstOrDefault();

        /// <summary>
        /// Rescans the folders. PBs first (full run, areas in order, challenges), then imported
        /// and hand-made ghosts, newest first.
        /// </summary>
        public void Refresh()
        {
            string root = GhostPlugin.RecordingsDir.Value;
            var entries = new List<Entry>();
            Scan(PersonalBests.Folder, SearchOption.AllDirectories, Source.Pb, entries);
            Scan(ImportedFolder, SearchOption.TopDirectoryOnly, Source.Imported, entries);
            Scan(root, SearchOption.TopDirectoryOnly, Source.Manual, entries);

            _entries = entries
                .OrderBy(e => e.Source)
                .ThenBy(e => e.Source == Source.Pb ? (int)e.Header.Category : 0)
                .ThenBy(e => e.Source == Source.Pb && e.Header.Category == GhostCategory.Area && int.TryParse(e.Header.CategoryId, out int id) ? id : 0)
                .ThenByDescending(e => e.Header.RecordedAtUtc)
                .ToList();
        }

        public Entry Find(string path) => _entries.FirstOrDefault(e => e.Path == path);

        private void Scan(string dir, SearchOption depth, Source source, List<Entry> into)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*" + GhostRecording.Extension, depth))
            {
                try
                {
                    var header = GhostRecording.LoadSummary(file, out double duration);
                    var entry = new Entry
                    {
                        Path = file,
                        Name = System.IO.Path.GetFileName(file),
                        Source = source,
                        Header = header,
                        Duration = duration,
                    };
                    entry.Title = TitleOf(entry);
                    into.Add(entry);
                }
                catch (Exception ex)
                {
                    GhostPlugin.Logger.LogWarning($"[LIB] Skipping {file}: {ex.Message}");
                }
            }
        }

        private string TitleOf(Entry e)
        {
            var h = e.Header;
            string what = h.Category switch
            {
                GhostCategory.FullRun => "Full run",
                GhostCategory.Area => "Area " + (int.TryParse(h.CategoryId, out int id) ? _world.FindArea(id)?.Name ?? h.CategoryId : h.CategoryId),
                GhostCategory.Challenge => "Challenge " + _world.ChallengeName(h.CategoryId),
                GhostCategory.Route => _routes.NameOf(h.CategoryId),
                _ => System.IO.Path.GetFileNameWithoutExtension(e.Name),
            };
            return e.Source switch
            {
                Source.Pb => "PB · " + what,
                Source.Imported => "Imported · " + what,
                _ => what,
            };
        }
    }
}
