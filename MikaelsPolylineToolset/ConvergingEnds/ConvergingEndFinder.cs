using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using MikaelsPolylineToolset.Common;
using System;
using System.Collections.Generic;

namespace MikaelsPolylineToolset.ConvergingEnds
{
    /// <summary>
    /// Finds nodes where directed lines END (flow converges) with NO line starting there to
    /// continue the flow, and returns the OIDs of those converging lines. Lines are treated as
    /// DIRECTED -- first vertex = START, last vertex = END. Ported from WrongFlowFinder.pyt
    /// (SelectConvergingEnds). Must be called from within QueuedTask.Run.
    /// </summary>
    internal static class ConvergingEndFinder
    {
        private class NodeInfo
        {
            public HashSet<long> Starts = new HashSet<long>();
            public HashSet<long> Ends = new HashSet<long>();
        }

        public static List<long> FindConvergingEndOids(
            FeatureLayer lineLayer,
            double tolerance,
            bool exactlyTwo,
            Action<string> log,
            QcProgress progress = null)
        {
            double? snap = tolerance > 0 ? tolerance : (double?)null;

            (long, long) NodeKey(double x, double y)
            {
                if (snap.HasValue)
                    return ((long)Math.Round(x / snap.Value), (long)Math.Round(y / snap.Value));
                return ((long)Math.Round(x * 1e8), (long)Math.Round(y * 1e8));
            }

            var nodes = new Dictionary<(long, long), NodeInfo>();
            int nLines = 0;

            progress?.Begin("Reading polylines", QcRunUi.CountNow(lineLayer), 0, 0.9);
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
                    nLines++;
                    var sp = geom.Points[0];
                    var ep = geom.Points[geom.PointCount - 1];
                    var sk = NodeKey(sp.X, sp.Y);
                    var ek = NodeKey(ep.X, ep.Y);

                    if (!nodes.TryGetValue(sk, out var sInfo))
                        nodes[sk] = sInfo = new NodeInfo();
                    sInfo.Starts.Add(oid);

                    if (!nodes.TryGetValue(ek, out var eInfo))
                        nodes[ek] = eInfo = new NodeInfo();
                    eInfo.Ends.Add(oid);
                }
            }

            var selected = new HashSet<long>();
            int badNodes = 0;
            progress?.Begin("Finding converging end nodes", nodes.Count, 0.9, 0.1);
            foreach (var info in nodes.Values)
            {
                progress?.Tick();
                if (info.Starts.Count > 0)
                    continue; // a line continues the flow -> valid

                int nEnds = info.Ends.Count;
                bool converges = exactlyTwo ? nEnds == 2 : nEnds >= 2;
                if (converges)
                {
                    badNodes++;
                    selected.UnionWith(info.Ends);
                }
            }

            var result = new List<long>(selected);
            result.Sort();
            log($"Found {badNodes} converging dead-end node(s); selecting {result.Count} line(s) out of {nLines}.");
            return result;
        }
    }
}
