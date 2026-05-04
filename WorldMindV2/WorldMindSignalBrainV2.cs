using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindSignalBrainV2", "Devi8d0ne", "2.1.0")]
    [Description("Island nervous system for the WorldMind suite. Collects Rust signals, detects patterns, tracks player/grid heat, and feeds WorldMind quietly.")]
    public class WorldMindSignalBrainV2 : RustPlugin
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
DDDDDDDDD        VVVV        8888888888     DDDDDDDDD
DDDDDDDD          VV          88888888      DDDDDDDD
";
        private const string PermissionAdmin = "worldmindsignalbrainv2.admin";
        private const string DataFile = "WorldMindSignalBrainV2/SignalBrainData";

        [PluginReference] private Plugin WorldMindV2;
        [PluginReference] private Plugin WorldMindDiscordMind;

        private PluginConfig _config;
        private StoredData _data;
        private readonly Dictionary<string, double> _playerDamageCooldown = new Dictionary<string, double>();
        private readonly Dictionary<string, double> _eventCooldowns = new Dictionary<string, double>();
        private readonly Dictionary<string, double> _dedupeCooldowns = new Dictionary<string, double>();

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermissionAdmin, this);
            LoadConfigValues();
            LoadData();
            PrintStartup();
        }

        private void OnServerInitialized()
        {
            timer.Once(3f, RegisterWithWorldMind);
            timer.Every(Math.Max(15f, _config.Diagnostics.HeartbeatSeconds), SendHeartbeat);
            if (_config.IslandPulse.Enabled)
                timer.Every(Math.Max(60f, _config.IslandPulse.PulseEverySeconds), SendIslandPulse);
        }

        private void Unload()
        {
            SaveData();
        }

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
                if (_config == null) throw new Exception("Config read returned null.");
                _config.Normalize();
            }
            catch (Exception ex)
            {
                PrintError("Config read failed. Existing config was NOT overwritten. Runtime defaults are being used this session only. Error: " + ex.Message);
                _config = PluginConfig.Default();
                _config.Normalize();
            }
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void LoadConfigValues()
        {
            LoadConfig();
        }

        private void LoadData()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFile);
                if (_data == null) _data = new StoredData();
            }
            catch (Exception ex)
            {
                PrintError("Data read failed. Existing data JSON was NOT overwritten during load. Error: " + ex.Message);
                _data = new StoredData();
            }

            _data.Normalize();
        }

        private void SaveData()
        {
            if (_data == null) return;
            Interface.Oxide.DataFileSystem.WriteObject(DataFile, _data);
        }

        #endregion

        #region Rust Hooks - Quiet Signal Collection

        private void OnPlayerConnected(BasePlayer player)
        {
            if (!_config.Signals.PlayerConnections) return;
            if (player == null) return;

            Dictionary<string, object> truth = BasePlayerTruth(player);
            truth["connectionType"] = "connected";
            truth["onlineCount"] = BasePlayer.activePlayerList == null ? 0 : BasePlayer.activePlayerList.Count;
            EmitSignal("player_connected", player.UserIDString, truth, false, false);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (!_config.Signals.PlayerConnections) return;
            if (player == null) return;

            Dictionary<string, object> truth = BasePlayerTruth(player);
            truth["connectionType"] = "disconnected";
            truth["reason"] = reason ?? "";
            truth["onlineCount"] = BasePlayer.activePlayerList == null ? 0 : BasePlayer.activePlayerList.Count;
            EmitSignal("player_disconnected", player.UserIDString, truth, false, false);
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (!_config.Signals.PlayerRespawns) return;
            if (player == null) return;

            Dictionary<string, object> truth = BasePlayerTruth(player);
            truth["respawned"] = true;
            EmitSignal("player_respawned", player.UserIDString, truth, false, false);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) return;

            BasePlayer victimPlayer = entity as BasePlayer;
            BasePlayer attackerPlayer = info == null ? null : info.InitiatorPlayer;

            if (victimPlayer != null && _config.Signals.PlayerDeaths)
            {
                Dictionary<string, object> truth = BasePlayerTruth(victimPlayer);
                truth["victimId"] = victimPlayer.UserIDString;
                truth["victimName"] = victimPlayer.displayName;
                truth["attackerId"] = attackerPlayer == null ? "" : attackerPlayer.UserIDString;
                truth["attackerName"] = attackerPlayer == null ? "" : attackerPlayer.displayName;
                truth["weapon"] = GetWeaponShortname(info);
                truth["damageType"] = GetDamageType(info);
                truth["distance"] = attackerPlayer == null ? 0f : Mathf.Round(Vector3.Distance(attackerPlayer.transform.position, victimPlayer.transform.position));
                truth["suicide"] = attackerPlayer != null && attackerPlayer.userID == victimPlayer.userID;
                truth["victimLocation"] = DescribeLocation(victimPlayer.transform.position);
                truth["attackerLocation"] = attackerPlayer == null ? "" : DescribeLocation(attackerPlayer.transform.position);
                EmitSignal("player_death", victimPlayer.UserIDString, truth, _config.WorldMindRoutes.SendPlayerDeathsToWorldMind, _config.DiscordRoutes.SendPlayerDeathsToDiscord);
                return;
            }

            if (_config.Signals.ImportantEntityDeaths && IsImportantEntity(entity))
            {
                Dictionary<string, object> truth = BaseEntityTruth(entity);
                truth["attackerId"] = attackerPlayer == null ? "" : attackerPlayer.UserIDString;
                truth["attackerName"] = attackerPlayer == null ? "" : attackerPlayer.displayName;
                truth["weapon"] = GetWeaponShortname(info);
                truth["damageType"] = GetDamageType(info);
                EmitSignal("important_entity_death", attackerPlayer == null ? "" : attackerPlayer.UserIDString, truth, false, _config.DiscordRoutes.SendImportantEntityDeathsToDiscord);
            }
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (!_config.Signals.SeverePlayerDamage) return null;
            if (entity == null || info == null) return null;

            BasePlayer victim = entity as BasePlayer;
            BasePlayer attacker = info.InitiatorPlayer;
            if (victim == null || attacker == null || victim.userID == attacker.userID) return null;

            float damage = info.damageTypes == null ? 0f : info.damageTypes.Total();
            if (damage < _config.Thresholds.SevereDamageMinimum) return null;

            string key = victim.UserIDString + ":" + attacker.UserIDString + ":severe_damage";
            if (InCooldown(_playerDamageCooldown, key, _config.Thresholds.SevereDamageCooldownSeconds)) return null;

            Dictionary<string, object> truth = BasePlayerTruth(victim);
            truth["victimId"] = victim.UserIDString;
            truth["victimName"] = victim.displayName;
            truth["attackerId"] = attacker.UserIDString;
            truth["attackerName"] = attacker.displayName;
            truth["damage"] = Mathf.Round(damage);
            truth["weapon"] = GetWeaponShortname(info);
            truth["damageType"] = GetDamageType(info);
            truth["distance"] = Mathf.Round(Vector3.Distance(attacker.transform.position, victim.transform.position));
            truth["victimLocation"] = DescribeLocation(victim.transform.position);
            truth["attackerLocation"] = DescribeLocation(attacker.transform.position);
            EmitSignal("severe_player_damage", victim.UserIDString, truth, _config.WorldMindRoutes.SendSevereDamageToWorldMind, false);
            return null;
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (!_config.Signals.LootActivity) return;
            if (player == null || entity == null) return;
            if (!IsInterestingLootEntity(entity)) return;

            string key = player.UserIDString + ":loot:" + (entity.net == null ? "unknown" : entity.net.ID.ToString());
            if (InCooldown(_eventCooldowns, key, _config.Thresholds.LootCooldownSeconds)) return;

            Dictionary<string, object> truth = BasePlayerTruth(player);
            truth["lootEntity"] = EntityName(entity);
            truth["entityId"] = entity.net == null ? "" : entity.net.ID.ToString();
            truth["entityLocation"] = DescribeLocation(entity.transform.position);
            EmitSignal("loot_activity", player.UserIDString, truth, false, false);
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            if (!_config.Signals.ResourceGathering) return;
            if (entity == null || item == null || item.info == null) return;

            BasePlayer player = entity.ToPlayer();
            if (player == null) return;
            if (item.amount < _config.Thresholds.MinimumGatherAmount) return;

            string key = player.UserIDString + ":gather:" + item.info.shortname;
            if (InCooldown(_eventCooldowns, key, _config.Thresholds.GatherCooldownSeconds)) return;

            Dictionary<string, object> truth = BasePlayerTruth(player);
            truth["itemShortname"] = item.info.shortname;
            truth["itemName"] = item.info.displayName == null ? item.info.shortname : item.info.displayName.english;
            truth["amount"] = item.amount;
            truth["resourceType"] = dispenser == null ? "unknown" : dispenser.gatherType.ToString();
            EmitSignal("resource_gathered", player.UserIDString, truth, false, false);
        }

        private void OnEntityBuilt(Planner planner, GameObject gameObject)
        {
            if (!_config.Signals.BuildingActivity) return;
            if (planner == null || gameObject == null) return;

            BasePlayer player = planner.GetOwnerPlayer();
            BaseEntity entity = gameObject.ToBaseEntity();
            if (player == null || entity == null) return;

            Dictionary<string, object> truth = BasePlayerTruth(player);
            truth["builtEntity"] = EntityName(entity);
            truth["builtLocation"] = DescribeLocation(entity.transform.position);
            EmitSignal("entity_built", player.UserIDString, truth, false, false);
        }

        private void OnStructureUpgrade(BuildingBlock block, BasePlayer player, BuildingGrade.Enum grade)
        {
            if (!_config.Signals.BuildingActivity) return;
            if (block == null || player == null) return;

            string key = player.UserIDString + ":upgrade:" + grade;
            if (InCooldown(_eventCooldowns, key, _config.Thresholds.BuildCooldownSeconds)) return;

            Dictionary<string, object> truth = BasePlayerTruth(player);
            truth["block"] = EntityName(block);
            truth["grade"] = grade.ToString();
            truth["location"] = DescribeLocation(block.transform.position);
            EmitSignal("structure_upgraded", player.UserIDString, truth, false, false);
        }

        #endregion

        #region Commands

        [ChatCommand("wmsignal")]
        private void CmdSignal(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player))
            {
                Reply(player, "No permission.");
                return;
            }

            string sub = args == null || args.Length == 0 ? "status" : args[0].ToLowerInvariant();
            if (sub == "status") { Reply(player, BuildStatusText()); return; }
            if (sub == "testworldmind") { TestWorldMind(player); return; }
            if (sub == "testdiscord") { TestDiscord(player); return; }
            if (sub == "last") { Reply(player, BuildLastText()); return; }
            if (sub == "heat") { Reply(player, BuildHeatText()); return; }
            if (sub == "recent") { Reply(player, BuildRecentText()); return; }
            if (sub == "pulse") { Reply(player, BuildPulseSummary()); return; }
            if (sub == "clear") { ClearRuntimeData(); Reply(player, "SignalBrain runtime data cleared."); return; }
            if (sub == "debug") { ToggleDebug(player, args); return; }
            if (sub == "grid")
            {
                if (args.Length < 2) { Reply(player, "Usage: /wmsignal grid <grid>"); return; }
                Reply(player, BuildGridText(args[1].ToUpperInvariant())); return;
            }
            if (sub == "player")
            {
                if (args.Length < 2) { Reply(player, "Usage: /wmsignal player <name/id>"); return; }
                Reply(player, BuildPlayerText(string.Join(" ", args.Skip(1).ToArray()))); return;
            }
            if (sub == "reload")
            {
                LoadConfigValues();
                LoadData();
                Reply(player, "WorldMindSignalBrainV2 config and data reloaded.");
                return;
            }

            Reply(player, "Usage: /wmsignal status | heat | grid <grid> | player <name/id> | recent | pulse | last | testworldmind | testdiscord | debug on/off | clear | reload");
        }

        [ChatCommand("testworldmindsignal")]
        private void CmdTestWorldMindSignal(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player)) { Reply(player, "No permission."); return; }
            TestWorldMind(player);
        }

        [ConsoleCommand("wmsignal.status")]
        private void CcmdStatus(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            if (arg.Player() != null && !IsAdmin(arg.Player())) { arg.ReplyWith("No permission."); return; }
            arg.ReplyWith(BuildStatusText());
        }

        [ConsoleCommand("wmsignal.testworldmind")]
        private void CcmdTestWorldMind(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            if (arg.Player() != null && !IsAdmin(arg.Player())) { arg.ReplyWith("No permission."); return; }
            TestWorldMind(null, arg);
        }

        #endregion

        #region Public WorldMindSignal API Hooks

        private object WorldMindSignal_GetPlayerState(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return null;
            PlayerSignalState state;
            return _data.PlayerStates.TryGetValue(playerId, out state) ? state : null;
        }

        private object WorldMindSignal_GetAreaHeat(string grid)
        {
            if (string.IsNullOrEmpty(grid)) return null;
            GridHeatState heat;
            return _data.GridHeat.TryGetValue(grid.ToUpperInvariant(), out heat) ? heat : null;
        }

        private object WorldMindSignal_GetRecentEvents(int count)
        {
            int take = Mathf.Clamp(count, 1, 50);
            return _data.Timeline.OrderByDescending(x => x.TimestampUtc).Take(take).ToList();
        }

        private object WorldMindSignal_GetLastSignal()
        {
            if (_data.Timeline.Count == 0) return null;
            return _data.Timeline[_data.Timeline.Count - 1];
        }

        private object WorldMindSignal_GetSignalSummary()
        {
            return BuildPulsePacket();
        }

        private object WorldMindSignal_IsAreaHot(string grid)
        {
            if (string.IsNullOrEmpty(grid)) return false;
            GridHeatState heat;
            return _data.GridHeat.TryGetValue(grid.ToUpperInvariant(), out heat) && heat.TotalHeat >= _config.Patterns.HotGridThreshold;
        }

        private object WorldMindSignal_IsPlayerInDanger(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return false;
            PlayerSignalState state;
            return _data.PlayerStates.TryGetValue(playerId, out state) && state.DangerScore >= _config.Patterns.PlayerDangerThreshold;
        }

        #endregion

        #region Signal Routing / Intelligence

        private void EmitSignal(string eventType, string playerId, Dictionary<string, object> truth, bool sendToWorldMind, bool sendToDiscord)
        {
            if (!_config.Enabled || string.IsNullOrEmpty(eventType)) return;
            if (truth == null) truth = new Dictionary<string, object>();

            SignalDecision decision = AnalyzeSignal(eventType, playerId, truth);
            if (decision == null) return;

            if (decision.SuppressAsDuplicate)
            {
                _data.Counters.EventsMergedOrSuppressed++;
                UpdateMergeBucket(decision.DedupeKey, eventType, playerId, decision.Grid);
                if (_config.Diagnostics.PrintDebugToConsole) Puts("Suppressed duplicate signal: " + decision.DedupeKey);
                SaveData();
                return;
            }

            truth["category"] = decision.Category;
            truth["severity"] = decision.Severity;
            truth["grid"] = decision.Grid;
            truth["signalScore"] = decision.Score;
            truth["shouldSpeakToPlayer"] = decision.ShouldSpeakToPlayer;
            truth["shouldReportDiscord"] = decision.ShouldReportDiscord;
            truth["shouldSaveMemory"] = decision.ShouldSaveMemory;
            truth["dedupeKey"] = decision.DedupeKey;
            truth["patternReason"] = decision.PatternReason;
            truth["playerSignalState"] = GetPlayerStateSummary(playerId);
            truth["areaHeat"] = GetGridHeatSummary(decision.Grid);
            truth["recentRelatedEvents"] = GetRecentRelatedEvents(eventType, playerId, decision.Grid, 5);

            _data.Counters.EventsDetected++;
            _data.Proof.EventDetected = true;
            _data.Proof.LastEventType = eventType;
            _data.Proof.LastEventUtc = DateTime.UtcNow.ToString("o");
            _data.Proof.LastPlayerId = playerId ?? "";
            _data.Proof.LastSeverity = decision.Severity;
            _data.Proof.LastGrid = decision.Grid;
            _data.Proof.LastTruthJson = JsonConvert.SerializeObject(truth);

            AddTimeline(eventType, playerId, decision.Category, decision.Severity, decision.Grid, truth);

            if (decision.ShouldRecordToWorldMind)
                RecordWorldMindEvent(eventType, playerId, truth);

            if (sendToWorldMind || decision.ShouldAskWorldMind)
                AskWorldMindForSignal(eventType, playerId, truth);

            if (sendToDiscord || decision.ShouldReportDiscord)
                RouteDiscord(eventType, playerId, truth);

            SaveData();
        }

        private SignalDecision AnalyzeSignal(string eventType, string playerId, Dictionary<string, object> truth)
        {
            string category = GetCategoryForEvent(eventType);
            SignalCategorySettings settings = GetCategorySettings(category);
            if (settings != null && !settings.Enabled) return null;

            string grid = ExtractGrid(truth);
            string severity = ClassifySeverity(eventType, truth);
            int score = SeverityScore(severity);
            string patternReason = DetectPatterns(eventType, playerId, grid, truth, ref severity, ref score);

            UpdatePlayerState(eventType, playerId, grid, truth, severity, score, patternReason);
            UpdateGridHeat(eventType, grid, severity, score);

            string dedupeKey = BuildDedupeKey(eventType, playerId, grid, truth);
            bool suppress = false;
            if (_config.Patterns.EnableDedupeBuckets && settings != null)
            {
                bool protectedSeverity = SeverityScore(severity) >= SeverityScore("danger");
                suppress = !protectedSeverity && InCooldown(_dedupeCooldowns, dedupeKey, settings.CooldownSeconds);
            }

            bool record = settings == null || settings.RecordToWorldMind;
            bool ask = settings != null && settings.AllowModelAsk && SeverityScore(severity) >= SeverityScore(settings.MinimumSeverityToAskModel);
            bool discord = settings != null && settings.AllowDiscordReport && SeverityScore(severity) >= SeverityScore(settings.MinimumSeverityToReport);
            bool memory = SeverityScore(severity) >= SeverityScore(settings == null ? "interesting" : settings.MinimumSeverityToSaveMemory);
            bool speak = _config.WorldMindRoutes.AllowPlayerFacingHints && SeverityScore(severity) >= SeverityScore("danger");

            return new SignalDecision
            {
                Category = category,
                Severity = severity,
                Grid = grid,
                Score = score,
                PatternReason = patternReason,
                DedupeKey = dedupeKey,
                SuppressAsDuplicate = suppress,
                ShouldRecordToWorldMind = record,
                ShouldAskWorldMind = ask,
                ShouldReportDiscord = discord,
                ShouldSaveMemory = memory,
                ShouldSpeakToPlayer = speak
            };
        }

        private string ClassifySeverity(string eventType, Dictionary<string, object> truth)
        {
            if (eventType == "player_death") return "danger";
            if (eventType == "severe_player_damage") return "danger";
            if (eventType == "important_entity_death") return "interesting";
            if (eventType == "structure_upgraded")
            {
                string grade = truth.ContainsKey("grade") ? Convert.ToString(truth["grade"]) : "";
                if (grade.IndexOf("TopTier", StringComparison.OrdinalIgnoreCase) >= 0 || grade.IndexOf("Metal", StringComparison.OrdinalIgnoreCase) >= 0) return "interesting";
                return "normal";
            }
            if (eventType == "loot_activity") return "normal";
            if (eventType == "resource_gathered")
            {
                int amount = truth.ContainsKey("amount") ? Convert.ToInt32(truth["amount"]) : 0;
                if (amount >= _config.Patterns.LargeGatherAmount) return "interesting";
                return "noise";
            }
            if (eventType == "player_connected" || eventType == "player_disconnected" || eventType == "player_respawned") return "normal";
            return "normal";
        }

        private string DetectPatterns(string eventType, string playerId, string grid, Dictionary<string, object> truth, ref string severity, ref int score)
        {
            List<string> reasons = new List<string>();
            double now = Interface.Oxide.Now;
            double window = Math.Max(30, _config.Patterns.PatternWindowSeconds);

            if (!string.IsNullOrEmpty(playerId))
            {
                int recentDeaths = CountRecent(playerId, "player_death", window);
                int recentDamage = CountRecent(playerId, "severe_player_damage", window);
                int recentRespawns = CountRecent(playerId, "player_respawned", window);

                if (eventType == "player_death" && recentDeaths >= _config.Patterns.RepeatedDeathThreshold)
                {
                    severity = UpgradeSeverity(severity, "memory_worthy");
                    score += 25;
                    reasons.Add("repeated_deaths");
                }
                if (recentDamage >= _config.Patterns.MultiDamageThreshold)
                {
                    severity = UpgradeSeverity(severity, "danger");
                    score += 20;
                    reasons.Add("multiple_recent_damage_events");
                }
                if (eventType == "player_death" && recentRespawns > 0)
                {
                    severity = UpgradeSeverity(severity, "interesting");
                    score += 10;
                    reasons.Add("died_after_recent_respawn");
                }
            }

            string attackerId = truth.ContainsKey("attackerId") ? Convert.ToString(truth["attackerId"]) : "";
            string victimId = truth.ContainsKey("victimId") ? Convert.ToString(truth["victimId"]) : "";
            if (!string.IsNullOrEmpty(attackerId) && !string.IsNullOrEmpty(victimId))
            {
                string pairKey = attackerId + ":" + victimId;
                PatternCounter pair = GetPatternCounter("pair:" + pairKey);
                if (now - pair.WindowStarted > window)
                {
                    pair.WindowStarted = now;
                    pair.Count = 0;
                }
                pair.Count++;
                pair.LastUtc = DateTime.UtcNow.ToString("o");
                if (pair.Count >= _config.Patterns.SameAttackerVictimThreshold)
                {
                    severity = UpgradeSeverity(severity, "memory_worthy");
                    score += 20;
                    reasons.Add("same_attacker_victim_repeating");
                }
            }

            if (!string.IsNullOrEmpty(grid))
            {
                GridHeatState heat;
                if (_data.GridHeat.TryGetValue(grid, out heat) && heat.TotalHeat >= _config.Patterns.HotGridThreshold)
                {
                    severity = UpgradeSeverity(severity, "interesting");
                    score += 10;
                    reasons.Add("hot_grid");
                }
            }

            return reasons.Count == 0 ? "none" : string.Join(",", reasons.ToArray());
        }

        private void UpdatePlayerState(string eventType, string playerId, string grid, Dictionary<string, object> truth, string severity, int score, string reason)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            PlayerSignalState state;
            if (!_data.PlayerStates.TryGetValue(playerId, out state))
            {
                state = new PlayerSignalState { PlayerId = playerId, CreatedUtc = DateTime.UtcNow.ToString("o") };
                _data.PlayerStates[playerId] = state;
            }

            if (truth.ContainsKey("playerName")) state.PlayerName = Convert.ToString(truth["playerName"]);
            if (truth.ContainsKey("victimName")) state.PlayerName = Convert.ToString(truth["victimName"]);
            state.LastKnownGrid = grid ?? "";
            state.LastEventType = eventType ?? "";
            state.LastSeverity = severity ?? "";
            state.LastThreatReason = reason ?? "";
            state.UpdatedUtc = DateTime.UtcNow.ToString("o");
            state.TotalSignals++;

            if (eventType == "player_death") state.RecentDeaths++;
            if (eventType == "severe_player_damage") state.RecentDamageTaken += truth.ContainsKey("damage") ? Convert.ToInt32(truth["damage"]) : 0;
            if (eventType == "player_respawned") state.RecentRespawns++;

            string attackerId = truth.ContainsKey("attackerId") ? Convert.ToString(truth["attackerId"]) : "";
            if (!string.IsNullOrEmpty(attackerId) && attackerId != playerId)
            {
                PlayerSignalState attacker;
                if (!_data.PlayerStates.TryGetValue(attackerId, out attacker))
                {
                    attacker = new PlayerSignalState { PlayerId = attackerId, CreatedUtc = DateTime.UtcNow.ToString("o") };
                    _data.PlayerStates[attackerId] = attacker;
                }
                if (truth.ContainsKey("attackerName")) attacker.PlayerName = Convert.ToString(truth["attackerName"]);
                attacker.LastKnownGrid = grid ?? attacker.LastKnownGrid;
                attacker.UpdatedUtc = DateTime.UtcNow.ToString("o");
                if (eventType == "player_death") attacker.RecentKills++;
                if (eventType == "severe_player_damage") attacker.RecentDamageDealt += truth.ContainsKey("damage") ? Convert.ToInt32(truth["damage"]) : 0;
                attacker.DangerScore = Mathf.Clamp(attacker.DangerScore + score / 2, 0, 999);
                attacker.CombatHeat = HeatLabel(attacker.DangerScore);
            }

            state.DangerScore = Mathf.Clamp(state.DangerScore + score, 0, 999);
            state.CombatHeat = HeatLabel(state.DangerScore);
            DecayPlayerState(state);
        }

        private void UpdateGridHeat(string eventType, string grid, string severity, int score)
        {
            if (string.IsNullOrEmpty(grid)) return;
            GridHeatState heat;
            if (!_data.GridHeat.TryGetValue(grid, out heat))
            {
                heat = new GridHeatState { Grid = grid, CreatedUtc = DateTime.UtcNow.ToString("o") };
                _data.GridHeat[grid] = heat;
            }
            heat.UpdatedUtc = DateTime.UtcNow.ToString("o");
            heat.TotalHeat = Mathf.Clamp(heat.TotalHeat + score, 0, 9999);
            heat.LastEventType = eventType;
            heat.LastSeverity = severity;
            if (eventType == "player_death") heat.DeathHeat += score;
            else if (eventType == "severe_player_damage") heat.CombatHeat += score;
            else if (eventType == "entity_built" || eventType == "structure_upgraded") heat.BuildHeat += score;
            else if (eventType == "resource_gathered") heat.GatherHeat += score;
            else if (eventType == "loot_activity") heat.LootHeat += score;
            else if (eventType == "important_entity_death") heat.EntityHeat += score;
            heat.HeatLabel = HeatLabel(heat.TotalHeat);
            DecayGridHeat(heat);
        }

        private void RecordWorldMindEvent(string eventType, string playerId, Dictionary<string, object> truth)
        {
            _data.Counters.WorldMindRecordAttempts++;
            object result = null;
            try
            {
                result = Interface.CallHook("WorldMind_RecordEvent", Name, eventType, playerId ?? "", truth ?? new Dictionary<string, object>());
            }
            catch (Exception ex)
            {
                _data.Counters.WorldMindRecordFailures++;
                SetError("WorldMind_RecordEvent exception: " + ex.Message);
                return;
            }

            if (result == null)
            {
                _data.Counters.WorldMindRecordFailures++;
                _data.Proof.WorldMindRecordSucceeded = false;
                SetError("WorldMind_RecordEvent returned null. Core may be missing or hook unavailable.");
                return;
            }

            _data.Counters.WorldMindRecordSuccess++;
            _data.Proof.WorldMindRecordSucceeded = true;
            _data.Proof.LastWorldMindRecordUtc = DateTime.UtcNow.ToString("o");
        }

        private void AskWorldMindForSignal(string eventType, string playerId, Dictionary<string, object> truth)
        {
            _data.Counters.WorldMindAskAttempts++;
            _data.Proof.WorldMindAskAttempted = true;
            _data.Proof.LastWorldMindAskUtc = DateTime.UtcNow.ToString("o");

            Dictionary<string, object> request = new Dictionary<string, object>
            {
                ["Plugin"] = Name,
                ["EventType"] = eventType,
                ["PlayerId"] = playerId ?? "",
                ["PlayerName"] = truth != null && truth.ContainsKey("playerName") ? Convert.ToString(truth["playerName"]) : "",
                ["Tone"] = _config.WorldMindRoutes.WorldMindTone,
                ["Urgency"] = _config.WorldMindRoutes.WorldMindUrgency,
                ["Truth"] = truth ?? new Dictionary<string, object>()
            };

            try
            {
                object result = Interface.CallHook("WorldMind_AskText", request, new Action<string>(message =>
                {
                    _data.Proof.LastWorldMindResponseUtc = DateTime.UtcNow.ToString("o");
                    _data.Proof.LastWorldMindResponsePreview = Truncate(message, 300);
                    if (string.IsNullOrEmpty(message))
                    {
                        _data.Counters.WorldMindAskFailures++;
                        _data.Proof.WorldMindAskSucceeded = false;
                    }
                    else
                    {
                        _data.Counters.WorldMindAskSuccess++;
                        _data.Proof.WorldMindAskSucceeded = true;
                    }
                    SaveData();
                }));

                if (result == null)
                {
                    _data.Counters.WorldMindAskFailures++;
                    _data.Proof.WorldMindAskSucceeded = false;
                    SetError("WorldMind_AskText returned null. Core may be missing or hook unavailable.");
                }
            }
            catch (Exception ex)
            {
                _data.Counters.WorldMindAskFailures++;
                _data.Proof.WorldMindAskSucceeded = false;
                SetError("WorldMind_AskText exception: " + ex.Message);
            }
        }

        private void RouteDiscord(string eventType, string playerId, Dictionary<string, object> truth)
        {
            if (!_config.DiscordRoutes.Enabled) return;

            _data.Counters.DiscordAttempts++;
            _data.Proof.DiscordCalled = true;
            _data.Proof.LastDiscordRouteUtc = DateTime.UtcNow.ToString("o");

            Dictionary<string, object> packet = new Dictionary<string, object>
            {
                ["plugin"] = Name,
                ["eventType"] = eventType,
                ["playerId"] = playerId ?? "",
                ["category"] = truth != null && truth.ContainsKey("category") ? Convert.ToString(truth["category"]) : "signalbrain",
                ["channelKey"] = _config.DiscordRoutes.ChannelKey,
                ["title"] = "WorldMind Signal: " + eventType,
                ["facts"] = truth ?? new Dictionary<string, object>(),
                ["timestampUtc"] = DateTime.UtcNow.ToString("o")
            };

            object result = null;
            try
            {
                result = Interface.CallHook("WorldMindDiscordMind_Route", packet);
                if (result == null) result = Interface.CallHook("WorldMindDiscord_Route", packet);
                if (result == null) result = Interface.CallHook("DiscordMind_Route", packet);
            }
            catch (Exception ex)
            {
                _data.Counters.DiscordFailures++;
                _data.Proof.DiscordQueuedOrSent = false;
                SetError("Discord route exception: " + ex.Message);
                return;
            }

            if (result == null)
            {
                _data.Counters.DiscordFailures++;
                _data.Proof.DiscordQueuedOrSent = false;
                SetError("Discord route returned null. DiscordMind may be missing or hook unavailable.");
                return;
            }

            _data.Counters.DiscordSuccess++;
            _data.Proof.DiscordQueuedOrSent = true;
        }

        private void SendIslandPulse()
        {
            if (!_config.IslandPulse.Enabled) return;
            Dictionary<string, object> pulse = BuildPulsePacket();
            _data.Proof.LastIslandPulseUtc = DateTime.UtcNow.ToString("o");
            _data.Proof.LastIslandPulseSummary = Convert.ToString(pulse["summary"]);
            _data.Counters.IslandPulses++;

            if (_config.IslandPulse.RecordPulseToWorldMind)
                RecordWorldMindEvent("island_pulse", "", pulse);

            if (_config.IslandPulse.AskModelForPulse && _config.IslandPulse.AllowModelPulse)
                AskWorldMindForSignal("island_pulse", "", pulse);

            if (_config.IslandPulse.ReportPulseToDiscord && _config.DiscordRoutes.Enabled)
                RouteDiscord("island_pulse", "", pulse);

            SaveData();
        }

        private Dictionary<string, object> BuildPulsePacket()
        {
            double windowSeconds = Math.Max(60, _config.IslandPulse.PulseLookbackSeconds);
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-windowSeconds);
            List<TimelineEntry> recent = _data.Timeline.Where(x => ParseUtc(x.TimestampUtc) >= cutoff).ToList();
            GridHeatState hottest = _data.GridHeat.Values.OrderByDescending(x => x.TotalHeat).FirstOrDefault();
            PlayerSignalState hotPlayer = _data.PlayerStates.Values.OrderByDescending(x => x.DangerScore).FirstOrDefault();

            Dictionary<string, int> byCategory = new Dictionary<string, int>();
            Dictionary<string, int> bySeverity = new Dictionary<string, int>();
            foreach (TimelineEntry e in recent)
            {
                Increment(byCategory, string.IsNullOrEmpty(e.Category) ? "unknown" : e.Category);
                Increment(bySeverity, string.IsNullOrEmpty(e.Severity) ? "unknown" : e.Severity);
            }

            string summary = "Last " + Mathf.RoundToInt((float)(windowSeconds / 60)) + "m: " + recent.Count + " signals; hottest grid " + (hottest == null ? "none" : hottest.Grid + " " + hottest.HeatLabel) + "; hottest player " + (hotPlayer == null ? "none" : Safe(hotPlayer.PlayerName) + " " + hotPlayer.CombatHeat) + "; island mood " + BuildIslandMood(hottest, hotPlayer, recent.Count) + ".";

            return new Dictionary<string, object>
            {
                ["plugin"] = Name,
                ["eventType"] = "island_pulse",
                ["summary"] = summary,
                ["lookbackSeconds"] = windowSeconds,
                ["recentSignalCount"] = recent.Count,
                ["byCategory"] = byCategory,
                ["bySeverity"] = bySeverity,
                ["hottestGrid"] = hottest,
                ["hottestPlayer"] = hotPlayer,
                ["onlineCount"] = BasePlayer.activePlayerList == null ? 0 : BasePlayer.activePlayerList.Count,
                ["timestampUtc"] = DateTime.UtcNow.ToString("o")
            };
        }

        private void RegisterWithWorldMind()
        {
            Dictionary<string, object> capabilities = new Dictionary<string, object>
            {
                ["purpose"] = "Island nervous system: central Rust hook/signal collector, pattern detector, player state tracker, grid heat map, and pulse generator",
                ["collects"] = new string[] { "connections", "deaths", "severe_damage", "loot", "gathering", "building", "entity_deaths", "area_heat", "player_danger", "island_pulse" },
                ["speaksToPlayers"] = _config.WorldMindRoutes.AllowPlayerFacingHints,
                ["recordsEvents"] = true,
                ["routesDiscord"] = _config.DiscordRoutes.Enabled,
                ["publicHooks"] = new string[] { "WorldMindSignal_GetPlayerState", "WorldMindSignal_GetAreaHeat", "WorldMindSignal_GetRecentEvents", "WorldMindSignal_GetLastSignal", "WorldMindSignal_GetSignalSummary", "WorldMindSignal_IsAreaHot", "WorldMindSignal_IsPlayerInDanger" },
                ["ownerSafeConfig"] = true,
                ["version"] = Version.ToString()
            };

            object result = null;
            try { result = Interface.CallHook("WorldMind_RegisterPlugin", Name, Version.ToString(), "Island nervous system for WorldMind.", capabilities); }
            catch (Exception ex) { SetError("WorldMind_RegisterPlugin exception: " + ex.Message); }

            _data.Proof.RegisteredWithWorldMind = result != null;
            _data.Proof.LastRegistrationUtc = DateTime.UtcNow.ToString("o");
            if (result == null) SetError("WorldMind_RegisterPlugin returned null. PluginReference alone is not proof of connection.");
            SaveData();
        }

        private void SendHeartbeat()
        {
            Dictionary<string, object> heartbeat = new Dictionary<string, object>
            {
                ["plugin"] = Name,
                ["version"] = Version.ToString(),
                ["enabled"] = _config.Enabled,
                ["lastEventType"] = _data.Proof.LastEventType,
                ["lastEventUtc"] = _data.Proof.LastEventUtc,
                ["lastSeverity"] = _data.Proof.LastSeverity,
                ["lastGrid"] = _data.Proof.LastGrid,
                ["lastError"] = _data.Proof.LastError,
                ["eventsDetected"] = _data.Counters.EventsDetected,
                ["eventsMergedOrSuppressed"] = _data.Counters.EventsMergedOrSuppressed,
                ["worldMindRecordSuccess"] = _data.Counters.WorldMindRecordSuccess,
                ["worldMindRecordFailures"] = _data.Counters.WorldMindRecordFailures,
                ["discordSuccess"] = _data.Counters.DiscordSuccess,
                ["discordFailures"] = _data.Counters.DiscordFailures,
                ["playerStates"] = _data.PlayerStates.Count,
                ["trackedGrids"] = _data.GridHeat.Count,
                ["lastIslandPulse"] = _data.Proof.LastIslandPulseSummary
            };

            object result = null;
            try { result = Interface.CallHook("WorldMind_Heartbeat", Name, heartbeat); }
            catch (Exception ex) { SetError("WorldMind_Heartbeat exception: " + ex.Message); }

            _data.Proof.LastHeartbeatUtc = DateTime.UtcNow.ToString("o");
            _data.Proof.HeartbeatAccepted = result != null;
            SaveData();
        }

        #endregion

        #region Tests / Status

        private void TestWorldMind(BasePlayer player, ConsoleSystem.Arg arg = null)
        {
            Dictionary<string, object> truth = new Dictionary<string, object>
            {
                ["test"] = true,
                ["plugin"] = Name,
                ["message"] = "WorldMindSignalBrainV2 island nervous system proof test",
                ["onlineCount"] = BasePlayer.activePlayerList == null ? 0 : BasePlayer.activePlayerList.Count,
                ["timeUtc"] = DateTime.UtcNow.ToString("o")
            };

            EmitSignal("signalbrain_test", player == null ? "" : player.UserIDString, truth, true, false);

            string msg = "WorldMind signal test fired. Check /wmsignal status, /wmsignal last, /wmsignal heat, and /wmsignal pulse for proof.";
            if (player != null) Reply(player, msg);
            if (arg != null) arg.ReplyWith(msg);
        }

        private void TestDiscord(BasePlayer player)
        {
            Dictionary<string, object> truth = new Dictionary<string, object>
            {
                ["test"] = true,
                ["plugin"] = Name,
                ["message"] = "WorldMindSignalBrainV2 Discord route proof test",
                ["timeUtc"] = DateTime.UtcNow.ToString("o")
            };
            RouteDiscord("signalbrain_discord_test", player == null ? "" : player.UserIDString, truth);
            SaveData();
            Reply(player, "Discord signal test fired. Check /wmsignal status and DiscordMind diagnostics for proof.");
        }

        private string BuildStatusText()
        {
            return string.Join("\n", new string[]
            {
                "WorldMindSignalBrainV2 status:",
                "Enabled: " + _config.Enabled,
                "Registered with WorldMind: " + _data.Proof.RegisteredWithWorldMind,
                "Heartbeat accepted: " + _data.Proof.HeartbeatAccepted,
                "Event detected: " + _data.Proof.EventDetected,
                "Last event: " + Safe(_data.Proof.LastEventType) + " / " + Safe(_data.Proof.LastSeverity) + " / " + Safe(_data.Proof.LastGrid),
                "WorldMind records: " + _data.Counters.WorldMindRecordSuccess + " success / " + _data.Counters.WorldMindRecordFailures + " failed",
                "WorldMind asks: " + _data.Counters.WorldMindAskSuccess + " success / " + _data.Counters.WorldMindAskFailures + " failed",
                "Discord routes: " + _data.Counters.DiscordSuccess + " success / " + _data.Counters.DiscordFailures + " failed",
                "Events detected: " + _data.Counters.EventsDetected + " | merged/suppressed: " + _data.Counters.EventsMergedOrSuppressed,
                "Player states: " + _data.PlayerStates.Count + " | grid heat cells: " + _data.GridHeat.Count,
                "Island pulses: " + _data.Counters.IslandPulses,
                "Last error: " + (string.IsNullOrEmpty(_data.Proof.LastError) ? "none" : _data.Proof.LastError)
            });
        }

        private string BuildLastText()
        {
            return string.Join("\n", new string[]
            {
                "WorldMindSignalBrainV2 last proof:",
                "Last event: " + Safe(_data.Proof.LastEventType),
                "Severity/grid: " + Safe(_data.Proof.LastSeverity) + " / " + Safe(_data.Proof.LastGrid),
                "Last player: " + Safe(_data.Proof.LastPlayerId),
                "Last truth: " + Truncate(_data.Proof.LastTruthJson, 650),
                "Last WorldMind response: " + Safe(_data.Proof.LastWorldMindResponsePreview),
                "Last island pulse: " + Safe(_data.Proof.LastIslandPulseSummary),
                "Last Discord route UTC: " + Safe(_data.Proof.LastDiscordRouteUtc),
                "Last error: " + (string.IsNullOrEmpty(_data.Proof.LastError) ? "none" : _data.Proof.LastError)
            });
        }

        private string BuildHeatText()
        {
            List<GridHeatState> top = _data.GridHeat.Values.OrderByDescending(x => x.TotalHeat).Take(8).ToList();
            if (top.Count == 0) return "No grid heat tracked yet.";
            List<string> lines = new List<string> { "Top WorldMind grid heat:" };
            foreach (GridHeatState h in top)
                lines.Add(h.Grid + " | " + h.HeatLabel + " | total " + h.TotalHeat + " | combat " + h.CombatHeat + " | deaths " + h.DeathHeat + " | build " + h.BuildHeat + " | loot " + h.LootHeat);
            return string.Join("\n", lines.ToArray());
        }

        private string BuildGridText(string grid)
        {
            GridHeatState h;
            if (!_data.GridHeat.TryGetValue(grid, out h)) return "No tracked heat for grid " + grid + ".";
            return "Grid " + grid + "\nHeat: " + h.TotalHeat + " / " + h.HeatLabel + "\nCombat: " + h.CombatHeat + "\nDeaths: " + h.DeathHeat + "\nBuild: " + h.BuildHeat + "\nGather: " + h.GatherHeat + "\nLoot: " + h.LootHeat + "\nEntity: " + h.EntityHeat + "\nLast: " + h.LastEventType + " / " + h.LastSeverity + " @ " + h.UpdatedUtc;
        }

        private string BuildPlayerText(string nameOrId)
        {
            PlayerSignalState state = null;
            if (_data.PlayerStates.TryGetValue(nameOrId, out state)) return FormatPlayerState(state);
            state = _data.PlayerStates.Values.FirstOrDefault(x => !string.IsNullOrEmpty(x.PlayerName) && x.PlayerName.IndexOf(nameOrId, StringComparison.OrdinalIgnoreCase) >= 0);
            if (state == null) return "No signal state found for " + nameOrId + ".";
            return FormatPlayerState(state);
        }

        private string FormatPlayerState(PlayerSignalState s)
        {
            return string.Join("\n", new string[]
            {
                "Player signal state:",
                "Name: " + Safe(s.PlayerName),
                "Id: " + Safe(s.PlayerId),
                "Grid: " + Safe(s.LastKnownGrid),
                "Danger: " + s.DangerScore + " / " + Safe(s.CombatHeat),
                "Kills/deaths/respawns: " + s.RecentKills + " / " + s.RecentDeaths + " / " + s.RecentRespawns,
                "Damage dealt/taken: " + s.RecentDamageDealt + " / " + s.RecentDamageTaken,
                "Last event: " + Safe(s.LastEventType) + " / " + Safe(s.LastSeverity),
                "Reason: " + Safe(s.LastThreatReason),
                "Updated: " + Safe(s.UpdatedUtc)
            });
        }

        private string BuildRecentText()
        {
            List<TimelineEntry> recent = _data.Timeline.OrderByDescending(x => x.TimestampUtc).Take(10).ToList();
            if (recent.Count == 0) return "No recent signals.";
            List<string> lines = new List<string> { "Recent WorldMind signals:" };
            foreach (TimelineEntry e in recent)
                lines.Add(e.TimestampUtc + " | " + e.EventType + " | " + e.Severity + " | " + e.Grid + " | " + e.PlayerId);
            return string.Join("\n", lines.ToArray());
        }

        private string BuildPulseSummary()
        {
            Dictionary<string, object> packet = BuildPulsePacket();
            return Convert.ToString(packet["summary"]);
        }

        private void ToggleDebug(BasePlayer player, string[] args)
        {
            if (args == null || args.Length < 2)
            {
                Reply(player, "Debug console logging is " + (_config.Diagnostics.PrintDebugToConsole ? "ON" : "OFF") + ". Usage: /wmsignal debug on/off");
                return;
            }
            string v = args[1].ToLowerInvariant();
            _config.Diagnostics.PrintDebugToConsole = v == "on" || v == "true" || v == "1";
            SaveConfig();
            Reply(player, "Debug console logging is now " + (_config.Diagnostics.PrintDebugToConsole ? "ON" : "OFF") + ".");
        }

        private void ClearRuntimeData()
        {
            _data.PlayerStates.Clear();
            _data.GridHeat.Clear();
            _data.Timeline.Clear();
            _data.Patterns.Clear();
            _data.MergeBuckets.Clear();
            SaveData();
        }

        #endregion

        #region Helpers

        private Dictionary<string, object> BasePlayerTruth(BasePlayer player)
        {
            Vector3 pos = player == null ? Vector3.zero : player.transform.position;
            return new Dictionary<string, object>
            {
                ["playerId"] = player == null ? "" : player.UserIDString,
                ["playerName"] = player == null ? "" : player.displayName,
                ["position"] = FormatPosition(pos),
                ["grid"] = GetGrid(pos),
                ["location"] = DescribeLocation(pos),
                ["health"] = player == null ? 0f : Mathf.Round(player.health),
                ["isSleeping"] = player != null && player.IsSleeping(),
                ["isWounded"] = player != null && player.IsWounded(),
                ["isNpc"] = player != null && player.IsNpc,
                ["timestampUtc"] = DateTime.UtcNow.ToString("o")
            };
        }

        private Dictionary<string, object> BaseEntityTruth(BaseEntity entity)
        {
            Vector3 pos = entity == null ? Vector3.zero : entity.transform.position;
            return new Dictionary<string, object>
            {
                ["entity"] = EntityName(entity),
                ["shortPrefabName"] = entity == null ? "" : entity.ShortPrefabName,
                ["position"] = FormatPosition(pos),
                ["grid"] = GetGrid(pos),
                ["location"] = DescribeLocation(pos),
                ["timestampUtc"] = DateTime.UtcNow.ToString("o")
            };
        }

        private string DescribeLocation(Vector3 position)
        {
            object location = null;
            try { location = Interface.CallHook("WorldMind_DescribeLocation", position); } catch { }
            if (location != null) return Convert.ToString(location);
            return "Grid " + GetGrid(position) + " at " + FormatPosition(position);
        }

        private string GetGrid(Vector3 position)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt((position.x + 4500f) / 150f), 0, 25);
            int z = Mathf.Clamp(Mathf.FloorToInt((4500f - position.z) / 150f), 0, 25);
            char letter = (char)('A' + x);
            return letter + z.ToString();
        }

        private bool IsImportantEntity(BaseCombatEntity entity)
        {
            if (entity == null) return false;
            string name = EntityName(entity).ToLowerInvariant();
            return name.Contains("bradley") || name.Contains("patrolhelicopter") || name.Contains("ch47") || name.Contains("scientist") || name.Contains("npc") || name.Contains("turret") || name.Contains("sam_site");
        }

        private bool IsInterestingLootEntity(BaseEntity entity)
        {
            if (entity == null) return false;
            string name = EntityName(entity).ToLowerInvariant();
            return name.Contains("crate") || name.Contains("barrel") || name.Contains("corpse") || name.Contains("box") || name.Contains("stash") || name.Contains("locker") || name.Contains("furnace") || name.Contains("refinery");
        }

        private string GetWeaponShortname(HitInfo info)
        {
            if (info == null) return "unknown";
            try
            {
                if (info.WeaponPrefab != null) return EntityName(info.WeaponPrefab);
                if (info.Weapon != null && info.Weapon.GetItem() != null && info.Weapon.GetItem().info != null) return info.Weapon.GetItem().info.shortname;
            }
            catch { }
            return "unknown";
        }

        private string GetDamageType(HitInfo info)
        {
            if (info == null || info.damageTypes == null) return "unknown";
            try { return info.damageTypes.GetMajorityDamageType().ToString(); } catch { return "unknown"; }
        }

        private string EntityName(BaseEntity entity)
        {
            if (entity == null) return "unknown";
            if (!string.IsNullOrEmpty(entity.ShortPrefabName)) return entity.ShortPrefabName;
            return entity.GetType().Name;
        }

        private bool InCooldown(Dictionary<string, double> map, string key, double seconds)
        {
            if (map == null || string.IsNullOrEmpty(key)) return false;
            double now = Interface.Oxide.Now;
            double last;
            if (map.TryGetValue(key, out last) && now - last < seconds) return true;
            map[key] = now;
            return false;
        }

        private void AddTimeline(string eventType, string playerId, string category, string severity, string grid, Dictionary<string, object> truth)
        {
            _data.Timeline.Add(new TimelineEntry
            {
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                EventType = eventType ?? "unknown",
                PlayerId = playerId ?? "",
                Category = category ?? "unknown",
                Severity = severity ?? "normal",
                Grid = grid ?? "",
                TruthJson = JsonConvert.SerializeObject(truth ?? new Dictionary<string, object>())
            });

            int max = Math.Max(50, _config.Diagnostics.MaxLocalTimelineEvents);
            while (_data.Timeline.Count > max) _data.Timeline.RemoveAt(0);
        }

        private string ExtractGrid(Dictionary<string, object> truth)
        {
            if (truth != null && truth.ContainsKey("grid")) return Convert.ToString(truth["grid"]).ToUpperInvariant();
            return "unknown";
        }

        private string GetCategoryForEvent(string eventType)
        {
            if (eventType == "player_death" || eventType == "severe_player_damage") return "Combat";
            if (eventType == "player_respawned" || eventType == "player_connected" || eventType == "player_disconnected") return "Lifecycle";
            if (eventType == "loot_activity") return "Loot";
            if (eventType == "resource_gathered") return "Gathering";
            if (eventType == "entity_built" || eventType == "structure_upgraded") return "Building";
            if (eventType == "important_entity_death") return "EntityDeath";
            if (eventType == "island_pulse") return "Pulse";
            return "Utility";
        }

        private SignalCategorySettings GetCategorySettings(string category)
        {
            if (_config.Categories == null) return new SignalCategorySettings();
            if (category == "Combat") return _config.Categories.Combat;
            if (category == "Lifecycle") return _config.Categories.Lifecycle;
            if (category == "Loot") return _config.Categories.Loot;
            if (category == "Gathering") return _config.Categories.Gathering;
            if (category == "Building") return _config.Categories.Building;
            if (category == "EntityDeath") return _config.Categories.EntityDeath;
            if (category == "Pulse") return _config.Categories.Pulse;
            return _config.Categories.Utility;
        }

        private int SeverityScore(string severity)
        {
            if (string.IsNullOrEmpty(severity)) return 10;
            string s = severity.ToLowerInvariant();
            if (s == "noise") return 1;
            if (s == "normal") return 10;
            if (s == "interesting") return 25;
            if (s == "danger") return 50;
            if (s == "critical") return 75;
            if (s == "memory_worthy") return 65;
            if (s == "owner_report") return 70;
            return 10;
        }

        private string UpgradeSeverity(string current, string candidate)
        {
            return SeverityScore(candidate) > SeverityScore(current) ? candidate : current;
        }

        private string HeatLabel(int score)
        {
            if (score >= 250) return "Critical";
            if (score >= 150) return "High";
            if (score >= 75) return "Medium";
            if (score >= 25) return "Low";
            return "Quiet";
        }

        private string BuildDedupeKey(string eventType, string playerId, string grid, Dictionary<string, object> truth)
        {
            string actor = playerId ?? "";
            if (string.IsNullOrEmpty(actor) && truth.ContainsKey("attackerId")) actor = Convert.ToString(truth["attackerId"]);
            return eventType + ":" + actor + ":" + (grid ?? "unknown");
        }

        private void UpdateMergeBucket(string key, string eventType, string playerId, string grid)
        {
            if (string.IsNullOrEmpty(key)) return;
            MergeBucket bucket;
            if (!_data.MergeBuckets.TryGetValue(key, out bucket))
            {
                bucket = new MergeBucket { Key = key, EventType = eventType, PlayerId = playerId ?? "", Grid = grid ?? "" };
                _data.MergeBuckets[key] = bucket;
            }
            bucket.Count++;
            bucket.LastUtc = DateTime.UtcNow.ToString("o");
        }

        private PatternCounter GetPatternCounter(string key)
        {
            PatternCounter counter;
            if (!_data.Patterns.TryGetValue(key, out counter))
            {
                counter = new PatternCounter { Key = key, WindowStarted = Interface.Oxide.Now, Count = 0 };
                _data.Patterns[key] = counter;
            }
            return counter;
        }

        private int CountRecent(string playerId, string eventType, double seconds)
        {
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-seconds);
            return _data.Timeline.Count(x => x.PlayerId == playerId && x.EventType == eventType && ParseUtc(x.TimestampUtc) >= cutoff);
        }

        private List<Dictionary<string, object>> GetRecentRelatedEvents(string eventType, string playerId, string grid, int max)
        {
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-Math.Max(60, _config.Patterns.PatternWindowSeconds));
            return _data.Timeline.Where(x => ParseUtc(x.TimestampUtc) >= cutoff && (x.EventType == eventType || x.PlayerId == playerId || x.Grid == grid))
                .OrderByDescending(x => x.TimestampUtc)
                .Take(Mathf.Clamp(max, 1, 10))
                .Select(x => new Dictionary<string, object> { ["eventType"] = x.EventType, ["playerId"] = x.PlayerId, ["grid"] = x.Grid, ["severity"] = x.Severity, ["utc"] = x.TimestampUtc })
                .ToList();
        }

        private Dictionary<string, object> GetPlayerStateSummary(string playerId)
        {
            PlayerSignalState s;
            if (string.IsNullOrEmpty(playerId) || !_data.PlayerStates.TryGetValue(playerId, out s)) return new Dictionary<string, object>();
            return new Dictionary<string, object> { ["dangerScore"] = s.DangerScore, ["combatHeat"] = s.CombatHeat, ["recentKills"] = s.RecentKills, ["recentDeaths"] = s.RecentDeaths, ["recentDamageTaken"] = s.RecentDamageTaken, ["lastThreatReason"] = s.LastThreatReason };
        }

        private Dictionary<string, object> GetGridHeatSummary(string grid)
        {
            GridHeatState h;
            if (string.IsNullOrEmpty(grid) || !_data.GridHeat.TryGetValue(grid, out h)) return new Dictionary<string, object>();
            return new Dictionary<string, object> { ["grid"] = h.Grid, ["totalHeat"] = h.TotalHeat, ["label"] = h.HeatLabel, ["combat"] = h.CombatHeat, ["deaths"] = h.DeathHeat, ["build"] = h.BuildHeat, ["loot"] = h.LootHeat };
        }

        private void DecayPlayerState(PlayerSignalState state)
        {
            if (state == null) return;
            state.DangerScore = Mathf.Clamp(state.DangerScore - _config.Patterns.PlayerDangerDecayPerSignal, 0, 999);
        }

        private void DecayGridHeat(GridHeatState heat)
        {
            if (heat == null) return;
            int d = _config.Patterns.GridHeatDecayPerSignal;
            heat.TotalHeat = Mathf.Clamp(heat.TotalHeat - d, 0, 9999);
            heat.CombatHeat = Mathf.Clamp(heat.CombatHeat - d, 0, 9999);
            heat.DeathHeat = Mathf.Clamp(heat.DeathHeat - d, 0, 9999);
            heat.BuildHeat = Mathf.Clamp(heat.BuildHeat - d, 0, 9999);
            heat.GatherHeat = Mathf.Clamp(heat.GatherHeat - d, 0, 9999);
            heat.LootHeat = Mathf.Clamp(heat.LootHeat - d, 0, 9999);
            heat.EntityHeat = Mathf.Clamp(heat.EntityHeat - d, 0, 9999);
        }

        private string BuildIslandMood(GridHeatState hottest, PlayerSignalState hotPlayer, int recentCount)
        {
            int gridScore = hottest == null ? 0 : hottest.TotalHeat;
            int playerScore = hotPlayer == null ? 0 : hotPlayer.DangerScore;
            int total = gridScore + playerScore + recentCount * 5;
            if (total >= 400) return "violent";
            if (total >= 220) return "hot";
            if (total >= 100) return "tense";
            if (total >= 30) return "watchful";
            return "quiet";
        }

        private DateTime ParseUtc(string value)
        {
            DateTime dt;
            if (DateTime.TryParse(value, out dt)) return dt.ToUniversalTime();
            return DateTime.MinValue;
        }

        private void SetError(string error)
        {
            _data.Proof.LastError = error ?? "";
            _data.Proof.LastErrorUtc = DateTime.UtcNow.ToString("o");
            if (_config.Diagnostics.PrintDebugToConsole && !string.IsNullOrEmpty(error)) PrintWarning(error);
        }

        private bool IsAdmin(BasePlayer player)
        {
            if (player == null) return false;
            return player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin);
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null) return;
            SendReply(player, "<color=#8FB573>[WorldMindSignal]</color> " + message);
        }

        private string FormatPosition(Vector3 position)
        {
            return Mathf.RoundToInt(position.x) + ", " + Mathf.RoundToInt(position.y) + ", " + Mathf.RoundToInt(position.z);
        }

        private string Safe(string value)
        {
            return string.IsNullOrEmpty(value) ? "none" : value;
        }

        private string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Length <= max) return value;
            return value.Substring(0, max) + "...";
        }

        private void Increment(Dictionary<string, int> counters, string key)
        {
            if (counters == null || string.IsNullOrEmpty(key)) return;
            if (!counters.ContainsKey(key)) counters[key] = 0;
            counters[key]++;
        }

        private void PrintStartup()
        {
            Puts(DV8DAsciiTag);
            Puts("WorldMindSignalBrainV2 loaded. " + MadeWithLoveTag + ".");
            Puts("Purpose: island nervous system -> Rust hooks -> patterns -> player/grid heat -> WorldMind memory/proof routes.");
        }

        #endregion

        #region Config

        private class PluginConfig
        {
            [JsonProperty(Order = 1)] public bool Enabled = true;
            [JsonProperty(Order = 2, PropertyName = "Signal Collection - quiet Rust hooks")] public SignalCollectionConfig Signals = new SignalCollectionConfig();
            [JsonProperty(Order = 3, PropertyName = "Signal Categories - routing and severity gates")] public SignalCategoryConfig Categories = new SignalCategoryConfig();
            [JsonProperty(Order = 4, PropertyName = "Thresholds and Cooldowns")] public ThresholdConfig Thresholds = new ThresholdConfig();
            [JsonProperty(Order = 5, PropertyName = "Pattern Detection and Heat Tracking")] public PatternConfig Patterns = new PatternConfig();
            [JsonProperty(Order = 6, PropertyName = "Island Pulse Summary")] public IslandPulseConfig IslandPulse = new IslandPulseConfig();
            [JsonProperty(Order = 7, PropertyName = "WorldMind Routing")] public WorldMindRouteConfig WorldMindRoutes = new WorldMindRouteConfig();
            [JsonProperty(Order = 8, PropertyName = "Discord Routing")] public DiscordRouteConfig DiscordRoutes = new DiscordRouteConfig();
            [JsonProperty(Order = 9, PropertyName = "Diagnostics")] public DiagnosticsConfig Diagnostics = new DiagnosticsConfig();

            public static PluginConfig Default() { return new PluginConfig(); }

            public void Normalize()
            {
                if (Signals == null) Signals = new SignalCollectionConfig();
                if (Categories == null) Categories = new SignalCategoryConfig();
                if (Thresholds == null) Thresholds = new ThresholdConfig();
                if (Patterns == null) Patterns = new PatternConfig();
                if (IslandPulse == null) IslandPulse = new IslandPulseConfig();
                if (WorldMindRoutes == null) WorldMindRoutes = new WorldMindRouteConfig();
                if (DiscordRoutes == null) DiscordRoutes = new DiscordRouteConfig();
                if (Diagnostics == null) Diagnostics = new DiagnosticsConfig();
                Thresholds.Normalize(); Patterns.Normalize(); IslandPulse.Normalize(); Diagnostics.Normalize(); Categories.Normalize();
            }
        }

        private class SignalCollectionConfig
        {
            public bool PlayerConnections = true;
            public bool PlayerDeaths = true;
            public bool PlayerRespawns = true;
            public bool SeverePlayerDamage = true;
            public bool LootActivity = true;
            public bool ResourceGathering = true;
            public bool BuildingActivity = true;
            public bool ImportantEntityDeaths = true;
        }

        private class SignalCategoryConfig
        {
            public SignalCategorySettings Combat = new SignalCategorySettings { Enabled = true, RecordToWorldMind = true, AllowModelAsk = true, AllowDiscordReport = true, MinimumSeverityToAskModel = "danger", MinimumSeverityToReport = "danger", MinimumSeverityToSaveMemory = "interesting", CooldownSeconds = 20 };
            public SignalCategorySettings Lifecycle = new SignalCategorySettings { Enabled = true, RecordToWorldMind = true, AllowModelAsk = false, AllowDiscordReport = false, MinimumSeverityToReport = "owner_report", CooldownSeconds = 20 };
            public SignalCategorySettings Loot = new SignalCategorySettings { Enabled = true, RecordToWorldMind = true, AllowModelAsk = false, AllowDiscordReport = false, MinimumSeverityToReport = "danger", CooldownSeconds = 30 };
            public SignalCategorySettings Gathering = new SignalCategorySettings { Enabled = true, RecordToWorldMind = true, AllowModelAsk = false, AllowDiscordReport = false, MinimumSeverityToReport = "danger", CooldownSeconds = 60 };
            public SignalCategorySettings Building = new SignalCategorySettings { Enabled = true, RecordToWorldMind = true, AllowModelAsk = false, AllowDiscordReport = false, MinimumSeverityToReport = "critical", CooldownSeconds = 45 };
            public SignalCategorySettings EntityDeath = new SignalCategorySettings { Enabled = true, RecordToWorldMind = true, AllowModelAsk = false, AllowDiscordReport = true, MinimumSeverityToReport = "interesting", CooldownSeconds = 30 };
            public SignalCategorySettings Pulse = new SignalCategorySettings { Enabled = true, RecordToWorldMind = true, AllowModelAsk = false, AllowDiscordReport = false, MinimumSeverityToReport = "owner_report", CooldownSeconds = 60 };
            public SignalCategorySettings Utility = new SignalCategorySettings { Enabled = true, RecordToWorldMind = true, AllowModelAsk = false, AllowDiscordReport = false, MinimumSeverityToReport = "danger", CooldownSeconds = 30 };

            public void Normalize()
            {
                if (Combat == null) Combat = new SignalCategorySettings();
                if (Lifecycle == null) Lifecycle = new SignalCategorySettings();
                if (Loot == null) Loot = new SignalCategorySettings();
                if (Gathering == null) Gathering = new SignalCategorySettings();
                if (Building == null) Building = new SignalCategorySettings();
                if (EntityDeath == null) EntityDeath = new SignalCategorySettings();
                if (Pulse == null) Pulse = new SignalCategorySettings();
                if (Utility == null) Utility = new SignalCategorySettings();
                Combat.Normalize(); Lifecycle.Normalize(); Loot.Normalize(); Gathering.Normalize(); Building.Normalize(); EntityDeath.Normalize(); Pulse.Normalize(); Utility.Normalize();
            }
        }

        private class SignalCategorySettings
        {
            public bool Enabled = true;
            public bool RecordToWorldMind = true;
            public bool AllowModelAsk = false;
            public bool AllowDiscordReport = false;
            public bool SaveToLocalTimeline = true;
            public string MinimumSeverityToAskModel = "danger";
            public string MinimumSeverityToReport = "danger";
            public string MinimumSeverityToSaveMemory = "interesting";
            public int CooldownSeconds = 30;
            public void Normalize() { CooldownSeconds = Math.Max(1, CooldownSeconds); }
        }

        private class ThresholdConfig
        {
            public float SevereDamageMinimum = 45f;
            public int SevereDamageCooldownSeconds = 20;
            public int LootCooldownSeconds = 20;
            public int GatherCooldownSeconds = 45;
            public int BuildCooldownSeconds = 30;
            public int MinimumGatherAmount = 500;
            public void Normalize()
            {
                SevereDamageMinimum = Mathf.Max(1f, SevereDamageMinimum);
                SevereDamageCooldownSeconds = Math.Max(1, SevereDamageCooldownSeconds);
                LootCooldownSeconds = Math.Max(1, LootCooldownSeconds);
                GatherCooldownSeconds = Math.Max(1, GatherCooldownSeconds);
                BuildCooldownSeconds = Math.Max(1, BuildCooldownSeconds);
                MinimumGatherAmount = Math.Max(1, MinimumGatherAmount);
            }
        }

        private class PatternConfig
        {
            public bool Enabled = true;
            public bool EnableDedupeBuckets = true;
            public int PatternWindowSeconds = 180;
            public int RepeatedDeathThreshold = 2;
            public int MultiDamageThreshold = 2;
            public int SameAttackerVictimThreshold = 2;
            public int HotGridThreshold = 150;
            public int PlayerDangerThreshold = 90;
            public int LargeGatherAmount = 2500;
            public int PlayerDangerDecayPerSignal = 2;
            public int GridHeatDecayPerSignal = 1;
            public void Normalize()
            {
                PatternWindowSeconds = Math.Max(30, PatternWindowSeconds);
                RepeatedDeathThreshold = Math.Max(1, RepeatedDeathThreshold);
                MultiDamageThreshold = Math.Max(1, MultiDamageThreshold);
                SameAttackerVictimThreshold = Math.Max(1, SameAttackerVictimThreshold);
                HotGridThreshold = Math.Max(1, HotGridThreshold);
                PlayerDangerThreshold = Math.Max(1, PlayerDangerThreshold);
                LargeGatherAmount = Math.Max(1, LargeGatherAmount);
                PlayerDangerDecayPerSignal = Math.Max(0, PlayerDangerDecayPerSignal);
                GridHeatDecayPerSignal = Math.Max(0, GridHeatDecayPerSignal);
            }
        }

        private class IslandPulseConfig
        {
            public bool Enabled = true;
            public int PulseEverySeconds = 300;
            public int PulseLookbackSeconds = 300;
            public bool RecordPulseToWorldMind = true;
            public bool AskModelForPulse = false;
            public bool AllowModelPulse = false;
            public bool ReportPulseToDiscord = false;
            public void Normalize()
            {
                PulseEverySeconds = Math.Max(60, PulseEverySeconds);
                PulseLookbackSeconds = Math.Max(60, PulseLookbackSeconds);
            }
        }

        private class WorldMindRouteConfig
        {
            public bool SendPlayerDeathsToWorldMind = true;
            public bool SendSevereDamageToWorldMind = false;
            public bool AllowPlayerFacingHints = false;
            public string WorldMindTone = "tactical, sarcastic, Rust-aware, hostile island intelligence, adult chaos, no slurs or real-world hate";
            public int WorldMindUrgency = 2;
        }

        private class DiscordRouteConfig
        {
            public bool Enabled = false;
            public string ChannelKey = "signals";
            public bool SendPlayerDeathsToDiscord = true;
            public bool SendImportantEntityDeathsToDiscord = true;
        }

        private class DiagnosticsConfig
        {
            public bool PrintDebugToConsole = false;
            public int HeartbeatSeconds = 30;
            public int MaxLocalTimelineEvents = 500;
            public void Normalize()
            {
                HeartbeatSeconds = Math.Max(15, HeartbeatSeconds);
                MaxLocalTimelineEvents = Math.Max(50, MaxLocalTimelineEvents);
            }
        }

        #endregion

        #region Data / DTOs

        private class StoredData
        {
            public ProofState Proof = new ProofState();
            public CounterState Counters = new CounterState();
            public List<TimelineEntry> Timeline = new List<TimelineEntry>();
            public Dictionary<string, PlayerSignalState> PlayerStates = new Dictionary<string, PlayerSignalState>();
            public Dictionary<string, GridHeatState> GridHeat = new Dictionary<string, GridHeatState>();
            public Dictionary<string, PatternCounter> Patterns = new Dictionary<string, PatternCounter>();
            public Dictionary<string, MergeBucket> MergeBuckets = new Dictionary<string, MergeBucket>();

            public void Normalize()
            {
                if (Proof == null) Proof = new ProofState();
                if (Counters == null) Counters = new CounterState();
                if (Timeline == null) Timeline = new List<TimelineEntry>();
                if (PlayerStates == null) PlayerStates = new Dictionary<string, PlayerSignalState>();
                if (GridHeat == null) GridHeat = new Dictionary<string, GridHeatState>();
                if (Patterns == null) Patterns = new Dictionary<string, PatternCounter>();
                if (MergeBuckets == null) MergeBuckets = new Dictionary<string, MergeBucket>();
            }
        }

        private class ProofState
        {
            public bool RegisteredWithWorldMind = false;
            public string LastRegistrationUtc = "";
            public bool HeartbeatAccepted = false;
            public string LastHeartbeatUtc = "";
            public bool EventDetected = false;
            public string LastEventType = "";
            public string LastEventUtc = "";
            public string LastPlayerId = "";
            public string LastSeverity = "";
            public string LastGrid = "";
            public string LastTruthJson = "";
            public bool WorldMindRecordSucceeded = false;
            public string LastWorldMindRecordUtc = "";
            public bool WorldMindAskAttempted = false;
            public bool WorldMindAskSucceeded = false;
            public string LastWorldMindAskUtc = "";
            public string LastWorldMindResponseUtc = "";
            public string LastWorldMindResponsePreview = "";
            public bool DiscordCalled = false;
            public bool DiscordQueuedOrSent = false;
            public string LastDiscordRouteUtc = "";
            public string LastIslandPulseUtc = "";
            public string LastIslandPulseSummary = "";
            public string LastError = "";
            public string LastErrorUtc = "";
        }

        private class CounterState
        {
            public int EventsDetected = 0;
            public int EventsMergedOrSuppressed = 0;
            public int WorldMindRecordAttempts = 0;
            public int WorldMindRecordSuccess = 0;
            public int WorldMindRecordFailures = 0;
            public int WorldMindAskAttempts = 0;
            public int WorldMindAskSuccess = 0;
            public int WorldMindAskFailures = 0;
            public int DiscordAttempts = 0;
            public int DiscordSuccess = 0;
            public int DiscordFailures = 0;
            public int IslandPulses = 0;
        }

        private class TimelineEntry
        {
            public string TimestampUtc = "";
            public string EventType = "";
            public string PlayerId = "";
            public string Category = "";
            public string Severity = "";
            public string Grid = "";
            public string TruthJson = "";
        }

        private class PlayerSignalState
        {
            public string PlayerId = "";
            public string PlayerName = "";
            public string CreatedUtc = "";
            public string UpdatedUtc = "";
            public string LastKnownGrid = "";
            public string LastEventType = "";
            public string LastSeverity = "";
            public string LastThreatReason = "";
            public string CombatHeat = "Quiet";
            public int DangerScore = 0;
            public int TotalSignals = 0;
            public int RecentKills = 0;
            public int RecentDeaths = 0;
            public int RecentRespawns = 0;
            public int RecentDamageTaken = 0;
            public int RecentDamageDealt = 0;
        }

        private class GridHeatState
        {
            public string Grid = "";
            public string CreatedUtc = "";
            public string UpdatedUtc = "";
            public int TotalHeat = 0;
            public int CombatHeat = 0;
            public int DeathHeat = 0;
            public int BuildHeat = 0;
            public int GatherHeat = 0;
            public int LootHeat = 0;
            public int EntityHeat = 0;
            public string HeatLabel = "Quiet";
            public string LastEventType = "";
            public string LastSeverity = "";
        }

        private class PatternCounter
        {
            public string Key = "";
            public double WindowStarted = 0;
            public int Count = 0;
            public string LastUtc = "";
        }

        private class MergeBucket
        {
            public string Key = "";
            public string EventType = "";
            public string PlayerId = "";
            public string Grid = "";
            public int Count = 0;
            public string LastUtc = "";
        }

        private class SignalDecision
        {
            public string Category = "Utility";
            public string Severity = "normal";
            public string Grid = "unknown";
            public int Score = 10;
            public string PatternReason = "none";
            public string DedupeKey = "";
            public bool SuppressAsDuplicate = false;
            public bool ShouldRecordToWorldMind = true;
            public bool ShouldAskWorldMind = false;
            public bool ShouldReportDiscord = false;
            public bool ShouldSaveMemory = false;
            public bool ShouldSpeakToPlayer = false;
        }

        #endregion
    }
}
