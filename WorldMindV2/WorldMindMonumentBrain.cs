using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindMonumentBrain", "Devi8d0ne", "1.0.3")]
    [Description("Generic monument atmosphere, entry warnings, and activity context for the WorldMind plugin ecosystem.")]
    public class WorldMindMonumentBrain : RustPlugin
    {
        private const string PermissionAdmin = "worldmindmonumentbrain.admin";
        private const string PermissionUse = "worldmindmonumentbrain.use";
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
        [PluginReference] private Plugin WorldMindMapBrain;

        private PluginConfig _config;
        private StoredData _data;
        private readonly Dictionary<ulong, string> _lastPlayerZone = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, double> _lastPlayerMessage = new Dictionary<ulong, double>();

        #region Oxide

        private void Init()
        {
            permission.RegisterPermission(PermissionAdmin, this);
            permission.RegisterPermission(PermissionUse, this);
            LoadPluginConfig();
            LoadData();
        }

        private void OnServerInitialized()
        {
            if (_config.General.PrintAsciiOnLoad)
            {
                Puts(Dv8dAscii);
                Puts($"{MadeWithLoveTag} | Author: Devi8d0ne | Plugin: WorldMindMonumentBrain");
            }

            SeedKnownMonuments();

            if (_config.Monitoring.EnablePositionScanner)
            {
                timer.Every(Math.Max(5f, _config.Monitoring.ScanIntervalSeconds), ScanPlayers);
            }

            timer.Every(Math.Max(60f, _config.General.AutoSaveSeconds), SaveData);

            Puts($"WorldMindMonumentBrain loaded. Zones: {_data.Monuments.Count}");
        }

        private void Unload()
        {
            SaveData();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _lastPlayerZone.Remove(player.userID);
            _lastPlayerMessage.Remove(player.userID);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;

            BasePlayer attacker = info.InitiatorPlayer;
            if (attacker == null) return;

            MonumentZone zone = FindNearestMonument(attacker.transform.position);
            if (zone == null) return;

            BasePlayer victim = entity as BasePlayer;
            if (victim != null && victim != attacker)
            {
                RecordMonumentActivity(zone.Id, "player_kill", attacker.UserIDString, attacker.displayName, 2, $"Killed {victim.displayName}");
                RecordMonumentActivity(zone.Id, "player_death", victim.UserIDString, victim.displayName, 2, $"Killed by {attacker.displayName}");
                return;
            }

            string shortName = entity.ShortPrefabName ?? "";
            if (entity is NPCPlayer || shortName.ToLowerInvariant().Contains("scientist"))
            {
                RecordMonumentActivity(zone.Id, "npc_killed", attacker.UserIDString, attacker.displayName, 1, shortName);
            }
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (!_config.Monitoring.TrackCombatDamage) return;
            if (entity == null || info == null) return;

            BasePlayer attacker = info.InitiatorPlayer;
            if (attacker == null) return;

            MonumentZone zone = FindNearestMonument(attacker.transform.position);
            if (zone == null) return;

            string shortName = entity.ShortPrefabName ?? "";
            RecordMonumentActivity(zone.Id, "combat_damage", attacker.UserIDString, attacker.displayName, 1, shortName, false);
        }

        #endregion

        #region Commands

        [ChatCommand("wmmonument")]
        private void CmdMonument(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (args == null || args.Length == 0)
            {
                if (!HasAdmin(player)) return;
                Reply(player,
                    "WorldMindMonumentBrain commands:\n" +
                    "/wmmonument status\n" +
                    "/wmmonument where\n" +
                    "/wmmonument list\n" +
                    "/wmmonument add <id> <display name> <radius>\n" +
                    "/wmmonument remove <id>\n" +
                    "/wmmonument describe <id>\n" +
                    "/wmmonument recap <id>\n" +
                    "/wmmonument reload\n" +
                    "/wmmonument save\n" +
                    "/wmmonument clearactivity");
                return;
            }

            string sub = args[0].ToLowerInvariant();

            if (sub == "where")
            {
                if (!CanUse(player)) return;
                ShowWhere(player);
                return;
            }

            if (!HasAdmin(player)) return;

            if (sub == "status")
            {
                Reply(player, GetStatusText());
                return;
            }

            if (sub == "list")
            {
                Reply(player, BuildMonumentListText());
                return;
            }

            if (sub == "add")
            {
                if (args.Length < 4)
                {
                    Reply(player, "Usage: /wmmonument add <id> <display name> <radius>");
                    return;
                }

                string id = SafeId(args[1]);
                float radius;
                if (!float.TryParse(args[args.Length - 1], out radius))
                {
                    Reply(player, "Radius must be a number.");
                    return;
                }

                string displayName = string.Join(" ", args.Skip(2).Take(args.Length - 3).ToArray());
                if (string.IsNullOrWhiteSpace(displayName)) displayName = id;

                _data.Monuments[id] = new MonumentZone
                {
                    Id = id,
                    DisplayName = displayName,
                    PositionX = player.transform.position.x,
                    PositionY = player.transform.position.y,
                    PositionZ = player.transform.position.z,
                    Radius = radius,
                    Enabled = true,
                    OwnerDefined = true,
                    RiskLevel = "owner-defined",
                    Notes = "Owner-defined monument zone."
                };

                SaveData();
                Reply(player, $"Added monument zone {displayName} at your position.");
                return;
            }

            if (sub == "remove")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmmonument remove <id>");
                    return;
                }

                bool removed = _data.Monuments.Remove(SafeId(args[1]));
                SaveData();
                Reply(player, removed ? "Monument removed." : "Monument not found.");
                return;
            }

            if (sub == "describe")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmmonument describe <id>");
                    return;
                }

                DescribeMonument(player, SafeId(args[1]));
                return;
            }

            if (sub == "recap")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmmonument recap <id>");
                    return;
                }

                GenerateMonumentRecap(player, SafeId(args[1]));
                return;
            }

            if (sub == "reload")
            {
                LoadPluginConfig();
                LoadData();
                Reply(player, "WorldMindMonumentBrain reloaded.");
                return;
            }

            if (sub == "save")
            {
                SaveData();
                Reply(player, "WorldMindMonumentBrain data saved.");
                return;
            }

            if (sub == "clearactivity")
            {
                _data.Activity.Clear();
                SaveData();
                Reply(player, "Monument activity cleared.");
                return;
            }

            Reply(player, "Unknown command. Use /wmmonument for help.");
        }

        [ConsoleCommand("worldmindmonument.status")]
        private void ConsoleStatus(ConsoleSystem.Arg arg)
        {
            arg?.ReplyWith(GetStatusText());
        }

        [ConsoleCommand("worldmindmonument.reload")]
        private void ConsoleReload(ConsoleSystem.Arg arg)
        {
            LoadPluginConfig();
            LoadData();
            arg?.ReplyWith("WorldMindMonumentBrain reloaded.");
        }

        #endregion

        #region Public hooks

        private object WorldMindMonumentBrain_GetNearestMonument(Vector3 position)
        {
            return FindNearestMonument(position);
        }

        private object WorldMindMonumentBrain_DescribePosition(Vector3 position)
        {
            MonumentZone zone = FindNearestMonument(position);
            return zone == null ? null : BuildMonumentPacket(zone, position);
        }

        private object WorldMindMonumentBrain_RecordActivity(Dictionary<string, object> packet)
        {
            if (packet == null) return false;

            string monumentId = GetString(packet, "monumentId", "");
            string type = GetString(packet, "activityType", "external");
            string playerId = GetString(packet, "playerId", "");
            string playerName = GetString(packet, "playerName", "");
            int weight = GetInt(packet, "weight", 1);
            string notes = GetString(packet, "notes", "");

            RecordMonumentActivity(monumentId, type, playerId, playerName, weight, notes);
            return true;
        }

        private object WorldMindMonumentBrain_GetMonumentSummary(string monumentId)
        {
            return BuildMonumentSummaryPacket(SafeId(monumentId));
        }

        private object WorldMindMonumentBrain_GetAllMonuments()
        {
            return _data.Monuments.Values.Where(x => x.Enabled).ToList();
        }

        #endregion

        #region Scanner/core

        private void ScanPlayers()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                if (!CanUseSilent(player)) continue;

                MonumentZone zone = FindNearestMonument(player.transform.position);
                string current = zone == null ? "" : zone.Id;

                string last;
                _lastPlayerZone.TryGetValue(player.userID, out last);

                if (current != last)
                {
                    _lastPlayerZone[player.userID] = current;
                    if (zone != null) HandlePlayerEnteredMonument(player, zone);
                }
            }
        }

        private void HandlePlayerEnteredMonument(BasePlayer player, MonumentZone zone)
        {
            if (player == null || zone == null) return;
            if (!CanSendPlayerMessage(player)) return;

            RecordMonumentActivity(zone.Id, "player_entered", player.UserIDString, player.displayName, 1, "Player entered monument range.");

            if (!_config.PlayerMessages.SendEntryMessages)
                return;

            if (_config.PlayerMessages.UseWorldMindForEntryMessages && WorldMindV2 != null)
            {
                SendWorldMindEntryMessage(player, zone);
                return;
            }

            Reply(player, BuildFallbackEntryMessage(zone));
        }

        private bool CanSendPlayerMessage(BasePlayer player)
        {
            double now = UnixNow();
            double last;
            if (_lastPlayerMessage.TryGetValue(player.userID, out last))
            {
                if (now - last < _config.PlayerMessages.PlayerMessageCooldownSeconds)
                    return false;
            }

            _lastPlayerMessage[player.userID] = now;
            return true;
        }

        private void SendWorldMindEntryMessage(BasePlayer player, MonumentZone zone)
        {
            string location = GetMapDescription(player.transform.position);

            string prompt =
                "You are WorldMind creating a short atmospheric Rust monument entry line for a player.\n" +
                "Do not assume VIP, Discord, WarMode, kits, homes, teleport, factions, custom economy, DeepSea, or server-specific commands.\n" +
                "Mention only the monument/location data provided. Keep it under 28 words.\n" +
                "Tone: immersive Rust-aware, not admin tutorial.\n" +
                $"Player: {player.displayName}\n" +
                $"Monument: {zone.DisplayName}\n" +
                $"Risk Level: {zone.RiskLevel}\n" +
                $"Notes: {zone.Notes}\n" +
                $"Location: {location}\n";

            try
            {
                object result = WorldMindV2.Call("WorldMind_AskText", prompt, "WorldMindMonumentBrain", "monument_entry");
                string message = result == null ? "" : result.ToString();
                Reply(player, string.IsNullOrWhiteSpace(message) ? BuildFallbackEntryMessage(zone) : message);
            }
            catch
            {
                Reply(player, BuildFallbackEntryMessage(zone));
            }
        }

        private string BuildFallbackEntryMessage(MonumentZone zone)
        {
            string risk = string.IsNullOrWhiteSpace(zone.RiskLevel) ? "unknown" : zone.RiskLevel;
            return $"Entering {zone.DisplayName}. Risk: {risk}. Stay aware.";
        }

        private MonumentZone FindNearestMonument(Vector3 position)
        {
            MonumentZone best = null;
            float bestDistance = float.MaxValue;

            foreach (MonumentZone zone in _data.Monuments.Values)
            {
                if (zone == null || !zone.Enabled) continue;

                float distance = Vector3.Distance(position, zone.Position);
                if (distance <= zone.Radius && distance < bestDistance)
                {
                    best = zone;
                    bestDistance = distance;
                }
            }

            return best;
        }

        private void SeedKnownMonuments()
        {
            if (!_config.MonumentDetection.SeedKnownMonumentsFromTerrain || _data.Monuments.Count > 0)
                return;

            try
            {
                if (TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null)
                    return;

                foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
                {
                    if (monument == null) continue;

                    string displayName = GuessMonumentName(monument);
                    if (string.IsNullOrWhiteSpace(displayName)) continue;

                    Vector3 pos = monument.transform.position;
                    string id = MakeMonumentId(displayName, pos);
                    if (_data.Monuments.ContainsKey(id)) continue;

                    _data.Monuments[id] = new MonumentZone
                    {
                        Id = id,
                        DisplayName = displayName,
                        PositionX = pos.x,
                        PositionY = pos.y,
                        PositionZ = pos.z,
                        Radius = GuessRadius(displayName),
                        Enabled = true,
                        OwnerDefined = false,
                        RiskLevel = GuessRiskLevel(displayName),
                        Notes = "Auto-detected Rust monument. Owner can edit/remove this zone in data."
                    };
                }

                SaveData();
            }
            catch (Exception ex)
            {
                if (_config.General.Debug)
                    Puts($"Auto monument seed failed: {ex.Message}");
            }
        }

        private string GuessMonumentName(MonumentInfo monument)
        {
            if (monument == null) return "";

            try
            {
                if (monument.displayPhrase != null && !string.IsNullOrWhiteSpace(monument.displayPhrase.english))
                    return monument.displayPhrase.english;
            }
            catch { }

            string name = "";

            try { name = monument.name ?? ""; } catch { }

            if (string.IsNullOrWhiteSpace(name))
            {
                object prefabName = GetFieldOrProperty(monument, "prefabName") ?? GetFieldOrProperty(monument, "shortPrefabName");
                if (prefabName != null) name = prefabName.ToString();
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                try { name = monument.gameObject == null ? "" : monument.gameObject.name; } catch { }
            }

            if (string.IsNullOrWhiteSpace(name)) return "";

            name = name.Replace("(Clone)", "").Replace("_", " ").Replace("-", " ");
            string[] parts = name.Split('/');
            name = parts.Length > 0 ? parts[parts.Length - 1] : name;

            return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name);
        }

        private string MakeMonumentId(string displayName, Vector3 position)
        {
            string clean = SafeId(displayName);
            return $"{clean}_{Mathf.RoundToInt(position.x)}_{Mathf.RoundToInt(position.z)}";
        }

        private string SafeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "monument";
            string clean = new string(value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
            while (clean.Contains("__")) clean = clean.Replace("__", "_");
            return clean.Trim('_');
        }

        private float GuessRadius(string displayName)
        {
            string name = (displayName ?? "").ToLowerInvariant();

            if (name.Contains("launch")) return 220f;
            if (name.Contains("military") || name.Contains("airfield") || name.Contains("train yard")) return 190f;
            if (name.Contains("power") || name.Contains("water") || name.Contains("harbor")) return 160f;
            if (name.Contains("gas") || name.Contains("supermarket") || name.Contains("mining")) return 100f;
            if (name.Contains("dome") || name.Contains("satellite")) return 130f;

            return _config.MonumentDetection.DefaultRadius;
        }

        private string GuessRiskLevel(string displayName)
        {
            string name = (displayName ?? "").ToLowerInvariant();

            if (name.Contains("launch") || name.Contains("military") || name.Contains("oil") || name.Contains("cargo"))
                return "high";

            if (name.Contains("airfield") || name.Contains("train") || name.Contains("power") || name.Contains("water"))
                return "medium";

            return "variable";
        }

        private void RecordMonumentActivity(string monumentId, string type, string playerId, string playerName, int weight, string notes, bool save = true)
        {
            if (string.IsNullOrWhiteSpace(monumentId))
            {
                BasePlayer player = FindPlayerById(playerId);
                MonumentZone zone = player == null ? null : FindNearestMonument(player.transform.position);
                monumentId = zone == null ? "" : zone.Id;
            }

            if (string.IsNullOrWhiteSpace(monumentId)) return;

            MonumentActivity activity = new MonumentActivity
            {
                MonumentId = monumentId,
                ActivityType = type ?? "unknown",
                PlayerId = playerId ?? "",
                PlayerName = playerName ?? "",
                Weight = Math.Max(0, weight),
                Notes = notes ?? "",
                TimestampUtc = DateTime.UtcNow.ToString("o")
            };

            _data.Activity.Add(activity);
            while (_data.Activity.Count > _config.Reporting.KeepRecentActivity)
                _data.Activity.RemoveAt(0);

            MonumentStats stats;
            if (!_data.Stats.TryGetValue(monumentId, out stats))
            {
                stats = new MonumentStats { MonumentId = monumentId };
                _data.Stats[monumentId] = stats;
            }

            stats.TotalActivity++;
            stats.ActivityWeight += activity.Weight;
            stats.LastActivityUtc = activity.TimestampUtc;

            int current;
            stats.ActivityCounts.TryGetValue(activity.ActivityType, out current);
            stats.ActivityCounts[activity.ActivityType] = current + 1;

            if (_config.WorldMindIntegration.RecordEventsToWorldMind)
                RecordWorldMindEvent("monument_activity", activity);

            if (save) SaveData();
        }

        #endregion

        #region WorldMind summaries

        private void DescribeMonument(BasePlayer admin, string monumentId)
        {
            object packet = BuildMonumentSummaryPacket(monumentId);
            Reply(admin, packet == null ? "Monument not found." : JsonConvert.SerializeObject(packet, Formatting.Indented));
        }

        private void GenerateMonumentRecap(BasePlayer admin, string monumentId)
        {
            if (WorldMindV2 == null)
            {
                Reply(admin, "WorldMindV2 is not loaded.");
                return;
            }

            object packet = BuildMonumentSummaryPacket(monumentId);
            if (packet == null)
            {
                Reply(admin, "Monument not found.");
                return;
            }

            string prompt =
                "You are WorldMind creating an admin-facing Rust monument activity recap.\n" +
                "Do not assume VIP, Discord, WarMode, kits, homes, teleport, factions, custom economy, DeepSea, or server-specific systems.\n" +
                "Use only the provided monument activity data. State uncertainty if activity is light.\n" +
                $"Data:\n{JsonConvert.SerializeObject(packet, Formatting.Indented)}";

            try
            {
                object result = WorldMindV2.Call("WorldMind_AskText", prompt, "WorldMindMonumentBrain", "monument_recap");
                string message = result == null ? "" : result.ToString();
                Reply(admin, string.IsNullOrWhiteSpace(message) ? "WorldMind returned no recap." : message);
            }
            catch (Exception ex)
            {
                Reply(admin, $"WorldMind recap failed: {ex.Message}");
            }
        }

        private void RecordWorldMindEvent(string eventType, object payload)
        {
            if (WorldMindV2 == null) return;

            try
            {
                Dictionary<string, object> packet = new Dictionary<string, object>
                {
                    ["plugin"] = "WorldMindMonumentBrain",
                    ["eventType"] = eventType,
                    ["timestampUtc"] = DateTime.UtcNow.ToString("o"),
                    ["payloadJson"] = JsonConvert.SerializeObject(payload)
                };

                WorldMindV2.Call("WorldMind_RecordEvent", packet);
            }
            catch (Exception ex)
            {
                if (_config.General.Debug)
                    Puts($"WorldMind_RecordEvent failed: {ex.Message}");
            }
        }

        #endregion

        #region Reporting

        private void ShowWhere(BasePlayer player)
        {
            MonumentZone zone = FindNearestMonument(player.transform.position);
            string map = GetMapDescription(player.transform.position);

            if (zone == null)
            {
                Reply(player, $"No configured monument zone detected nearby.\nLocation: {map}");
                return;
            }

            Reply(player, $"Nearest monument: {zone.DisplayName}\nRisk: {zone.RiskLevel}\nLocation: {map}");
        }

        private string GetMapDescription(Vector3 position)
        {
            if (WorldMindMapBrain != null)
            {
                try
                {
                    object result = WorldMindMapBrain.Call("WorldMindMapBrain_DescribePosition", position);
                    if (result != null && !string.IsNullOrWhiteSpace(result.ToString()))
                        return result.ToString();
                }
                catch { }
            }

            return $"x:{Mathf.RoundToInt(position.x)} z:{Mathf.RoundToInt(position.z)}";
        }

        private object BuildMonumentPacket(MonumentZone zone, Vector3 position)
        {
            if (zone == null) return null;

            return new Dictionary<string, object>
            {
                ["id"] = zone.Id,
                ["displayName"] = zone.DisplayName,
                ["riskLevel"] = zone.RiskLevel,
                ["distance"] = Vector3.Distance(position, zone.Position),
                ["radius"] = zone.Radius,
                ["notes"] = zone.Notes
            };
        }

        private object BuildMonumentSummaryPacket(string monumentId)
        {
            MonumentZone zone;
            if (!_data.Monuments.TryGetValue(monumentId, out zone))
                return null;

            MonumentStats stats;
            _data.Stats.TryGetValue(monumentId, out stats);

            List<MonumentActivity> recent = _data.Activity
                .Where(x => x.MonumentId == monumentId)
                .OrderByDescending(x => x.TimestampUtc)
                .Take(_config.Reporting.SummaryActivityLimit)
                .ToList();

            return new Dictionary<string, object>
            {
                ["monument"] = zone,
                ["stats"] = stats,
                ["recentActivity"] = recent
            };
        }

        private string BuildMonumentListText()
        {
            if (_data.Monuments.Count == 0)
                return "No monument zones configured.";

            List<string> lines = new List<string> { "Configured monument zones:" };

            foreach (MonumentZone zone in _data.Monuments.Values.OrderBy(x => x.DisplayName).Take(60))
                lines.Add($"- {zone.Id}: {zone.DisplayName} | enabled={zone.Enabled} | radius={zone.Radius} | risk={zone.RiskLevel}");

            return string.Join("\n", lines.ToArray());
        }

        private string GetStatusText()
        {
            return
                "WorldMindMonumentBrain status\n" +
                $"WorldMindV2 linked: {(WorldMindV2 != null ? "yes" : "no")}\n" +
                $"MapBrain linked: {(WorldMindMapBrain != null ? "yes" : "no")}\n" +
                $"Monument zones: {_data.Monuments.Count}\n" +
                $"Recent activity: {_data.Activity.Count}\n" +
                $"Tracked stats: {_data.Stats.Count}\n" +
                $"Scanner enabled: {_config.Monitoring.EnablePositionScanner}";
        }

        #endregion

        #region Helpers

        private double UnixNow()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private object GetFieldOrProperty(object target, string name)
        {
            if (target == null || string.IsNullOrWhiteSpace(name)) return null;

            Type type = target.GetType();

            System.Reflection.FieldInfo field = type.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (field != null) return field.GetValue(target);

            System.Reflection.PropertyInfo property = type.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (property != null) return property.GetValue(target, null);

            return null;
        }

        private string GetString(Dictionary<string, object> packet, string key, string fallback)
        {
            if (packet == null) return fallback;
            object value;
            return packet.TryGetValue(key, out value) && value != null ? value.ToString() : fallback;
        }

        private int GetInt(Dictionary<string, object> packet, string key, int fallback)
        {
            if (packet == null) return fallback;
            object value;
            if (!packet.TryGetValue(key, out value) || value == null) return fallback;
            int parsed;
            return int.TryParse(value.ToString(), out parsed) ? parsed : fallback;
        }

        private BasePlayer FindPlayerById(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return null;
            return BasePlayer.activePlayerList.FirstOrDefault(x => x != null && x.UserIDString == playerId);
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null || string.IsNullOrWhiteSpace(message)) return;
            player.ChatMessage($"<color=#d7b46a>[WorldMind MonumentBrain]</color> {message}");
        }

        private bool HasAdmin(BasePlayer player)
        {
            if (player == null) return false;
            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin)) return true;

            Reply(player, "You do not have permission to use that command.");
            return false;
        }

        private bool CanUse(BasePlayer player)
        {
            if (player == null) return false;

            if (!_config.General.RequireUsePermission) return true;
            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionUse)) return true;

            Reply(player, "You do not have permission to use this.");
            return false;
        }

        private bool CanUseSilent(BasePlayer player)
        {
            if (player == null) return false;
            if (!_config.General.RequireUsePermission) return true;
            return player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionUse);
        }

        #endregion

        #region Config/Data

        protected override void LoadDefaultConfig()
        {
            _config = PluginConfig.Default();
        }

        private void LoadPluginConfig()
        {
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("Config was null.");
                _config.EnsureDefaults();
                SaveConfig();
            }
            catch (Exception ex)
            {
                PrintWarning($"Could not read config. Creating default config. Error: {ex.Message}");
                LoadDefaultConfig();
                SaveConfig();
            }
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
                _data.EnsureDefaults();
            }
            catch
            {
                _data = new StoredData();
            }
        }

        private void SaveData()
        {
            if (_data == null) return;
            Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        private class PluginConfig
        {
            [JsonProperty("General")]
            public GeneralSettings General = new GeneralSettings();

            [JsonProperty("Monitoring")]
            public MonitoringSettings Monitoring = new MonitoringSettings();

            [JsonProperty("Player Messages")]
            public PlayerMessageSettings PlayerMessages = new PlayerMessageSettings();

            [JsonProperty("Monument Detection")]
            public MonumentDetectionSettings MonumentDetection = new MonumentDetectionSettings();

            [JsonProperty("Reporting")]
            public ReportingSettings Reporting = new ReportingSettings();

            [JsonProperty("WorldMind Integration")]
            public WorldMindIntegrationSettings WorldMindIntegration = new WorldMindIntegrationSettings();

            public static PluginConfig Default()
            {
                PluginConfig config = new PluginConfig();
                config.EnsureDefaults();
                return config;
            }

            public void EnsureDefaults()
            {
                if (General == null) General = new GeneralSettings();
                if (Monitoring == null) Monitoring = new MonitoringSettings();
                if (PlayerMessages == null) PlayerMessages = new PlayerMessageSettings();
                if (MonumentDetection == null) MonumentDetection = new MonumentDetectionSettings();
                if (Reporting == null) Reporting = new ReportingSettings();
                if (WorldMindIntegration == null) WorldMindIntegration = new WorldMindIntegrationSettings();
            }
        }

        private class GeneralSettings
        {
            [JsonProperty("PrintAsciiOnLoad")]
            public bool PrintAsciiOnLoad = true;

            [JsonProperty("Debug")]
            public bool Debug = false;

            [JsonProperty("RequireUsePermission")]
            public bool RequireUsePermission = false;

            [JsonProperty("AutoSaveSeconds")]
            public float AutoSaveSeconds = 300f;
        }

        private class MonitoringSettings
        {
            [JsonProperty("EnablePositionScanner")]
            public bool EnablePositionScanner = true;

            [JsonProperty("ScanIntervalSeconds")]
            public float ScanIntervalSeconds = 12f;

            [JsonProperty("TrackCombatDamage")]
            public bool TrackCombatDamage = true;
        }

        private class PlayerMessageSettings
        {
            [JsonProperty("SendEntryMessages")]
            public bool SendEntryMessages = true;

            [JsonProperty("UseWorldMindForEntryMessages")]
            public bool UseWorldMindForEntryMessages = true;

            [JsonProperty("PlayerMessageCooldownSeconds")]
            public float PlayerMessageCooldownSeconds = 180f;
        }

        private class MonumentDetectionSettings
        {
            [JsonProperty("SeedKnownMonumentsFromTerrain")]
            public bool SeedKnownMonumentsFromTerrain = true;

            [JsonProperty("DefaultRadius")]
            public float DefaultRadius = 140f;
        }

        private class ReportingSettings
        {
            [JsonProperty("KeepRecentActivity")]
            public int KeepRecentActivity = 300;

            [JsonProperty("SummaryActivityLimit")]
            public int SummaryActivityLimit = 25;
        }

        private class WorldMindIntegrationSettings
        {
            [JsonProperty("RecordEventsToWorldMind")]
            public bool RecordEventsToWorldMind = true;
        }

        private class StoredData
        {
            [JsonProperty("Monuments")]
            public Dictionary<string, MonumentZone> Monuments = new Dictionary<string, MonumentZone>();

            [JsonProperty("Stats")]
            public Dictionary<string, MonumentStats> Stats = new Dictionary<string, MonumentStats>();

            [JsonProperty("Activity")]
            public List<MonumentActivity> Activity = new List<MonumentActivity>();

            public void EnsureDefaults()
            {
                if (Monuments == null) Monuments = new Dictionary<string, MonumentZone>();
                if (Stats == null) Stats = new Dictionary<string, MonumentStats>();
                if (Activity == null) Activity = new List<MonumentActivity>();
            }
        }

        public class MonumentZone
        {
            public string Id = "";
            public string DisplayName = "";
            public float PositionX;
            public float PositionY;
            public float PositionZ;
            public float Radius = 140f;
            public bool Enabled = true;
            public bool OwnerDefined = false;
            public string RiskLevel = "variable";
            public string Notes = "";

            [JsonIgnore]
            public Vector3 Position
            {
                get { return new Vector3(PositionX, PositionY, PositionZ); }
            }
        }

        public class MonumentStats
        {
            public string MonumentId = "";
            public int TotalActivity = 0;
            public int ActivityWeight = 0;
            public string LastActivityUtc = "";
            public Dictionary<string, int> ActivityCounts = new Dictionary<string, int>();
        }

        public class MonumentActivity
        {
            public string MonumentId = "";
            public string ActivityType = "";
            public string PlayerId = "";
            public string PlayerName = "";
            public int Weight = 1;
            public string Notes = "";
            public string TimestampUtc = "";
        }

        #endregion
    }
}
