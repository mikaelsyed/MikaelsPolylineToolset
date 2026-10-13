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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MikaelsPolylineToolset.RunPolylineQC
{
    public partial class RunPolylineQcWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private const int StepCount = 11;

        /// <summary>What one step produced: how many features/points it flagged, and how to export them (returns the path written).</summary>
        private class StepResult
        {
            public int Count { get; set; }
            public Func<Task<string>> Export { get; set; }
            /// <summary>Steps 8 and 9 write every point they computed, so they export even when nothing was flagged.</summary>
            public bool ExportWhenZero { get; set; }
        }

        /// <summary>One numbered step: its controls, how to describe its settings in the log, and how to run it.</summary>
        private class StepDef
        {
            public int Number { get; set; }
            public string Key { get; set; }
            public string Name { get; set; }
            public string Parameters { get; set; }
            public CheckBox Skip { get; set; }
            public CheckBox FpCheck { get; set; }
            public TextBox FpThreshold { get; set; }
            public CheckBox Export { get; set; }
            /// <summary>Rough cost per record, used only to shape the progress bar and the time estimate.</summary>
            public double Factor { get; set; }
            /// <summary>Extra polygon layer the step reads (anchor or boundary polygons), counted into its record total.</summary>
            public FeatureLayer ExtraLayer { get; set; }
            public Func<Task<StepResult>> Run { get; set; }
        }

        private readonly System.Windows.Forms.TextBox _logTextBox;
        private List<string> _log;
        private CancellationTokenSource _cts;
        private QcRunProgress _run;

        public RunPolylineQcWindow()
        {
            InitializeComponent();

            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;
            LogPathText.Text = DefaultLogPath();

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

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
            CancelButton.Content = "Cancelling...";
            _log?.Add("Cancel requested - stopping at the next safe point (an operation already underway must finish first).");
            FlushLog();
        }

        // ------------------------------------------------------------------
        // QC output log file
        // ------------------------------------------------------------------

        private static string DefaultLogPath()
        {
            string folder = Project.Current?.HomeFolderPath;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(folder, QcRunLog.PolylineDefaultFileName);
        }

        private void LogBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            string current = LogPathText.Text?.Trim();
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "QC output log file",
                Filter = "Text file (*.txt)|*.txt",
                DefaultExt = ".txt",
                OverwritePrompt = false, // the log is meant to be replaced by each run
                FileName = string.IsNullOrEmpty(current) ? QcRunLog.PolylineDefaultFileName : Path.GetFileName(current),
                InitialDirectory = string.IsNullOrEmpty(current) ? "" : Path.GetDirectoryName(current)
            };
            if (dialog.ShowDialog(this) == true)
                LogPathText.Text = dialog.FileName;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static double ParseOr(TextBox box, double fallback) =>
            double.TryParse(box.Text?.Trim(), out var v) ? v : fallback;

        private static int ParseIntOr(TextBox box, int fallback) =>
            int.TryParse(box.Text?.Trim(), out var v) ? v : fallback;

        private static string YesNo(bool value) => value ? "yes" : "no";

        private string DefaultOutputPath(string name)
        {
            var gdb = Project.Current?.DefaultGeodatabasePath;
            return string.IsNullOrEmpty(gdb) ? null : Path.Combine(gdb, name);
        }

        /// <summary>Copies the layer's current selection to the default geodatabase; returns the path written, or null if it could not be.</summary>
        private async Task<string> ExportSelectionAsync(FeatureLayer layer, string outputName, string label)
        {
            var outputPath = DefaultOutputPath(outputName);
            if (outputPath == null)
            {
                _log.Add($"{label}: could not resolve the project's default geodatabase; export skipped.");
                return null;
            }
            var gpParams = Geoprocessing.MakeValueArray(layer, outputPath);
            var gpResult = await Geoprocessing.ExecuteToolAsync("management.CopyFeatures", gpParams,
                null, null, null, GPExecuteToolFlags.None);
            if (gpResult.IsFailed)
            {
                _log.Add($"{label}: CopyFeatures failed: " + string.Join("; ", gpResult.ErrorMessages.Select(m => m.Text)));
                return null;
            }
            _log.Add($"{label}: exported to {outputPath}");
            return outputPath;
        }

        /// <summary>Runs a "select" step: compute + select the flagged lines, then hand back how to export them.</summary>
        private async Task<StepResult> SelectStepAsync(FeatureLayer layer, string outputName, string label, Func<List<long>> find)
        {
            var oids = await QueuedTask.Run(() =>
            {
                var found = find();
                if (found.Count > 0) layer.Select(new QueryFilter { ObjectIDs = found }, SelectionCombinationMethod.New);
                else layer.ClearSelection();
                return found;
            });
            return new StepResult { Count = oids.Count, Export = () => ExportSelectionAsync(layer, outputName, label) };
        }

        /// <summary>
        /// Runs one step: skip check, the check itself, the false-positive gate, then the optional export. Records the
        /// outcome and timing in the step's log record. Returns false if the run should stop (false-positive mismatch).
        /// </summary>
        private async Task<bool> RunStepAsync(StepDef def, QcLogStep rec, FeatureLayer lineLayer, string dataset)
        {
            if (def.Skip.IsChecked == true)
            {
                _log.Add($"{def.Name}: skipped.");
                FlushLog();
                return true;
            }

            var timer = Stopwatch.StartNew();
            _run.BeginUnit(def.Name, lineLayer.Name);
            try
            {
                var result = await def.Run();

                var layerResult = new QcLogLayerResult { Layer = lineLayer.Name, Dataset = dataset, Found = result.Count };
                rec.Layers.Add(layerResult);

                var (shouldWrite, abort) = QcUiHelpers.EvaluateFalsePositiveGate(
                    result.Count, rec.FpEnabled, rec.ExpectedFp, def.Name, _log);

                if (abort)
                    rec.Status = QcStatus.FpMismatch;
                else
                    rec.ApplyGate(rec.FpEnabled); // no abort with the check on means the count matched the expected false positives

                if (shouldWrite && rec.ExportRequested)
                {
                    if (result.Count > 0 || result.ExportWhenZero)
                    {
                        _run.Current.Working("Exporting results to the default geodatabase", 1, 0);
                        layerResult.ExportedTo = await result.Export() ?? "";
                    }
                    else
                        _log.Add($"{def.Name}: nothing found; output not created.");
                }

                layerResult.Seconds = timer.Elapsed.TotalSeconds;
                return !abort;
            }
            catch (OperationCanceledException)
            {
                rec.Status = QcStatus.Cancelled;
                throw;
            }
            catch (Exception ex)
            {
                rec.Status = QcStatus.Error;
                _log.Add($"{def.Name}: error - {ex.Message}");
                ex.Data["qcLogged"] = true; // so the run-level handler doesn't repeat it
                throw;
            }
            finally
            {
                timer.Stop();
                rec.Seconds = timer.Elapsed.TotalSeconds;
                _run.EndUnit();
                FlushLog();
            }
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

            bool writeLog = WriteLogCheck.IsChecked == true;
            string logPath = LogPathText.Text?.Trim();
            if (writeLog)
            {
                if (string.IsNullOrEmpty(logPath)) logPath = DefaultLogPath();
                try { logPath = Path.GetFullPath(logPath); }
                catch
                {
                    MessageBox.Show("The QC output log path is not a valid file path.", "Run Polyline QC Checks", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            RunButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            _log = new List<string> { "Running..." };
            FlushLog();
            var cts = new CancellationTokenSource();
            _cts = cts;
            CancelButton.Content = "Cancel";
            CancelButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Visible;
            string outcome = "Finished";
            var runLog = new QcRunLog
            {
                ToolbarId = QcRunLog.PolylineToolbarId,
                ToolbarName = "Polyline QC",
                Title = "Polyline QC Output Log",
                Started = DateTime.Now,
                Project = Project.Current?.Path ?? "",
                DefaultGeodatabase = Project.Current?.DefaultGeodatabasePath ?? ""
            };

            try
            {
                var anchorPolyLayer = (AnchorPolyCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
                QcProgress progress = null; // set once the run's progress is created; the step closures below read it when they run

                // ---- Capture every setting up front (the work below leaves the UI thread) ----
                double s1Tol = ParseOr(S1_ToleranceText, 0);
                bool s2IncludeOverlaps = S2_IncludeOverlapsCheck.IsChecked == true;
                bool s3ExactOnly = S3_ExactOnlyCheck.IsChecked == true;
                double s3MinLength = ParseOr(S3_MinLengthText, 0);
                double s4Tol = ParseOr(S4_ToleranceText, 0);
                bool s4ExactlyTwo = S4_ExactlyTwoCheck.IsChecked == true;
                int s5Valence = ParseIntOr(S5_ValenceText, 3);
                bool s5OnePer = S5_OnePerJunctionCheck.IsChecked == true;
                double? s5ClusterTol = double.TryParse(S5_ClusterTolText.Text?.Trim(), out var s5ct) ? s5ct : (double?)null;
                int s6Valence = ParseIntOr(S6_ValenceText, 4);
                bool s6OnePer = S6_OnePerJunctionCheck.IsChecked == true;
                double? s6ClusterTol = double.TryParse(S6_ClusterTolText.Text?.Trim(), out var s6ct) ? s6ct : (double?)null;
                double s7Tol = ParseOr(S7_ToleranceText, 0.01);
                bool s7TouchingOnly = S7_TouchingOnlyCheck.IsChecked == true;
                double s8Tol = ParseOr(S8_ToleranceText, 0);
                double s9Tol = ParseOr(S9_ToleranceText, 0);
                int s10Expected = ParseIntOr(S10_ExpectedText, 2);
                double s11Tol = ParseOr(S11_ToleranceText, 0);

                string Cluster(double? tol) => tol.HasValue ? tol.Value.ToString() : "layer XY tolerance";

                // ---- The steps ----
                var defs = new List<StepDef>
                {
                    new StepDef
                    {
                        Number = 1, Key = "IsolatedLines", Name = "Isolated Lines", Factor = 0.5, ExtraLayer = anchorPolyLayer,
                        Skip = S1_SkipCheck, FpCheck = S1_FpCheck, FpThreshold = S1_FpThresholdText, Export = S1_ExportCheck,
                        Parameters = $"Snap tolerance = {s1Tol}; anchor polygons = {anchorPolyLayer?.Name ?? "none"}",
                        Run = () => SelectStepAsync(lineLayer, "IsolatedLinesOutput", "Isolated Lines",
                            () => IsolatedLineFinder.FindIsolatedLineOids(lineLayer, s1Tol, anchorPolyLayer, msg => _log.Add(msg), progress))
                    },
                    new StepDef
                    {
                        Number = 2, Key = "CrossingLines", Name = "Crossing Lines", Factor = 1.0,
                        Skip = S2_SkipCheck, FpCheck = S2_FpCheck, FpThreshold = S2_FpThresholdText, Export = S2_ExportCheck,
                        Parameters = $"Also select lines that partially overlap = {YesNo(s2IncludeOverlaps)}",
                        Run = () => SelectStepAsync(lineLayer, "CrossingLinesOutput", "Crossing Lines",
                            () => CrossingPolylinesFinder.FindCrossingLineOids(lineLayer, s2IncludeOverlaps, msg => _log.Add(msg), progress))
                    },
                    new StepDef
                    {
                        Number = 3, Key = "OverlappingLines", Name = "Overlapping Lines", Factor = 1.0,
                        Skip = S3_SkipCheck, FpCheck = S3_FpCheck, FpThreshold = S3_FpThresholdText, Export = S3_ExportCheck,
                        Parameters = $"Exact duplicates only = {YesNo(s3ExactOnly)}; minimum shared length = {s3MinLength}",
                        Run = () => SelectStepAsync(lineLayer, "OverlappingLinesOutput", "Overlapping Lines",
                            () => OverlapPolylinesFinder.FindOverlappingLineOids(lineLayer, s3ExactOnly, s3MinLength, msg => _log.Add(msg), progress))
                    },
                    new StepDef
                    {
                        Number = 4, Key = "ConvergingEnds", Name = "Converging Ends", Factor = 0.15,
                        Skip = S4_SkipCheck, FpCheck = S4_FpCheck, FpThreshold = S4_FpThresholdText, Export = S4_ExportCheck,
                        Parameters = $"Snap tolerance = {s4Tol}; only nodes where exactly two lines converge = {YesNo(s4ExactlyTwo)}",
                        Run = () => SelectStepAsync(lineLayer, "ConvergingEndsOutput", "Converging Ends",
                            () => ConvergingEndFinder.FindConvergingEndOids(lineLayer, s4Tol, s4ExactlyTwo, msg => _log.Add(msg), progress))
                    },
                    new StepDef
                    {
                        Number = 5, Key = "NValence1", Name = "N-Valence Junctions Run 1", Factor = 0.3,
                        Skip = S5_SkipCheck, FpCheck = S5_FpCheck, FpThreshold = S5_FpThresholdText, Export = S5_ExportCheck,
                        Parameters = $"Valence = {s5Valence}; one polyline per junction = {YesNo(s5OnePer)}; cluster tolerance = {Cluster(s5ClusterTol)}",
                        Run = () => SelectStepAsync(lineLayer, "ValenceJunctionsOutput1", "N-Valence Junctions Run 1",
                            () => ValenceJunctionFinder.FindValenceJunctionOids(lineLayer, s5Valence, s5OnePer, s5ClusterTol, msg => _log.Add(msg), progress))
                    },
                    new StepDef
                    {
                        Number = 6, Key = "NValence2", Name = "N-Valence Junctions Run 2", Factor = 0.3,
                        Skip = S6_SkipCheck, FpCheck = S6_FpCheck, FpThreshold = S6_FpThresholdText, Export = S6_ExportCheck,
                        Parameters = $"Valence = {s6Valence}; one polyline per junction = {YesNo(s6OnePer)}; cluster tolerance = {Cluster(s6ClusterTol)}",
                        Run = () => SelectStepAsync(lineLayer, "ValenceJunctionsOutput2", "N-Valence Junctions Run 2",
                            () => ValenceJunctionFinder.FindValenceJunctionOids(lineLayer, s6Valence, s6OnePer, s6ClusterTol, msg => _log.Add(msg), progress))
                    },
                    new StepDef
                    {
                        Number = 7, Key = "EdgeTouching", Name = "Edge-Touching Polylines", Factor = 0.8,
                        Skip = S7_SkipCheck, FpCheck = S7_FpCheck, FpThreshold = S7_FpThresholdText, Export = S7_ExportCheck,
                        Parameters = $"Snap tolerance = {s7Tol}; select only the touching lines = {YesNo(s7TouchingOnly)}",
                        Run = () => SelectStepAsync(lineLayer, "EdgeTouchingPolylinesOutput", "Edge-Touching Polylines",
                            () => EdgeTouchingFinder.FindEdgeTouchingOids(lineLayer, s7Tol, s7TouchingOnly, msg => _log.Add(msg), progress))
                    },
                    new StepDef
                    {
                        Number = 8, Key = "IntersectPoints", Name = "Polyline Intersect Points", Factor = 1.5, ExtraLayer = waterPolyLayer,
                        Skip = S8_SkipCheck, FpCheck = S8_FpCheck, FpThreshold = S8_FpThresholdText, Export = S8_ExportCheck,
                        Parameters = $"Snap tolerance = {s8Tol}; boundary polygons = {waterPolyLayer?.Name}",
                        Run = async () =>
                        {
                            var (clusters, sr8, computeMsgs) = await PolylineIntersectPointsService.ComputeAsync(lineLayer, waterPolyLayer, s8Tol, progress);
                            _log.AddRange(computeMsgs);
                            return new StepResult
                            {
                                Count = clusters.Count,
                                ExportWhenZero = true,
                                Export = async () =>
                                {
                                    var path = DefaultOutputPath("PolylineIntersectPointsOutput");
                                    if (path == null)
                                    {
                                        _log.Add("Polyline Intersect Points: could not resolve the project's default geodatabase; export skipped.");
                                        return null;
                                    }
                                    var (writeOk, writeMsgs) = await PolylineIntersectPointsService.WriteOutputAsync(clusters, sr8, path);
                                    _log.AddRange(writeMsgs);
                                    return writeOk && clusters.Count > 0 ? path : null;
                                }
                            };
                        }
                    },
                    new StepDef
                    {
                        Number = 9, Key = "InflowOutflow", Name = "Validate Inflow/Outflow", Factor = 2.0, ExtraLayer = waterPolyLayer,
                        Skip = S9_SkipCheck, FpCheck = S9_FpCheck, FpThreshold = S9_FpThresholdText, Export = S9_ExportCheck,
                        Parameters = $"Boundary type field = {s9TypeField}; ocean value = {s9OceanValue}; snap tolerance = {s9Tol}; boundary polygons = {waterPolyLayer?.Name}. Errors counted = points with status CHECK",
                        Run = async () =>
                        {
                            var (results, sr9, computeMsgs) = await ValidateInflowOutflowService.ComputeAsync(lineLayer, waterPolyLayer, s9Tol, s9TypeField, s9OceanValue, progress);
                            _log.AddRange(computeMsgs);
                            return new StepResult
                            {
                                Count = results.Count(r => r.Status == "CHECK"),
                                ExportWhenZero = true,
                                Export = async () =>
                                {
                                    var path = DefaultOutputPath("InflowOutflowValidationOutput");
                                    if (path == null)
                                    {
                                        _log.Add("Validate Inflow/Outflow: could not resolve the project's default geodatabase; export skipped.");
                                        return null;
                                    }
                                    var (writeOk, writeMsgs) = await ValidateInflowOutflowService.WriteOutputAsync(results, sr9, path);
                                    _log.AddRange(writeMsgs);
                                    return writeOk && results.Count > 0 ? path : null;
                                }
                            };
                        }
                    },
                    new StepDef
                    {
                        Number = 10, Key = "VertexCount", Name = "Vertex Count Check", Factor = 0.1,
                        Skip = S10_SkipCheck, FpCheck = S10_FpCheck, FpThreshold = S10_FpThresholdText, Export = S10_ExportCheck,
                        Parameters = $"Attribute = {s10Field} = {s10Value}; expected vertex count = {s10Expected}",
                        Run = () => SelectStepAsync(lineLayer, "VertexCountOutput", "Vertex Count Check",
                            () => VertexCountFinder.FindVertexCountFlags(lineLayer, s10Field, s10Value, s10Expected, msg => _log.Add(msg), progress))
                    },
                    new StepDef
                    {
                        Number = 11, Key = "UnconnectedEnds", Name = "Unconnected Polyline Ends", Factor = 1.0, ExtraLayer = waterPolyLayer,
                        Skip = S11_SkipCheck, FpCheck = S11_FpCheck, FpThreshold = S11_FpThresholdText, Export = S11_ExportCheck,
                        Parameters = $"Match tolerance = {s11Tol}; boundary polygons = {waterPolyLayer?.Name}",
                        Run = () => SelectStepAsync(lineLayer, "UnconnectedEndsOutput", "Unconnected Polyline Ends",
                            () => UnconnectedEndsFinder.FindUnconnectedEndOids(lineLayer, waterPolyLayer, s11Tol, msg => _log.Add(msg), progress))
                    },
                };

                // ---- Log: layers and one record per step (steps that never run stay "Not run") ----
                var (dataset, source) = await QcRunUi.DescribeAsync(lineLayer);
                long lineCount = await QcRunUi.CountAsync(lineLayer);
                runLog.Layers.Add(new QcLogLayerInfo { Role = "Polylines", Name = lineLayer.Name, Source = source, Features = lineCount });
                if (anchorPolyLayer != null && S1_SkipCheck.IsChecked != true)
                    runLog.Layers.Add(new QcLogLayerInfo { Role = "Anchor polygons", Name = anchorPolyLayer.Name, Source = (await QcRunUi.DescribeAsync(anchorPolyLayer)).Source, Features = await QcRunUi.CountAsync(anchorPolyLayer) });
                if (waterPolyLayer != null && needsWaterPoly)
                    runLog.Layers.Add(new QcLogLayerInfo { Role = "Boundary polygons", Name = waterPolyLayer.Name, Source = (await QcRunUi.DescribeAsync(waterPolyLayer)).Source, Features = await QcRunUi.CountAsync(waterPolyLayer) });

                var records = new Dictionary<int, QcLogStep>();
                foreach (var def in defs)
                {
                    bool skipped = def.Skip.IsChecked == true;
                    var rec = new QcLogStep
                    {
                        Number = def.Number, Key = def.Key, Name = def.Name, Parameters = def.Parameters, Skipped = skipped,
                        FpEnabled = def.FpCheck.IsChecked == true, ExpectedFp = ParseIntOr(def.FpThreshold, 0),
                        ExportRequested = def.Export.IsChecked == true,
                        Status = skipped ? QcStatus.Skipped : QcStatus.NotRun
                    };
                    records[def.Number] = rec;
                    runLog.Steps.Add(rec);
                }

                // ---- Plan the progress: every step on this layer, weighted by expected cost ----
                var units = new List<QcRunProgress.Unit>();
                foreach (var def in defs.Where(d => d.Skip.IsChecked != true))
                {
                    long recordsForStep = lineCount + (def.ExtraLayer != null ? await QcRunUi.CountAsync(def.ExtraLayer) : 0);
                    units.Add(new QcRunProgress.Unit
                    {
                        Step = def.Name, StepNumber = def.Number, Layer = lineLayer.Name,
                        Records = recordsForStep, Weight = Math.Max(recordsForStep, 1) * def.Factor
                    });
                }
                _run = new QcRunProgress(units, StepCount, cts.Token);
                ProgressPanel.Start(_run);
                progress = _run.Current;

                foreach (var def in defs)
                {
                    bool ok = await RunStepAsync(def, records[def.Number], lineLayer, dataset);
                    if (!ok) { outcome = "Stopped by false-positive check"; return; }
                }

                _log.Add("All steps complete.");
                FlushLog();
            }
            catch (OperationCanceledException)
            {
                outcome = "Cancelled";
                _log.Add("Cancelled by user. Results already exported by earlier steps are kept.");
                FlushLog();
            }
            catch (Exception ex)
            {
                outcome = "Stopped by an error";
                if (!ex.Data.Contains("qcLogged")) _log.Add("Error: " + ex.Message);
                FlushLog();
            }
            finally
            {
                runLog.Finished = DateTime.Now;
                runLog.Outcome = outcome;
                ProgressPanel.Stop(outcome);

                if (writeLog)
                {
                    try
                    {
                        runLog.Messages.AddRange(_log.Where(m => m != "Running..."));
                        string written = runLog.Write(logPath);
                        _log.Add($"QC output log written to {written}");
                    }
                    catch (Exception ex)
                    {
                        _log.Add($"Could not write the QC output log to {logPath}: {ex.Message}");
                    }
                    FlushLog();
                }

                CancelButton.Visibility = Visibility.Collapsed;
                _cts = null;
                _run = null;
                cts.Dispose();
                RunButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
            }
        }
    }
}
