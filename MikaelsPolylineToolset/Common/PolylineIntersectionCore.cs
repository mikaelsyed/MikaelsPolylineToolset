using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;

namespace MikaelsPolylineToolset.Common
{
    /// <summary>
    /// Computes every point where flowlines touch a polygon layer's boundary (crossings AND
    /// touch/tangent cases), clustered by location, with the set of distinct lines that
    /// contribute to each point. Shared by the Flowline Intersect Points and Validate
    /// Inflow/Outflow tools. Must be called from within QueuedTask.Run.
    /// </summary>
    internal static class PolylineIntersectionCore
    {
        public class IntersectionCluster
        {
            public MapPoint Point;
            public HashSet<long> LineOids = new HashSet<long>();
        }

        public static List<IntersectionCluster> ComputeIntersectionClusters(
            FeatureLayer lineLayer,
            FeatureLayer polygonLayer,
            double tolerance,
            Action<string> log,
            out SpatialReference spatialReference)
        {
            double? snap = tolerance > 0 ? tolerance : (double?)null;
            SpatialReference sr = null;

            (long, long) NodeKey(double x, double y)
            {
                if (snap.HasValue)
                    return ((long)Math.Floor(x / snap.Value), (long)Math.Floor(y / snap.Value));
                return ((long)Math.Round(x * 1e8), (long)Math.Round(y * 1e8));
            }

            var lines = new List<(long Oid, Polyline Geom)>();
            using (var cursor = lineLayer.Search(null))
            {
                while (cursor.MoveNext())
                {
                    using var feature = (Feature)cursor.Current;
                    long oid = feature.GetObjectID();
                    if (feature.GetShape() is not Polyline geom || geom.IsEmpty)
                        continue;
                    sr ??= geom.SpatialReference;
                    lines.Add((oid, geom));
                }
            }
            spatialReference = sr;

            if (sr == null || lines.Count == 0)
            {
                log("No valid polylines found.");
                return new List<IntersectionCluster>();
            }

            // ---- Load polygon boundaries, projected into the line layer's SR, grid-indexed. ----
            var boundaries = new List<Polyline>();
            var polyExtents = new List<Envelope>();
            using (var pcur = polygonLayer.Search(null))
            {
                while (pcur.MoveNext())
                {
                    using var f = (Feature)pcur.Current;
                    if (f.GetShape() is not Polygon pg || pg.IsEmpty) continue;
                    if (pg.SpatialReference != null)
                        pg = (Polygon)GeometryEngine.Instance.Project(pg, sr);
                    boundaries.Add((Polyline)GeometryEngine.Instance.Boundary(pg));
                    polyExtents.Add(pg.Extent);
                }
            }

            if (boundaries.Count == 0)
            {
                log("Polygon layer has no features.");
                return new List<IntersectionCluster>();
            }

            double sw = 0, sh = 0;
            foreach (var e in polyExtents)
            {
                sw += e.XMax - e.XMin;
                sh += e.YMax - e.YMin;
            }
            double cell = Math.Max(sw / polyExtents.Count, sh / polyExtents.Count);
            if (cell <= 0) cell = 1.0;

            var polyGrid = new Dictionary<(long, long), List<int>>();
            for (int pi = 0; pi < polyExtents.Count; pi++)
            {
                var e = polyExtents[pi];
                long c0 = (long)Math.Floor(e.XMin / cell);
                long c1 = (long)Math.Floor(e.XMax / cell);
                long r0 = (long)Math.Floor(e.YMin / cell);
                long r1 = (long)Math.Floor(e.YMax / cell);
                for (long c = c0; c <= c1; c++)
                    for (long r = r0; r <= r1; r++)
                    {
                        var key = (c, r);
                        if (!polyGrid.TryGetValue(key, out var list))
                            polyGrid[key] = list = new List<int>();
                        list.Add(pi);
                    }
            }

            // ---- For each line, intersect it with every candidate polygon's boundary. ----
            var clusters = new Dictionary<(long, long), IntersectionCluster>();

            foreach (var (oid, geom) in lines)
            {
                var e = geom.Extent;
                long c0 = (long)Math.Floor(e.XMin / cell);
                long c1 = (long)Math.Floor(e.XMax / cell);
                long r0 = (long)Math.Floor(e.YMin / cell);
                long r1 = (long)Math.Floor(e.YMax / cell);
                var candidates = new HashSet<int>();
                for (long c = c0; c <= c1; c++)
                    for (long r = r0; r <= r1; r++)
                        if (polyGrid.TryGetValue((c, r), out var list))
                            candidates.UnionWith(list);

                foreach (var pi in candidates)
                {
                    Geometry inter;
                    try
                    {
                        inter = GeometryEngine.Instance.Intersection(geom, boundaries[pi], GeometryDimensionType.EsriGeometry0Dimension);
                    }
                    catch
                    {
                        continue;
                    }
                    if (inter == null || inter.IsEmpty) continue;

                    void AddPoint(MapPoint pt)
                    {
                        var key = NodeKey(pt.X, pt.Y);
                        if (!clusters.TryGetValue(key, out var cluster))
                            clusters[key] = cluster = new IntersectionCluster { Point = pt };
                        cluster.LineOids.Add(oid);
                    }

                    if (inter is Multipoint mp)
                    {
                        for (int i = 0; i < mp.PointCount; i++)
                            AddPoint(mp.Points[i]);
                    }
                    else if (inter is MapPoint singlePt)
                    {
                        AddPoint(singlePt);
                    }
                }
            }

            var result = new List<IntersectionCluster>(clusters.Values);
            log($"Found {result.Count} intersection point(s) from {lines.Count} polyline(s) against {boundaries.Count} polygon(s).");
            return result;
        }
    }
}
