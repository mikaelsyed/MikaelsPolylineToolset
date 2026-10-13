using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MikaelsPolylineToolset.EdgeTouchingPolylines
{
    /// <summary>
    /// Finds flowlines whose start or end point lands on the INTERIOR (edge) of another
    /// flowline rather than on that flowline's start or end vertex (a T-junction where the
    /// target line hasn't been split). Normal endpoint-to-endpoint connections are ignored.
    /// Ported from FlowlineStartEndOnEdge.pyt (SelectEdgeTouchLines). Must be called from
    /// within QueuedTask.Run.
    /// </summary>
    internal static class EdgeTouchingFinder
    {
        public static List<long> FindEdgeTouchingOids(
            FeatureLayer lineLayer,
            double tolerance,
            bool touchingOnly,
            Action<string> log,
            QcProgress progress = null)
        {
            var geoms = new Dictionary<long, Polyline>();
            var endpoints = new Dictionary<long, (double X, double Y)[]>();
            var env = new Dictionary<long, (double XMin, double YMin, double XMax, double YMax)>();

            progress?.Begin("Reading polylines", QcRunUi.CountNow(lineLayer), 0, 0.2);
            using (var cursor = lineLayer.Search(null))
            {
                while (cursor.MoveNext())
                {
                    using var feature = (Feature)cursor.Current;
                    long oid = feature.GetObjectID();
                    progress?.Tick(oid);
                    if (feature.GetShape() is not Polyline shape || shape.IsEmpty || shape.Length == 0)
                        continue;
                    var fp = shape.Points[0];
                    var lp = shape.Points[shape.PointCount - 1];
                    geoms[oid] = shape;
                    endpoints[oid] = new[] { (fp.X, fp.Y), (lp.X, lp.Y) };
                    var e = shape.Extent;
                    env[oid] = (e.XMin, e.YMin, e.XMax, e.YMax);
                }
            }

            int n = geoms.Count;
            log($"Loaded {n} polylines.");
            if (n < 2)
            {
                log("Fewer than two polylines; nothing to compare.");
                return new List<long>();
            }

            double avgDim = 0;
            foreach (var (xmin, ymin, xmax, ymax) in env.Values)
                avgDim += Math.Max(xmax - xmin, ymax - ymin);
            avgDim /= env.Count;
            if (avgDim <= 0) avgDim = 100.0;
            double cell = Math.Max(avgDim, Math.Max(tolerance * 100.0, 1e-9));

            IEnumerable<(long, long)> CellsForExtent(double xmin, double ymin, double xmax, double ymax)
            {
                long c0 = (long)Math.Floor(xmin / cell);
                long r0 = (long)Math.Floor(ymin / cell);
                long c1 = (long)Math.Floor(xmax / cell);
                long r1 = (long)Math.Floor(ymax / cell);
                for (long c = c0; c <= c1; c++)
                    for (long r = r0; r <= r1; r++)
                        yield return (c, r);
            }

            var grid = new Dictionary<(long, long), List<long>>();
            foreach (var kvp in env)
            {
                var (xmin, ymin, xmax, ymax) = kvp.Value;
                foreach (var key in CellsForExtent(xmin - tolerance, ymin - tolerance, xmax + tolerance, ymax + tolerance))
                {
                    if (!grid.TryGetValue(key, out var list))
                        grid[key] = list = new List<long>();
                    list.Add(kvp.Key);
                }
            }

            List<(double X, double Y)> PartEndpoints(Polyline geom)
            {
                var pts = new List<(double, double)>();
                for (int p = 0; p < geom.PartCount; p++)
                {
                    var part = geom.Parts[p];
                    if (part.Count == 0) continue;
                    var first = part[0].StartPoint;
                    var last = part[part.Count - 1].EndPoint;
                    pts.Add((first.X, first.Y));
                    pts.Add((last.X, last.Y));
                }
                return pts;
            }

            var touching = new HashSet<long>();
            var touched = new HashSet<long>();
            var endVertexCache = new Dictionary<long, List<(double X, double Y)>>();

            progress?.Begin("Testing line end points against other lines", endpoints.Count, 0.2, 0.8);
            foreach (var kvp in endpoints)
            {
                long oidA = kvp.Key;
                progress?.Tick(oidA);
                foreach (var (px, py) in kvp.Value)
                {
                    var cand = new HashSet<long>();
                    foreach (var key in CellsForExtent(px - tolerance, py - tolerance, px + tolerance, py + tolerance))
                        if (grid.TryGetValue(key, out var list))
                            cand.UnionWith(list);
                    cand.Remove(oidA);

                    foreach (var oidB in cand)
                    {
                        var (xmin, ymin, xmax, ymax) = env[oidB];
                        if (px < xmin - tolerance || px > xmax + tolerance ||
                            py < ymin - tolerance || py > ymax + tolerance)
                            continue;

                        if (!endVertexCache.TryGetValue(oidB, out var endVerts))
                            endVertexCache[oidB] = endVerts = PartEndpoints(geoms[oidB]);

                        bool atEndVertex = false;
                        foreach (var (bx, by) in endVerts)
                        {
                            if (Math.Sqrt((px - bx) * (px - bx) + (py - by) * (py - by)) <= tolerance)
                            {
                                atEndVertex = true;
                                break;
                            }
                        }
                        if (atEndVertex) continue; // normal end-to-end connection: ignore

                        var pt = MapPointBuilderEx.CreateMapPoint(px, py, geoms[oidB].SpatialReference);
                        double dist = GeometryEngine.Instance.Distance(geoms[oidB], pt);
                        if (dist <= tolerance)
                        {
                            touching.Add(oidA);
                            touched.Add(oidB);
                        }
                    }
                }
            }

            log($"Edge-touch relationships found. Lines with an endpoint on another line's edge: {touching.Count}. Lines whose edge is touched: {touched.Count}.");

            var selected = touchingOnly ? touching : new HashSet<long>(touching.Union(touched));
            var result = new List<long>(selected);
            result.Sort();
            return result;
        }
    }
}
