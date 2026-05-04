using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindRoadPatrolsV2", "Devi8d0ne", "2.1.0")]
    [Description("WorldMind V2 Living Roads: roaming NPC soldier squads, route heat, radio chatter, reinforcements, wanted levels, boss phases, scanner intel, heavy response, memory, and WorldMind hooks.")]
    public class WorldMindRoadPatrolsV2 : RustPlugin
    {
        private const string MadeWithLoveTag = "Made with love by Deviated Systems";

        private const string DV8DAsciiTag = @"
DDDDDDDD      VV        VV     88888888      DDDDDDDD
DDDDDDDDD     VV        VV    8888888888     DDDDDDDDD
DD     DDD    VV        VV    88      88     DD     DDD
DD      DD    VV        VV    88      88     DD      DD
DD      DD     VV      VV      88888888      DD      DD
DD      DD     VV      VV     8888888888     DD      DD
DD      DD      VV    VV      88      88     DD      DD
DD     DDD       VV  VV       88      88     DD     DDD
DDDDDDDDD         VVVV        8888888888     DDDDDDDDD
DDDDDDDD           VV          88888888      DDDDDDDD

Made with love by Deviated Systems
Author: Devi8d0ne
";

        [PluginReference] private Plugin WorldMindCoreV2;
        [PluginReference] private Plugin NpcSpawn;
        [PluginReference] private Plugin ServerRewards;
        [PluginReference] private Plugin Economics;

        private PluginConfig _config;
        private StoredData _data;
        private readonly Dictionary<ulong, ActiveNpc> _activeNpcs = new Dictionary<ulong, ActiveNpc>();
        private readonly Dictionary<string, ActiveSquad> _activeSquads = new Dictionary<string, ActiveSquad>();
        private readonly Dictionary<ulong, RouteRecorder> _recorders = new Dictionary<ulong, RouteRecorder>();
        private readonly Dictionary<ulong, string> _ownedEventEntities = new Dictionary<ulong, string>();
        private Timer _spawnTimer;
        private Timer _thinkTimer;
        private Timer _radioTimer;
        private System.Random _rng;
        private float _lastHunterSweepAt;
        private float _lastWipeScaleCheckAt;

        private const string PermAdmin = "worldmindroadpatrolsv2.admin";
        private const string PermReinforce = "worldmindroadpatrolsv2.reinforce";
        private const string PermScannerBasic = "worldmindroadpatrolsv2.scanner.basic";
        private const string PermScannerAdvanced = "worldmindroadpatrolsv2.scanner.advanced";
        private const string PermScannerAdmin = "worldmindroadpatrolsv2.scanner.admin";

        private readonly Dictionary<ulong, float> _reinforceCooldowns = new Dictionary<ulong, float>();
        private float _lastWantedDecayAt;

        #region Oxide Hooks

        private void Init()
        {
            _rng = new System.Random();
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermReinforce, this);
            permission.RegisterPermission(PermScannerBasic, this);
            permission.RegisterPermission(PermScannerAdvanced, this);
            permission.RegisterPermission(PermScannerAdmin, this);
            LoadConfigValues();
            LoadData();
            cmd.AddChatCommand(_config.Commands.MainCommand, this, nameof(CommandRoadPatrol));
            cmd.AddChatCommand(_config.Commands.ReinforceCommand, this, nameof(CommandReinforce));
            cmd.AddChatCommand(_config.Commands.ScannerCommand, this, nameof(CommandPatrolScanner));
        }

        private void OnServerInitialized()
        {
            StartTimers();
            Puts(DV8DAsciiTag);
            PrintWarning($"{Name} v{Version} loaded. {MadeWithLoveTag}");
        }

        private void Unload()
        {
            _spawnTimer?.Destroy();
            _thinkTimer?.Destroy();
            _radioTimer?.Destroy();

            if (_config.PatrolSettings.CleanupOnPluginUnload)
                CleanupAllSquads();

            SaveData();
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || entity.net == null) return;
            ulong id = entity.net.ID.Value;
            ActiveNpc npc;
            if (!_activeNpcs.TryGetValue(id, out npc)) return;

            BasePlayer attacker = info?.InitiatorPlayer;
            if (attacker != null && attacker.userID.IsSteamId())
            {
                RecordPlayerKill(attacker, npc);
                if (!npc.IsFriendly) AddWanted(attacker, npc.IsBoss ? _config.WantedSystem.BossKillWantedPoints : _config.WantedSystem.NpcKillWantedPoints, npc.IsBoss ? "boss kill" : "patrol NPC kill");
                else
                {
                    AddWanted(attacker, _config.WantedSystem.FriendlyKillWantedPoints, "friendly reinforcement kill");
                    AddSupportTrust(attacker.userID, attacker.displayName, -_config.PlayerReinforcements.TrustLossFriendlyKill, "friendly reinforcement kill");
                }
            }

            ActiveSquad squad;
            if (_activeSquads.TryGetValue(npc.SquadId, out squad))
            {
                squad.NpcIds.Remove(id);
                squad.LastCombatTime = Time.realtimeSinceStartup;
                if (attacker != null) squad.LastKnownEnemyPosition = attacker.transform.position;

                if (!npc.IsFriendly) AddRouteHeat(squad.RouteName, npc.IsBoss ? _config.RouteHeat.BossNpcKilledHeat : _config.RouteHeat.NpcKilledHeat, npc.IsBoss ? "boss killed" : "patrol NPC killed");

                if (squad.NpcIds.Count <= 0)
                    CompleteSquad(squad, attacker, "squad_destroyed");
            }

            _activeNpcs.Remove(id);
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || entity.net == null) return;
            ulong id = entity.net.ID.Value;
            ActiveNpc npc;
            if (!_activeNpcs.TryGetValue(id, out npc)) return;

            ActiveSquad squad;
            if (!_activeSquads.TryGetValue(npc.SquadId, out squad)) return;

            squad.InCombat = true;
            squad.LastCombatTime = Time.realtimeSinceStartup;
            if (info?.InitiatorPlayer != null)
            {
                squad.LastKnownEnemyPosition = info.InitiatorPlayer.transform.position;
                squad.LastAttackerName = info.InitiatorPlayer.displayName;
                if (squad.Friendly && _config.WantedSystem.Enabled)
                    AddWanted(info.InitiatorPlayer, Mathf.Max(1, _config.WantedSystem.FriendlyDamageWantedPoints), "friendly fire");
            }

            AddRouteHeat(squad.RouteName, _config.RouteHeat.CombatHeat, "patrol combat");
            MaybeCallHeavyResponse(squad);
            ProcessBossPhases(squad);
        }

        #endregion

        #region Commands

        private void CommandRoadPatrol(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!HasAdmin(player))
            {
                Reply(player, "No permission.");
                return;
            }

            if (args == null || args.Length == 0)
            {
                SendHelp(player);
                return;
            }

            string sub = args[0].ToLowerInvariant();

            if (sub == "start")
            {
                _config.PatrolSettings.EnableRoadPatrols = true;
                SaveConfig();
                StartTimers();
                Reply(player, "Road patrols enabled.");
                return;
            }

            if (sub == "stop")
            {
                _config.PatrolSettings.EnableRoadPatrols = false;
                SaveConfig();
                _spawnTimer?.Destroy();
                Reply(player, "Road patrols disabled. Existing squads remain unless cleaned up.");
                return;
            }

            if (sub == "status")
            {
                Reply(player, $"Active squads: {_activeSquads.Count}/{_config.PatrolSettings.MaxActiveSquads}. Routes: {_data.Routes.Count}. Enabled: {_config.PatrolSettings.EnableRoadPatrols}.");
                foreach (var squad in _activeSquads.Values)
                    Reply(player, $"- {squad.SquadName} [{squad.SquadType}] route={squad.RouteName}, heat={GetRouteHeat(squad.RouteName)}, alive={squad.NpcIds.Count}, wp={squad.CurrentWaypointIndex + 1}/{squad.Waypoints.Count}, state={squad.State}, combat={squad.InCombat}");
                return;
            }

            if (sub == "cleanup")
            {
                CleanupAllSquads();
                Reply(player, "All active patrol squads cleaned up.");
                return;
            }

            if (sub == "spawn")
            {
                string type = args.Length >= 2 ? args[1].ToLowerInvariant() : "random";
                bool ok = SpawnRandomSquad(type, player.transform.position);
                Reply(player, ok ? $"Spawned patrol squad type: {type}." : "Could not spawn patrol. Add at least one route with 2+ waypoints first.");
                return;
            }

            if (sub == "route")
            {
                HandleRouteCommand(player, args.Skip(1).ToArray());
                return;
            }

            if (sub == "recall")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmrp recall <squad id/name/all>");
                    return;
                }

                string query = string.Join(" ", args.Skip(1).ToArray()).Trim();
                int removed = RecallSquads(query);
                Reply(player, $"Recalled {removed} squad(s).");
                return;
            }

            if (sub == "nearest")
            {
                PatrolRoute nearest = PickRoute(player.transform.position);
                Reply(player, nearest == null ? "No route found." : $"Nearest route: {nearest.Name} ({nearest.Waypoints.Count} waypoints, {nearest.Biome}).");
                return;
            }

            if (sub == "event")
            {
                string type = args.Length >= 2 ? args[1].ToLowerInvariant() : "random";
                bool ok = SpawnRandomSquad(type, player.transform.position);
                Reply(player, ok ? $"Forced patrol event: {type}." : "Could not start event. Add at least one enabled route with 2+ waypoints first.");
                return;
            }


            if (sub == "boss")
            {
                bool ok = SpawnRandomSquad("boss", player.transform.position);
                Reply(player, ok ? "Boss patrol deployed." : "Could not deploy boss patrol. Add at least one route first.");
                return;
            }

            if (sub == "wanted")
            {
                HandleWantedCommand(player, args.Skip(1).ToArray());
                return;
            }

            if (sub == "heat")
            {
                HandleHeatCommand(player, args.Skip(1).ToArray());
                return;
            }

            if (sub == "scanner" || sub == "patrols")
            {
                SendScanner(player, HasAdmin(player) || permission.UserHasPermission(player.UserIDString, PermScannerAdmin) ? 3 : 2);
                return;
            }

            if (sub == "debug")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Debug: routes | squads | ai");
                    return;
                }

                string mode = args[1].ToLowerInvariant();
                if (mode == "routes")
                {
                    foreach (var route in _data.Routes)
                        Reply(player, $"Route {route.Name}: enabled={route.Enabled}, biome={route.Biome}, waypoints={route.Waypoints.Count}");
                    return;
                }

                if (mode == "squads")
                {
                    foreach (var squad in _activeSquads.Values)
                        Reply(player, JsonConvert.SerializeObject(squad, Formatting.Indented));
                    return;
                }

                if (mode == "ai")
                {
                    Reply(player, $"WorldMindCoreV2 present: {WorldMindCoreV2 != null}. NpcSpawn present: {NpcSpawn != null}.");
                    return;
                }
            }

            SendHelp(player);
        }

        private void HandleRouteCommand(BasePlayer player, string[] args)
        {
            if (args == null || args.Length == 0)
            {
                Reply(player, "Route commands: start <name>, add, finish, cancel, list, delete <name>, show <name>, enable <name>, disable <name>, sample <name> [radius] [points]");
                return;
            }

            string sub = args[0].ToLowerInvariant();

            if (sub == "start")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmrp route start <name>");
                    return;
                }

                string name = string.Join(" ", args.Skip(1).ToArray()).Trim();
                _recorders[player.userID] = new RouteRecorder { Name = name, Points = new List<Vector3>() };
                Reply(player, $"Recording route '{name}'. Use /wmrp route add to add waypoints, then /wmrp route finish.");
                return;
            }

            if (sub == "add")
            {
                RouteRecorder recorder;
                if (!_recorders.TryGetValue(player.userID, out recorder))
                {
                    Reply(player, "You are not recording a route. Use /wmrp route start <name>.");
                    return;
                }

                recorder.Points.Add(player.transform.position);
                Reply(player, $"Added waypoint #{recorder.Points.Count} at {FormatVector(player.transform.position)}.");
                return;
            }

            if (sub == "finish")
            {
                RouteRecorder recorder;
                if (!_recorders.TryGetValue(player.userID, out recorder))
                {
                    Reply(player, "You are not recording a route.");
                    return;
                }

                if (recorder.Points.Count < 2)
                {
                    Reply(player, "Route needs at least 2 waypoints.");
                    return;
                }

                PatrolRoute existing = _data.Routes.FirstOrDefault(r => r.Name.Equals(recorder.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null) _data.Routes.Remove(existing);

                _data.Routes.Add(new PatrolRoute
                {
                    Name = recorder.Name,
                    Enabled = true,
                    Biome = GuessBiome(player.transform.position),
                    Waypoints = recorder.Points.Select(VectorDto.FromVector3).ToList()
                });

                _recorders.Remove(player.userID);
                SaveData();
                Reply(player, $"Saved route '{recorder.Name}' with {recorder.Points.Count} waypoints.");
                return;
            }

            if (sub == "cancel")
            {
                _recorders.Remove(player.userID);
                Reply(player, "Route recording cancelled.");
                return;
            }

            if (sub == "list")
            {
                if (_data.Routes.Count == 0)
                {
                    Reply(player, "No routes saved.");
                    return;
                }

                foreach (var route in _data.Routes)
                    Reply(player, $"- {route.Name}: enabled={route.Enabled}, biome={route.Biome}, waypoints={route.Waypoints.Count}");
                return;
            }

            if (sub == "delete")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmrp route delete <name>");
                    return;
                }

                string name = string.Join(" ", args.Skip(1).ToArray()).Trim();
                PatrolRoute route = _data.Routes.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (route == null)
                {
                    Reply(player, "Route not found.");
                    return;
                }

                _data.Routes.Remove(route);
                SaveData();
                Reply(player, $"Deleted route '{route.Name}'.");
                return;
            }

            if (sub == "enable" || sub == "disable")
            {
                if (args.Length < 2)
                {
                    Reply(player, $"Usage: /wmrp route {sub} <name>");
                    return;
                }

                string name = string.Join(" ", args.Skip(1).ToArray()).Trim();
                PatrolRoute route = _data.Routes.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (route == null)
                {
                    Reply(player, "Route not found.");
                    return;
                }

                route.Enabled = sub == "enable";
                SaveData();
                Reply(player, $"Route '{route.Name}' enabled={route.Enabled}.");
                return;
            }

            if (sub == "sample")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmrp route sample <name> [radius] [points]");
                    return;
                }

                string name = args[1];
                float radius = args.Length >= 3 ? ParseFloat(args[2], 125f) : 125f;
                int points = args.Length >= 4 ? Mathf.Clamp(ParseInt(args[3], 8), 4, 32) : 8;
                CreateSampleRoute(player, name, radius, points);
                return;
            }

            if (sub == "show")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmrp route show <name>");
                    return;
                }

                string name = string.Join(" ", args.Skip(1).ToArray()).Trim();
                PatrolRoute route = _data.Routes.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (route == null)
                {
                    Reply(player, "Route not found.");
                    return;
                }

                Reply(player, $"Route '{route.Name}' waypoints:");
                for (int i = 0; i < route.Waypoints.Count; i++)
                    Reply(player, $"#{i + 1}: {route.Waypoints[i]}");
                return;
            }

            Reply(player, "Route commands: start <name>, add, finish, cancel, list, delete <name>, show <name>, enable <name>, disable <name>, sample <name> [radius] [points]");
        }

        private void SendHelp(BasePlayer player)
        {
            Reply(player, "WorldMind Road Patrols V2 commands:");
            Reply(player, "/wmrp start | stop | status | cleanup | nearest | recall <all/name/id>");
            Reply(player, "/wmrp spawn random|checkpoint|courier|scientist|bandit|hunter|ambush|roadblock|boss|heavy");
            Reply(player, "/wmrp event random|courier|ambush|roadblock|boss|heavy");
            Reply(player, "/wmrp boss | wanted | wanted <player> | wanted clear <player> | wanted set <player> <level>");
            Reply(player, "/wmrp heat | heat clear <route> | heat set <route> <0-100>");
            Reply(player, $"/{_config.Commands.ReinforceCommand} squad|medic|overwatch|emergency | /{_config.Commands.ScannerCommand}");
            Reply(player, "/wmrp route start <name> | add | finish | cancel | list | delete <name> | show <name>");
            Reply(player, "/wmrp debug routes|squads|ai");
        }



        private void CommandReinforce(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!_config.PlayerReinforcements.Enabled)
            {
                Reply(player, "Player reinforcements are disabled.");
                return;
            }

            if (_config.PlayerReinforcements.RequirePermission && !permission.UserHasPermission(player.UserIDString, PermReinforce) && !HasAdmin(player))
            {
                Reply(player, "No reinforcement permission.");
                return;
            }

            string type = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "squad";
            if (type == "cancel")
            {
                int removed = RecallFriendlySquads(player.userID);
                Reply(player, $"Cancelled {removed} friendly reinforcement squad(s).");
                return;
            }

            if (GetWantedLevel(player.userID) >= _config.PlayerReinforcements.DenyAtWantedLevel)
            {
                Reply(player, "Reinforcement denied. Your wanted level is too hot.");
                AddSupportTrust(player.userID, player.displayName, -_config.PlayerReinforcements.TrustLossDeniedWanted, "wanted denial");
                if (_config.PlayerReinforcements.WantedDenialCanTriggerHunter)
                    SpawnHunterNearPlayer(player);
                return;
            }

            if (GetSupportTrust(player.userID) <= _config.PlayerReinforcements.MinimumTrustBeforeDenial)
            {
                Reply(player, "Reinforcement denied. Support command does not trust your calls yet.");
                return;
            }

            float now = Time.realtimeSinceStartup;
            float next;
            if (_reinforceCooldowns.TryGetValue(player.userID, out next) && now < next)
            {
                Reply(player, $"Reinforcements cooling down. Ready in {Mathf.CeilToInt(next - now)}s.");
                return;
            }

            if (_config.PlayerReinforcements.OnlyDuringActivePatrolCombat && !IsNearActivePatrolCombat(player.transform.position, _config.PlayerReinforcements.CombatCheckRange))
            {
                Reply(player, "No active patrol combat nearby. Reinforcements are reserved for real contact.");
                return;
            }

            if (CountFriendlySquads(player.userID) >= _config.PlayerReinforcements.MaxActiveFriendlySquadsPerPlayer)
            {
                Reply(player, "You already have active reinforcement support.");
                return;
            }

            ReinforcementProfile rp = _config.PlayerReinforcements.GetProfile(type);
            if (rp == null)
            {
                Reply(player, "Usage: /reinforce squad|medic|overwatch|emergency|cancel");
                return;
            }

            if (!TryChargeReinforcement(player, rp)) return;
            bool ok = SpawnFriendlyReinforcement(player, rp);
            if (!ok)
            {
                Reply(player, "Could not deploy reinforcements at your position.");
                return;
            }

            _reinforceCooldowns[player.userID] = now + Mathf.Max(1f, rp.CooldownSeconds);
            Reply(player, $"Reinforcements inbound: {rp.DisplayName}.");
        }

        private void CommandPatrolScanner(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!_config.PatrolScanner.Enabled)
            {
                Reply(player, "Patrol scanner is disabled.");
                return;
            }

            int level = 0;
            if (HasAdmin(player) || permission.UserHasPermission(player.UserIDString, PermScannerAdmin)) level = 3;
            else if (permission.UserHasPermission(player.UserIDString, PermScannerAdvanced)) level = 2;
            else if (permission.UserHasPermission(player.UserIDString, PermScannerBasic) || !_config.PatrolScanner.RequirePermissionForBasic) level = 1;

            if (level <= 0)
            {
                Reply(player, "No scanner permission.");
                return;
            }

            SendScanner(player, level);
        }

        private void HandleWantedCommand(BasePlayer player, string[] args)
        {
            if (args == null || args.Length == 0)
            {
                Reply(player, "Wanted players:");
                foreach (var pair in _data.Wanted.OrderByDescending(x => x.Value.Score).Take(10))
                    Reply(player, $"- {pair.Value.DisplayName}: level {GetWantedLevel(pair.Key)} score {pair.Value.Score}");
                return;
            }

            string action = args[0].ToLowerInvariant();
            if (action == "clear" && args.Length >= 2)
            {
                BasePlayer target = FindPlayerByNameOrId(string.Join(" ", args.Skip(1).ToArray()));
                if (target == null) { Reply(player, "Player not found online."); return; }
                _data.Wanted.Remove(target.userID.ToString());
                SaveData();
                Reply(player, $"Wanted cleared for {target.displayName}.");
                return;
            }

            if (action == "set" && args.Length >= 3)
            {
                BasePlayer target = FindPlayerByNameOrId(args[1]);
                if (target == null) { Reply(player, "Player not found online."); return; }
                int level = Mathf.Clamp(ParseInt(args[2], 0), 0, 5);
                WantedRecord rec = GetWantedRecord(target.userID, target.displayName);
                rec.Score = LevelToWantedScore(level);
                rec.LastChangedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                SaveData();
                Reply(player, $"Wanted set for {target.displayName}: level {level}, score {rec.Score}.");
                return;
            }

            BasePlayer p = FindPlayerByNameOrId(string.Join(" ", args));
            if (p == null) { Reply(player, "Player not found online."); return; }
            WantedRecord wr = GetWantedRecord(p.userID, p.displayName);
            Reply(player, $"{p.displayName}: wanted level {GetWantedLevel(p.userID)}, score {wr.Score}, last reason: {wr.LastReason}");
        }

        #endregion

        #region Timers / Runtime

        private void StartTimers()
        {
            _thinkTimer?.Destroy();
            _thinkTimer = timer.Every(Mathf.Max(1f, _config.PatrolSettings.ThinkIntervalSeconds), PatrolThink);

            _spawnTimer?.Destroy();
            if (_config.PatrolSettings.EnableRoadPatrols)
                _spawnTimer = timer.Every(Mathf.Max(30f, _config.PatrolSettings.SecondsBetweenPatrolSpawns), TryAutoSpawnPatrol);

            _radioTimer?.Destroy();
            if (_config.PatrolRadio.Enabled)
                _radioTimer = timer.Every(Mathf.Max(30f, _config.PatrolRadio.SecondsBetweenChatter), BroadcastRadioChatter);
        }

        private void TryAutoSpawnPatrol()
        {
            if (!_config.PatrolSettings.EnableRoadPatrols) return;
            if (BasePlayer.activePlayerList.Count < _config.PatrolSettings.MinimumOnlinePlayers) return;
            if (_activeSquads.Count >= _config.PatrolSettings.MaxActiveSquads) return;

            string type = PickAutoSpawnType();
            SpawnRandomSquad(type, Vector3.zero);
        }

        private void PatrolThink()
        {
            DecayWantedIfNeeded();
            DecayRouteHeatIfNeeded();
            TrySmartWantedHunterSweep();
            CheckWipeScalingNotice();
            if (_activeSquads.Count == 0) return;

            float now = Time.realtimeSinceStartup;
            var squadIds = _activeSquads.Keys.ToList();
            foreach (string squadId in squadIds)
            {
                ActiveSquad squad;
                if (!_activeSquads.TryGetValue(squadId, out squad)) continue;

                if (now - squad.SpawnedAt > _config.PatrolSettings.DespawnSquadAfterMinutes * 60f)
                {
                    CompleteSquad(squad, null, "despawn_timeout");
                    continue;
                }

                if (squad.NpcIds.Count == 0)
                {
                    CompleteSquad(squad, null, "no_npcs_remaining");
                    continue;
                }

                if (squad.InCombat && now - squad.LastCombatTime > _config.SquadSettings.SecondsAfterCombatBeforeReturning)
                    squad.InCombat = false;
                    squad.State = squad.Friendly ? "Supporting" : "ReturningToRoute";

                UpdateSquadMorale(squad);
                ProcessBossPhases(squad);
                MoveSquad(squad);
                AlertNearbyPlayers(squad);
            }
        }

        private void MoveSquad(ActiveSquad squad)
        {
            if (squad.Waypoints == null || squad.Waypoints.Count < 2) return;

            Vector3 target = squad.Waypoints[squad.CurrentWaypointIndex];
            Vector3 center = GetSquadCenter(squad);
            if (center == Vector3.zero) return;

            if (Vector3.Distance(center, target) <= _config.RouteSettings.WaypointReachDistance)
            {
                squad.CurrentWaypointIndex++;
                if (squad.CurrentWaypointIndex >= squad.Waypoints.Count)
                {
                    if (squad.LoopRoute) squad.CurrentWaypointIndex = 0;
                    else
                    {
                        CompleteSquad(squad, null, "route_completed");
                        return;
                    }
                }

                target = squad.Waypoints[squad.CurrentWaypointIndex];
            }

            BasePlayer enemy = squad.Friendly ? FindNearestHostileNpc(center, _config.SquadSettings.EngageRange) : FindNearestEnemy(center, _config.SquadSettings.EngageRange);
            if (enemy != null)
            {
                squad.InCombat = true;
                squad.State = "Engaging";
                squad.LastCombatTime = Time.realtimeSinceStartup;
                squad.LastKnownEnemyPosition = enemy.transform.position;
                target = enemy.transform.position;
            }
            else if (squad.InCombat && squad.LastKnownEnemyPosition != Vector3.zero)
            {
                target = squad.LastKnownEnemyPosition;
            }

            int index = 0;
            foreach (ulong npcId in squad.NpcIds.ToList())
            {
                BasePlayer npc = FindNpc(npcId);
                if (npc == null || npc.IsDestroyed || npc.IsDead())
                {
                    squad.NpcIds.Remove(npcId);
                    _activeNpcs.Remove(npcId);
                    continue;
                }

                Vector3 offset = GetFormationOffset(index);
                Vector3 destination = target + offset;
                MoveNpcToward(npc, destination);
                index++;
            }
        }

        private void MoveNpcToward(BasePlayer npc, Vector3 destination)
        {
            if (npc == null) return;

            // Rust NPC internals move around between updates. Use reflection so this plugin
            // does not hard-fail compile when Facepunch changes concrete NPC classes.
            TryCallNpcMethod(npc, "SetDestination", destination);
            TryCallNpcMethod(npc, "SetMoveDestination", destination);
            TryCallNpcMethod(npc, "SetTargetDestination", destination);
        }

        private void TryCallNpcMethod(BasePlayer npc, string methodName, Vector3 destination)
        {
            try
            {
                var method = npc.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (method == null) return;
                method.Invoke(npc, new object[] { destination });
            }
            catch
            {
                // Ignore method mismatch. Other supported methods may exist on this NPC type.
            }
        }

        #endregion

        #region Spawning

        private bool SpawnRandomSquad(string requestedType, Vector3 nearPosition)
        {
            PatrolRoute route = PickRoute(nearPosition);
            if (route == null || route.Waypoints.Count < 2) return false;

            requestedType = ApplyHeatAndWipeToRequestedType(requestedType, route);
            if (!IsRouteSpawnSafe(route)) return false;

            SquadProfile profile = PickSquadProfile(requestedType);
            if (profile == null) return false;

            string squadId = Guid.NewGuid().ToString("N");
            string squadName = BuildSquadName(profile, route);
            List<Vector3> waypoints = route.Waypoints.Select(w => w.ToVector3()).ToList();
            bool reverse = _rng.NextDouble() < 0.5;
            if (reverse) waypoints.Reverse();

            ActiveSquad squad = new ActiveSquad
            {
                SquadId = squadId,
                SquadName = squadName,
                SquadType = profile.Id,
                Faction = profile.Faction,
                RouteName = route.Name,
                SpawnedAt = Time.realtimeSinceStartup,
                CurrentWaypointIndex = 1,
                Waypoints = waypoints,
                LoopRoute = _config.RouteSettings.LoopRoutes,
                NpcIds = new List<ulong>(),
                InCombat = false,
                Friendly = profile.Friendly,
                IsBoss = profile.Id.Equals("boss", StringComparison.OrdinalIgnoreCase),
                IsHeavyResponse = profile.Id.Equals("heavy", StringComparison.OrdinalIgnoreCase),
                State = profile.Id.Equals("ambush", StringComparison.OrdinalIgnoreCase) ? "Suspicious" : "Patrolling",
                StartedNpcCount = Mathf.Clamp(profile.NpcCount, 1, _config.SquadSettings.MaxSquadSize),
                BossPhase = 0,
                Morale = "Confident"
            };

            Vector3 spawn = waypoints[0];
            int count = Mathf.Clamp(profile.NpcCount, 1, _config.SquadSettings.MaxSquadSize);
            for (int i = 0; i < count; i++)
            {
                BasePlayer npc = SpawnNpc(profile, spawn + GetFormationOffset(i), squadId, i);
                if (npc == null) continue;

                ulong id = npc.net.ID.Value;
                squad.NpcIds.Add(id);
                _activeNpcs[id] = new ActiveNpc
                {
                    SquadId = squadId,
                    ProfileId = profile.Id,
                    Role = profile.Id.Equals("boss", StringComparison.OrdinalIgnoreCase) && i == 0 ? "boss" : (i == 0 ? "leader" : "guard"),
                    IsFriendly = profile.Friendly,
                    IsBoss = profile.Id.Equals("boss", StringComparison.OrdinalIgnoreCase) && i == 0,
                    SpawnedAt = Time.realtimeSinceStartup
                };
            }

            if (squad.NpcIds.Count == 0) return false;

            _activeSquads[squadId] = squad;
            SpawnRoadblockProps(squad, profile, spawn);
            RecordRouteUse(route.Name);
            AnnounceSquadSpawned(squad, profile, route);
            return true;
        }

        private BasePlayer SpawnNpc(SquadProfile profile, Vector3 position, string squadId, int index)
        {
            position = GetGroundPosition(position);

            if (_config.RequiredSetup.UseNpcSpawnPluginIfInstalled && NpcSpawn != null)
            {
                object npcObj = NpcSpawn.Call("SpawnNpc", position, profile.NpcSpawnKitOrProfile);
                BasePlayer pluginNpc = npcObj as BasePlayer;
                if (pluginNpc != null)
                {
                    SetupNpc(pluginNpc, profile, squadId, index);
                    return pluginNpc;
                }
            }

            string prefab = string.IsNullOrEmpty(profile.Prefab) ? _config.SquadSettings.DefaultNpcPrefab : profile.Prefab;
            BaseEntity ent = GameManager.server.CreateEntity(prefab, position, Quaternion.identity, true);
            if (ent == null)
            {
                PrintWarning($"Failed to create NPC prefab: {prefab}");
                return null;
            }

            ent.Spawn();
            BasePlayer npc = ent as BasePlayer;
            if (npc == null)
            {
                ent.Kill();
                PrintWarning($"Prefab did not create BasePlayer NPC: {prefab}");
                return null;
            }

            SetupNpc(npc, profile, squadId, index);
            return npc;
        }

        private void SetupNpc(BasePlayer npc, SquadProfile profile, string squadId, int index)
        {
            npc.displayName = index == 0 ? profile.LeaderName : profile.MemberName;
            npc.health = profile.Health;
            npc._maxHealth = profile.Health;
            npc.SendNetworkUpdateImmediate();

            GiveLoadout(npc, profile, index);
        }

        private void GiveLoadout(BasePlayer npc, SquadProfile profile, int index)
        {
            if (npc?.inventory == null) return;

            npc.inventory.Strip();

            foreach (string shortname in profile.Clothing)
                GiveItem(npc, shortname, 1, npc.inventory.containerWear);

            string weapon = profile.Weapons.Count > 0 ? profile.Weapons[Mathf.Clamp(index, 0, profile.Weapons.Count - 1)] : "rifle.semiauto";
            GiveItem(npc, weapon, 1, npc.inventory.containerBelt);
            GiveItem(npc, "syringe.medical", profile.MedicalSyringes, npc.inventory.containerBelt);

            foreach (var ammo in profile.Ammo)
                GiveItem(npc, ammo.Key, ammo.Value, npc.inventory.containerMain);
        }

        private void GiveItem(BasePlayer npc, string shortname, int amount, ItemContainer container)
        {
            if (string.IsNullOrEmpty(shortname) || amount <= 0 || container == null) return;
            Item item = ItemManager.CreateByName(shortname, amount);
            if (item == null) return;
            if (!item.MoveToContainer(container)) item.Remove();
        }



        private bool SpawnFriendlyReinforcement(BasePlayer owner, ReinforcementProfile rp)
        {
            if (owner == null || rp == null) return false;
            SquadProfile profile = new SquadProfile
            {
                Id = "friendly_" + rp.Id,
                DisplayName = rp.DisplayName,
                LeaderName = rp.LeaderName,
                MemberName = rp.MemberName,
                NpcCount = rp.NpcCount,
                Health = rp.Health,
                Weapons = rp.Weapons == null || rp.Weapons.Count == 0 ? new List<string> { "rifle.semiauto" } : rp.Weapons,
                Clothing = rp.Clothing == null || rp.Clothing.Count == 0 ? new List<string> { "hazmatsuit_scientist_peacekeeper" } : rp.Clothing,
                Friendly = true,
                DropLootCrate = false
            };

            string squadId = Guid.NewGuid().ToString("N");
            Vector3 spawn = GetGroundPosition(owner.transform.position + owner.transform.forward * 18f + UnityEngine.Random.insideUnitSphere * 6f);
            List<Vector3> waypoints = new List<Vector3>
            {
                spawn,
                GetGroundPosition(owner.transform.position + owner.transform.forward * 4f),
                GetGroundPosition(owner.transform.position)
            };

            ActiveSquad squad = new ActiveSquad
            {
                SquadId = squadId,
                SquadName = rp.DisplayName,
                SquadType = profile.Id,
                Faction = "Friendly Support",
                RouteName = "Player Reinforcement",
                SpawnedAt = Time.realtimeSinceStartup,
                CurrentWaypointIndex = 1,
                Waypoints = waypoints,
                LoopRoute = true,
                Friendly = true,
                OwnerUserId = owner.userID,
                State = "Supporting",
                StartedNpcCount = Mathf.Clamp(rp.NpcCount, 1, _config.SquadSettings.MaxSquadSize),
                Morale = "Confident"
            };

            int count = Mathf.Clamp(rp.NpcCount, 1, _config.SquadSettings.MaxSquadSize);
            for (int i = 0; i < count; i++)
            {
                BasePlayer npc = SpawnNpc(profile, spawn + GetFormationOffset(i), squadId, i);
                if (npc == null) continue;
                ulong id = npc.net.ID.Value;
                squad.NpcIds.Add(id);
                _activeNpcs[id] = new ActiveNpc { SquadId = squadId, ProfileId = profile.Id, Role = i == 0 ? "leader" : "support", IsFriendly = true, SpawnedAt = Time.realtimeSinceStartup };
            }
            if (squad.NpcIds.Count == 0) return false;
            _activeSquads[squadId] = squad;
            return true;
        }

        private void MaybeCallHeavyResponse(ActiveSquad squad)
        {
            if (squad == null || squad.Friendly || squad.BackupCalled || !_config.HeavyResponse.Enabled) return;
            SquadProfile profile = _config.SquadProfiles.FirstOrDefault(p => p.Id.Equals(squad.SquadType, StringComparison.OrdinalIgnoreCase));
            if (profile == null) return;
            int starting = Mathf.Max(1, profile.NpcCount);
            if (squad.NpcIds.Count > starting * _config.HeavyResponse.TriggerWhenRemainingFraction) return;
            if (UnityEngine.Random.Range(0, 100) >= _config.HeavyResponse.ChancePercent) return;
            squad.BackupCalled = true;
            Vector3 center = GetSquadCenter(squad);
            if (center == Vector3.zero) return;
            AddRouteHeat(squad.RouteName, _config.RouteHeat.HeavyResponseHeat, "heavy response called");
            SpawnResponseSquad("heavy", center, squad.RouteName);
            if (_config.HeavyResponse.Broadcast)
                Server.Broadcast(Colorize($"Heavy response team moving toward {squad.SquadName}."));
        }

        private bool SpawnResponseSquad(string type, Vector3 position, string routeName)
        {
            SquadProfile profile = PickSquadProfile(type);
            if (profile == null) return false;
            string squadId = Guid.NewGuid().ToString("N");
            Vector3 spawn = GetGroundPosition(position + UnityEngine.Random.insideUnitSphere * _config.HeavyResponse.SpawnDistance);
            List<Vector3> waypoints = new List<Vector3> { spawn, GetGroundPosition(position), GetGroundPosition(position + UnityEngine.Random.insideUnitSphere * 25f) };
            ActiveSquad squad = new ActiveSquad
            {
                SquadId = squadId,
                SquadName = profile.DisplayName,
                SquadType = profile.Id,
                Faction = profile.Faction,
                RouteName = routeName,
                SpawnedAt = Time.realtimeSinceStartup,
                CurrentWaypointIndex = 1,
                Waypoints = waypoints,
                LoopRoute = true,
                IsHeavyResponse = true,
                State = "CallingBackup",
                StartedNpcCount = Mathf.Clamp(profile.NpcCount, 1, _config.SquadSettings.MaxSquadSize),
                Morale = "Confident"
            };
            int count = Mathf.Clamp(profile.NpcCount, 1, _config.SquadSettings.MaxSquadSize);
            for (int i = 0; i < count; i++)
            {
                BasePlayer npc = SpawnNpc(profile, spawn + GetFormationOffset(i), squadId, i);
                if (npc == null) continue;
                ulong id = npc.net.ID.Value;
                squad.NpcIds.Add(id);
                _activeNpcs[id] = new ActiveNpc { SquadId = squadId, ProfileId = profile.Id, Role = i == 0 ? "leader" : "response", IsBoss = false, SpawnedAt = Time.realtimeSinceStartup };
            }
            if (squad.NpcIds.Count == 0) return false;
            _activeSquads[squadId] = squad;
            return true;
        }

        private void SpawnHunterNearPlayer(BasePlayer target)
        {
            if (target == null || !_config.WantedSystem.HunterSquadsEnabled) return;
            SpawnResponseSquad("hunter", target.transform.position, "Wanted Intercept");
            Reply(target, "Road command marked you. Hunter team inbound.");
        }


        private string PickAutoSpawnType()
        {
            int day = GetWipeAgeDays();
            if (_config.WipeScaling.Enabled && day <= _config.WipeScaling.EarlyWipeDays)
                return UnityEngine.Random.Range(0, 100) < 45 ? "recon" : "random";

            if (_config.WipeScaling.Enabled && day >= _config.WipeScaling.LateWipeStartsDay && UnityEngine.Random.Range(0, 100) < _config.WipeScaling.LateWipeExtraHeavyChancePercent)
                return "heavy";

            return (_config.BossPatrols.Enabled && UnityEngine.Random.Range(0, 100) < _config.BossPatrols.ChancePerPatrolPercent) ? "boss" : "random";
        }

        private string ApplyHeatAndWipeToRequestedType(string requestedType, PatrolRoute route)
        {
            if (route == null || string.IsNullOrEmpty(requestedType)) return requestedType;
            if (!requestedType.Equals("random", StringComparison.OrdinalIgnoreCase)) return requestedType;

            int heat = GetRouteHeat(route.Name);
            if (_config.RouteHeat.Enabled)
            {
                if (heat >= _config.RouteHeat.CriticalRouteStartsAt && _config.BossPatrols.Enabled && UnityEngine.Random.Range(0, 100) < _config.RouteHeat.CriticalRouteBossChancePercent)
                    return "boss";
                if (heat >= _config.RouteHeat.HotRouteStartsAt && UnityEngine.Random.Range(0, 100) < _config.RouteHeat.HotRouteHeavyChancePercent)
                    return "heavy";
                if (heat >= _config.RouteHeat.HotRouteStartsAt && UnityEngine.Random.Range(0, 100) < _config.RouteHeat.HotRouteAmbushChancePercent)
                    return "ambush";
            }

            int day = GetWipeAgeDays();
            if (_config.WipeScaling.Enabled && day <= _config.WipeScaling.EarlyWipeDays && UnityEngine.Random.Range(0, 100) < 35)
                return "recon";

            return requestedType;
        }

        private bool IsRouteSpawnSafe(PatrolRoute route)
        {
            if (route == null || !_config.BaseSafety.AvoidBuildingPrivilege) return true;
            if (route.Waypoints == null || route.Waypoints.Count == 0) return true;

            List<VectorDto> points = _config.BaseSafety.RejectRouteIfAnySpawnPointIsUnsafe ? route.Waypoints : route.Waypoints.Take(1).ToList();
            foreach (var point in points)
            {
                if (IsNearBuildingPrivilege(point.ToVector3(), _config.BaseSafety.MinimumDistanceFromToolCupboards))
                    return false;
            }
            return true;
        }

        private bool IsNearBuildingPrivilege(Vector3 position, float radius)
        {
            if (radius <= 0f) return false;
            Collider[] hits = Physics.OverlapSphere(position, radius);
            foreach (Collider hit in hits)
            {
                if (hit == null) continue;
                BuildingPrivlidge priv = hit.GetComponentInParent<BuildingPrivlidge>();
                if (priv != null && !priv.IsDestroyed) return true;
            }
            return false;
        }


        #endregion

        #region Selection / AI / Memory

        private PatrolRoute PickRoute(Vector3 nearPosition)
        {
            List<PatrolRoute> routes = _data.Routes.Where(r => r.Enabled && r.Waypoints != null && r.Waypoints.Count >= 2).ToList();
            if (routes.Count == 0) return null;

            if (nearPosition != Vector3.zero)
            {
                routes = routes.OrderBy(r => Vector3.Distance(r.Waypoints[0].ToVector3(), nearPosition)).ToList();
                return routes[0];
            }

            return routes[_rng.Next(routes.Count)];
        }

        private SquadProfile PickSquadProfile(string requestedType)
        {
            List<SquadProfile> profiles = _config.SquadProfiles.Where(p => p.Enabled).ToList();
            if (profiles.Count == 0) return null;

            if (!string.IsNullOrEmpty(requestedType) && requestedType != "random")
            {
                SquadProfile exact = profiles.FirstOrDefault(p => p.Id.Equals(requestedType, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }

            int totalWeight = profiles.Sum(p => Mathf.Max(1, p.Weight));
            int roll = _rng.Next(totalWeight);
            int cursor = 0;
            foreach (var profile in profiles)
            {
                cursor += Mathf.Max(1, profile.Weight);
                if (roll < cursor) return profile;
            }

            return profiles[0];
        }

        private string BuildSquadName(SquadProfile profile, PatrolRoute route)
        {
            string fallback = $"{profile.Faction} {profile.DisplayName}";
            if (!_config.WorldMindBehavior.AllowAIEventNames || WorldMindCoreV2 == null) return fallback;

            try
            {
                var context = new Dictionary<string, object>
                {
                    ["eventType"] = "road_patrol_spawned",
                    ["routeName"] = route.Name,
                    ["biome"] = route.Biome,
                    ["squadType"] = profile.Id,
                    ["faction"] = profile.Faction,
                    ["onlinePlayers"] = BasePlayer.activePlayerList.Count
                };

                object result = WorldMindCoreV2.Call("GenerateEventName", "road_patrol", context);
                string text = result as string;
                if (!string.IsNullOrEmpty(text)) return text.Length > 64 ? text.Substring(0, 64) : text;
            }
            catch (Exception ex)
            {
                if (_config.WorldMindBehavior.DebugWorldMindCalls) PrintWarning($"WorldMind name call failed: {ex.Message}");
            }

            return fallback;
        }

        private void AnnounceSquadSpawned(ActiveSquad squad, SquadProfile profile, PatrolRoute route)
        {
            string message = $"Road chatter: {squad.SquadName} spotted along {route.Name}.";

            if (_config.WorldMindBehavior.AllowAIRadioMessages && WorldMindCoreV2 != null)
            {
                try
                {
                    var context = new Dictionary<string, object>
                    {
                        ["eventType"] = "road_patrol_spawned",
                        ["patrolName"] = squad.SquadName,
                        ["routeName"] = route.Name,
                        ["biome"] = route.Biome,
                        ["squadType"] = profile.Id,
                        ["faction"] = profile.Faction,
                        ["onlinePlayers"] = BasePlayer.activePlayerList.Count,
                        ["trashTalkLevel"] = _config.WorldMindBehavior.TrashTalkLevel
                    };

                    object result = WorldMindCoreV2.Call("GenerateEventMessage", "road_patrol", context);
                    string text = result as string;
                    if (!string.IsNullOrEmpty(text)) message = text;
                }
                catch (Exception ex)
                {
                    if (_config.WorldMindBehavior.DebugWorldMindCalls) PrintWarning($"WorldMind message call failed: {ex.Message}");
                }
            }

            if (_config.PatrolSettings.BroadcastPatrolSpawns)
                Server.Broadcast(Colorize(message));
            else
                Puts(message);
        }

        private void CompleteSquad(ActiveSquad squad, BasePlayer finalPlayer, string reason)
        {
            if (squad == null) return;

            foreach (ulong propId in squad.PropIds.ToList())
            {
                BaseEntity prop = FindEntity(propId);
                if (prop != null && !prop.IsDestroyed) prop.Kill();
                _ownedEventEntities.Remove(propId);
            }

            if (reason == "squad_destroyed" && !squad.LootSpawned)
            {
                if (!squad.Friendly)
                {
                    AddRouteHeat(squad.RouteName, squad.IsBoss ? _config.RouteHeat.BossClearedHeat : _config.RouteHeat.SquadClearedHeat, squad.IsBoss ? "boss patrol cleared" : "patrol cleared");
                    SpawnRewardCrate(squad, finalPlayer);
                    GiveCompletionRewards(squad, finalPlayer);
                    if (finalPlayer != null)
                    {
                        AddWanted(finalPlayer, squad.IsBoss ? _config.WantedSystem.BossKillWantedPoints : 5, squad.IsBoss ? "boss patrol cleared" : "patrol cleared");
                        AddSupportTrust(finalPlayer.userID, finalPlayer.displayName, squad.IsBoss ? _config.PlayerReinforcements.TrustGainBossClear : _config.PlayerReinforcements.TrustGainPatrolClear, squad.IsBoss ? "boss clear" : "patrol clear");
                    }
                }
                squad.LootSpawned = true;
            }

            if (reason == "route_completed" && !squad.Friendly && squad.SquadType.Equals("recon", StringComparison.OrdinalIgnoreCase))
            {
                AddRouteHeat(squad.RouteName, _config.RouteHeat.ReconReportedHeat, "recon team reported back");
                if (_config.PatrolRadio.BroadcastReconReports)
                    Server.Broadcast(Colorize($"Road chatter: recon team reported clean movement on {squad.RouteName}. Expect heavier boots later."));
            }

            foreach (ulong id in squad.NpcIds.ToList())
            {
                BasePlayer npc = FindNpc(id);
                if (npc != null && !npc.IsDestroyed)
                    npc.Kill();
                _activeNpcs.Remove(id);
            }

            _activeSquads.Remove(squad.SquadId);

            RouteMemory routeMemory = GetRouteMemory(squad.RouteName);
            if (reason == "squad_destroyed") routeMemory.SquadsKilled++;
            routeMemory.LastResult = reason;
            routeMemory.LastCompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (finalPlayer != null) routeMemory.MostRecentKiller = finalPlayer.displayName;

            SaveData();

            if (_config.PatrolSettings.BroadcastPatrolResults && reason == "squad_destroyed")
                Server.Broadcast(Colorize($"Road patrol eliminated: {squad.SquadName}."));
        }

        private void RecordPlayerKill(BasePlayer player, ActiveNpc npc)
        {
            PlayerPatrolMemory memory;
            if (!_data.PlayerPatrolMemory.TryGetValue(player.userID.ToString(), out memory))
            {
                memory = new PlayerPatrolMemory { DisplayName = player.displayName };
                _data.PlayerPatrolMemory[player.userID.ToString()] = memory;
            }

            memory.DisplayName = player.displayName;
            memory.PatrolNpcKills++;
            memory.LastKilledProfile = npc.ProfileId;
            memory.LastKillAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            memory.ThreatRating = Mathf.Clamp(memory.PatrolNpcKills / 5, 0, 10);
        }

        private void RecordRouteUse(string routeName)
        {
            RouteMemory memory = GetRouteMemory(routeName);
            memory.TimesUsed++;
            memory.LastUsedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private RouteMemory GetRouteMemory(string routeName)
        {
            RouteMemory memory;
            if (!_data.RouteMemory.TryGetValue(routeName, out memory))
            {
                memory = new RouteMemory();
                _data.RouteMemory[routeName] = memory;
            }

            return memory;
        }



        private WantedRecord GetWantedRecord(ulong userId, string displayName)
        {
            WantedRecord record;
            string key = userId.ToString();
            if (!_data.Wanted.TryGetValue(key, out record))
            {
                record = new WantedRecord();
                _data.Wanted[key] = record;
            }
            record.DisplayName = displayName ?? record.DisplayName;
            return record;
        }

        private int GetWantedLevel(ulong userId)
        {
            return GetWantedLevel(userId.ToString());
        }

        private int GetWantedLevel(string key)
        {
            WantedRecord record;
            if (!_data.Wanted.TryGetValue(key, out record) || record == null) return 0;
            int score = record.Score;
            if (score >= _config.WantedSystem.Level5Score) return 5;
            if (score >= _config.WantedSystem.Level4Score) return 4;
            if (score >= _config.WantedSystem.Level3Score) return 3;
            if (score >= _config.WantedSystem.Level2Score) return 2;
            if (score >= _config.WantedSystem.Level1Score) return 1;
            return 0;
        }

        private int LevelToWantedScore(int level)
        {
            switch (level)
            {
                case 5: return _config.WantedSystem.Level5Score;
                case 4: return _config.WantedSystem.Level4Score;
                case 3: return _config.WantedSystem.Level3Score;
                case 2: return _config.WantedSystem.Level2Score;
                case 1: return _config.WantedSystem.Level1Score;
                default: return 0;
            }
        }

        private void AddWanted(BasePlayer player, int amount, string reason)
        {
            if (!_config.WantedSystem.Enabled || player == null || amount <= 0) return;
            WantedRecord record = GetWantedRecord(player.userID, player.displayName);
            int oldLevel = GetWantedLevel(player.userID);
            record.Score = Mathf.Clamp(record.Score + amount, 0, _config.WantedSystem.MaxScore);
            record.LastReason = reason;
            record.LastChangedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int newLevel = GetWantedLevel(player.userID);
            if (newLevel > oldLevel && _config.WantedSystem.NotifyPlayerOnLevelUp)
                Reply(player, $"Wanted level increased: {newLevel}. Reason: {reason}.");
            SaveData();
            if (newLevel >= _config.WantedSystem.HunterSpawnWantedLevel && _config.WantedSystem.HunterSquadsEnabled)
            {
                if (UnityEngine.Random.Range(0, 100) < _config.WantedSystem.HunterSpawnChancePercent)
                    SpawnHunterNearPlayer(player);
            }
        }

        private void DecayWantedIfNeeded()
        {
            if (!_config.WantedSystem.Enabled || _config.WantedSystem.DecayEveryMinutes <= 0 || _config.WantedSystem.DecayPoints <= 0) return;
            float now = Time.realtimeSinceStartup;
            if (now - _lastWantedDecayAt < _config.WantedSystem.DecayEveryMinutes * 60f) return;
            _lastWantedDecayAt = now;
            foreach (var record in _data.Wanted.Values)
                record.Score = Mathf.Max(0, record.Score - _config.WantedSystem.DecayPoints);
            SaveData();
        }

        private void SendScanner(BasePlayer player, int level)
        {
            Reply(player, "Road Scanner:");
            if (_activeSquads.Count == 0)
            {
                Reply(player, "- No active patrol chatter detected.");
                return;
            }
            foreach (var squad in _activeSquads.Values.Where(s => !s.Friendly).Take(_config.PatrolScanner.MaxResults))
            {
                Vector3 center = GetSquadCenter(squad);
                float dist = center == Vector3.zero ? 0f : Vector3.Distance(player.transform.position, center);
                string distance = dist <= 0f ? "unknown" : (dist < 250f ? "near" : dist < 750f ? "medium" : "far");
                if (level <= 1)
                    Reply(player, $"- Movement reported near {squad.RouteName}. Distance: {distance}.");
                else if (level == 2)
                    Reply(player, $"- {squad.SquadType} | Threat: {EstimateThreat(squad)} | Heat: {GetRouteHeat(squad.RouteName)} | Route: {squad.RouteName} | Distance: {distance}.");
                else
                    Reply(player, $"- {squad.SquadName} [{squad.SquadId.Substring(0, 6)}] type={squad.SquadType}, alive={squad.NpcIds.Count}, route={squad.RouteName}, heat={GetRouteHeat(squad.RouteName)}, morale={squad.Morale}, phase={squad.BossPhase}, state={squad.State}, combat={squad.InCombat}, dist={Mathf.RoundToInt(dist)}m.");
            }

            int wanted = GetWantedLevel(player.userID);
            if (wanted > 0)
                Reply(player, $"- Scanner warning: your wanted level is {wanted}. Patrol chatter may include your name.");
        }



        private void HandleHeatCommand(BasePlayer player, string[] args)
        {
            if (args == null || args.Length == 0)
            {
                Reply(player, "Route heat:");
                foreach (var route in _data.Routes.OrderByDescending(r => GetRouteHeat(r.Name)).Take(12))
                {
                    RouteMemory mem = GetRouteMemory(route.Name);
                    Reply(player, $"- {route.Name}: heat {mem.Heat}/{_config.RouteHeat.MaxHeat} | last={mem.LastHeatReason}");
                }
                return;
            }

            string action = args[0].ToLowerInvariant();
            if (action == "clear" && args.Length >= 2)
            {
                string routeName = string.Join(" ", args.Skip(1).ToArray());
                RouteMemory mem = GetRouteMemory(routeName);
                mem.Heat = 0;
                mem.LastHeatReason = "admin clear";
                SaveData();
                Reply(player, $"Heat cleared for {routeName}.");
                return;
            }

            if (action == "set" && args.Length >= 3)
            {
                string routeName = string.Join(" ", args.Skip(1).Take(args.Length - 2).ToArray());
                int amount = Mathf.Clamp(ParseInt(args[args.Length - 1], 0), 0, _config.RouteHeat.MaxHeat);
                RouteMemory mem = GetRouteMemory(routeName);
                mem.Heat = amount;
                mem.LastHeatReason = "admin set";
                SaveData();
                Reply(player, $"Heat set for {routeName}: {amount}.");
                return;
            }

            Reply(player, "Usage: /wmrp heat | /wmrp heat clear <route> | /wmrp heat set <route> <0-100>");
        }

        private int GetRouteHeat(string routeName)
        {
            if (!_config.RouteHeat.Enabled || string.IsNullOrEmpty(routeName)) return 0;
            return Mathf.Clamp(GetRouteMemory(routeName).Heat, 0, _config.RouteHeat.MaxHeat);
        }

        private void AddRouteHeat(string routeName, int amount, string reason)
        {
            if (!_config.RouteHeat.Enabled || string.IsNullOrEmpty(routeName) || amount <= 0) return;
            RouteMemory mem = GetRouteMemory(routeName);
            mem.Heat = Mathf.Clamp(mem.Heat + amount, 0, _config.RouteHeat.MaxHeat);
            mem.LastHeatReason = reason;
        }

        private void DecayRouteHeatIfNeeded()
        {
            if (!_config.RouteHeat.Enabled || _config.RouteHeat.HeatDecayEveryMinutes <= 0 || _config.RouteHeat.HeatDecayPoints <= 0) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_data.WorldState.LastHeatDecayAt > 0 && now - _data.WorldState.LastHeatDecayAt < _config.RouteHeat.HeatDecayEveryMinutes * 60) return;
            _data.WorldState.LastHeatDecayAt = now;
            foreach (var mem in _data.RouteMemory.Values)
                mem.Heat = Mathf.Max(0, mem.Heat - _config.RouteHeat.HeatDecayPoints);
            SaveData();
        }

        private void UpdateSquadMorale(ActiveSquad squad)
        {
            if (squad == null || squad.StartedNpcCount <= 0) return;
            float remaining = (float)squad.NpcIds.Count / Mathf.Max(1, squad.StartedNpcCount);
            if (squad.IsBoss && remaining <= 0.25f) squad.Morale = "Desperate";
            else if (remaining <= 0.35f) squad.Morale = "Breaking";
            else if (remaining <= 0.60f) squad.Morale = "Pressured";
            else if (squad.InCombat) squad.Morale = "Alert";
            else squad.Morale = "Confident";
        }

        private void ProcessBossPhases(ActiveSquad squad)
        {
            if (!_config.BossPhases.Enabled || squad == null || !squad.IsBoss || squad.StartedNpcCount <= 0) return;
            float remaining = (float)squad.NpcIds.Count / Mathf.Max(1, squad.StartedNpcCount);
            int nextPhase = squad.BossPhase;
            if (remaining <= _config.BossPhases.Phase4RemainingFraction) nextPhase = 4;
            else if (remaining <= _config.BossPhases.Phase3RemainingFraction) nextPhase = 3;
            else if (remaining <= _config.BossPhases.Phase2RemainingFraction) nextPhase = 2;

            if (nextPhase <= squad.BossPhase) return;
            squad.BossPhase = nextPhase;
            squad.LastPhaseAt = Time.realtimeSinceStartup;

            if (_config.BossPhases.BroadcastBossPhases)
                Server.Broadcast(Colorize($"Road boss phase {nextPhase}: {squad.SquadName} is escalating on {squad.RouteName}."));

            if (nextPhase >= 3 && _config.BossPhases.CallBackupOnPhase3 && !squad.BackupCalled)
            {
                squad.BackupCalled = true;
                SpawnResponseSquad("heavy", GetSquadCenter(squad), squad.RouteName);
            }
        }

        private void BroadcastRadioChatter()
        {
            if (!_config.PatrolRadio.Enabled) return;
            if (_activeSquads.Count == 0)
            {
                if (_config.PatrolRadio.IncludeRouteHeatWarnings)
                {
                    var hot = _data.RouteMemory.OrderByDescending(x => x.Value.Heat).FirstOrDefault(x => x.Value.Heat >= _config.RouteHeat.HotRouteStartsAt);
                    if (!string.IsNullOrEmpty(hot.Key)) Server.Broadcast(Colorize($"Road chatter: {hot.Key} is running hot. Heat {hot.Value.Heat}/{_config.RouteHeat.MaxHeat}."));
                }
                return;
            }

            ActiveSquad squad = _activeSquads.Values.Where(sq => !sq.Friendly).OrderByDescending(sq => GetRouteHeat(sq.RouteName)).FirstOrDefault();
            if (squad == null) return;
            string threat = EstimateThreat(squad);
            string msg = $"Road chatter: {threat} movement on {squad.RouteName}. Unit state: {squad.State}.";
            if (_config.PatrolRadio.IncludeWantedWarnings)
            {
                var wanted = _data.Wanted.OrderByDescending(x => x.Value.Score).FirstOrDefault(x => GetWantedLevel(x.Key) >= 3);
                if (!string.IsNullOrEmpty(wanted.Key) && UnityEngine.Random.Range(0, 100) < 35)
                    msg = $"Road chatter: patrol command keeps repeating {wanted.Value.DisplayName}. Wanted level {GetWantedLevel(wanted.Key)}.";
            }
            Server.Broadcast(Colorize(msg));
        }

        private void TrySmartWantedHunterSweep()
        {
            if (!_config.WantedSystem.Enabled || !_config.WantedSystem.HunterSquadsEnabled) return;
            float now = Time.realtimeSinceStartup;
            if (now - _lastHunterSweepAt < Mathf.Max(60f, _config.WantedSystem.SmartHunterCheckSeconds)) return;
            _lastHunterSweepAt = now;
            if (_activeSquads.Count >= _config.PatrolSettings.MaxActiveSquads) return;

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsDead() || player.IsSleeping()) continue;
                int level = GetWantedLevel(player.userID);
                if (level < _config.WantedSystem.HunterSpawnWantedLevel) continue;
                if (UnityEngine.Random.Range(0, 100) >= _config.WantedSystem.SmartHunterChancePercent) continue;
                PatrolRoute route = PickRoute(player.transform.position);
                if (route == null) continue;
                float dist = Vector3.Distance(route.Waypoints[0].ToVector3(), player.transform.position);
                if (dist > _config.WantedSystem.SmartHunterRoadRange) continue;
                SpawnHunterNearPlayer(player);
                break;
            }
        }

        private void MaybeAddIntelDrop(StorageContainer container, ActiveSquad squad, SquadProfile profile)
        {
            if (!_config.IntelDrops.Enabled || container == null || container.inventory == null || squad == null) return;
            if (UnityEngine.Random.Range(0, 100) >= _config.IntelDrops.ChancePercent) return;
            Item item = ItemManager.CreateByName(_config.IntelDrops.IntelItemShortname, 1);
            if (item == null) return;
            int heat = GetRouteHeat(squad.RouteName);
            item.name = _config.IntelDrops.IncludeRouteHeatInItemName ? $"Patrol Intel: {squad.RouteName} Heat {heat}" : $"Patrol Intel: {squad.RouteName}";
            if (!item.MoveToContainer(container.inventory)) item.Remove();
        }

        private SupportTrustRecord GetSupportTrustRecord(ulong userId, string displayName = null)
        {
            string key = userId.ToString();
            SupportTrustRecord rec;
            if (!_data.SupportTrust.TryGetValue(key, out rec))
            {
                rec = new SupportTrustRecord();
                _data.SupportTrust[key] = rec;
            }
            if (!string.IsNullOrEmpty(displayName)) rec.DisplayName = displayName;
            return rec;
        }

        private int GetSupportTrust(ulong userId)
        {
            return Mathf.Clamp(GetSupportTrustRecord(userId).Score, -100, 100);
        }

        private void AddSupportTrust(ulong userId, string displayName, int amount, string reason)
        {
            if (amount == 0) return;
            SupportTrustRecord rec = GetSupportTrustRecord(userId, displayName);
            rec.Score = Mathf.Clamp(rec.Score + amount, -100, 100);
            rec.LastReason = reason;
            rec.LastChangedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveData();
        }

        private float GetSupportTrustCostMultiplier(ulong userId)
        {
            int trust = Mathf.Max(0, GetSupportTrust(userId));
            float maxDiscount = Mathf.Clamp(_config.PlayerReinforcements.TrustedScrapDiscountPercentAt100Trust, 0, 90) / 100f;
            return 1f - (maxDiscount * (trust / 100f));
        }

        private float GetSupportTrustDiscount(ulong userId)
        {
            return 1f - GetSupportTrustCostMultiplier(userId);
        }

        private int GetWipeAgeDays()
        {
            if (_data.WorldState.FirstSeenAt <= 0)
            {
                _data.WorldState.FirstSeenAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                SaveData();
            }
            long ageSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _data.WorldState.FirstSeenAt;
            return Mathf.Max(0, Mathf.FloorToInt(ageSeconds / 86400f));
        }

        private void CheckWipeScalingNotice()
        {
            if (!_config.WipeScaling.Enabled) return;
            float now = Time.realtimeSinceStartup;
            if (now - _lastWipeScaleCheckAt < 3600f) return;
            _lastWipeScaleCheckAt = now;
            Puts($"Wipe scaling age: day {GetWipeAgeDays()}.");
        }

        private string EstimateThreat(ActiveSquad squad)
        {
            if (squad.IsBoss) return "Extreme";
            if (GetRouteHeat(squad.RouteName) >= _config.RouteHeat.CriticalRouteStartsAt) return "Extreme";
            if (squad.IsHeavyResponse || squad.SquadType.Contains("heavy") || squad.SquadType.Contains("hunter")) return "High";
            if (GetRouteHeat(squad.RouteName) >= _config.RouteHeat.HotRouteStartsAt) return "High";
            if (squad.NpcIds.Count >= 5) return "Medium";
            return "Low";
        }


        #endregion

        #region Utility

        private bool HasAdmin(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin));
        }

        private void Reply(BasePlayer player, string message)
        {
            SendReply(player, Colorize(message));
        }

        private string Colorize(string message)
        {
            return $"<color={_config.Chat.ChatColor}>{message}</color>";
        }

        private string FormatVector(Vector3 v)
        {
            return $"{v.x.ToString("0.0", CultureInfo.InvariantCulture)}, {v.y.ToString("0.0", CultureInfo.InvariantCulture)}, {v.z.ToString("0.0", CultureInfo.InvariantCulture)}";
        }

        private Vector3 GetFormationOffset(int index)
        {
            float spacing = Mathf.Max(1f, _config.SquadSettings.FormationSpacing);
            switch (index % 8)
            {
                case 0: return Vector3.zero;
                case 1: return new Vector3(spacing, 0, -spacing);
                case 2: return new Vector3(-spacing, 0, -spacing);
                case 3: return new Vector3(spacing * 2, 0, -spacing * 2);
                case 4: return new Vector3(-spacing * 2, 0, -spacing * 2);
                case 5: return new Vector3(0, 0, -spacing * 3);
                case 6: return new Vector3(spacing * 3, 0, -spacing * 3);
                default: return new Vector3(-spacing * 3, 0, -spacing * 3);
            }
        }

        private Vector3 GetGroundPosition(Vector3 position)
        {
            float y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(position) : position.y;
            position.y = y + 0.2f;
            return position;
        }

        private Vector3 GetSquadCenter(ActiveSquad squad)
        {
            Vector3 total = Vector3.zero;
            int count = 0;
            foreach (ulong id in squad.NpcIds)
            {
                BasePlayer npc = FindNpc(id);
                if (npc == null || npc.IsDead()) continue;
                total += npc.transform.position;
                count++;
            }

            return count <= 0 ? Vector3.zero : total / count;
        }

        private BasePlayer FindNpc(ulong netId)
        {
            BaseNetworkable ent = BaseNetworkable.serverEntities.Find(new NetworkableId(netId));
            return ent as BasePlayer;
        }

        private BasePlayer FindNearestEnemy(Vector3 position, float range)
        {
            BasePlayer best = null;
            float bestDist = range;
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsDead() || player.IsSleeping()) continue;
                float d = Vector3.Distance(position, player.transform.position);
                if (d < bestDist)
                {
                    best = player;
                    bestDist = d;
                }
            }

            return best;
        }

        private string GuessBiome(Vector3 position)
        {
            if (TerrainMeta.BiomeMap == null) return "unknown";
            float arid = TerrainMeta.BiomeMap.GetBiome(position, (int)TerrainBiome.Enum.Arid);
            float temperate = TerrainMeta.BiomeMap.GetBiome(position, (int)TerrainBiome.Enum.Temperate);
            float tundra = TerrainMeta.BiomeMap.GetBiome(position, (int)TerrainBiome.Enum.Tundra);
            float arctic = TerrainMeta.BiomeMap.GetBiome(position, (int)TerrainBiome.Enum.Arctic);
            float max = Mathf.Max(arid, temperate, tundra, arctic);
            if (Math.Abs(max - arid) < 0.01f) return "desert";
            if (Math.Abs(max - arctic) < 0.01f) return "snow";
            if (Math.Abs(max - tundra) < 0.01f) return "wasteland";
            return "forest";
        }

        private void CleanupAllSquads()
        {
            foreach (ActiveSquad squad in _activeSquads.Values.ToList())
            {
                foreach (ulong propId in squad.PropIds.ToList())
                {
                    BaseEntity prop = FindEntity(propId);
                    if (prop != null && !prop.IsDestroyed) prop.Kill();
                    _ownedEventEntities.Remove(propId);
                }

                foreach (ulong id in squad.NpcIds.ToList())
                {
                    BasePlayer npc = FindNpc(id);
                    if (npc != null && !npc.IsDestroyed) npc.Kill();
                    _activeNpcs.Remove(id);
                }
            }

            _activeSquads.Clear();
        }



        private int RecallSquads(string query)
        {
            int count = 0;
            foreach (var squad in _activeSquads.Values.ToList())
            {
                bool match = query.Equals("all", StringComparison.OrdinalIgnoreCase)
                             || squad.SquadId.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                             || squad.SquadName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                             || squad.RouteName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!match) continue;
                CompleteSquad(squad, null, "admin_recall");
                count++;
            }
            return count;
        }

        private void CreateSampleRoute(BasePlayer player, string name, float radius, int points)
        {
            PatrolRoute existing = _data.Routes.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) _data.Routes.Remove(existing);

            List<VectorDto> waypoints = new List<VectorDto>();
            Vector3 center = player.transform.position;
            for (int i = 0; i < points; i++)
            {
                float angle = (Mathf.PI * 2f) * ((float)i / points);
                Vector3 point = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                point = GetGroundPosition(point);
                waypoints.Add(VectorDto.FromVector3(point));
            }

            _data.Routes.Add(new PatrolRoute
            {
                Name = name,
                Enabled = true,
                Biome = GuessBiome(center),
                Waypoints = waypoints
            });
            SaveData();
            Reply(player, $"Created sample route '{name}' with {points} waypoints. Use it for testing, then record real road routes.");
        }

        private void SpawnRoadblockProps(ActiveSquad squad, SquadProfile profile, Vector3 center)
        {
            if (!profile.CreateRoadblockProps || profile.RoadblockPrefabs == null || profile.RoadblockPrefabs.Count == 0) return;

            int index = 0;
            foreach (string prefab in profile.RoadblockPrefabs)
            {
                if (string.IsNullOrEmpty(prefab)) continue;
                Vector3 pos = GetGroundPosition(center + GetRoadblockOffset(index));
                BaseEntity ent = GameManager.server.CreateEntity(prefab, pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f), true);
                if (ent == null) continue;
                ent.Spawn();
                ulong id = ent.net.ID.Value;
                squad.PropIds.Add(id);
                _ownedEventEntities[id] = squad.SquadId;
                index++;
            }
        }

        private Vector3 GetRoadblockOffset(int index)
        {
            float s = Mathf.Max(2f, _config.SquadSettings.FormationSpacing + 1f);
            switch (index % 6)
            {
                case 0: return new Vector3(s, 0f, 0f);
                case 1: return new Vector3(-s, 0f, 0f);
                case 2: return new Vector3(0f, 0f, s);
                case 3: return new Vector3(0f, 0f, -s);
                case 4: return new Vector3(s * 1.5f, 0f, s * 0.8f);
                default: return new Vector3(-s * 1.5f, 0f, -s * 0.8f);
            }
        }

        private void SpawnRewardCrate(ActiveSquad squad, BasePlayer finalPlayer)
        {
            if (!_config.LootSettings.EnableLootCrates) return;
            SquadProfile profile = _config.SquadProfiles.FirstOrDefault(p => p.Id.Equals(squad.SquadType, StringComparison.OrdinalIgnoreCase));
            if (profile == null || !profile.DropLootCrate) return;

            Vector3 pos = GetGroundPosition(GetSquadCenter(squad));
            if (pos == Vector3.zero && finalPlayer != null) pos = finalPlayer.transform.position;
            if (pos == Vector3.zero && squad.Waypoints.Count > 0) pos = squad.Waypoints[Mathf.Clamp(squad.CurrentWaypointIndex, 0, squad.Waypoints.Count - 1)];

            BaseEntity ent = GameManager.server.CreateEntity(_config.LootSettings.LootCratePrefab, pos + Vector3.up * 0.25f, Quaternion.identity, true);
            if (ent == null) return;
            ent.Spawn();
            StorageContainer container = ent as StorageContainer;
            if (container != null)
            {
                FillContainer(container, profile);
                MaybeAddIntelDrop(container, squad, profile);
            }
            _ownedEventEntities[ent.net.ID.Value] = squad.SquadId;
        }

        private void FillContainer(StorageContainer container, SquadProfile profile)
        {
            if (container.inventory == null) return;
            container.inventory.Clear();
            foreach (var entry in profile.Loot)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Shortname) || entry.MaxAmount <= 0) continue;
                int min = Mathf.Max(1, entry.MinAmount);
                int max = Mathf.Max(min, entry.MaxAmount);
                int amount = UnityEngine.Random.Range(min, max + 1);
                Item item = ItemManager.CreateByName(entry.Shortname, amount);
                if (item == null) continue;
                if (!item.MoveToContainer(container.inventory)) item.Remove();
            }
        }

        private void GiveCompletionRewards(ActiveSquad squad, BasePlayer finalPlayer)
        {
            if (finalPlayer == null) return;
            SquadProfile profile = _config.SquadProfiles.FirstOrDefault(p => p.Id.Equals(squad.SquadType, StringComparison.OrdinalIgnoreCase));
            if (profile == null) return;

            if (profile.ServerRewardsRP > 0 && ServerRewards != null)
                ServerRewards.Call("AddPoints", finalPlayer.userID, profile.ServerRewardsRP);

            if (profile.EconomicsMoney > 0 && Economics != null)
                Economics.Call("Deposit", finalPlayer.userID, (double)profile.EconomicsMoney);
        }

        private void AlertNearbyPlayers(ActiveSquad squad)
        {
            if (!_config.PatrolSettings.EnableProximityWarnings) return;
            float now = Time.realtimeSinceStartup;
            if (now - squad.LastWarningAt < _config.PatrolSettings.ProximityWarningCooldownSeconds) return;

            Vector3 center = GetSquadCenter(squad);
            if (center == Vector3.zero) return;

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsDead() || player.IsSleeping()) continue;
                float dist = Vector3.Distance(player.transform.position, center);
                if (dist > _config.PatrolSettings.ProximityWarningRange) continue;
                Reply(player, $"Road patrol nearby: {squad.SquadName}. Distance: {Mathf.RoundToInt(dist)}m.");
            }
            squad.LastWarningAt = now;
        }

        private BaseEntity FindEntity(ulong netId)
        {
            BaseNetworkable ent = BaseNetworkable.serverEntities.Find(new NetworkableId(netId));
            return ent as BaseEntity;
        }

        private int ParseInt(string text, int fallback)
        {
            int value;
            return int.TryParse(text, out value) ? value : fallback;
        }

        private float ParseFloat(string text, float fallback)
        {
            float value;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }



        private BasePlayer FindNearestHostileNpc(Vector3 position, float range)
        {
            BasePlayer best = null;
            float bestDist = range;
            foreach (var pair in _activeNpcs.ToList())
            {
                ActiveNpc meta = pair.Value;
                if (meta == null || meta.IsFriendly) continue;
                BasePlayer npc = FindNpc(pair.Key);
                if (npc == null || npc.IsDead() || npc.IsDestroyed) continue;
                float d = Vector3.Distance(position, npc.transform.position);
                if (d < bestDist)
                {
                    best = npc;
                    bestDist = d;
                }
            }
            return best;
        }

        private BasePlayer FindPlayerByNameOrId(string query)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (BasePlayer p in BasePlayer.activePlayerList)
            {
                if (p == null) continue;
                if (p.UserIDString == query) return p;
                if (!string.IsNullOrEmpty(p.displayName) && p.displayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return p;
            }
            return null;
        }

        private bool IsNearActivePatrolCombat(Vector3 position, float range)
        {
            foreach (var squad in _activeSquads.Values)
            {
                if (squad.Friendly) continue;
                if (!squad.InCombat) continue;
                Vector3 center = GetSquadCenter(squad);
                if (center != Vector3.zero && Vector3.Distance(center, position) <= range) return true;
            }
            return false;
        }

        private int CountFriendlySquads(ulong ownerId)
        {
            return _activeSquads.Values.Count(s => s.Friendly && s.OwnerUserId == ownerId);
        }

        private int RecallFriendlySquads(ulong ownerId)
        {
            int count = 0;
            foreach (var squad in _activeSquads.Values.ToList())
            {
                if (!squad.Friendly || squad.OwnerUserId != ownerId) continue;
                CompleteSquad(squad, null, "friendly_cancelled");
                count++;
            }
            return count;
        }

        private bool TryChargeReinforcement(BasePlayer player, ReinforcementProfile profile)
        {
            int scrapCost = Mathf.Max(0, Mathf.RoundToInt(profile.ScrapCost * GetSupportTrustCostMultiplier(player.userID)));
            if (scrapCost > 0)
            {
                ItemDefinition scrapDef = ItemManager.FindItemDefinition("scrap");
                if (scrapDef == null)
                {
                    Reply(player, "Scrap item definition not found.");
                    return false;
                }
                int available = player.inventory.GetAmount(scrapDef.itemid);
                if (available < scrapCost)
                {
                    Reply(player, $"Need {scrapCost} scrap for {profile.DisplayName}.");
                    return false;
                }
                player.inventory.Take(null, scrapDef.itemid, scrapCost);
                player.Command("note.inv", scrapDef.itemid, -scrapCost);
            }

            if (profile.ServerRewardsCost > 0 && ServerRewards != null)
            {
                object result = ServerRewards.Call("TakePoints", player.userID, profile.ServerRewardsCost);
                if (result is bool && !(bool)result)
                {
                    Reply(player, $"Need {profile.ServerRewardsCost} RP for {profile.DisplayName}.");
                    return false;
                }
            }

            if (profile.EconomicsCost > 0 && Economics != null)
            {
                object result = Economics.Call("Withdraw", player.userID, (double)profile.EconomicsCost);
                if (result is bool && !(bool)result)
                {
                    Reply(player, $"Need ${profile.EconomicsCost:0} for {profile.DisplayName}.");
                    return false;
                }
            }
            return true;
        }


        #endregion

        #region Config / Data

        protected override void LoadDefaultConfig()
        {
            _config = PluginConfig.Default();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("Config was null");
                _config.MergeDefaults();
            }
            catch
            {
                PrintWarning("Config invalid. Rebuilding default config. Rename your old config before reloading if you need to salvage manual edits.");
                _config = PluginConfig.Default();
                SaveConfig();
            }
        }

        private void LoadConfigValues()
        {
            LoadConfig();
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void LoadData()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
                if (_data == null) _data = new StoredData();
                _data.MergeDefaults();
            }
            catch
            {
                _data = new StoredData();
            }
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        private class PluginConfig
        {
            [JsonProperty("Required Setup")]
            public RequiredSetup RequiredSetup = new RequiredSetup();

            [JsonProperty("Commands")]
            public CommandSettings Commands = new CommandSettings();

            [JsonProperty("Chat")]
            public ChatSettings Chat = new ChatSettings();

            [JsonProperty("Patrol Settings")]
            public PatrolSettings PatrolSettings = new PatrolSettings();

            [JsonProperty("Route Settings")]
            public RouteSettings RouteSettings = new RouteSettings();

            [JsonProperty("Squad Settings")]
            public SquadSettings SquadSettings = new SquadSettings();

            [JsonProperty("WorldMind Behavior")]
            public WorldMindBehavior WorldMindBehavior = new WorldMindBehavior();

            [JsonProperty("Player Reinforcements")]
            public PlayerReinforcementSettings PlayerReinforcements = new PlayerReinforcementSettings();

            [JsonProperty("Wanted System")]
            public WantedSystemSettings WantedSystem = new WantedSystemSettings();

            [JsonProperty("Boss Patrols")]
            public BossPatrolSettings BossPatrols = new BossPatrolSettings();

            [JsonProperty("Patrol Scanner")]
            public PatrolScannerSettings PatrolScanner = new PatrolScannerSettings();

            [JsonProperty("Heavy Response")]
            public HeavyResponseSettings HeavyResponse = new HeavyResponseSettings();

            [JsonProperty("Route Heat")]
            public RouteHeatSettings RouteHeat = new RouteHeatSettings();

            [JsonProperty("Patrol Radio")]
            public PatrolRadioSettings PatrolRadio = new PatrolRadioSettings();

            [JsonProperty("Boss Phases")]
            public BossPhaseSettings BossPhases = new BossPhaseSettings();

            [JsonProperty("Intel Drops")]
            public IntelDropSettings IntelDrops = new IntelDropSettings();

            [JsonProperty("Wipe Scaling")]
            public WipeScalingSettings WipeScaling = new WipeScalingSettings();

            [JsonProperty("Base Safety")]
            public BaseSafetySettings BaseSafety = new BaseSafetySettings();

            [JsonProperty("Loot Settings")]
            public LootSettings LootSettings = new LootSettings();

            [JsonProperty("Squad Profiles")]
            public List<SquadProfile> SquadProfiles = DefaultSquadProfiles();

            public static PluginConfig Default() => new PluginConfig();

            public void MergeDefaults()
            {
                if (RequiredSetup == null) RequiredSetup = new RequiredSetup();
                if (Commands == null) Commands = new CommandSettings();
                if (Chat == null) Chat = new ChatSettings();
                if (PatrolSettings == null) PatrolSettings = new PatrolSettings();
                if (RouteSettings == null) RouteSettings = new RouteSettings();
                if (SquadSettings == null) SquadSettings = new SquadSettings();
                if (WorldMindBehavior == null) WorldMindBehavior = new WorldMindBehavior();
                if (PlayerReinforcements == null) PlayerReinforcements = new PlayerReinforcementSettings();
                PlayerReinforcements.MergeDefaults();
                if (WantedSystem == null) WantedSystem = new WantedSystemSettings();
                if (BossPatrols == null) BossPatrols = new BossPatrolSettings();
                if (PatrolScanner == null) PatrolScanner = new PatrolScannerSettings();
                if (HeavyResponse == null) HeavyResponse = new HeavyResponseSettings();
                if (RouteHeat == null) RouteHeat = new RouteHeatSettings();
                if (PatrolRadio == null) PatrolRadio = new PatrolRadioSettings();
                if (BossPhases == null) BossPhases = new BossPhaseSettings();
                if (IntelDrops == null) IntelDrops = new IntelDropSettings();
                if (WipeScaling == null) WipeScaling = new WipeScalingSettings();
                if (BaseSafety == null) BaseSafety = new BaseSafetySettings();
                if (LootSettings == null) LootSettings = new LootSettings();
                if (SquadProfiles == null || SquadProfiles.Count == 0) SquadProfiles = DefaultSquadProfiles();
            }
        }

        private class RequiredSetup
        {
            [JsonProperty("WorldMind Core Plugin Name")]
            public string WorldMindCorePluginName = "WorldMindCoreV2";

            [JsonProperty("Use WorldMind AI")]
            public bool UseWorldMindAI = true;

            [JsonProperty("Use NPC Spawn Plugin If Installed")]
            public bool UseNpcSpawnPluginIfInstalled = true;
        }

        private class CommandSettings
        {
            [JsonProperty("Main Command")]
            public string MainCommand = "wmrp";

            [JsonProperty("Reinforce Command")]
            public string ReinforceCommand = "reinforce";

            [JsonProperty("Scanner Command")]
            public string ScannerCommand = "patrols";
        }

        private class ChatSettings
        {
            [JsonProperty("Chat Color")]
            public string ChatColor = "#00F0FF";
        }

        private class PatrolSettings
        {
            [JsonProperty("Enable Road Patrols")]
            public bool EnableRoadPatrols = true;

            [JsonProperty("Max Active Squads")]
            public int MaxActiveSquads = 3;

            [JsonProperty("Seconds Between Patrol Spawns")]
            public float SecondsBetweenPatrolSpawns = 900f;

            [JsonProperty("Minimum Online Players")]
            public int MinimumOnlinePlayers = 1;

            [JsonProperty("Cleanup On Plugin Unload")]
            public bool CleanupOnPluginUnload = true;

            [JsonProperty("Despawn Squad After Minutes")]
            public float DespawnSquadAfterMinutes = 45f;

            [JsonProperty("Think Interval Seconds")]
            public float ThinkIntervalSeconds = 3f;

            [JsonProperty("Broadcast Patrol Spawns")]
            public bool BroadcastPatrolSpawns = true;

            [JsonProperty("Broadcast Patrol Results")]
            public bool BroadcastPatrolResults = true;

            [JsonProperty("Enable Proximity Warnings")]
            public bool EnableProximityWarnings = true;

            [JsonProperty("Proximity Warning Range")]
            public float ProximityWarningRange = 85f;

            [JsonProperty("Proximity Warning Cooldown Seconds")]
            public float ProximityWarningCooldownSeconds = 120f;
        }

        private class RouteSettings
        {
            [JsonProperty("Use Manual Routes")]
            public bool UseManualRoutes = true;

            [JsonProperty("Loop Routes")]
            public bool LoopRoutes = true;

            [JsonProperty("Waypoint Reach Distance")]
            public float WaypointReachDistance = 8f;
        }

        private class SquadSettings
        {
            [JsonProperty("Default NPC Prefab")]
            public string DefaultNpcPrefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab";

            [JsonProperty("Max Squad Size")]
            public int MaxSquadSize = 8;

            [JsonProperty("Formation Spacing")]
            public float FormationSpacing = 2.5f;

            [JsonProperty("Engage Range")]
            public float EngageRange = 60f;

            [JsonProperty("Chase Range")]
            public float ChaseRange = 90f;

            [JsonProperty("Return To Route After Combat")]
            public bool ReturnToRouteAfterCombat = true;

            [JsonProperty("Seconds After Combat Before Returning")]
            public float SecondsAfterCombatBeforeReturning = 20f;
        }



        private class ReinforcementProfile
        {
            [JsonProperty("Id")] public string Id = "squad";
            [JsonProperty("Display Name")] public string DisplayName = "Friendly Backup Squad";
            [JsonProperty("Leader Name")] public string LeaderName = "Support Lead";
            [JsonProperty("Member Name")] public string MemberName = "Support Rifleman";
            [JsonProperty("NPC Count")] public int NpcCount = 3;
            [JsonProperty("Health")] public float Health = 150f;
            [JsonProperty("Cooldown Seconds")] public float CooldownSeconds = 1800f;
            [JsonProperty("Scrap Cost")] public int ScrapCost = 250;
            [JsonProperty("ServerRewards Cost")] public int ServerRewardsCost = 0;
            [JsonProperty("Economics Cost")] public double EconomicsCost = 0;
            [JsonProperty("Weapons")] public List<string> Weapons = new List<string> { "rifle.semiauto", "smg.thompson", "shotgun.pump" };
            [JsonProperty("Clothing")] public List<string> Clothing = new List<string> { "hazmatsuit_scientist_peacekeeper" };
        }

        private class PlayerReinforcementSettings
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Require Permission")] public bool RequirePermission = false;
            [JsonProperty("Only During Active Patrol Combat")] public bool OnlyDuringActivePatrolCombat = true;
            [JsonProperty("Combat Check Range")] public float CombatCheckRange = 140f;
            [JsonProperty("Max Active Friendly Squads Per Player")] public int MaxActiveFriendlySquadsPerPlayer = 1;
            [JsonProperty("Deny At Wanted Level")] public int DenyAtWantedLevel = 4;
            [JsonProperty("Wanted Denial Can Trigger Hunter")]
            public bool WantedDenialCanTriggerHunter = true;

            [JsonProperty("Minimum Trust Before Denial")]
            public int MinimumTrustBeforeDenial = -50;

            [JsonProperty("Trust Gain Patrol Clear")]
            public int TrustGainPatrolClear = 3;

            [JsonProperty("Trust Gain Boss Clear")]
            public int TrustGainBossClear = 8;

            [JsonProperty("Trust Loss Friendly Kill")]
            public int TrustLossFriendlyKill = 15;

            [JsonProperty("Trust Loss Denied Wanted")]
            public int TrustLossDeniedWanted = 2;

            [JsonProperty("Trusted Scrap Discount Percent At 100 Trust")]
            public int TrustedScrapDiscountPercentAt100Trust = 25;
            [JsonProperty("Profiles")]
            public List<ReinforcementProfile> Profiles = DefaultReinforcementProfiles();

            public void MergeDefaults()
            {
                if (Profiles == null || Profiles.Count == 0) Profiles = DefaultReinforcementProfiles();
            }

            public ReinforcementProfile GetProfile(string id)
            {
                if (Profiles == null) return null;
                return Profiles.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static List<ReinforcementProfile> DefaultReinforcementProfiles()
        {
            var profiles = new List<ReinforcementProfile>();

            profiles.Add(new ReinforcementProfile
            {
                Id = "squad",
                DisplayName = "Friendly Backup Squad",
                NpcCount = 3,
                ScrapCost = 250,
                CooldownSeconds = 1800f,
                Weapons = new List<string> { "rifle.semiauto", "smg.thompson", "shotgun.pump" }
            });

            profiles.Add(new ReinforcementProfile
            {
                Id = "medic",
                DisplayName = "Medic Support Team",
                LeaderName = "Support Medic",
                MemberName = "Medic Guard",
                NpcCount = 2,
                ScrapCost = 175,
                CooldownSeconds = 1200f,
                Weapons = new List<string> { "smg.thompson", "rifle.semiauto" }
            });

            profiles.Add(new ReinforcementProfile
            {
                Id = "overwatch",
                DisplayName = "Overwatch Pair",
                LeaderName = "Overwatch Lead",
                MemberName = "Marksman",
                NpcCount = 2,
                ScrapCost = 350,
                CooldownSeconds = 2700f,
                Weapons = new List<string> { "rifle.bolt", "rifle.semiauto" }
            });

            profiles.Add(new ReinforcementProfile
            {
                Id = "emergency",
                DisplayName = "Emergency Response Squad",
                LeaderName = "Emergency Lead",
                MemberName = "Response Soldier",
                NpcCount = 5,
                ScrapCost = 750,
                CooldownSeconds = 5400f,
                Health = 190f,
                Weapons = new List<string> { "rifle.ak", "smg.mp5", "shotgun.spas12", "rifle.semiauto", "smg.thompson" }
            });

            return profiles;
        }

        private class WantedSystemSettings
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("NPC Kill Wanted Points")] public int NpcKillWantedPoints = 2;
            [JsonProperty("Boss Kill Wanted Points")] public int BossKillWantedPoints = 15;
            [JsonProperty("Friendly Damage Wanted Points")] public int FriendlyDamageWantedPoints = 1;
            [JsonProperty("Friendly Kill Wanted Points")] public int FriendlyKillWantedPoints = 25;
            [JsonProperty("Max Score")] public int MaxScore = 500;
            [JsonProperty("Level 1 Score")] public int Level1Score = 8;
            [JsonProperty("Level 2 Score")] public int Level2Score = 20;
            [JsonProperty("Level 3 Score")] public int Level3Score = 40;
            [JsonProperty("Level 4 Score")] public int Level4Score = 75;
            [JsonProperty("Level 5 Score")] public int Level5Score = 120;
            [JsonProperty("Decay Every Minutes")] public float DecayEveryMinutes = 30f;
            [JsonProperty("Decay Points")] public int DecayPoints = 3;
            [JsonProperty("Notify Player On Level Up")] public bool NotifyPlayerOnLevelUp = true;
            [JsonProperty("Hunter Squads Enabled")] public bool HunterSquadsEnabled = true;
            [JsonProperty("Hunter Spawn Wanted Level")] public int HunterSpawnWantedLevel = 3;
            [JsonProperty("Hunter Spawn Chance Percent")] public int HunterSpawnChancePercent = 18;
            [JsonProperty("Smart Hunter Check Seconds")] public float SmartHunterCheckSeconds = 300f;
            [JsonProperty("Smart Hunter Chance Percent")] public int SmartHunterChancePercent = 12;
            [JsonProperty("Smart Hunter Road Range")] public float SmartHunterRoadRange = 250f;
        }

        private class BossPatrolSettings
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Chance Per Patrol Percent")] public int ChancePerPatrolPercent = 6;
            [JsonProperty("Broadcast Boss Spawns")] public bool BroadcastBossSpawns = true;
        }

        private class PatrolScannerSettings
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Require Permission For Basic Scanner")] public bool RequirePermissionForBasic = false;
            [JsonProperty("Max Results")] public int MaxResults = 5;
        }

        private class HeavyResponseSettings
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Trigger When Remaining Fraction")]
            public float TriggerWhenRemainingFraction = 0.5f;
            [JsonProperty("Chance Percent")] public int ChancePercent = 35;
            [JsonProperty("Spawn Distance")] public float SpawnDistance = 45f;
            [JsonProperty("Broadcast")] public bool Broadcast = true;
        }




        private class RouteHeatSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Max Heat")]
            public int MaxHeat = 100;

            [JsonProperty("Hot Route Starts At")]
            public int HotRouteStartsAt = 60;

            [JsonProperty("Critical Route Starts At")]
            public int CriticalRouteStartsAt = 85;

            [JsonProperty("NPC Killed Heat")]
            public int NpcKilledHeat = 1;

            [JsonProperty("Boss NPC Killed Heat")]
            public int BossNpcKilledHeat = 4;

            [JsonProperty("Patrol Combat Heat")]
            public int CombatHeat = 1;

            [JsonProperty("Squad Cleared Heat")]
            public int SquadClearedHeat = 8;

            [JsonProperty("Boss Cleared Heat")]
            public int BossClearedHeat = 15;

            [JsonProperty("Heavy Response Heat")]
            public int HeavyResponseHeat = 6;

            [JsonProperty("Recon Reported Heat")]
            public int ReconReportedHeat = 5;

            [JsonProperty("Heat Decay Every Minutes")]
            public int HeatDecayEveryMinutes = 45;

            [JsonProperty("Heat Decay Points")]
            public int HeatDecayPoints = 3;

            [JsonProperty("Hot Route Ambush Chance Percent")]
            public int HotRouteAmbushChancePercent = 20;

            [JsonProperty("Hot Route Heavy Chance Percent")]
            public int HotRouteHeavyChancePercent = 12;

            [JsonProperty("Critical Route Boss Chance Percent")]
            public int CriticalRouteBossChancePercent = 10;
        }

        private class PatrolRadioSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Seconds Between Chatter")]
            public float SecondsBetweenChatter = 420f;

            [JsonProperty("Broadcast Recon Reports")]
            public bool BroadcastReconReports = true;

            [JsonProperty("Include Wanted Warnings")]
            public bool IncludeWantedWarnings = true;

            [JsonProperty("Include Route Heat Warnings")]
            public bool IncludeRouteHeatWarnings = true;
        }

        private class BossPhaseSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Phase 2 Remaining Fraction")]
            public float Phase2RemainingFraction = 0.60f;

            [JsonProperty("Phase 3 Remaining Fraction")]
            public float Phase3RemainingFraction = 0.35f;

            [JsonProperty("Phase 4 Remaining Fraction")]
            public float Phase4RemainingFraction = 0.15f;

            [JsonProperty("Call Backup On Phase 3")]
            public bool CallBackupOnPhase3 = true;

            [JsonProperty("Broadcast Boss Phases")]
            public bool BroadcastBossPhases = true;
        }

        private class IntelDropSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Chance Percent")]
            public int ChancePercent = 35;

            [JsonProperty("Intel Item Shortname")]
            public string IntelItemShortname = "paper";

            [JsonProperty("Include Route Heat In Item Name")]
            public bool IncludeRouteHeatInItemName = true;
        }

        private class WipeScalingSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Early Wipe Days")]
            public int EarlyWipeDays = 2;

            [JsonProperty("Late Wipe Starts Day")]
            public int LateWipeStartsDay = 7;

            [JsonProperty("Early Wipe Max Auto Type")]
            public string EarlyWipeMaxAutoType = "recon";

            [JsonProperty("Late Wipe Extra Heavy Chance Percent")]
            public int LateWipeExtraHeavyChancePercent = 8;
        }

        private class BaseSafetySettings
        {
            [JsonProperty("Avoid Building Privilege")]
            public bool AvoidBuildingPrivilege = true;

            [JsonProperty("Minimum Distance From Tool Cupboards")]
            public float MinimumDistanceFromToolCupboards = 80f;

            [JsonProperty("Reject Route If Any Spawn Point Is Unsafe")]
            public bool RejectRouteIfAnySpawnPointIsUnsafe = false;
        }

        private class LootSettings
        {
            [JsonProperty("Enable Loot Crates")]
            public bool EnableLootCrates = true;

            [JsonProperty("Loot Crate Prefab")]
            public string LootCratePrefab = "assets/prefabs/deployable/large wood storage/box.wooden.large.prefab";
        }

        private class WorldMindBehavior
        {
            [JsonProperty("Allow AI Event Names")]
            public bool AllowAIEventNames = true;

            [JsonProperty("Allow AI Radio Messages")]
            public bool AllowAIRadioMessages = true;

            [JsonProperty("Allow Player Specific Taunts")]
            public bool AllowPlayerSpecificTaunts = true;

            [JsonProperty("Allow Event Memory Writes")]
            public bool AllowEventMemoryWrites = true;

            [JsonProperty("Trash Talk Level")]
            public int TrashTalkLevel = 2;

            [JsonProperty("Debug WorldMind Calls")]
            public bool DebugWorldMindCalls = false;
        }

        private class LootEntry
        {
            [JsonProperty("Shortname")]
            public string Shortname = "scrap";

            [JsonProperty("Min Amount")]
            public int MinAmount = 1;

            [JsonProperty("Max Amount")]
            public int MaxAmount = 1;
        }

        private class SquadProfile
        {
            [JsonProperty("Id")]
            public string Id = "checkpoint";

            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Weight")]
            public int Weight = 10;

            [JsonProperty("Display Name")]
            public string DisplayName = "Road Checkpoint Patrol";

            [JsonProperty("Faction")]
            public string Faction = "Road Command";

            [JsonProperty("Leader Name")]
            public string LeaderName = "Road Captain";

            [JsonProperty("Member Name")]
            public string MemberName = "Road Guard";

            [JsonProperty("NPC Count")]
            public int NpcCount = 4;

            [JsonProperty("Health")]
            public float Health = 150f;

            [JsonProperty("Prefab Override")]
            public string Prefab = "";

            [JsonProperty("NpcSpawn Kit Or Profile")]
            public string NpcSpawnKitOrProfile = "";

            [JsonProperty("Weapons")]
            public List<string> Weapons = new List<string> { "rifle.semiauto", "shotgun.pump", "smg.thompson", "rifle.semiauto" };

            [JsonProperty("Ammo")]
            public Dictionary<string, int> Ammo = new Dictionary<string, int> { ["ammo.rifle"] = 120, ["ammo.pistol"] = 120, ["ammo.shotgun"] = 40 };

            [JsonProperty("Clothing")]
            public List<string> Clothing = new List<string> { "hazmatsuit_scientist" };

            [JsonProperty("Medical Syringes")]
            public int MedicalSyringes = 2;

            [JsonProperty("Create Roadblock Props")]
            public bool CreateRoadblockProps = false;

            [JsonProperty("Roadblock Prefabs")]
            public List<string> RoadblockPrefabs = new List<string>();

            [JsonProperty("Friendly Support Profile")]
            public bool Friendly = false;

            [JsonProperty("Drop Loot Crate On Squad Clear")]
            public bool DropLootCrate = true;

            [JsonProperty("ServerRewards RP On Clear")]
            public int ServerRewardsRP = 0;

            [JsonProperty("Economics Money On Clear")]
            public double EconomicsMoney = 0;

            [JsonProperty("Loot")]
            public List<LootEntry> Loot = new List<LootEntry>
            {
                new LootEntry { Shortname = "scrap", MinAmount = 75, MaxAmount = 175 },
                new LootEntry { Shortname = "ammo.rifle", MinAmount = 40, MaxAmount = 120 },
                new LootEntry { Shortname = "syringe.medical", MinAmount = 1, MaxAmount = 4 }
            };
        }

        private static List<SquadProfile> DefaultSquadProfiles()
        {
            return new List<SquadProfile>
            {
                new SquadProfile
                {
                    Id = "checkpoint",
                    DisplayName = "Road Checkpoint Patrol",
                    Faction = "Road Command",
                    LeaderName = "Road Captain",
                    MemberName = "Road Soldier",
                    NpcCount = 4,
                    Weight = 20,
                    Weapons = new List<string> { "rifle.semiauto", "shotgun.pump", "smg.thompson", "rifle.semiauto" }
                },
                new SquadProfile
                {
                    Id = "courier",
                    DisplayName = "Road Courier Squad",
                    Faction = "Road Command",
                    LeaderName = "Road Courier",
                    MemberName = "Courier Escort",
                    NpcCount = 4,
                    Weight = 12,
                    Health = 175f,
                    Weapons = new List<string> { "smg.thompson", "rifle.semiauto", "shotgun.pump", "smg.thompson" }
                },
                new SquadProfile
                {
                    Id = "scientist",
                    DisplayName = "Scientist Road Sweep",
                    Faction = "Road Command",
                    LeaderName = "Scientist Lead",
                    MemberName = "Scientist Trooper",
                    NpcCount = 5,
                    Weight = 10,
                    Health = 200f,
                    Weapons = new List<string> { "rifle.lr300", "rifle.semiauto", "smg.mp5", "shotgun.spas12", "rifle.semiauto" },
                    Clothing = new List<string> { "hazmatsuit_scientist_peacekeeper" }
                },
                new SquadProfile
                {
                    Id = "bandit",
                    DisplayName = "Bandit Toll Crew",
                    Faction = "Road Command",
                    LeaderName = "Toll Boss",
                    MemberName = "Road Bandit",
                    NpcCount = 4,
                    Weight = 14,
                    Health = 160f,
                    Weapons = new List<string> { "shotgun.spas12", "smg.thompson", "shotgun.pump", "rifle.semiauto" }
                },
                new SquadProfile
                {
                    Id = "hunter",
                    DisplayName = "Hunter Killer Squad",
                    Faction = "Road Command",
                    LeaderName = "Hunter Lead",
                    MemberName = "Hunter Rifleman",
                    NpcCount = 5,
                    Weight = 4,
                    Health = 225f,
                    Weapons = new List<string> { "rifle.ak", "rifle.bolt", "smg.mp5", "shotgun.spas12", "rifle.semiauto" },
                    Loot = new List<LootEntry> { new LootEntry { Shortname = "scrap", MinAmount = 250, MaxAmount = 500 }, new LootEntry { Shortname = "ammo.rifle", MinAmount = 120, MaxAmount = 240 }, new LootEntry { Shortname = "syringe.medical", MinAmount = 3, MaxAmount = 8 } }
                }
                ,new SquadProfile
                {
                    Id = "ambush",
                    DisplayName = "Road Ambush Team",
                    Faction = "Road Patrol",
                    LeaderName = "Ambush Lead",
                    MemberName = "Ambusher",
                    NpcCount = 5,
                    Weight = 9,
                    Health = 180f,
                    Weapons = new List<string> { "rifle.semiauto", "smg.thompson", "shotgun.pump", "smg.mp5", "rifle.semiauto" },
                    Loot = new List<LootEntry> { new LootEntry { Shortname = "scrap", MinAmount = 125, MaxAmount = 250 }, new LootEntry { Shortname = "grenade.f1", MinAmount = 1, MaxAmount = 3 }, new LootEntry { Shortname = "ammo.pistol", MinAmount = 80, MaxAmount = 160 } }
                },
                new SquadProfile
                {
                    Id = "roadblock",
                    DisplayName = "Roadblock Crew",
                    Faction = "Road Patrol",
                    LeaderName = "Roadblock Boss",
                    MemberName = "Blockade Guard",
                    NpcCount = 6,
                    Weight = 7,
                    Health = 190f,
                    CreateRoadblockProps = true,
                    RoadblockPrefabs = new List<string> { "assets/prefabs/misc/barricades/woodenbarricade/barricade.wood.prefab", "assets/prefabs/misc/barricades/woodenbarricade/barricade.wood.prefab", "assets/prefabs/misc/barricades/sandbags/sandbag.deployed.prefab" },
                    Weapons = new List<string> { "shotgun.spas12", "rifle.semiauto", "smg.thompson", "rifle.semiauto", "shotgun.pump", "smg.mp5" },
                    Loot = new List<LootEntry> { new LootEntry { Shortname = "scrap", MinAmount = 175, MaxAmount = 350 }, new LootEntry { Shortname = "metal.refined", MinAmount = 10, MaxAmount = 40 }, new LootEntry { Shortname = "roadsigns", MinAmount = 2, MaxAmount = 8 } }
                },
                new SquadProfile
                {
                    Id = "recon",
                    DisplayName = "Road Recon Team",
                    Faction = "Road Patrol",
                    LeaderName = "Recon Lead",
                    MemberName = "Recon Scout",
                    NpcCount = 2,
                    Weight = 12,
                    Health = 140f,
                    Weapons = new List<string> { "rifle.semiauto", "smg.thompson" },
                    Loot = new List<LootEntry> { new LootEntry { Shortname = "scrap", MinAmount = 60, MaxAmount = 140 }, new LootEntry { Shortname = "paper", MinAmount = 1, MaxAmount = 2 }, new LootEntry { Shortname = "ammo.pistol", MinAmount = 40, MaxAmount = 90 } }
                },
                new SquadProfile
                {
                    Id = "heavy",
                    DisplayName = "Heavy Response Team",
                    Faction = "Road Patrol",
                    LeaderName = "Heavy Lead",
                    MemberName = "Heavy Soldier",
                    NpcCount = 5,
                    Weight = 3,
                    Health = 240f,
                    Weapons = new List<string> { "rifle.ak", "rifle.lr300", "smg.mp5", "shotgun.spas12", "rifle.semiauto" },
                    Loot = new List<LootEntry> { new LootEntry { Shortname = "scrap", MinAmount = 225, MaxAmount = 450 }, new LootEntry { Shortname = "ammo.rifle", MinAmount = 100, MaxAmount = 220 }, new LootEntry { Shortname = "syringe.medical", MinAmount = 3, MaxAmount = 8 } }
                },
                new SquadProfile
                {
                    Id = "boss",
                    DisplayName = "Heavy Road Boss Escort",
                    Faction = "Road Patrol",
                    LeaderName = "Road Boss",
                    MemberName = "Boss Guard",
                    NpcCount = 7,
                    Weight = 2,
                    Health = 275f,
                    Weapons = new List<string> { "rifle.ak", "rifle.lr300", "rifle.bolt", "smg.mp5", "shotgun.spas12", "rifle.semiauto", "lmg.m249" },
                    ServerRewardsRP = 50,
                    EconomicsMoney = 250,
                    Loot = new List<LootEntry> { new LootEntry { Shortname = "scrap", MinAmount = 300, MaxAmount = 700 }, new LootEntry { Shortname = "metal.refined", MinAmount = 25, MaxAmount = 100 }, new LootEntry { Shortname = "riflebody", MinAmount = 1, MaxAmount = 3 }, new LootEntry { Shortname = "techparts", MinAmount = 2, MaxAmount = 6 } }
                }
            };
        }

        private class StoredData
        {
            [JsonProperty("Routes")]
            public List<PatrolRoute> Routes = new List<PatrolRoute>();

            [JsonProperty("Player Patrol Memory")]
            public Dictionary<string, PlayerPatrolMemory> PlayerPatrolMemory = new Dictionary<string, PlayerPatrolMemory>();

            [JsonProperty("Route Memory")]
            public Dictionary<string, RouteMemory> RouteMemory = new Dictionary<string, RouteMemory>();

            [JsonProperty("Wanted Players")]
            public Dictionary<string, WantedRecord> Wanted = new Dictionary<string, WantedRecord>();

            [JsonProperty("Support Trust")]
            public Dictionary<string, SupportTrustRecord> SupportTrust = new Dictionary<string, SupportTrustRecord>();

            [JsonProperty("World State")]
            public WorldStateRecord WorldState = new WorldStateRecord();

            public void MergeDefaults()
            {
                if (Routes == null) Routes = new List<PatrolRoute>();
                if (PlayerPatrolMemory == null) PlayerPatrolMemory = new Dictionary<string, PlayerPatrolMemory>();
                if (RouteMemory == null) RouteMemory = new Dictionary<string, RouteMemory>();
                if (Wanted == null) Wanted = new Dictionary<string, WantedRecord>();
                if (SupportTrust == null) SupportTrust = new Dictionary<string, SupportTrustRecord>();
                if (WorldState == null) WorldState = new WorldStateRecord();
            }
        }

        private class PatrolRoute
        {
            [JsonProperty("Name")]
            public string Name = "Unnamed Route";

            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Biome")]
            public string Biome = "unknown";

            [JsonProperty("Waypoints")]
            public List<VectorDto> Waypoints = new List<VectorDto>();
        }

        private class PlayerPatrolMemory
        {
            [JsonProperty("Display Name")]
            public string DisplayName = "";

            [JsonProperty("Patrol NPC Kills")]
            public int PatrolNpcKills;

            [JsonProperty("Last Killed Profile")]
            public string LastKilledProfile = "";

            [JsonProperty("Threat Rating")]
            public int ThreatRating;

            [JsonProperty("Last Kill At")]
            public long LastKillAt;
        }

        private class RouteMemory
        {
            [JsonProperty("Times Used")]
            public int TimesUsed;

            [JsonProperty("Squads Killed")]
            public int SquadsKilled;

            [JsonProperty("Most Recent Killer")]
            public string MostRecentKiller = "";

            [JsonProperty("Last Result")]
            public string LastResult = "";

            [JsonProperty("Last Used At")]
            public long LastUsedAt;

            [JsonProperty("Last Completed At")]
            public long LastCompletedAt;

            [JsonProperty("Heat")]
            public int Heat;

            [JsonProperty("Last Heat Reason")]
            public string LastHeatReason = "";
        }

        private class WantedRecord
        {
            [JsonProperty("Display Name")]
            public string DisplayName = "";

            [JsonProperty("Score")]
            public int Score;

            [JsonProperty("Last Reason")]
            public string LastReason = "";

            [JsonProperty("Last Changed At")]
            public long LastChangedAt;
        }

        private class SupportTrustRecord
        {
            [JsonProperty("Display Name")]
            public string DisplayName = "";

            [JsonProperty("Score")]
            public int Score;

            [JsonProperty("Last Reason")]
            public string LastReason = "";

            [JsonProperty("Last Changed At")]
            public long LastChangedAt;
        }

        private class WorldStateRecord
        {
            [JsonProperty("First Seen At")]
            public long FirstSeenAt;

            [JsonProperty("Last Heat Decay At")]
            public long LastHeatDecayAt;
        }

        private class VectorDto
        {
            [JsonProperty("X")]
            public float X;

            [JsonProperty("Y")]
            public float Y;

            [JsonProperty("Z")]
            public float Z;

            public Vector3 ToVector3() => new Vector3(X, Y, Z);

            public static VectorDto FromVector3(Vector3 v) => new VectorDto { X = v.x, Y = v.y, Z = v.z };

            public override string ToString()
            {
                return $"{X.ToString("0.0", CultureInfo.InvariantCulture)}, {Y.ToString("0.0", CultureInfo.InvariantCulture)}, {Z.ToString("0.0", CultureInfo.InvariantCulture)}";
            }
        }

        private class ActiveNpc
        {
            public string SquadId;
            public string ProfileId;
            public string Role;
            public bool IsFriendly;
            public bool IsBoss;
            public float SpawnedAt;
        }

        private class ActiveSquad
        {
            public string SquadId;
            public string SquadName;
            public string SquadType;
            public string Faction;
            public string RouteName;
            public float SpawnedAt;
            public int CurrentWaypointIndex;
            public List<Vector3> Waypoints = new List<Vector3>();
            public bool LoopRoute;
            public List<ulong> NpcIds = new List<ulong>();
            public bool InCombat;
            public float LastCombatTime;
            public Vector3 LastKnownEnemyPosition;
            public string LastAttackerName;
            public List<ulong> PropIds = new List<ulong>();
            public bool LootSpawned;
            public float LastWarningAt;
            public bool Friendly;
            public ulong OwnerUserId;
            public bool BackupCalled;
            public bool IsBoss;
            public bool IsHeavyResponse;
            public ulong TargetPlayerId;
            public string State;
            public int StartedNpcCount;
            public int BossPhase;
            public string Morale;
            public float LastPhaseAt;
        }

        private class RouteRecorder
        {
            public string Name;
            public List<Vector3> Points = new List<Vector3>();
        }

        #endregion
    }
}
