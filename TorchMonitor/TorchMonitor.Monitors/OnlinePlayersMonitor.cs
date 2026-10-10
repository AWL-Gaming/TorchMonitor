using System.Collections.Generic;
using System.Linq;
using InfluxDb.Torch;
using Intervals;
using NLog;
using Sandbox.Game.World;
using TorchMonitor.Utils;
using Utils.General;
using Utils.Torch;

namespace TorchMonitor.Monitors
{
    public sealed class OnlinePlayersMonitor : IIntervalListener
    {
        const int IntervalSecs = 10;
        static readonly ILogger Log = LogManager.GetCurrentClassLogger();
        readonly NameConflictSolver<ulong> _nameConflictSolver;
        readonly PlayerOnlineTimeDb _playerOnlineTimeDb;
        readonly TorchMonitorNexus _nexus;
        readonly HashSet<string> _previousNexusSegments = new HashSet<string>();

        public OnlinePlayersMonitor(
            NameConflictSolver<ulong> nameConflictSolver,
            PlayerOnlineTimeDb playerOnlineTimeDb,
            TorchMonitorNexus nexus)
        {
            _nameConflictSolver = nameConflictSolver;
            _playerOnlineTimeDb = playerOnlineTimeDb;
            _nexus = nexus;
        }

        public void OnInterval(int intervalsSinceStart)
        {
            if (intervalsSinceStart % IntervalSecs != 0) return;

            var onlinePlayers = MySession.Static.Players.GetOnlinePlayers().ToArray();
            var factionList = MySession.Static.Factions.Factions.Values;
            var factions = new Dictionary<string, int>();

            foreach (var onlinePlayer in onlinePlayers)
            {
                if (onlinePlayer == null) continue;

                var playerId = onlinePlayer.PlayerId();
                var steamId = onlinePlayer.SteamId();
                if (steamId == 0) continue;

                _playerOnlineTimeDb.IncrementPlayerOnlineTime(steamId, (double)IntervalSecs / 3600);
                var onlineTime = _playerOnlineTimeDb.GetPlayerOnlineTime(steamId);

                var faction = factionList.FirstOrDefault(f => f.Members.ContainsKey(playerId));
                var factionTag = faction?.Tag ?? "<single>";
                factions.Increment(factionTag);

                var playerName = onlinePlayer.DisplayName;
                playerName = _nameConflictSolver.GetSafeName(playerName, steamId);

                TorchInfluxDbWriter
                    .Measurement("players_players")
                    .Tag("steam_id", $"{steamId}")
                    .Tag("player_name", playerName)
                    .Tag("faction_tag", factionTag)
                    .Field("is_online", 1)
                    .Field("online_time", onlineTime)
                    .Write();
            }

            foreach (var (factionTag, onlineMemberCount) in factions)
            {
                TorchInfluxDbWriter
                    .Measurement("players_factions")
                    .Tag("faction_tag", factionTag)
                    .Field("online_member_count", onlineMemberCount)
                    .Write();
            }

            var totalOnlineTime = _playerOnlineTimeDb.GetTotalOnlineTime();

            TorchInfluxDbWriter
                .Measurement("server")
                .Field("players", onlinePlayers.Length)
                .Field("online_time", totalOnlineTime)
                .Write();

            // nexus
            if (_nexus.IsEnabled)
            {
                var segments = CompleteNexusPopulation(_nexus.GetSegmentedPopulation(onlinePlayers));
                foreach (var (segmentName, playerCount) in segments)
                {
                    TorchInfluxDbWriter
                        .Measurement($"nexus_{segmentName}")
                        .Field("players", playerCount)
                        .Write();

                    Log.Debug($"nexus segmented pop: {segmentName} -> {playerCount}");
                }
            }

            _playerOnlineTimeDb.WriteToDb();
        }

        internal IReadOnlyDictionary<string, int> CompleteNexusPopulation(IReadOnlyDictionary<string, int> current)
        {
            var counts = new Dictionary<string, int>();
            foreach (var pair in current) counts.Add(pair.Key, pair.Value);
            foreach (var previous in _previousNexusSegments)
                if (!counts.ContainsKey(previous)) counts.Add(previous, 0);

            _previousNexusSegments.Clear();
            foreach (var pair in current) _previousNexusSegments.Add(pair.Key);
            return counts;
        }
    }
}
