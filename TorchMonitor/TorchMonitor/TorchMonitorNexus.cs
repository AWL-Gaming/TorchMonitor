using System;
using System.Collections.Generic;
using Sandbox.Game.World;
using Utils.General;
using Utils.Torch;
using VRageMath;

namespace TorchMonitor
{
    public sealed class TorchMonitorNexus
    {
        public interface IConfig
        {
            bool EnableNexusFeature { get; }
            Vector3D NexusOriginPosition { get; }
            double NexusSectorDiameter { get; }
            int NexusSegmentationCount { get; }
            string NexusPrefix { get; }
        }

        readonly IConfig _config;

        public TorchMonitorNexus(IConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public bool IsEnabled => _config.EnableNexusFeature && HasValidGeometry();

        static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        static bool HasValidRange(double origin, double halfDiameter)
        {
            var min = origin - halfDiameter;
            var max = origin + halfDiameter;
            return IsFinite(min) && IsFinite(max) && max > min && IsFinite(max - min);
        }

        bool HasValidGeometry()
        {
            var origin = _config.NexusOriginPosition;
            var halfDiameter = _config.NexusSectorDiameter / 2;
            return _config.NexusSegmentationCount > 0 && _config.NexusSectorDiameter > 0 &&
                   IsFinite(_config.NexusSectorDiameter) && IsFinite(origin.X) && IsFinite(origin.Y) && IsFinite(origin.Z) &&
                   HasValidRange(origin.X, halfDiameter) && HasValidRange(origin.Y, halfDiameter) && HasValidRange(origin.Z, halfDiameter);
        }

        void ValidateGeometry()
        {
            if (!HasValidGeometry()) throw new ArgumentException("Nexus geometry requires finite coordinates, a positive diameter and a positive segmentation count.");
        }

        public IReadOnlyDictionary<string, int> GetSegmentedPopulation(IEnumerable<MyPlayer> players)
        {
            ValidateGeometry();
            var segmentPlayerCounts = new Dictionary<string, int>();
            if (players == null) return segmentPlayerCounts;
            foreach (var onlinePlayer in players)
            {
                if (!(onlinePlayer?.Character is { } character)) continue;

                var characterPos = character.WorldMatrix.Translation;
                if (!IsFinite(characterPos.X) || !IsFinite(characterPos.Y) || !IsFinite(characterPos.Z)) continue;
                var segmentName = GetNexusSegmentName(characterPos);
                segmentPlayerCounts.Increment(segmentName);
            }

            return segmentPlayerCounts;
        }

        public IReadOnlyList<Vector3D> GetCorners()
        {
            ValidateGeometry();
            var corners = new List<Vector3D>();
            var (minPos3, maxPos3) = GetMinMaxPositions();
            for (var x = 0; x < 2; x++)
            for (var y = 0; y < 2; y++)
            for (var z = 0; z < 2; z++)
            {
                var corner = new Vector3D
                {
                    X = x == 0 ? minPos3.X : maxPos3.X,
                    Y = y == 0 ? minPos3.Y : maxPos3.Y,
                    Z = z == 0 ? minPos3.Z : maxPos3.Z,
                };

                corners.Add(corner);
            }

            return corners;
        }

        public IReadOnlyList<(Vector3I, Vector3D)> GetCenters()
        {
            ValidateGeometry();
            var centers = new List<(Vector3I, Vector3D)>();
            var originPos3 = _config.NexusOriginPosition;
            var sectorDiameter = _config.NexusSectorDiameter;
            var segmentationCount = _config.NexusSegmentationCount;
            try { _ = checked(segmentationCount * segmentationCount * segmentationCount); }
            catch (OverflowException) { throw new ArgumentOutOfRangeException(nameof(segmentationCount), "The requested centers cannot fit in a list."); }
            for (var x = 0; x < segmentationCount; x++)
            for (var y = 0; y < segmentationCount; y++)
            for (var z = 0; z < segmentationCount; z++)
            {
                var segmentIndex3 = new Vector3I(x, y, z);
                var center3 = new Vector3D();
                for (var i = 0; i < 3; i++)
                {
                    var originPos = originPos3.GetValueAtIndex(i);
                    var originOffset = originPos - sectorDiameter / 2;
                    var segmentIndex = segmentIndex3.GetValueAtIndex(i);
                    var centerPos = (segmentIndex + 0.5) / segmentationCount * sectorDiameter + originOffset;
                    center3.SetValueAtIndex(i, centerPos);
                }

                centers.Add((segmentIndex3, center3));
            }

            return centers;
        }

        string GetNexusSegmentName(Vector3D position3)
        {
            var (minPos3, maxPos3) = GetMinMaxPositions();
            var segmentPos3 = new Vector3I();
            for (var i = 0; i < 3; i++)
            {
                var pos = position3.GetValueAtIndex(i);
                var minPos = minPos3.GetValueAtIndex(i);
                var maxPos = maxPos3.GetValueAtIndex(i);
                var resultPos = MathUtils.Remap(minPos, maxPos, 0, _config.NexusSegmentationCount, pos);
                resultPos = MathUtils.Clamp(resultPos, 0, _config.NexusSegmentationCount - 1);
                segmentPos3.SetValueAtIndex(i, (int)resultPos);
            }

            return $"{_config.NexusPrefix}{segmentPos3.X}_{segmentPos3.Y}_{segmentPos3.Z}";
        }

        (Vector3D, Vector3D) GetMinMaxPositions()
        {
            var minPos3 = new Vector3D();
            var maxPos3 = new Vector3D();
            var originPos3 = _config.NexusOriginPosition;
            for (var i = 0; i < 3; i++)
            {
                var originPos = originPos3.GetValueAtIndex(i);
                var minPos = originPos - _config.NexusSectorDiameter / 2;
                var maxPos = originPos + _config.NexusSectorDiameter / 2;

                minPos3.SetValueAtIndex(i, minPos);
                maxPos3.SetValueAtIndex(i, maxPos);
            }

            return (minPos3, maxPos3);
        }
    }
}
