using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using TorchMonitor;
using TorchMonitor.Monitors;
using VRageMath;

static class Bootstrap
{
    static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) => {
            string name = new AssemblyName(request.Name).Name + ".dll";
            foreach (string dir in new[] { @"TorchMonitor\bin\Release", "TorchBinaries", "GameBinaries", "extern" }) {
                string path = Path.Combine(root, dir, name);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        return NativeCases.Run();
    }
}

static class NativeCases
{
    sealed class Config : TorchMonitorNexus.IConfig
    {
        public bool EnableNexusFeature { get; set; } = true;
        public Vector3D NexusOriginPosition { get; set; }
        public double NexusSectorDiameter { get; set; } = 30;
        public int NexusSegmentationCount { get; set; } = 3;
        public string NexusPrefix { get; set; } = "cell_";
    }
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); checks++; }
    static void Throws<T>(Action action, string name) where T : Exception {
        try { action(); } catch (T) { checks++; return; }
        throw new Exception("FAIL: " + name);
    }
    static bool Near(Vector3D left, Vector3D right) => Vector3D.DistanceSquared(left, right) < 1e-12;
    static string Segment(TorchMonitorNexus nexus, Vector3D point) => (string)typeof(TorchMonitorNexus)
        .GetMethod("GetNexusSegmentName", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(nexus, new object[] { point });

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run()
    {
        foreach (Vector3D origin in new[] { Vector3D.Zero, new Vector3D(1000,2000,3000), new Vector3D(-1000,-2000,-3000), new Vector3D(-1000,2000,-3000) }) {
            var cfg = new Config { NexusOriginPosition = origin };
            var nexus = new TorchMonitorNexus(cfg);
            var corners = nexus.GetCorners();
            Check(corners.Count == 8, "Eight corners");
            Check(Near(corners[0], origin - new Vector3D(15)), "Translated minimum corner");
            Check(Near(corners[7], origin + new Vector3D(15)), "Translated maximum corner");
            var centers = nexus.GetCenters();
            Check(centers.Count == 27, "Twenty-seven centers");
            Check(Near(centers[0].Item2, origin - new Vector3D(10)), "First translated center");
            Check(Near(centers[26].Item2, origin + new Vector3D(10)), "Last translated center");
            Check(Segment(nexus, origin) == "cell_1_1_1", "Origin maps to middle cell");
            Check(Segment(nexus, origin - new Vector3D(15)) == "cell_0_0_0", "Minimum boundary");
            Check(Segment(nexus, origin + new Vector3D(15)) == "cell_2_2_2", "Maximum boundary");
            Check(Segment(nexus, origin - new Vector3D(1000)) == "cell_0_0_0", "Low outside positions clamp");
            Check(Segment(nexus, origin + new Vector3D(1000)) == "cell_2_2_2", "High outside positions clamp");
            Check(nexus.GetSegmentedPopulation(null).Count == 0, "Null population is empty");
        }
        var disabled = new Config { EnableNexusFeature = false };
        Check(!new TorchMonitorNexus(disabled).IsEnabled, "Optional feature remains disabled");
        foreach (double diameter in new[] { 0d, -1d, double.NaN, double.PositiveInfinity }) {
            var nexus = new TorchMonitorNexus(new Config { NexusSectorDiameter = diameter });
            Check(!nexus.IsEnabled, "Invalid diameter disables telemetry");
            Throws<ArgumentException>(() => nexus.GetCorners(), "Invalid diameter fails before calculation");
        }
        foreach (int count in new[] { 0, -1 }) {
            var nexus = new TorchMonitorNexus(new Config { NexusSegmentationCount = count });
            Check(!nexus.IsEnabled, "Invalid count disables telemetry");
            Throws<ArgumentException>(() => nexus.GetCenters(), "Invalid count fails before enumeration");
        }
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity }) {
            var nexus = new TorchMonitorNexus(new Config { NexusOriginPosition = new Vector3D(value, 0, 0) });
            Check(!nexus.IsEnabled, "Nonfinite origin disables telemetry");
            Throws<ArgumentException>(() => nexus.GetCorners(), "Nonfinite origin rejected");
        }
        Throws<ArgumentNullException>(() => new TorchMonitorNexus(null), "Null config rejected");
        foreach (var cfg in new[] {
            new Config { NexusOriginPosition = new Vector3D(double.MaxValue,0,0), NexusSectorDiameter = double.MaxValue },
            new Config { NexusOriginPosition = new Vector3D(double.MaxValue,0,0), NexusSectorDiameter = 30 },
            new Config { NexusSectorDiameter = double.Epsilon }
        }) {
            var nexus = new TorchMonitorNexus(cfg);
            Check(!nexus.IsEnabled, "Nonrepresentable bounds disable telemetry");
            Throws<ArgumentException>(() => nexus.GetCorners(), "Nonrepresentable bounds fail before calculation");
        }
        var huge = new TorchMonitorNexus(new Config { NexusSegmentationCount = int.MaxValue });
        Throws<ArgumentOutOfRangeException>(() => huge.GetCenters(), "Unrepresentable center count rejected before allocation");
        var monitor = new OnlinePlayersMonitor(null, null, new TorchMonitorNexus(new Config()));
        var completeMethod = typeof(OnlinePlayersMonitor).GetMethod("CompleteNexusPopulation", BindingFlags.NonPublic | BindingFlags.Instance);
        Func<IReadOnlyDictionary<string, int>, IReadOnlyDictionary<string, int>> complete = current =>
            (IReadOnlyDictionary<string, int>)completeMethod.Invoke(monitor, new object[] { current });
        var first = new Dictionary<string, int> { ["cell_1_1_1"] = 2, ["cell_0_0_0"] = 1 };
        var firstResult = complete(first);
        Check(firstResult.Count == 2, "First population reports occupied segments");
        Check(firstResult["cell_1_1_1"] == 2, "First population count preserved");
        Check(firstResult["cell_0_0_0"] == 1, "Second occupied segment preserved");
        var moved = new Dictionary<string, int> { ["cell_1_1_1"] = 1, ["cell_2_2_2"] = 3 };
        var movedResult = complete(moved);
        Check(movedResult.Count == 3, "Move reports current and emptied segments");
        Check(movedResult["cell_1_1_1"] == 1, "Remaining population updates");
        Check(movedResult["cell_2_2_2"] == 3, "New segment population reports");
        Check(movedResult["cell_0_0_0"] == 0, "Emptied segment resets to zero");
        Check(moved.Count == 2 && first.Count == 2, "Telemetry completion preserves caller dictionaries");
        var departed = complete(new Dictionary<string, int>());
        Check(departed.Count == 2, "All players leaving emits both remaining segments");
        Check(departed["cell_1_1_1"] == 0, "Last central player leaving resets count");
        Check(departed["cell_2_2_2"] == 0, "Last outer players leaving reset count");
        Check(complete(new Dictionary<string, int>()).Count == 0, "Already emptied segments are released");
        var renamed = complete(new Dictionary<string, int> { ["new_prefix_1_1_1"] = 1 });
        Check(renamed.Count == 1 && renamed["new_prefix_1_1_1"] == 1, "New prefix reports without stale counts");
        var independent = new OnlinePlayersMonitor(null, null, new TorchMonitorNexus(new Config()));
        Check(((IReadOnlyDictionary<string, int>)completeMethod.Invoke(independent, new object[] { new Dictionary<string, int>() })).Count == 0,
            "Population history stays local to each monitor");
        Console.WriteLine("PASS: " + checks + " actual compiled Nexus cases using native server math; no world or player objects created");
        return 0;
    }
}
