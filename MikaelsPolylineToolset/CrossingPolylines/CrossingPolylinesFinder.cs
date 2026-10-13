using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;

namespace MikaelsPolylineToolset.CrossingPolylines
{
    /// <summary>
    /// Finds line features that CROSS another line in the same layer (the classic "X"
    /// intersection where two lines cross at a point interior to both), optionally including
    /// lines that partially overlap. Ported from CrossingPolylinesFinder.pyt. Must be called from
    /// within QueuedTask.Run.
    /// </summary>
    internal static class CrossingPolylinesFinder
    {
        public static List<long> FindCrossingLineOids(
            FeatureLayer lineLayer,
            bool includeOverlaps,
            Action<string> log,
            QcProgress progress = null)
        {
            var feats = new List<(long Oid, Polyline Geom)>();
            progress?.Begin("Reading polylines", QcRunUi.CountNow(lineLayer), 0, 0.2);
            using (var cursor = lineLayer.Search(null))
            {
                while (cursor.MoveNext())
                {
                    using var feature = (Feature)cursor.Current;
                    long oid = feature.GetObjectID();
                    progress?.Tick(oid);
                    if (feature.GetShape() is not Polyline geom || geom.IsEmpty)
                    {
                        log($"OID {oid} has null/empty geometry; skipped.");
                        continue;
                    }
                    feats.Add((oid, geom));
                }
            }

            int n = feats.Count;
            if (n < 2)
            {
                log("Fewer than two valid lines; nothing to compare.");
                return new List<long>();
            }

            double sumW = 0, sumH = 0;
            foreach (var (_, g) in feats)
            {
                var e = g.Extent;
                sumW += e.XMax - e.XMin;
                sumH += e.YMax - e.YMin;
            }
            double cell = Math.Max(sumW / n, sumH / n);
            if (cell <= 0) cell = 1.0;

            IEnumerable<(long, long)> CellsForExtent(Envelope e)
            {
                long c0 = (long)Math.Floor(e.XMin / cell);
                long c1 = (long)Math.Floor(e.XMax / cell);
                long r0 = (long)Math.Floor(e.YMin / cell);
                long r1 = (long)Math.Floor(e.YMax / cell);
                for (long c = c0; c <= c1; c++)
                    for (long r = r0; r <= r1; r++)
                        yield return (c, r);
            }

            var grid = new Dictionary<(long, long), List<int>>();
            for (int i = 0; i < n; i++)
            {
                foreach (var key in CellsForExtent(feats[i].Geom.Extent))
                {
                    if (!grid.TryGetValue(key, out var list))
                        grid[key] = list = new List<int>();
                    list.Add(i);
                }
            }

            var crossing = new HashSet<long>();
            progress?.Begin("Comparing each line with its neighbours", n, 0.2, 0.8);
            for (int i = 0; i < n; i++)
            {
                var (oidI, gI) = feats[i];
                progress?.Tick(oidI);
                var eI = gI.Extent;
                var candidates = new HashSet<int>();
                foreach (var key in CellsForExtent(eI))
                    if (grid.TryGetValue(key, out var list))
                        candidates.UnionWith(list);

                foreach (var j in candidates)
                {
                    if (j <= i) continue;
                    var (oidJ, gJ) = feats[j];
                    var eJ = gJ.Extent;
                    if (eI.XMin > eJ.XMax || eJ.XMin > eI.XMax ||
                        eI.YMin > eJ.YMax || eJ.YMin > eI.YMax)
                        continue;

                    if (GeometryEngine.Instance.Crosses(gI, gJ) ||
                        (includeOverlaps && GeometryEngine.Instance.Overlaps(gI, gJ)))
                    {
                        crossing.Add(oidI);
                        crossing.Add(oidJ);
                    }
                }
            }

            var result = new List<long>(crossing);
            result.Sort();
            log($"Found {result.Count} crossing line(s) out of {n} feature(s).");
            return result;
        }
    }
}
