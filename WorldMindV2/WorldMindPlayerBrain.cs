using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindPlayerBrain", "Devi8d0ne", "1.0.0")]
    [Description("Player behavior memory and profile layer for WorldMindV2.")]
    public class WorldMindPlayerBrain : RustPlugin
    {
        private const string MadeWithLoveTag = "Made with love by Deviated Systems";

        private const string Dv8dAscii = @"
DDDDDDDDDD    VVVV        VVVV     888888      DDDDDDDDDD
DDDDDDDDDDD    VVVV      VVVV    88888888     DDDDDDDDDDD
DD      DDD     VVVV    VVVV    88      88    DD      DDD
DD      DDD      VVVV  VVVV      88888888     DD      DDD
DD      DDD       VVVVVVVV      88      88    DD      DDD
DDDDDDDDDDD        VVVVVV       88888888     DDDDDDDDDDD
DDDDDDDDDD          VVVV         888888      DDDDDDDDDD
";

        [PluginReference] private Plugin WorldMindV2;

        private const string PermAdmin = "worldmindplayerbrain.admin";
        private const string PermUse = "worldmindplayerbrain.use";

        private PluginConfig _config;
        private StoredData _data;

        private readonly Dictionary<ulong, SessionState> _sessions = new Dictionary<ulong, SessionState>();
        private readonly Dictionary<ulong, Timer> _timers = new Dictionary<ulong, Timer>();

        #region Oxide Hooks

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermUse, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            if (_config.PrintAsciiOnLoad)
            {
                Puts(Dv8dAscii);
                Puts($"{MadeWithLoveTag} | Author: Devi8d0ne | WorldMindPlayerBrain loaded.");
            }

            foreach (var player in BasePlayer.activePlayerList)
                StartSession(player);
        }

        private void Unload()
        {
            foreach (var kvp in _timers.ToList())
            {
                kvp.Value?.Destroy();
            }

            _timers.Clear();
            FlushAllSessions();
            SaveData();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            StartSession(player);
            AddFact(player.userID, "last_seen_name", player.displayName);
            AddFact(player.userID, "last_connected_utc", DateTime.UtcNow.ToString("o"));
            RecordWorldMindEvent(player, "player_connected", "Player connected.");
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            EndSession(player);
            AddFact(player.userID, "last_disconnect_reason", reason ?? "Unknown");
            AddFact(player.userID, "last_disconnected_utc", DateTime.UtcNow.ToString("o"));
            RecordWorldMindEvent(player, "player_disconnected", "Player disconnected.");
            SaveData();
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null || player.userID == 0) return;

            var profile = GetOrCreateProfile(player.userID, player.displayName);
            profile.Deaths++;
            profile.LastDeathUtc = DateTime.UtcNow.ToString("o");

            var killer = info?.InitiatorPlayer;
            if (killer != null && killer.userID != player.userID)
            {
                profile.KilledByPlayers++;
                profile.LastKilledBy = killer.displayName;
                AddTag(profile, "dies_to_players");
            }
            else
            {
                profile.NonPlayerDeaths++;
                AddTag(profile, "environment_risk");
            }

            var weapon = info?.WeaponPrefab?.ShortPrefabName ?? info?.damageTypes?.GetMajorityDamageType().ToString() ?? "unknown";
            profile.LastDeathCause = weapon;

            BumpLocation(profile, player.transform.position, "death");
            RecalculateProfile(profile);
            SaveDataSoon();

            RecordWorldMindEvent(player, "player_death_profile_update", BuildProfileSummary(profile));
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            var killer = info?.InitiatorPlayer;
            if (killer == null || killer.userID == 0) return;

            var victimPlayer = entity as BasePlayer;
            if (victimPlayer != null && victimPlayer.userID != killer.userID)
            {
                var killerProfile = GetOrCreateProfile(killer.userID, killer.displayName);
                killerProfile.PlayerKills++;
                killerProfile.LastKillUtc = DateTime.UtcNow.ToString("o");
                killerProfile.LastKilledPlayer = victimPlayer.displayName;
                AddTag(killerProfile, "pvp_active");
                BumpLocation(killerProfile, killer.transform.position, "player_kill");
                RecalculateProfile(killerProfile);
                SaveDataSoon();
                return;
            }

            if (IsNpc(entity))
            {
                var profile = GetOrCreateProfile(killer.userID, killer.displayName);
                profile.NpcKills++;
                profile.LastNpcKillUtc = DateTime.UtcNow.ToString("o");
                AddTag(profile, "npc_hunter");
                BumpLocation(profile, killer.transform.position, "npc_kill");
                RecalculateProfile(profile);
                SaveDataSoon();
            }
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            var player = entity as BasePlayer;
            if (player == null || item == null) return;

            var profile = GetOrCreateProfile(player.userID, player.displayName);
            profile.ResourcesGathered += Math.Max(0, item.amount);
            profile.LastGatherUtc = DateTime.UtcNow.ToString("o");
            BumpItem(profile, item.info?.shortname ?? "unknown", item.amount);

            if (profile.ResourcesGathered >= _config.FarmerTagThreshold)
                AddTag(profile, "farmer");

            RecalculateProfile(profile);
            SaveDataSoon();
        }

        private void OnDispenserBonus(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            OnDispenserGather(dispenser, player, item);
        }

        private void OnItemCraftFinished(ItemCraftTask task, Item item, ItemCrafter crafter)
        {
            var player = crafter?.owner;
            if (player == null || item == null) return;

            var profile = GetOrCreateProfile(player.userID, player.displayName);
            profile.ItemsCrafted++;
            profile.LastCraftUtc = DateTime.UtcNow.ToString("o");
            BumpItem(profile, item.info?.shortname ?? "unknown", item.amount);
            AddTag(profile, "crafter");
            RecalculateProfile(profile);
            SaveDataSoon();
        }

        private void OnStructureBuilt(Planner planner, GameObject gameObject)
        {
            var player = planner?.GetOwnerPlayer();
            if (player == null) return;

            var profile = GetOrCreateProfile(player.userID, player.displayName);
            profile.StructuresBuilt++;
            profile.LastBuildUtc = DateTime.UtcNow.ToString("o");

            if (profile.StructuresBuilt >= _config.BuilderTagThreshold)
                AddTag(profile, "builder");

            RecalculateProfile(profile);
            SaveDataSoon();
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || !_sessions.ContainsKey(player.userID)) return;

            var session = _sessions[player.userID];
            session.LastPosition = player.transform.position;
            session.LastActiveUtc = DateTime.UtcNow;
        }

        #endregion

        #region Commands

        [ChatCommand("wmplayer")]
        private void CmdPlayerBrain(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (args == null || args.Length == 0)
            {
                ReplyProfile(player, player.userID, false);
                return;
            }

            var sub = args[0].ToLowerInvariant();

            if (sub == "status")
            {
                if (!HasAdmin(player)) return;
                SendReply(player, $"<color=#d7c19b>WorldMindPlayerBrain</color> profiles={_data.Profiles.Count}, sessions={_sessions.Count}, WorldMindV2={(WorldMindV2 != null ? "found" : "missing")}");
                return;
            }

            if (sub == "reload")
            {
                if (!HasAdmin(player)) return;
                LoadConfig();
                LoadData();
                SendReply(player, "WorldMindPlayerBrain config/data reloaded.");
                return;
            }

            if (sub == "save")
            {
                if (!HasAdmin(player)) return;
                FlushAllSessions();
                SaveData();
                SendReply(player, "WorldMindPlayerBrain data saved.");
                return;
            }

            if (sub == "clear" && args.Length >= 2)
            {
                if (!HasAdmin(player)) return;
                ulong id;
                if (!ulong.TryParse(args[1], out id))
                {
                    SendReply(player, "Usage: /wmplayer clear <steamId>");
                    return;
                }

                _data.Profiles.Remove(id);
                SaveData();
                SendReply(player, $"Cleared WorldMind profile for {id}.");
                return;
            }

            if (sub == "profile")
            {
                if (args.Length >= 2 && HasAdmin(player))
                {
                    ulong id;
                    if (ulong.TryParse(args[1], out id))
                    {
                        ReplyProfile(player, id, true);
                        return;
                    }
                }

                ReplyProfile(player, player.userID, false);
                return;
            }

            if (sub == "ask")
            {
                var question = string.Join(" ", args.Skip(1).ToArray()).Trim();
                if (string.IsNullOrEmpty(question))
                {
                    SendReply(player, "Usage: /wmplayer ask <question about your profile>");
                    return;
                }

                AskAboutProfile(player, question);
                return;
            }

            SendReply(player, "Usage: /wmplayer, /wmplayer profile, /wmplayer ask <question>, /wmplayer status");
        }

        [ConsoleCommand("worldmindplayerbrain.status")]
        private void CCmdStatus(ConsoleSystem.Arg arg)
        {
            if (arg == null || arg.Connection != null) return;
            Puts($"WorldMindPlayerBrain profiles={_data.Profiles.Count}, sessions={_sessions.Count}, WorldMindV2={(WorldMindV2 != null ? "found" : "missing")}");
        }

        [ConsoleCommand("worldmindplayerbrain.save")]
        private void CCmdSave(ConsoleSystem.Arg arg)
        {
            if (arg == null || arg.Connection != null) return;
            FlushAllSessions();
            SaveData();
            Puts("WorldMindPlayerBrain data saved.");
        }

        #endregion

        #region Public Hooks For Other Plugins

        private Dictionary<string, object> WorldMindPlayerBrain_GetProfile(ulong steamId)
        {
            PlayerProfile profile;
            if (!_data.Profiles.TryGetValue(steamId, out profile)) return null;
            return ProfileToDictionary(profile);
        }

        private object WorldMindPlayerBrain_AddFact(ulong steamId, string key, string value)
        {
            if (steamId == 0 || string.IsNullOrEmpty(key)) return false;
            AddFact(steamId, key, value ?? string.Empty);
            SaveDataSoon();
            return true;
        }

        private object WorldMindPlayerBrain_AddTag(ulong steamId, string tag)
        {
            if (steamId == 0 || string.IsNullOrEmpty(tag)) return false;
            var profile = GetOrCreateProfile(steamId, steamId.ToString());
            AddTag(profile, tag);
            RecalculateProfile(profile);
            SaveDataSoon();
            return true;
        }

        private string WorldMindPlayerBrain_GetSummary(ulong steamId)
        {
            PlayerProfile profile;
            if (!_data.Profiles.TryGetValue(steamId, out profile)) return "No WorldMind player profile exists yet.";
            return BuildProfileSummary(profile);
        }

        #endregion

        #region Profile Logic

        private void StartSession(BasePlayer player)
        {
            if (player == null || player.userID == 0) return;

            var profile = GetOrCreateProfile(player.userID, player.displayName);
            profile.LastSeenName = player.displayName;
            profile.LastConnectedUtc = DateTime.UtcNow.ToString("o");
            profile.ConnectionCount++;

            _sessions[player.userID] = new SessionState
            {
                SteamId = player.userID,
                PlayerName = player.displayName,
                StartedUtc = DateTime.UtcNow,
                LastActiveUtc = DateTime.UtcNow,
                LastPosition = player.transform.position
            };

            if (_config.SessionSnapshotMinutes > 0 && !_timers.ContainsKey(player.userID))
            {
                _timers[player.userID] = timer.Every(_config.SessionSnapshotMinutes * 60f, () => SnapshotSession(player));
            }

            SaveDataSoon();
        }

        private void EndSession(BasePlayer player)
        {
            if (player == null) return;

            SessionState session;
            if (_sessions.TryGetValue(player.userID, out session))
            {
                var profile = GetOrCreateProfile(player.userID, player.displayName);
                var minutes = Math.Max(0, (int)(DateTime.UtcNow - session.StartedUtc).TotalMinutes);
                profile.TotalMinutesOnline += minutes;
                profile.LastSessionMinutes = minutes;
                profile.LastPosition = PositionString(player.transform.position);
                BumpLocation(profile, player.transform.position, "session_end");
                RecalculateProfile(profile);
                _sessions.Remove(player.userID);
            }

            Timer t;
            if (_timers.TryGetValue(player.userID, out t))
            {
                t.Destroy();
                _timers.Remove(player.userID);
            }
        }

        private void SnapshotSession(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;

            var profile = GetOrCreateProfile(player.userID, player.displayName);
            profile.LastPosition = PositionString(player.transform.position);
            BumpLocation(profile, player.transform.position, "session_snapshot");
            RecalculateProfile(profile);
            SaveDataSoon();
        }

        private void FlushAllSessions()
        {
            foreach (var player in BasePlayer.activePlayerList.ToList())
            {
                EndSession(player);
            }
        }

        private PlayerProfile GetOrCreateProfile(ulong steamId, string name)
        {
            PlayerProfile profile;
            if (!_data.Profiles.TryGetValue(steamId, out profile))
            {
                profile = new PlayerProfile
                {
                    SteamId = steamId,
                    FirstSeenUtc = DateTime.UtcNow.ToString("o"),
                    LastSeenName = name ?? steamId.ToString(),
                    Tags = new List<string>(),
                    Facts = new Dictionary<string, string>(),
                    TopItems = new Dictionary<string, int>(),
                    LocationEvents = new Dictionary<string, int>()
                };
                _data.Profiles[steamId] = profile;
            }

            if (!string.IsNullOrEmpty(name)) profile.LastSeenName = name;
            return profile;
        }

        private void AddFact(ulong steamId, string key, string value)
        {
            var profile = GetOrCreateProfile(steamId, steamId.ToString());
            profile.Facts[key] = value ?? string.Empty;
            profile.LastUpdatedUtc = DateTime.UtcNow.ToString("o");
        }

        private void AddTag(PlayerProfile profile, string tag)
        {
            if (profile == null || string.IsNullOrEmpty(tag)) return;
            tag = tag.Trim().ToLowerInvariant().Replace(" ", "_");
            if (!profile.Tags.Contains(tag)) profile.Tags.Add(tag);
        }

        private void BumpItem(PlayerProfile profile, string shortname, int amount)
        {
            if (profile == null || string.IsNullOrEmpty(shortname)) return;
            if (!profile.TopItems.ContainsKey(shortname)) profile.TopItems[shortname] = 0;
            profile.TopItems[shortname] += Math.Max(1, amount);
        }

        private void BumpLocation(PlayerProfile profile, Vector3 pos, string reason)
        {
            if (profile == null) return;
            var grid = Grid(pos);
            if (!profile.LocationEvents.ContainsKey(grid)) profile.LocationEvents[grid] = 0;
            profile.LocationEvents[grid]++;
            profile.LastPosition = PositionString(pos);
        }

        private void RecalculateProfile(PlayerProfile profile)
        {
            if (profile == null) return;

            if (profile.PlayerKills >= _config.PvpTagKillThreshold) AddTag(profile, "pvp_minded");
            if (profile.Deaths >= _config.DeathProneThreshold) AddTag(profile, "death_prone");
            if (profile.NpcKills >= _config.NpcHunterThreshold) AddTag(profile, "npc_hunter");
            if (profile.StructuresBuilt >= _config.BuilderTagThreshold) AddTag(profile, "builder");
            if (profile.ResourcesGathered >= _config.FarmerTagThreshold) AddTag(profile, "farmer");

            var kd = profile.Deaths <= 0 ? profile.PlayerKills : (double)profile.PlayerKills / Math.Max(1, profile.Deaths);
            profile.RiskProfile = kd >= 1.5 ? "aggressive" : profile.Deaths >= profile.PlayerKills + 5 ? "fragile" : "balanced";

            var playstyle = new List<string>();
            if (profile.Tags.Contains("pvp_minded")) playstyle.Add("PvP");
            if (profile.Tags.Contains("farmer")) playstyle.Add("farming");
            if (profile.Tags.Contains("builder")) playstyle.Add("building");
            if (profile.Tags.Contains("npc_hunter")) playstyle.Add("NPC hunting");
            profile.PlaystyleSummary = playstyle.Count == 0 ? "not enough data yet" : string.Join(", ", playstyle.ToArray());

            profile.LastUpdatedUtc = DateTime.UtcNow.ToString("o");
        }

        private string BuildProfileSummary(PlayerProfile p)
        {
            if (p == null) return "No player profile.";

            var topTags = p.Tags != null && p.Tags.Count > 0 ? string.Join(", ", p.Tags.Take(6).ToArray()) : "none yet";
            var topItems = p.TopItems != null && p.TopItems.Count > 0
                ? string.Join(", ", p.TopItems.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key + ":" + x.Value).ToArray())
                : "none yet";
            var topLocations = p.LocationEvents != null && p.LocationEvents.Count > 0
                ? string.Join(", ", p.LocationEvents.OrderByDescending(x => x.Value).Take(4).Select(x => x.Key + ":" + x.Value).ToArray())
                : "none yet";

            return $"Player {p.LastSeenName} profile: playstyle={p.PlaystyleSummary}; risk={p.RiskProfile}; kills={p.PlayerKills}; deaths={p.Deaths}; npcKills={p.NpcKills}; resourcesGathered={p.ResourcesGathered}; structuresBuilt={p.StructuresBuilt}; minutesOnline={p.TotalMinutesOnline}; tags={topTags}; topItems={topItems}; topLocations={topLocations}.";
        }

        private Dictionary<string, object> ProfileToDictionary(PlayerProfile p)
        {
            return new Dictionary<string, object>
            {
                ["steamId"] = p.SteamId,
                ["name"] = p.LastSeenName,
                ["playstyle"] = p.PlaystyleSummary,
                ["riskProfile"] = p.RiskProfile,
                ["kills"] = p.PlayerKills,
                ["deaths"] = p.Deaths,
                ["npcKills"] = p.NpcKills,
                ["resourcesGathered"] = p.ResourcesGathered,
                ["structuresBuilt"] = p.StructuresBuilt,
                ["minutesOnline"] = p.TotalMinutesOnline,
                ["tags"] = p.Tags,
                ["facts"] = p.Facts,
                ["topItems"] = p.TopItems,
                ["locationEvents"] = p.LocationEvents
            };
        }

        #endregion

        #region WorldMind Bridge

        private void AskAboutProfile(BasePlayer player, string question)
        {
            var profile = GetOrCreateProfile(player.userID, player.displayName);
            var context = BuildProfileSummary(profile);

            var prompt = "A Rust player asked about their WorldMind player profile. " +
                         "Answer briefly and use only the provided profile data. " +
                         "Do not mention unconfigured server commands, VIP, Discord, WarMode, custom economy, or custom events. " +
                         "Question: " + question + "\n" + context;

            var result = AskWorldMind(prompt, player, "player_profile_question");
            SendReply(player, string.IsNullOrEmpty(result) ? context : result);
        }

        private string AskWorldMind(string prompt, BasePlayer player, string eventType)
        {
            if (WorldMindV2 == null) return null;

            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["plugin"] = Name,
                    ["eventType"] = eventType,
                    ["playerId"] = player?.UserIDString ?? "0",
                    ["playerName"] = player?.displayName ?? "unknown",
                    ["prompt"] = prompt,
                    ["maxTokens"] = _config.MaxResponseTokens
                };

                var response = WorldMindV2.Call("WorldMind_AskText", payload);
                return response as string;
            }
            catch (Exception ex)
            {
                if (_config.Debug) PrintWarning($"WorldMind_AskText failed: {ex.Message}");
                return null;
            }
        }

        private void RecordWorldMindEvent(BasePlayer player, string eventType, string summary)
        {
            if (!_config.RecordEventsToWorldMind || WorldMindV2 == null) return;

            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["plugin"] = Name,
                    ["eventType"] = eventType,
                    ["playerId"] = player?.UserIDString ?? "0",
                    ["playerName"] = player?.displayName ?? "unknown",
                    ["summary"] = summary,
                    ["utc"] = DateTime.UtcNow.ToString("o")
                };

                WorldMindV2.Call("WorldMind_RecordEvent", payload);
            }
            catch (Exception ex)
            {
                if (_config.Debug) PrintWarning($"WorldMind_RecordEvent failed: {ex.Message}");
            }
        }

        #endregion

        #region Helpers

        private bool HasAdmin(BasePlayer player)
        {
            if (player == null) return false;
            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin)) return true;
            SendReply(player, "You do not have permission to use that command.");
            return false;
        }

        private void ReplyProfile(BasePlayer requester, ulong steamId, bool adminView)
        {
            PlayerProfile profile;
            if (!_data.Profiles.TryGetValue(steamId, out profile))
            {
                SendReply(requester, "No WorldMind player profile exists yet.");
                return;
            }

            SendReply(requester, BuildProfileSummary(profile));
        }

        private bool IsNpc(BaseCombatEntity entity)
        {
            if (entity == null) return false;
            var player = entity as BasePlayer;
            if (player == null) return false;
            return player.IsNpc || player.userID < 1000000000UL;
        }

        private string PositionString(Vector3 pos)
        {
            return $"{Mathf.RoundToInt(pos.x)},{Mathf.RoundToInt(pos.y)},{Mathf.RoundToInt(pos.z)} grid {Grid(pos)}";
        }

        private string Grid(Vector3 pos)
        {
            var size = ConVar.Server.worldsize;
            if (size <= 0) size = 4500;
            var half = size / 2f;
            var x = Mathf.Clamp(pos.x + half, 0, size - 1);
            var z = Mathf.Clamp(size - (pos.z + half), 0, size - 1);
            var gridSize = 146.3f;
            var col = Mathf.FloorToInt(x / gridSize);
            var row = Mathf.FloorToInt(z / gridSize);
            return NumberToLetters(col) + row;
        }

        private string NumberToLetters(int number)
        {
            number = Math.Max(0, number);
            var result = string.Empty;
            do
            {
                result = (char)('A' + (number % 26)) + result;
                number = number / 26 - 1;
            } while (number >= 0);
            return result;
        }

        private void SaveDataSoon()
        {
            if (!_config.AutoSave) return;
            timer.Once(Math.Max(1f, _config.SaveDelaySeconds), SaveData);
        }

        #endregion

        #region Config/Data

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
            }
            catch
            {
                PrintWarning("Config invalid; loading defaults.");
                _config = PluginConfig.Default();
            }

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
            }
            catch
            {
                _data = new StoredData();
            }

            if (_data == null) _data = new StoredData();
            if (_data.Profiles == null) _data.Profiles = new Dictionary<ulong, PlayerProfile>();
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        private class PluginConfig
        {
            [JsonProperty("Print DV8D ASCII On Load")]
            public bool PrintAsciiOnLoad = true;

            [JsonProperty("Debug Logging")]
            public bool Debug = false;

            [JsonProperty("Auto Save Data")]
            public bool AutoSave = true;

            [JsonProperty("Save Delay Seconds")]
            public float SaveDelaySeconds = 5f;

            [JsonProperty("Session Snapshot Minutes")]
            public int SessionSnapshotMinutes = 10;

            [JsonProperty("Record Events To WorldMind")]
            public bool RecordEventsToWorldMind = true;

            [JsonProperty("Max WorldMind Response Tokens")]
            public int MaxResponseTokens = 120;

            [JsonProperty("PvP Tag Kill Threshold")]
            public int PvpTagKillThreshold = 5;

            [JsonProperty("Death Prone Threshold")]
            public int DeathProneThreshold = 8;

            [JsonProperty("NPC Hunter Threshold")]
            public int NpcHunterThreshold = 10;

            [JsonProperty("Builder Tag Structure Threshold")]
            public int BuilderTagThreshold = 75;

            [JsonProperty("Farmer Tag Resource Threshold")]
            public int FarmerTagThreshold = 10000;

            public static PluginConfig Default()
            {
                return new PluginConfig();
            }
        }

        private class StoredData
        {
            public Dictionary<ulong, PlayerProfile> Profiles = new Dictionary<ulong, PlayerProfile>();
        }

        private class PlayerProfile
        {
            public ulong SteamId;
            public string FirstSeenUtc;
            public string LastUpdatedUtc;
            public string LastSeenName;
            public string LastConnectedUtc;
            public string LastDisconnectedUtc;
            public string LastDisconnectReason;
            public string LastPosition;

            public int ConnectionCount;
            public int TotalMinutesOnline;
            public int LastSessionMinutes;

            public int PlayerKills;
            public int Deaths;
            public int KilledByPlayers;
            public int NonPlayerDeaths;
            public int NpcKills;
            public int ItemsCrafted;
            public int StructuresBuilt;
            public int ResourcesGathered;

            public string LastKillUtc;
            public string LastKilledPlayer;
            public string LastDeathUtc;
            public string LastKilledBy;
            public string LastDeathCause;
            public string LastNpcKillUtc;
            public string LastGatherUtc;
            public string LastCraftUtc;
            public string LastBuildUtc;

            public string RiskProfile = "unknown";
            public string PlaystyleSummary = "not enough data yet";

            public List<string> Tags = new List<string>();
            public Dictionary<string, string> Facts = new Dictionary<string, string>();
            public Dictionary<string, int> TopItems = new Dictionary<string, int>();
            public Dictionary<string, int> LocationEvents = new Dictionary<string, int>();
        }

        private class SessionState
        {
            public ulong SteamId;
            public string PlayerName;
            public DateTime StartedUtc;
            public DateTime LastActiveUtc;
            public Vector3 LastPosition;
        }

        #endregion
    }
}
