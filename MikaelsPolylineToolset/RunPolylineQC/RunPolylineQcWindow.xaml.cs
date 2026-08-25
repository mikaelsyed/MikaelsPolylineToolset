using ArcGIS.Core.Data;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using MikaelsPolylineToolset.Common;
using MikaelsPolylineToolset.ConvergingEnds;
using MikaelsPolylineToolset.CrossingPolylines;
using MikaelsPolylineToolset.EdgeTouchingPolylines;
using MikaelsPolylineToolset.PolylineIntersectPoints;
using MikaelsPolylineToolset.IsolatedPolylines;
using MikaelsPolylineToolset.NValenceJunctions;
using MikaelsPolylineToolset.OverlapPolylines;
using MikaelsPolylineToolset.UnconnectedPolylineEnds;
using MikaelsPolylineToolset.ValidateInflowOutflow;
using MikaelsPolylineToolset.VertexCountCheck;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MikaelsPolylineToolset.RunPolylineQC
{
    public partial class RunPolylineQcWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;
        private List<string> _log;

        public RunPolylineQcWindow()
        {
            InitializeComponent();

            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;

            Loaded += RunPolylineQcWindow_Loaded;
        }

        private void FlushLog() => _logTextBox.Text = string.Join(Environment.NewLine, _log);

        private async void RunPolylineQcWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var lineLayers = await QcUiHelpers.GetPolylineLayerItemsAsync();
            LineLayerCombo.ItemsSource = lineLayers;
            if (lineLayers.Count > 0) LineLayerCombo.SelectedIndex = 0;

            var polyLayers = await QcUiHelpers.GetPolygonLayerItemsAsync();
            var anchorOptions = new List<QcUiHelpers.LayerItem> { new QcUiHelpers.LayerItem { Name = "<None>", Layer = null } };
            anchorOptions.AddRange(polyLayers);
            AnchorPolyCombo.ItemsSource = anchorOptions;
            AnchorPolyCombo.SelectedIndex = 0;

            WaterPolyCombo.ItemsSource = polyLayers;
            if (polyLayers.Count > 0) WaterPolyCombo.SelectedIndex = 0;

            if (lineLayers.Count == 0)
                _logTextBox.Text = "No polyline layers found in the active map.";
        }

        private async void LineLayerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var lineLayer = (LineLayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            var fields = await QcUiHelpers.GetBroadAttributeFieldItemsAsync(lineLayer);
            S10_FieldCombo.ItemsSource = fields;
            if (fields.Count > 0) S10_FieldCombo.SelectedIndex = 0;
            else S10_FieldValueCombo.ItemsSource = null;
        }

        private async void S10_FieldCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var lineLayer = (LineLayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            var field = (S10_FieldCombo.SelectedItem as QcUiHelpers.FieldItem)?.Name;
            if (lineLayer == null || field == null) { S10_FieldValueCombo.ItemsSource = null; return; }
            var values = await QcUiHelpers.GetDistinctFieldValuesAsync(lineLayer, field);
            S10_FieldValueCombo.ItemsSource = values;
            if (values.Count > 0) S10_FieldValueCombo.SelectedIndex = 0;
        }

        private async void WaterPolyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var polyLayer = (WaterPolyCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            var fields = await QcUiHelpers.GetAttributeFieldItemsAsync(polyLayer);
            S9_TypeFieldCombo.ItemsSource = fields;
            if (fields.Count > 0) S9_TypeFieldCombo.SelectedIndex = 0;
            else S9_OceanValueCombo.ItemsSource = null;
        }

        private async void S9_TypeFieldCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var polyLayer = (WaterPolyCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            var field = (S9_TypeFieldCombo.SelectedItem as QcUiHelpers.FieldItem)?.Name;
            if (polyLayer == null || field == null) { S9_OceanValueCombo.ItemsSource = null; return; }
            var values = await QcUiHelpers.GetDistinctFieldValuesAsync(polyLayer, field);
            S9_OceanValueCombo.ItemsSource = values;
            if (values.Count > 0) S9_OceanValueCombo.SelectedIndex = 0;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static double ParseOr(TextBox box, double fallback) =>
            double.TryParse(box.Text?.Trim(), out var v) ? v : fallback;

        private static int ParseIntOr(TextBox box, int fallback) =>
            int.TryParse(box.Text?.Trim(), out var v) ? v : fallback;

        private string DefaultOutputPath(string name)
        {
            var gdb = Project.Current?.DefaultGeodatabasePath;
            return string.IsNullOrEmpty(gdb) ? null : Path.Combine(gdb, name);
        }

        private async Task ExportSelectionAsync(FeatureLayer layer, string outputName, string label)
        {
            var outputPath = DefaultOutputPath(outputName);
            if (outputPath == null)
            {
                _log.Add($"{label}: could not resolve the project's default geodatabase; export skipped.");
                return;
            }
            var gpParams = Geoprocessing.MakeValueArray(layer, outputPath);
            var gpResult = await Geoprocessing.ExecuteToolAsync("management.CopyFeatures", gpParams,
                null, null, null, GPExecuteToolFlags.None);
            if (gpResult.IsFailed)
                _log.Add($"{label}: CopyFeatures failed: " + string.Join("; ", gpResult.ErrorMessages.Select(m => m.Text)));
            else
                _log.Add($"{label}: exported to {outputPath}");
        }

        /// <summary>Runs one "select" step: skip check, compute+select, false-positive gate, optional export.
        /// Returns false if the run should abort (false-positive mismatch).</summary>
        private async Task<bool> RunSelectStepAsync(
            CheckBox skip, CheckBox fpCheck, TextBox fpThreshold, CheckBox export,
            string label, string defaultOutputName, FeatureLayer layer,
            Func<Task<List<long>>> computeAndSelect)
        {
            if (skip.IsChecked == true)
            {
                _log.Add($"{label}: skipped.");
                FlushLog();
                return true;
            }

            var oids = await computeAndSelect();
            int threshold = ParseIntOr(fpThreshold, 0);
            var (shouldWrite, abort) = QcUiHelpers.EvaluateFalsePositiveGate(
                oids.Count, fpCheck.IsChecked == true, threshold, label, _log);

            if (shouldWrite && export.IsChecked == true && oids.Count > 0)
                await ExportSelectionAsync(layer, defaultOutputName, label);
            else if (shouldWrite && export.IsChecked == true)
                _log.Add($"{label}: nothing found; output not created.");

            FlushLog();
            return !abort;
        }

        // ------------------------------------------------------------------
        // Run All
        // ------------------------------------------------------------------

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            var lineLayer = (LineLayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            if (lineLayer == null)
            {
                MessageBox.Show("Please select an input polyline layer.", "Run Polyline QC Checks", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Upfront validation for steps that need extra required inputs, but only if
            // those steps aren't skipped -- no point demanding a field/value for a check
            // the user has turned off.
            var waterPolyLayer = (WaterPolyCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            bool needsWaterPoly = S8_SkipCheck.IsChecked != true || S9_SkipCheck.IsChecked != true || S11_SkipCheck.IsChecked != true;
            if (needsWaterPoly && waterPolyLayer == null)
            {
                MessageBox.Show("Steps 8, 9, and 11 need a Boundary Polygon layer (or skip all three).", "Run Polyline QC Checks", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string s9TypeField = (S9_TypeFieldCombo.SelectedItem as QcUiHelpers.FieldItem)?.Name;
            string s9OceanValue = (S9_OceanValueCombo.SelectedItem as string) ?? S9_OceanValueCombo.Text?.Trim();
            if (S9_SkipCheck.IsChecked != true && (string.IsNullOrEmpty(s9TypeField) || string.IsNullOrEmpty(s9OceanValue)))
            {
                MessageBox.Show("Step 9 (Validate Inflow/Outflow) needs a Boundary Type Field and Ocean Attribute Value, or skip it.", "Run Polyline QC Checks", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string s10Field = (S10_FieldCombo.SelectedItem as QcUiHelpers.FieldItem)?.Name;
            string s10Value = (S10_FieldValueCombo.SelectedItem as string) ?? S10_FieldValueCombo.Text?.Trim();
            if (S10_SkipCheck.IsChecked != true && (string.IsNullOrEmpty(s10Field) || string.IsNullOrEmpty(s10Value)))
            {
                MessageBox.Show("Step 10 (Vertex Count Check) needs an Attribute Field and Field Value, or skip it.", "Run Polyline QC Checks", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (S10_SkipCheck.IsChecked != true && !int.TryParse(S10_ExpectedText.Text?.Trim(), out _))
            {
                MessageBox.Show("Step 10's expected vertex count must be a whole number.", "Run Polyline QC Checks", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (S5_SkipCheck.IsChecked != true && !int.TryParse(S5_ValenceText.Text?.Trim(), out _))
            {
                MessageBox.Show("Step 5's valence must be a whole number.", "Run Polyline QC Checks", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (S6_SkipCheck.IsChecked != true && !int.TryParse(S6_ValenceText.Text?.Trim(), out _))
            {
                MessageBox.Show("Step 6's valence must be a whole number.", "Run Polyline QC Checks", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RunButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            _log = new List<string> { "Running..." };
            FlushLog();

            try
            {
                var anchorPolyLayer = (AnchorPolyCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
                bool ok;

                // ---- Step 1: Isolated Lines ----
                double s1Tol = ParseOr(S1_ToleranceText, 0);
                ok = await RunSelectStepAsync(S1_SkipCheck, S1_FpCheck, S1_FpThresholdText, S1_ExportCheck,
                    "Isolated Lines", "IsolatedLinesOutput", lineLayer, async () => await QueuedTask.Run(() =>
                    {
                        var oids = IsolatedLineFinder.FindIsolatedLineOids(lineLayer, s1Tol, anchorPolyLayer, msg => _log.Add(msg));
                        if (oids.Count > 0) lineLayer.Select(new QueryFilter { ObjectIDs = oids }, SelectionCombinationMethod.New);
                        else lineLayer.ClearSelection();
                        return oids;
                    }));
                if (!ok) return;

                // ---- Step 2: Crossing Lines ----
                bool s2IncludeOverlaps = S2_IncludeOverlapsCheck.IsChecked == true;
                ok = await RunSelectStepAsync(S2_SkipCheck, S2_FpCheck, S2_FpThresholdText, S2_ExportCheck,
                    "Crossing Lines", "CrossingLinesOutput", lineLayer, async () => await QueuedTask.Run(() =>
                    {
                        var oids = CrossingPolylinesFinder.FindCrossingLineOids(lineLayer, s2IncludeOverlaps, msg => _log.Add(msg));
                        if (oids.Count > 0) lineLayer.Select(new QueryFilter { ObjectIDs = oids }, SelectionCombinationMethod.New);
                        else lineLayer.ClearSelection();
                        return oids;
                    }));
                if (!ok) return;

                // ---- Step 3: Overlapping Lines ----
                bool s3ExactOnly = S3_ExactOnlyCheck.IsChecked == true;
                double s3MinLength = ParseOr(S3_MinLengthText, 0);
                ok = await RunSelectStepAsync(S3_SkipCheck, S3_FpCheck, S3_FpThresholdText, S3_ExportCheck,
                    "Overlapping Lines", "OverlappingLinesOutput", lineLayer, async () => await QueuedTask.Run(() =>
                    {
                        var oids = OverlapPolylinesFinder.FindOverlappingLineOids(lineLayer, s3ExactOnly, s3MinLength, msg => _log.Add(msg));
                        if (oids.Count > 0) lineLayer.Select(new QueryFilter { ObjectIDs = oids }, SelectionCombinationMethod.New);
                        else lineLayer.ClearSelection();
                        return oids;
                    }));
                if (!ok) return;

                // ---- Step 4: Converging Ends ----
                double s4Tol = ParseOr(S4_ToleranceText, 0);
                bool s4ExactlyTwo = S4_ExactlyTwoCheck.IsChecked == true;
                ok = await RunSelectStepAsync(S4_SkipCheck, S4_FpCheck, S4_FpThresholdText, S4_ExportCheck,
                    "Converging Ends", "ConvergingEndsOutput", lineLayer, async () => await QueuedTask.Run(() =>
                    {
                        var oids = ConvergingEndFinder.FindConvergingEndOids(lineLayer, s4Tol, s4ExactlyTwo, msg => _log.Add(msg));
                        if (oids.Count > 0) lineLayer.Select(new QueryFilter { ObjectIDs = oids }, SelectionCombinationMethod.New);
                        else lineLayer.ClearSelection();
                        return oids;
                    }));
                if (!ok) return;

                // ---- Step 5: N-Valence Junctions (Run 1) ----
                int s5Valence = ParseIntOr(S5_ValenceText, 3);
                bool s5OnePer = S5_OnePerJunctionCheck.IsChecked == true;
                double? s5ClusterTol = double.TryParse(S5_ClusterTolText.Text?.Trim(), out var s5ct) ? s5ct : (double?)null;
                ok = await RunSelectStepAsync(S5_SkipCheck, S5_FpCheck, S5_FpThresholdText, S5_ExportCheck,
                    "N-Valence Junctions Run 1", "ValenceJunctionsOutput1", lineLayer, async () => await QueuedTask.Run(() =>
                    {
                        var oids = ValenceJunctionFinder.FindValenceJunctionOids(lineLayer, s5Valence, s5OnePer, s5ClusterTol, msg => _log.Add(msg));
                        if (oids.Count > 0) lineLayer.Select(new QueryFilter { ObjectIDs = oids }, SelectionCombinationMethod.New);
                        else lineLayer.ClearSelection();
                        return oids;
                    }));
                if (!ok) return;

                // ---- Step 6: N-Valence Junctions (Run 2) ----
                int s6Valence = ParseIntOr(S6_ValenceText, 4);
                bool s6OnePer = S6_OnePerJunctionCheck.IsChecked == true;
                double? s6ClusterTol = double.TryParse(S6_ClusterTolText.Text?.Trim(), out var s6ct) ? s6ct : (double?)null;
                ok = await RunSelectStepAsync(S6_SkipCheck, S6_FpCheck, S6_FpThresholdText, S6_ExportCheck,
                    "N-Valence Junctions Run 2", "ValenceJunctionsOutput2", lineLayer, async () => await QueuedTask.Run(() =>
                    {
                        var oids = ValenceJunctionFinder.FindValenceJunctionOids(lineLayer, s6Valence, s6OnePer, s6ClusterTol, msg => _log.Add(msg));
                        if (oids.Count > 0) lineLayer.Select(new QueryFilter { ObjectIDs = oids }, SelectionCombinationMethod.New);
                        else lineLayer.ClearSelection();
                        return oids;
                    }));
                if (!ok) return;

                // ---- Step 7: Edge-Touching Flowlines ----
                double s7Tol = ParseOr(S7_ToleranceText, 0.01);
                bool s7TouchingOnly = S7_TouchingOnlyCheck.IsChecked == true;
                ok = await RunSelectStepAsync(S7_SkipCheck, S7_FpCheck, S7_FpThresholdText, S7_ExportCheck,
                    "Edge-Touching Polylines", "EdgeTouchingPolylinesOutput", lineLayer, async () => await QueuedTask.Run(() =>
                    {
                        var oids = EdgeTouchingFinder.FindEdgeTouchingOids(lineLayer, s7Tol, s7TouchingOnly, msg => _log.Add(msg));
                        if (oids.Count > 0) lineLayer.Select(new QueryFilter { ObjectIDs = oids }, SelectionCombinationMethod.New);
                        else lineLayer.ClearSelection();
                        return oids;
                    }));
                if (!ok) return;

                // ---- Step 8: Flowline Intersect Points ----
                if (S8_SkipCheck.IsChecked == true)
                {
                    _log.Add("Polyline Intersect Points: skipped.");
                    FlushLog();
                }
                else
                {
                    double s8Tol = ParseOr(S8_ToleranceText, 0);
                    var (clusters, sr8, computeMsgs8) = await PolylineIntersectPointsService.ComputeAsync(lineLayer, waterPolyLayer, s8Tol);
                    _log.AddRange(computeMsgs8);
                    int threshold8 = ParseIntOr(S8_FpThresholdText, 0);
                    var (shouldWrite8, abort8) = QcUiHelpers.EvaluateFalsePositiveGate(
                        clusters.Count, S8_FpCheck.IsChecked == true, threshold8, "Polyline Intersect Points", _log);
                    if (shouldWrite8 && S8_ExportCheck.IsChecked == true)
                    {
                        var outputPath8 = DefaultOutputPath("PolylineIntersectPointsOutput");
                        if (outputPath8 == null)
                            _log.Add("Polyline Intersect Points: could not resolve the project's default geodatabase; export skipped.");
                        else
                        {
                            var (writeOk8, writeMsgs8) = await PolylineIntersectPointsService.WriteOutputAsync(clusters, sr8, outputPath8);
                            _log.AddRange(writeMsgs8);
                        }
                    }
                    FlushLog();
                    if (abort8) return;
                }

                // ---- Step 9: Validate Inflow/Outflow ----
                if (S9_SkipCheck.IsChecked == true)
                {
                    _log.Add("Validate Inflow/Outflow: skipped.");
                    FlushLog();
                }
                else
                {
                    double s9Tol = ParseOr(S9_ToleranceText, 0);
                    var (results9, sr9, computeMsgs9) = await ValidateInflowOutflowService.ComputeAsync(lineLayer, waterPolyLayer, s9Tol, s9TypeField, s9OceanValue);
                    _log.AddRange(computeMsgs9);
                    int checkCount9 = results9.Count(r => r.Status == "CHECK");
                    int threshold9 = ParseIntOr(S9_FpThresholdText, 0);
                    var (shouldWrite9, abort9) = QcUiHelpers.EvaluateFalsePositiveGate(
                        checkCount9, S9_FpCheck.IsChecked == true, threshold9, "Validate Inflow/Outflow", _log);
                    if (shouldWrite9 && S9_ExportCheck.IsChecked == true)
                    {
                        var outputPath9 = DefaultOutputPath("InflowOutflowValidationOutput");
                        if (outputPath9 == null)
                            _log.Add("Validate Inflow/Outflow: could not resolve the project's default geodatabase; export skipped.");
                        else
                        {
                            var (writeOk9, writeMsgs9) = await ValidateInflowOutflowService.WriteOutputAsync(results9, sr9, outputPath9);
                            _log.AddRange(writeMsgs9);
                        }
                    }
                    FlushLog();
                    if (abort9) return;
                }

                // ---- Step 10: Vertex Count Check ----
                int s10Expected = ParseIntOr(S10_ExpectedText, 2);
                ok = await RunSelectStepAsync(S10_SkipCheck, S10_FpCheck, S10_FpThresholdText, S10_ExportCheck,
                    "Vertex Count Check", "VertexCountOutput", lineLayer, async () => await QueuedTask.Run(() =>
                    {
                        var oids = VertexCountFinder.FindVertexCountFlags(lineLayer, s10Field, s10Value, s10Expected, msg => _log.Add(msg));
                        if (oids.Count > 0) lineLayer.Select(new QueryFilter { ObjectIDs = oids }, SelectionCombinationMethod.New);
                        else lineLayer.ClearSelection();
                        return oids;
                    }));
                if (!ok) return;

                // ---- Step 11: Flag Unconnected Flowline Ends ----
                double s11Tol = ParseOr(S11_ToleranceText, 0);
                ok = await RunSelectStepAsync(S11_SkipCheck, S11_FpCheck, S11_FpThresholdText, S11_ExportCheck,
                    "Unconnected Polyline Ends", "UnconnectedEndsOutput", lineLayer, async () => await QueuedTask.Run(() =>
                    {
                        var oids = UnconnectedEndsFinder.FindUnconnectedEndOids(lineLayer, waterPolyLayer, s11Tol, msg => _log.Add(msg));
                        if (oids.Count > 0) lineLayer.Select(new QueryFilter { ObjectIDs = oids }, SelectionCombinationMethod.New);
                        else lineLayer.ClearSelection();
                        return oids;
                    }));
                if (!ok) return;

                _log.Add("All steps complete.");
                FlushLog();
            }
            catch (Exception ex)
            {
                _log.Add("Error: " + ex.Message);
                FlushLog();
            }
            finally
            {
                RunButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
            }
        }
    }
}
