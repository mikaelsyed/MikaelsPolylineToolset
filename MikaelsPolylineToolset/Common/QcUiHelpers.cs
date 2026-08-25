using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace MikaelsPolylineToolset.Common
{
    /// <summary>Shared UI helpers reused by every flowline QC tool dialog.</summary>
    internal static class QcUiHelpers
    {
        public class LayerItem
        {
            public string Name { get; set; }
            public FeatureLayer Layer { get; set; }
            public string Path { get; set; }
            public override string ToString() => Name;
        }

        public class FieldItem
        {
            public string Name { get; set; }
            public override string ToString() => Name;
        }

        public class RasterLayerItem
        {
            public string Name { get; set; }
            public RasterLayer Layer { get; set; }
            public override string ToString() => Name;
        }

        private static readonly FieldType[] AttributeFieldTypes =
        {
            FieldType.String, FieldType.SmallInteger, FieldType.Integer
        };

        // Broader set used by tools that just need "any attribute field", matching the
        // original python tools' ["Short","Long","Float","Double","Text","Date","GUID"] filter.
        private static readonly FieldType[] BroadAttributeFieldTypes =
        {
            FieldType.SmallInteger, FieldType.Integer, FieldType.Single, FieldType.Double,
            FieldType.String, FieldType.Date, FieldType.GUID
        };

        public static Task<List<FieldItem>> GetAttributeFieldItemsAsync(FeatureLayer layer) =>
            GetAttributeFieldItemsAsync(layer, AttributeFieldTypes);

        public static Task<List<FieldItem>> GetBroadAttributeFieldItemsAsync(FeatureLayer layer) =>
            GetAttributeFieldItemsAsync(layer, BroadAttributeFieldTypes);

        public static async Task<List<FieldItem>> GetAttributeFieldItemsAsync(FeatureLayer layer, FieldType[] allowedTypes)
        {
            if (layer == null) return new List<FieldItem>();
            return await QueuedTask.Run(() =>
            {
                var fields = layer.GetTable().GetDefinition().GetFields();
                return fields.Where(f => allowedTypes.Contains(f.FieldType))
                    .Select(f => new FieldItem { Name = f.Name })
                    .ToList();
            });
        }

        public static async Task<List<RasterLayerItem>> GetRasterLayerItemsAsync()
        {
            return await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                return map?.GetLayersAsFlattenedList().OfType<RasterLayer>()
                    .Select(l => new RasterLayerItem { Name = l.Name, Layer = l })
                    .ToList() ?? new List<RasterLayerItem>();
            });
        }

        public static async Task<List<string>> GetDistinctFieldValuesAsync(FeatureLayer layer, string fieldName)
        {
            if (layer == null || string.IsNullOrEmpty(fieldName)) return new List<string>();
            return await QueuedTask.Run(() =>
            {
                var values = new HashSet<string>();
                using var cursor = layer.Search(null);
                while (cursor.MoveNext())
                {
                    using var feature = (Feature)cursor.Current;
                    var val = feature[fieldName];
                    if (val != null)
                        values.Add(val.ToString().Trim());
                }
                return values.OrderBy(v => v).ToList();
            });
        }

        public static Task<List<LayerItem>> GetPolylineLayerItemsAsync() =>
            GetLayerItemsAsync(esriGeometryType.esriGeometryPolyline);

        public static Task<List<LayerItem>> GetPolygonLayerItemsAsync() =>
            GetLayerItemsAsync(esriGeometryType.esriGeometryPolygon);

        public static async Task<List<LayerItem>> GetLayerItemsAsync(esriGeometryType shapeType)
        {
            return await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                return map?.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                    .Where(l => l.ShapeType == shapeType)
                    .Select(l => new LayerItem { Name = l.Name, Layer = l })
                    .ToList() ?? new List<LayerItem>();
            });
        }

        /// <summary>
        /// Feature layers in the active map whose metadata the Pro SDK can open directly --
        /// shapefiles (.shp) and feature classes inside a File/Mobile geodatabase (.gdb) --
        /// used by the metadata editor/viewer/export tools so a layer already in the map can
        /// be picked from a list instead of browsing to its source again. Enterprise (SDE)
        /// geodatabase feature classes are intentionally excluded: their metadata is
        /// versioned/connection-based and needs different handling than a plain on-disk path.
        /// </summary>
        public static async Task<List<LayerItem>> GetEditableFeatureLayerItemsAsync()
        {
            return await QueuedTask.Run(() =>
            {
                var items = new List<LayerItem>();
                var map = MapView.Active?.Map;
                if (map == null) return items;

                foreach (var layer in map.GetLayersAsFlattenedList().OfType<FeatureLayer>())
                {
                    string path = GetMetadataPath(layer);
                    if (path == null) continue;

                    items.Add(new LayerItem { Name = layer.Name, Layer = layer, Path = path });
                }
                return items;
            });
        }

        /// <summary>
        /// Resolves the path ItemFactory.Instance.Create needs to open a feature layer's
        /// metadata: the .shp file for a shapefile, or "...\name.gdb\FeatureClassName" for a
        /// File/Mobile geodatabase feature class (LocalDatabase). Everything else (enterprise
        /// geodatabase, feature service, in-memory workspace) returns null. Goes through the
        /// layer's Datastore rather than trusting MapMember.GetPath()'s file extension -- for
        /// a shapefile workspace that method returns the bare dataset name (e.g. "...\rivers",
        /// no ".shp"), the same way it would for a geodatabase feature class, so checking for a
        /// ".shp" suffix there silently matches nothing.
        /// Must be called from within QueuedTask.Run.
        /// </summary>
        private static string GetMetadataPath(FeatureLayer layer)
        {
            try
            {
                using var featureClass = layer.GetFeatureClass();
                if (featureClass == null) return null;

                using var datastore = featureClass.GetDatastore();
                string name = featureClass.GetDefinition().GetName();
                if (string.IsNullOrEmpty(name)) return null;

                if (datastore is Geodatabase geodatabase)
                {
                    if (geodatabase.GetGeodatabaseType() != GeodatabaseType.LocalDatabase) return null;

                    string gdbPath = geodatabase.GetPath()?.LocalPath;
                    if (string.IsNullOrEmpty(gdbPath) || !(Directory.Exists(gdbPath) || File.Exists(gdbPath))) return null;

                    return Path.Combine(gdbPath, name);
                }

                if (datastore is not FileSystemDatastore) return null;

                string folder = datastore.GetPath()?.LocalPath;
                if (string.IsNullOrEmpty(folder)) return null;

                string path = Path.Combine(folder, name);
                if (!path.EndsWith(".shp", System.StringComparison.OrdinalIgnoreCase))
                    path += ".shp";

                return File.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// True if a path resolved by GetEditableFeatureLayerItemsAsync (or picked via an
        /// OpenItemDialog using ItemFilters.FeatureClasses_All) points at something real on
        /// disk. A shapefile path is a literal file; a geodatabase feature class path
        /// (".../name.gdb/FeatureClassName") is not itself a file, so this checks that its
        /// container (the .gdb folder, or the .geodatabase file for a mobile geodatabase)
        /// exists instead.
        /// </summary>
        public static bool FeatureClassPathExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            if (path.EndsWith(".shp", System.StringComparison.OrdinalIgnoreCase))
                return File.Exists(path);

            string container = Path.GetDirectoryName(path);
            return !string.IsNullOrEmpty(container) && (Directory.Exists(container) || File.Exists(container));
        }

        /// <summary>
        /// A sensible default folder to export/save alongside a feature class: the shapefile's
        /// own folder, or one level above a geodatabase feature class's ".gdb"/".geodatabase"
        /// container so exported files don't end up dropped inside the geodatabase itself.
        /// </summary>
        public static string GetDefaultSiblingFolder(string featureClassPath)
        {
            string dir = Path.GetDirectoryName(featureClassPath) ?? "";
            if (dir.EndsWith(".gdb", System.StringComparison.OrdinalIgnoreCase) ||
                dir.EndsWith(".geodatabase", System.StringComparison.OrdinalIgnoreCase))
            {
                dir = Path.GetDirectoryName(dir) ?? dir;
            }
            return dir;
        }

        /// <summary>
        /// Wires a "use project default geodatabase" checkbox to an output-path textbox +
        /// browse button: checking it fills in "&lt;DefaultGdb&gt;\&lt;defaultName&gt;" and
        /// disables Browse (no need to navigate anywhere), but leaves the textbox editable so
        /// the output feature class can still be renamed. Unchecking clears it and hands full
        /// control (including Browse) back.
        /// </summary>
        public static void ConfigureDefaultGdbToggle(CheckBox toggle, TextBox outputPathTextBox, Button browseButton, string defaultName)
        {
            toggle.Checked += (s, e) =>
            {
                browseButton.IsEnabled = false;
                var gdbPath = ArcGIS.Desktop.Core.Project.Current?.DefaultGeodatabasePath;
                outputPathTextBox.Text = string.IsNullOrEmpty(gdbPath) ? "" : Path.Combine(gdbPath, defaultName);
            };
            toggle.Unchecked += (s, e) =>
            {
                browseButton.IsEnabled = true;
                outputPathTextBox.Text = "";
            };
        }

        /// <summary>
        /// Shared false-positive gate used by the Run Flowline QC Checks master tool: mirrors
        /// TotalFlowlineQCScript.pyt's _check_false_positive. When the check is enabled and the
        /// count doesn't match the expected threshold, the whole run aborts (ShouldWrite=false,
        /// Abort=true). When it matches, every hit is an already-known false positive, so no
        /// output is written even though the run continues (ShouldWrite=false, Abort=false).
        /// When disabled, output is always written (ShouldWrite=true, Abort=false).
        /// </summary>
        public static (bool ShouldWrite, bool Abort) EvaluateFalsePositiveGate(
            int count, bool fpEnabled, int fpThreshold, string label, List<string> messages)
        {
            if (!fpEnabled) return (true, false);
            if (count != fpThreshold)
            {
                messages.Add($"[{label}] {count} error(s) found when false positive input was {fpThreshold}, recheck errors. Aborting remaining steps.");
                return (false, true);
            }
            messages.Add($"{count} error(s) found matches expected false positive count of {fpThreshold} for {label} -- false positives equal errors, no output generated.");
            return (false, false);
        }

        /// <summary>
        /// A native WinForms TextBox hosted via WindowsFormsHost, used for every tool's log
        /// panel. Painted via Win32/GDI, independent of WPF's renderer and Pro's WPF theme --
        /// a plain WPF TextBox/ListBox intermittently failed to paint inside Pro's hosted
        /// add-in windows (stayed blank/white until a redraw was forced by mouse-over).
        /// </summary>
        public static System.Windows.Forms.TextBox CreateLogTextBox()
        {
            return new System.Windows.Forms.TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = System.Windows.Forms.ScrollBars.Vertical,
                BackColor = System.Drawing.Color.White,
                ForeColor = System.Drawing.Color.Black,
                BorderStyle = System.Windows.Forms.BorderStyle.None,
                Font = new System.Drawing.Font("Segoe UI", 9)
            };
        }
    }
}
