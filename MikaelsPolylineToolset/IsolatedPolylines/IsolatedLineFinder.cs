using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;

namespace MikaelsPolylineToolset.IsolatedPolylines
{
    /// <summary>
    /// Finds line features whose start AND end points are not shared with the start or end
    /// point of any other line in the same layer, and (optionally) are not on the edge of any
    /// feature in an anchor polygon layer. Must be called from within QueuedTask.Run.
    /// Ported from the IsolatedLinesFinder.pyt python toolbox.
    /// </summary>
    internal static class IsolatedLineFinder
    {
        public static List<long> FindIsolatedLineOids(
            FeatureLayer lineLayer,
            double tolerance,
            FeatureLayer polygonLayer,
            Action<string> log)
        {
            double? snap = tolerance > 0 ? tolerance : (double?)null;
            SpatialReference sr = null;

            (long, long) NodeKey(double x, double y)
            {
                if (snap.HasValue)
                    return ((long)Math.Floor(x / snap.Value), (long)Math.Floor(y / snap.Value));
                return ((long)Math.Round(x * 1e8), (long)Math.Round(y * 1e8));
            }

            var nodes = new Dictionary<(long, long), HashSet<long>>();
            var endpoints = new Dictionary<long, ((long, long) start, (long, long) end)>();
            var nodeXY = new Dictionary<(long, long), MapPoint>();

            using (var cursor = lineLayer.Search(null))
            {
                while (cursor.MoveNext())
                {
                    using var feature = (Feature)cursor.Current;
                    long oid = feature.GetObjectID();
                    if (feature.GetShape() is not Polyline geom || geom.IsEmpty)
                    {
                        log($"OID {oid} has null/empty geometry; skipped.");
                        continue;
                    }
                    sr ??= geom.SpatialReference;

                    var sp = geom.Points[0];
                    var ep = geom.Points[geom.PointCount - 1];
                    var sk = NodeKey(sp.X, sp.Y);
                    var ek = NodeKey(ep.X, ep.Y);
                    endpoints[oid] = (sk, ek);

                    if (!nodes.TryGetValue(sk, out var setS))
                        nodes[sk] = setS = new HashSet<long>();
                    setS.Add(oid);

                    if (!nodes.TryGetValue(ek, out var setE))
                        nodes[ek] = setE = new HashSet<long>();
                    setE.Add(oid);

                    if (!nodeXY.ContainsKey(sk)) nodeXY[sk] = sp;
                    if (!nodeXY.ContainsKey(ek)) nodeXY[ek] = ep;
                }
            }

            bool HasOthersAt((long, long) k, long selfOid)
            {
                if (snap.HasValue)
                {
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            if (nodes.TryGetValue((k.Item1 + dx, k.Item2 + dy), out var s))
                            {
                                foreach (var o in s)
                                    if (o != selfOid) return true;
                            }
                        }
                    return false;
                }
                if (nodes.TryGetValue(k, out var same))
                {
                    foreach (var o in same)
                        if (o != selfOid) return true;
                }
                return false;
            }

            // ---- Optional polygon setup: grid-index the polygon boundaries. ----
            bool usePolys = polygonLayer != null;
            var polyBoundaries = new List<Polyline>();
            var polyGrid = new Dictionary<(long, long), List<int>>();
            double pcell = 0;
            double edgeTol = snap ?? (sr?.XYTolerance ?? 1e-6);

            if (usePolys)
            {
                var polyGeoms = new List<Polygon>();
                using (var pcur = polygonLayer.Search(null))
                {
                    while (pcur.MoveNext())
                    {
                        using var f = (Feature)pcur.Current;
                        if (f.GetShape() is Polygon pg && !pg.IsEmpty)
                        {
                            // GeometryEngine throws "Incompatible spatial references" if the
                            // polygon layer isn't in the same SR as the lines -- project it in
                            // (a no-op if it already matches) rather than requiring the user's
                            // layers to already share one.
                            if (sr != null && pg.SpatialReference != null)
                                pg = (Polygon)GeometryEngine.Instance.Project(pg, sr);
                            polyGeoms.Add(pg);
                        }
                    }
                }

                if (polyGeoms.Count == 0)
                {
                    log("Polygon layer has no features; ignoring it.");
                    usePolys = false;
                }
                else
                {
                    foreach (var pg in polyGeoms)
                        polyBoundaries.Add((Polyline)GeometryEngine.Instance.Boundary(pg));

                    double sw = 0, sh = 0;
                    foreach (var pg in polyGeoms)
                    {
                        var e = pg.Extent;
                        sw += e.XMax - e.XMin;
                        sh += e.YMax - e.YMin;
                    }
                    pcell = Math.Max(sw / polyGeoms.Count, sh / polyGeoms.Count);
                    if (pcell <= 0) pcell = Math.Max(edgeTol * 10, 1.0);

                    for (int pi = 0; pi < polyGeoms.Count; pi++)
                    {
                        var e = polyGeoms[pi].Extent;
                        long c0 = (long)Math.Floor(e.XMin / pcell);
                        long c1 = (long)Math.Floor(e.XMax / pcell);
                        long r0 = (long)Math.Floor(e.YMin / pcell);
                        long r1 = (long)Math.Floor(e.YMax / pcell);
                        for (long c = c0; c <= c1; c++)
                            for (long r = r0; r <= r1; r++)
                            {
                                var key = (c, r);
                                if (!polyGrid.TryGetValue(key, out var list))
                                    polyGrid[key] = list = new List<int>();
                                list.Add(pi);
                            }
                    }
                }
            }

            var edgeCache = new Dictionary<(long, long), bool>();

            bool NodeOnPolygonEdge((long, long) k)
            {
                if (edgeCache.TryGetValue(k, out var cached)) return cached;
                var pt = nodeXY[k];
                long cx = (long)Math.Floor(pt.X / pcell);
                long cy = (long)Math.Floor(pt.Y / pcell);
                var candidates = new HashSet<int>();
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (polyGrid.TryGetValue((cx + dx, cy + dy), out var list))
                            candidates.UnionWith(list);

                bool onEdge = false;
                foreach (var pi in candidates)
                {
                    if (GeometryEngine.Instance.Distance(polyBoundaries[pi], pt) <= edgeTol)
                    {
                        onEdge = true;
                        break;
                    }
                }
                edgeCache[k] = onEdge;
                return onEdge;
            }

            // ---- A line is isolated only if NEITHER endpoint is anchored. ----
            var isolated = new List<long>();
            foreach (var kvp in endpoints)
            {
                long oid = kvp.Key;
                var (sk, ek) = kvp.Value;
                if (HasOthersAt(sk, oid) || HasOthersAt(ek, oid))
                    continue;
                if (usePolys && (NodeOnPolygonEdge(sk) || NodeOnPolygonEdge(ek)))
                    continue;
                isolated.Add(oid);
            }

            log($"Found {isolated.Count} isolated line(s) out of {endpoints.Count} feature(s).");
            return isolated;
        }
    }
}
