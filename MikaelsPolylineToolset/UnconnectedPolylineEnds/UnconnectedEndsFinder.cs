using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;

namespace MikaelsPolylineToolset.UnconnectedPolylineEnds
{
    /// <summary>
    /// Flags flowlines whose END point does not land on a hydro polygon (interior or
    /// boundary) or on any other flowline (edge or vertex). Start points are never checked --
    /// headwater lines are expected to dangle at their upstream end. Ported from
    /// FlowlineEndConnectivity.pyt (FlagUnconnectedPolylineEnds). Must be called from within
    /// QueuedTask.Run.
    /// </summary>
    internal static class UnconnectedEndsFinder
    {
        public static List<long> FindUnconnectedEndOids(
            FeatureLayer lineLayer,
            FeatureLayer polygonLayer,
            double tolerance,
            Action<string> log)
        {
            SpatialReference sr = null;
            var lines = new Dictionary<long, Polyline>();
            var endPts = new Dictionary<long, (double X, double Y)>();
            var lineExtents = new Dictionary<long, Envelope>();

            using (var cursor = lineLayer.Search(null))
            {
                while (cursor.MoveNext())
                {
                    using var feature = (Feature)cursor.Current;
                    long oid = feature.GetObjectID();
                    if (feature.GetShape() is not Polyline geom || geom.IsEmpty || geom.Length == 0)
                        continue;
                    sr ??= geom.SpatialReference;
                    var ep = geom.Points[geom.PointCount - 1];
                    lines[oid] = geom;
                    endPts[oid] = (ep.X, ep.Y);
                    lineExtents[oid] = geom.Extent;
                }
            }

            int nLines = lines.Count;
            log($"Loaded {nLines} polyline(s).");
            if (nLines == 0) return new List<long>();

            double tol = tolerance > 0 ? tolerance : (sr?.XYTolerance ?? 1e-6);
            log($"Match tolerance: {tol} map units");

            var polys = new Dictionary<long, Polygon>();
            var polyExtents = new Dictionary<long, Envelope>();
            if (polygonLayer != null)
            {
                using var pcur = polygonLayer.Search(null);
                while (pcur.MoveNext())
                {
                    using var f = (Feature)pcur.Current;
                    if (f.GetShape() is not Polygon pg || pg.IsEmpty) continue;
                    if (pg.SpatialReference != null && sr != null)
                        pg = (Polygon)GeometryEngine.Instance.Project(pg, sr);
                    polys[f.GetObjectID()] = pg;
                    polyExtents[f.GetObjectID()] = pg.Extent;
                }
            }
            log($"Loaded {polys.Count} hydro polygon(s).");

            (Dictionary<(long, long), List<long>> Grid, double Cell) BuildGrid(Dictionary<long, Envelope> extents)
            {
                var grid = new Dictionary<(long, long), List<long>>();
                double avgDim = 0;
                foreach (var e in extents.Values)
                    avgDim += Math.Max(e.XMax - e.XMin, e.YMax - e.YMin);
                avgDim = extents.Count > 0 ? avgDim / extents.Count : 100.0;
                double cell = Math.Max(avgDim, Math.Max(tol * 100.0, 1e-9));

                foreach (var kvp in extents)
                {
                    var e = kvp.Value;
                    long c0 = (long)Math.Floor((e.XMin - tol) / cell);
                    long c1 = (long)Math.Floor((e.XMax + tol) / cell);
                    long r0 = (long)Math.Floor((e.YMin - tol) / cell);
                    long r1 = (long)Math.Floor((e.YMax + tol) / cell);
                    for (long c = c0; c <= c1; c++)
                        for (long r = r0; r <= r1; r++)
                        {
                            var key = (c, r);
                            if (!grid.TryGetValue(key, out var list))
                                grid[key] = list = new List<long>();
                            list.Add(kvp.Key);
                        }
                }
                return (grid, cell);
            }

            HashSet<long> Candidates(Dictionary<(long, long), List<long>> grid, double cell, double x, double y)
            {
                var cand = new HashSet<long>();
                long c0 = (long)Math.Floor((x - tol) / cell);
                long c1 = (long)Math.Floor((x + tol) / cell);
                long r0 = (long)Math.Floor((y - tol) / cell);
                long r1 = (long)Math.Floor((y + tol) / cell);
                for (long c = c0; c <= c1; c++)
                    for (long r = r0; r <= r1; r++)
                        if (grid.TryGetValue((c, r), out var list))
                            cand.UnionWith(list);
                return cand;
            }

            var (lineGrid, lineCell) = BuildGrid(lineExtents);
            var (polyGrid, polyCell) = polys.Count > 0 ? BuildGrid(polyExtents) : (new Dictionary<(long, long), List<long>>(), 1.0);

            var flagged = new List<long>();
            foreach (var kvp in endPts)
            {
                long oid = kvp.Key;
                var (x, y) = kvp.Value;
                var pt = MapPointBuilderEx.CreateMapPoint(x, y, sr);
                bool connected = false;

                if (polys.Count > 0)
                {
                    foreach (var pid in Candidates(polyGrid, polyCell, x, y))
                    {
                        if (GeometryEngine.Instance.Distance(polys[pid], pt) <= tol)
                        {
                            connected = true;
                            break;
                        }
                    }
                }

                if (!connected)
                {
                    foreach (var otherOid in Candidates(lineGrid, lineCell, x, y))
                    {
                        if (otherOid == oid) continue;
                        if (GeometryEngine.Instance.Distance(lines[otherOid], pt) <= tol)
                        {
                            connected = true;
                            break;
                        }
                    }
                }

                if (!connected)
                    flagged.Add(oid);
            }

            flagged.Sort();
            log($"Flagged {flagged.Count} of {nLines} polyline(s) with an unconnected end point.");
            return flagged;
        }
    }
}
