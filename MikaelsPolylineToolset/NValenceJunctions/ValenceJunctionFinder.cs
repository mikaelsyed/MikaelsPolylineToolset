using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;

namespace MikaelsPolylineToolset.NValenceJunctions
{
    /// <summary>
    /// Finds junctions where exactly N flowline endpoints (all starts or all ends -- no mixing)
    /// meet within a cluster tolerance, and returns the OIDs of the participating flowlines.
    /// Ported from JunctionFinder.pyt (SelectJunctionLines). Must be called from within
    /// QueuedTask.Run.
    /// </summary>
    internal static class ValenceJunctionFinder
    {
        private struct Endpoint
        {
            public double X;
            public double Y;
            public long Oid;
            public bool IsStart;
        }

        public static List<long> FindValenceJunctionOids(
            FeatureLayer lineLayer,
            int valence,
            bool onePerJunction,
            double? clusterTolerance,
            Action<string> log)
        {
            double tol;
            if (clusterTolerance.HasValue && clusterTolerance.Value > 0)
            {
                tol = clusterTolerance.Value;
            }
            else
            {
                SpatialReference sr = null;
                using (var probe = lineLayer.Search(null))
                    if (probe.MoveNext())
                        using (var f = (Feature)probe.Current)
                            sr = (f.GetShape() as Polyline)?.SpatialReference;
                tol = sr?.XYTolerance ?? 0.001;
                if (tol <= 0) tol = 0.001;
            }
            double tolSq = tol * tol;
            log($"Cluster tolerance: {tol}");

            var endpoints = new List<Endpoint>();
            int lineCount = 0;
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
                    lineCount++;
                    var fp = geom.Points[0];
                    var lp = geom.Points[geom.PointCount - 1];
                    endpoints.Add(new Endpoint { X = fp.X, Y = fp.Y, Oid = oid, IsStart = true });
                    endpoints.Add(new Endpoint { X = lp.X, Y = lp.Y, Oid = oid, IsStart = false });
                }
            }

            if (lineCount == 0)
                return new List<long>();

            // ---- spatial grid index: cell size = tolerance ----
            var grid = new Dictionary<(long, long), List<int>>();
            for (int idx = 0; idx < endpoints.Count; idx++)
            {
                var key = ((long)Math.Floor(endpoints[idx].X / tol), (long)Math.Floor(endpoints[idx].Y / tol));
                if (!grid.TryGetValue(key, out var list))
                    grid[key] = list = new List<int>();
                list.Add(idx);
            }

            // ---- union-find clustering of endpoints within tolerance ----
            var parent = new int[endpoints.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            int Find(int i)
            {
                int root = i;
                while (parent[root] != root) root = parent[root];
                while (parent[i] != root) { int next = parent[i]; parent[i] = root; i = next; }
                return root;
            }

            void Union(int i, int j)
            {
                int ri = Find(i), rj = Find(j);
                if (ri != rj) parent[rj] = ri;
            }

            bool WithinTol(int i, int j)
            {
                double dx = endpoints[i].X - endpoints[j].X;
                double dy = endpoints[i].Y - endpoints[j].Y;
                return dx * dx + dy * dy <= tolSq;
            }

            var neighborOffsets = new (int, int)[]
            {
                (-1,-1),(-1,0),(-1,1),(0,-1),(0,0),(0,1),(1,-1),(1,0),(1,1)
            };

            foreach (var kvp in grid)
            {
                var (cx, cy) = kvp.Key;
                var members = kvp.Value;

                for (int a = 1; a < members.Count; a++)
                    if (WithinTol(members[0], members[a]))
                        Union(members[0], members[a]);

                foreach (var (dx, dy) in neighborOffsets)
                {
                    if (dx < 0 || (dx == 0 && dy <= 0)) continue; // forward neighbors only
                    var nkey = (cx + dx, cy + dy);
                    if (!grid.TryGetValue(nkey, out var otherMembers)) continue;
                    foreach (var i in members)
                        foreach (var j in otherMembers)
                            if (WithinTol(i, j))
                                Union(i, j);
                }
            }

            var clusters = new Dictionary<int, List<int>>();
            for (int idx = 0; idx < endpoints.Count; idx++)
            {
                int root = Find(idx);
                if (!clusters.TryGetValue(root, out var list))
                    clusters[root] = list = new List<int>();
                list.Add(idx);
            }

            var selOids = new HashSet<long>();
            int junctionCount = 0;
            foreach (var members in clusters.Values)
            {
                if (members.Count != valence) continue;

                bool allStart = true, allEnd = true;
                foreach (var i in members)
                {
                    if (endpoints[i].IsStart) allEnd = false;
                    else allStart = false;
                }
                if (!allStart && !allEnd) continue; // mixed start/end junction -- skip

                junctionCount++;
                if (onePerJunction)
                {
                    long min = long.MaxValue;
                    foreach (var i in members) min = Math.Min(min, endpoints[i].Oid);
                    selOids.Add(min);
                }
                else
                {
                    foreach (var i in members) selOids.Add(endpoints[i].Oid);
                }
            }

            var result = new List<long>(selOids);
            result.Sort();
            log($"Found {junctionCount} junction(s) where exactly {valence} same-type endpoint(s) meet; selecting {result.Count} line(s) out of {lineCount}.");
            return result;
        }
    }
}
