using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    /*
     *  DDDDDDDD      VV        VV     88888888      DDDDDDDD
     *  DDDDDDDDD     VV        VV    8888888888     DDDDDDDDD
     *  DD     DDD    VV        VV    88      88     DD     DDD
     *  DD      DD    VV        VV    88      88     DD      DD
     *  DD      DD     VV      VV      88888888      DD      DD
     *  DD      DD     VV      VV     8888888888     DD      DD
     *  DD      DD      VV    VV      88      88     DD      DD
     *  DD     DDD       VV  VV       88      88     DD     DDD
     *  DDDDDDDDD        VVVV         8888888888     DDDDDDDDD
     *  DDDDDDDD          VV           88888888      DDDDDDDD
     *
     *  Made with love by Deviated Systems
     *  Author: Devi8d0ne
     */

    [Info("WorldMindRaidBrain", "Devi8d0ne", "1.1.1")]
    [Description("Deviated Playgrounds WorldMind raid detection, raid grouping, player warnings, summaries, and DiscordMind routing.")]
    public class WorldMindRaidBrain : RustPlugin
    {
        private const string PermAdmin = "worldmindraidbrain.admin";
        private const string PermUse = "worldmindraidbrain.use";
        private const string MadeWithLoveTag = "Made with love by Deviated Systems";

        [PluginReference] private Plugin WorldMindV2;
        [PluginReference] private Plugin WorldMindDiscordMind;

        private PluginConfig _config;
        private readonly Dictionary<string, RaidZone> _zones = new Dictionary<string, RaidZone>();
        private readonly Dictionary<ulong, double> _lastPlayerAlert = new Dictionary<ulong, double>();
        private readonly Dictionary<string, double> _lastZoneWorldMindAsk = new Dictionary<string, double>();
        private readonly Dictionary<string, double> _lastZoneDiscordSend = new Dictionary<string, double>();

        #region Oxide Hooks

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermUse, this);
            LoadConfiguration();
        }

        private void OnServerInitialized()
        {
            if (_config.General.PrintAsciiOnLoad)
            {
                PrintWarning(Dv8dAscii);
                Puts(MadeWithLoveTag);
            }

            timer.Every(Mathf.Max(10f, _config.RaidDetection.ZoneCleanupIntervalSeconds), CleanupExpiredZones);
            timer.Every(Mathf.Max(15f, _config.RaidDetection.SummaryIntervalSeconds), TickZoneSummaries);
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (!_config.General.Enabled || entity == null || info == null) return;
            if (!IsRaidTarget(entity)) return;
            if (!IsRaidDamage(info)) return;

            var attacker = info.InitiatorPlayer;
            if (attacker != null && _config.Permissions.RequireUsePermissionForTracking && !HasUse(attacker)) return;

            var pos = entity.transform.position;
            var zoneKey = GetZoneKey(pos);
            var zone = GetOrCreateZone(zoneKey, pos);

            zone.LastActivity = Now;
            zone.HitCount++;
            zone.TotalDamage += info.damageTypes?.Total() ?? 0f;
            zone.LastTarget = GetEntityName(entity);
            zone.LastWeapon = info.WeaponPrefab != null ? info.WeaponPrefab.ShortPrefabName : "unknown";

            if (attacker != null)
            {
                zone.Attackers.Add(attacker.userID);
                zone.AttackerNames[attacker.userID] = attacker.displayName;
            }

            var ownerId = GetLikelyOwnerId(entity);
            if (ownerId != 0) zone.LikelyOwners.Add(ownerId);

            if (_config.Alerts.AlertNearbyPlayers)
            {
                AlertNearbyPlayers(zone, pos, attacker?.userID ?? 0UL);
            }

            if (_config.WorldMind.RecordRaidEvents)
            {
                RecordWorldMindEvent(zone, "raid_damage_detected");
            }

            SendDiscordRaidEvent(zone, "raid_damage_detected", attacker, entity, pos, false, null);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (!_config.General.Enabled || entity == null || info == null) return;
            if (!IsRaidTarget(entity)) return;
            if (!IsRaidDamage(info)) return;

            var pos = entity.transform.position;
            var zoneKey = GetZoneKey(pos);
            var zone = GetOrCreateZone(zoneKey, pos);

            zone.LastActivity = Now;
            zone.DestroyedEntities++;
            zone.LastTarget = GetEntityName(entity);
            zone.LastWeapon = info.WeaponPrefab != null ? info.WeaponPrefab.ShortPrefabName : "unknown";

            var attacker = info.InitiatorPlayer;
            if (attacker != null)
            {
                zone.Attackers.Add(attacker.userID);
                zone.AttackerNames[attacker.userID] = attacker.displayName;
            }

            if (_config.WorldMind.RecordRaidEvents)
            {
                RecordWorldMindEvent(zone, "raid_entity_destroyed");
            }

            SendDiscordRaidEvent(zone, "raid_entity_destroyed", attacker, entity, pos, true, null);
        }

        #endregion

        #region Commands

        [ChatCommand("wmraid")]
        private void CmdRaid(BasePlayer player, string command, string[] args)
        {
            if (player != null && !HasAdmin(player))
            {
                Reply(player, "You do not have permission to use this command.");
                return;
            }

            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "status":
                    Reply(player, BuildStatus());
                    break;

                case "reload":
                    LoadConfiguration();
                    Reply(player, "WorldMindRaidBrain config reloaded.");
                    break;

                case "zones":
                    Reply(player, BuildZonesList());
                    break;

                case "clear":
                    _zones.Clear();
                    Reply(player, "WorldMindRaidBrain active raid zones cleared.");
                    break;

                case "test":
                    RunTest(player);
                    break;

                case "testdiscord":
                    Reply(player, SendDiscordTest(player) ? "WorldMindRaidBrain Discord test queued." : "WorldMindRaidBrain Discord test failed. Check DiscordMind config/status.");
                    break;

                default:
                    Reply(player, "Usage: /wmraid status | zones | reload | clear | test | testdiscord");
                    break;
            }
        }

        #endregion

        [ConsoleCommand("worldmindraidbrain.status")]
        private void CcmdStatus(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            BasePlayer player = arg.Player();
            if (player != null && !HasAdmin(player))
            {
                arg.ReplyWith("No permission.");
                return;
            }

            arg.ReplyWith(BuildStatus());
        }

        [ConsoleCommand("worldmindraidbrain.testdiscord")]
        private void CcmdTestDiscord(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            BasePlayer player = arg.Player();
            if (player != null && !HasAdmin(player))
            {
                arg.ReplyWith("No permission.");
                return;
            }

            bool ok = SendDiscordTest(player);
            arg.ReplyWith(ok ? "WorldMindRaidBrain Discord test queued." : "WorldMindRaidBrain Discord test failed. Check DiscordMind config/status.");
        }


        #region Raid Detection

        private bool IsRaidTarget(BaseCombatEntity entity)
        {
            var name = entity.ShortPrefabName ?? string.Empty;
            var typeName = entity.GetType().Name ?? string.Empty;

            if (_config.RaidDetection.TrackBuildingBlocks && entity is BuildingBlock) return true;
            if (_config.RaidDetection.TrackDoors && (name.Contains("door") || typeName.Contains("Door"))) return true;
            if (_config.RaidDetection.TrackExternalWalls && (name.Contains("wall.external") || name.Contains("gates.external"))) return true;
            if (_config.RaidDetection.TrackCupboards && name.Contains("cupboard.tool")) return true;
            if (_config.RaidDetection.TrackDeployables && IsConfiguredDeployable(name)) return true;

            return false;
        }

        private bool IsConfiguredDeployable(string shortPrefabName)
        {
            if (string.IsNullOrEmpty(shortPrefabName)) return false;
            if (_config.RaidDetection.DeployableKeywords == null) return false;
            return _config.RaidDetection.DeployableKeywords.Any(k => !string.IsNullOrEmpty(k) && shortPrefabName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool IsRaidDamage(HitInfo info)
        {
            if (info.damageTypes == null) return false;

            if (_config.RaidDetection.ExplosiveDamageOnly)
            {
                var explosion = info.damageTypes.Get(Rust.DamageType.Explosion);
                var heat = info.damageTypes.Get(Rust.DamageType.Heat);
                return explosion >= _config.RaidDetection.MinimumDamageToTrack || heat >= _config.RaidDetection.MinimumDamageToTrack;
            }

            return info.damageTypes.Total() >= _config.RaidDetection.MinimumDamageToTrack;
        }

        private RaidZone GetOrCreateZone(string key, Vector3 pos)
        {
            RaidZone zone;
            if (_zones.TryGetValue(key, out zone)) return zone;

            zone = new RaidZone
            {
                Key = key,
                Center = pos,
                FirstActivity = Now,
                LastActivity = Now
            };
            _zones[key] = zone;
            return zone;
        }

        private string GetZoneKey(Vector3 pos)
        {
            var radius = Mathf.Max(25f, _config.RaidDetection.ZoneRadiusMeters);
            var x = Mathf.RoundToInt(pos.x / radius);
            var z = Mathf.RoundToInt(pos.z / radius);
            return $"{x}:{z}";
        }

        private ulong GetLikelyOwnerId(BaseCombatEntity entity)
        {
            if (entity == null) return 0UL;
            if (entity.OwnerID != 0) return entity.OwnerID;

            var block = entity as BuildingBlock;
            if (block != null && block.OwnerID != 0) return block.OwnerID;

            return 0UL;
        }

        private string GetEntityName(BaseEntity entity)
        {
            if (entity == null) return "unknown";
            if (!string.IsNullOrEmpty(entity.ShortPrefabName)) return entity.ShortPrefabName;
            return entity.GetType().Name;
        }

        #endregion

        #region Alerts and WorldMind

        private void AlertNearbyPlayers(RaidZone zone, Vector3 pos, ulong attackerId)
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                if (attackerId != 0 && player.userID == attackerId && !_config.Alerts.AlertAttackers) continue;
                if (_config.Permissions.RequireUsePermissionForAlerts && !HasUse(player)) continue;

                var distance = Vector3.Distance(player.transform.position, pos);
                if (distance > _config.Alerts.AlertRadiusMeters) continue;

                var last = 0d;
                _lastPlayerAlert.TryGetValue(player.userID, out last);
                if (Now - last < _config.Alerts.PlayerAlertCooldownSeconds) continue;

                _lastPlayerAlert[player.userID] = Now;

                if (_config.WorldMind.UseWorldMindForPlayerWarnings)
                {
                    AskWorldMindForPlayerWarning(player, zone, distance);
                }
                else
                {
                    Reply(player, _config.Messages.FallbackPlayerWarning.Replace("{distance}", Mathf.RoundToInt(distance).ToString()));
                }
            }
        }

        private void AskWorldMindForPlayerWarning(BasePlayer player, RaidZone zone, float distance)
        {
            if (WorldMindV2 == null)
            {
                Reply(player, _config.Messages.FallbackPlayerWarning.Replace("{distance}", Mathf.RoundToInt(distance).ToString()));
                return;
            }

            var prompt = BuildPlayerWarningPrompt(player, zone, distance);
            var result = WorldMindV2.Call("WorldMind_AskText", "WorldMindRaidBrain", player.UserIDString, player.displayName, "raid_warning", prompt, _config.WorldMind.MaxResponseCharacters);
            var text = CleanModelText(result as string);

            if (string.IsNullOrEmpty(text))
            {
                Reply(player, _config.Messages.FallbackPlayerWarning.Replace("{distance}", Mathf.RoundToInt(distance).ToString()));
                return;
            }

            Reply(player, text);
        }

        private void TickZoneSummaries()
        {
            if (!_config.General.Enabled || !_config.WorldMind.UseWorldMindForRaidSummaries || WorldMindV2 == null) return;

            foreach (var zone in _zones.Values.ToList())
            {
                if (zone.HitCount < _config.RaidDetection.MinimumHitsBeforeSummary) continue;

                var lastAsk = 0d;
                _lastZoneWorldMindAsk.TryGetValue(zone.Key, out lastAsk);
                if (Now - lastAsk < _config.WorldMind.ZoneSummaryCooldownSeconds) continue;

                _lastZoneWorldMindAsk[zone.Key] = Now;
                AskWorldMindForZoneSummary(zone);
            }
        }

        private void AskWorldMindForZoneSummary(RaidZone zone)
        {
            var prompt = BuildZoneSummaryPrompt(zone);
            var result = WorldMindV2.Call("WorldMind_AskText", "WorldMindRaidBrain", "server", "Server", "raid_summary", prompt, _config.WorldMind.MaxResponseCharacters);
            var text = CleanModelText(result as string);
            if (string.IsNullOrEmpty(text)) return;

            if (_config.Alerts.BroadcastRaidSummaries)
            {
                Server.Broadcast($"<color=#d8b56d>[Raid]</color> {text}");
            }
            else
            {
                Puts($"Raid summary {zone.Key}: {text}");
            }

            SendDiscordRaidEvent(zone, "raid_summary", null, null, zone.Center, false, text);
        }

        private void RecordWorldMindEvent(RaidZone zone, string eventType)
        {
            if (WorldMindV2 == null) return;

            var payload = new Dictionary<string, object>
            {
                ["eventType"] = eventType,
                ["plugin"] = "WorldMindRaidBrain",
                ["zoneKey"] = zone.Key,
                ["center"] = FormatVector(zone.Center),
                ["hitCount"] = zone.HitCount,
                ["destroyedEntities"] = zone.DestroyedEntities,
                ["attackerCount"] = zone.Attackers.Count,
                ["likelyOwnerCount"] = zone.LikelyOwners.Count,
                ["lastTarget"] = zone.LastTarget ?? "unknown",
                ["lastWeapon"] = zone.LastWeapon ?? "unknown",
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };

            WorldMindV2.Call("WorldMind_RecordEvent", eventType, payload);
        }

        private bool SendDiscordTest(BasePlayer player)
        {
            var fake = new RaidZone
            {
                Key = "test:discord",
                Center = player != null ? player.transform.position : Vector3.zero,
                FirstActivity = Now,
                LastActivity = Now,
                HitCount = 12,
                DestroyedEntities = 2,
                TotalDamage = 1800f,
                LastTarget = "wall.stone",
                LastWeapon = "explosive.timed"
            };

            if (player != null)
            {
                fake.Attackers.Add(player.userID);
                fake.AttackerNames[player.userID] = player.displayName;
            }

            return SendDiscordRaidEvent(fake, "raid_test", player, null, fake.Center, false, "Deviated Playgrounds raid test: breach pressure detected, loot greed awake, counters probably already listening.");
        }

        private bool SendDiscordRaidEvent(RaidZone zone, string eventType, BasePlayer attacker, BaseEntity target, Vector3 pos, bool destroyed, string summaryText)
        {
            if (!_config.DiscordMind.Enabled) return false;

            if (eventType == "raid_damage_detected" && !_config.DiscordMind.SendDamageEvents) return false;
            if (eventType == "raid_damage_detected" && !PassDiscordZoneCooldown(zone.Key)) return false;
            if (eventType == "raid_entity_destroyed" && !_config.DiscordMind.SendDestroyedEvents) return false;
            if (eventType == "raid_summary" && !_config.DiscordMind.SendSummaryEvents) return false;
            if (eventType == "raid_test" && !_config.DiscordMind.SendTestEvents) return false;

            if (zone == null) return false;

            var packet = BuildDiscordPacket(zone, eventType, attacker, target, pos, destroyed, summaryText);

            try
            {
                object result = Interface.CallHook("WorldMindDiscordMind_SendRaidEvent", packet);
                if (HookAccepted(result)) return true;

                result = Interface.CallHook("WorldMindDiscordMind_SendEvent", packet);
                if (HookAccepted(result)) return true;

                string title = GetString(packet, "title", "WorldMind Raid Event");
                string message = GetString(packet, "message", "Raid activity detected.");
                result = Interface.CallHook("WorldMindDiscordMind_SendMessageToChannel", _config.DiscordMind.ChannelKey, title, message, _config.DiscordMind.Category);
                if (HookAccepted(result)) return true;

                if (_config.DiscordMind.DebugDiscordRouting)
                    PrintWarning("DiscordMind raid route did not return success for " + eventType + ". Result=" + (result == null ? "null" : result.ToString()));
            }
            catch (Exception ex)
            {
                if (_config.DiscordMind.DebugDiscordRouting)
                    PrintWarning("DiscordMind raid routing failed: " + ex.Message);
            }

            return false;
        }

        private bool PassDiscordZoneCooldown(string zoneKey)
        {
            if (string.IsNullOrWhiteSpace(zoneKey)) return true;
            double cooldown = Math.Max(0d, _config.DiscordMind.MinimumSecondsBetweenZoneDamagePosts);
            if (cooldown <= 0d) return true;

            double last;
            if (_lastZoneDiscordSend.TryGetValue(zoneKey, out last) && Now - last < cooldown) return false;
            _lastZoneDiscordSend[zoneKey] = Now;
            return true;
        }

        private Dictionary<string, object> BuildDiscordPacket(RaidZone zone, string eventType, BasePlayer attacker, BaseEntity target, Vector3 pos, bool destroyed, string summaryText)
        {
            var location = BuildLocationFacts(pos);
            string title = BuildDiscordTitle(eventType, zone, location);
            string message = BuildDiscordMessage(zone, eventType, attacker, target, location, destroyed, summaryText);

            var facts = new Dictionary<string, object>
            {
                ["plugin"] = Name,
                ["category"] = _config.DiscordMind.Category,
                ["channelKey"] = _config.DiscordMind.ChannelKey,
                ["eventType"] = eventType,
                ["zoneKey"] = zone.Key,
                ["center"] = FormatVector(zone.Center),
                ["hitCount"] = zone.HitCount,
                ["destroyedEntities"] = zone.DestroyedEntities,
                ["totalDamage"] = Mathf.RoundToInt(zone.TotalDamage),
                ["attackerCount"] = zone.Attackers.Count,
                ["attackerNames"] = string.Join(", ", zone.AttackerNames.Values.ToArray()),
                ["likelyOwnerCount"] = zone.LikelyOwners.Count,
                ["lastTarget"] = zone.LastTarget ?? "unknown",
                ["lastWeapon"] = zone.LastWeapon ?? "unknown",
                ["target"] = target == null ? "unknown" : GetEntityName(target),
                ["attacker"] = attacker == null ? "unknown" : attacker.displayName,
                ["location"] = location,
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };

            return new Dictionary<string, object>
            {
                ["category"] = _config.DiscordMind.Category,
                ["channelKey"] = _config.DiscordMind.ChannelKey,
                ["title"] = CleanDiscordText(title, 180),
                ["message"] = CleanDiscordText(message, _config.DiscordMind.MaxDiscordMessageLength),
                ["eventType"] = eventType,
                ["facts"] = facts
            };
        }

        private string BuildDiscordTitle(string eventType, RaidZone zone, Dictionary<string, object> location)
        {
            string grid = GetString(location, "grid", "unknown grid");
            if (eventType == "raid_entity_destroyed") return "Raid breach confirmed — " + grid;
            if (eventType == "raid_summary") return "Raid pressure summary — " + grid;
            if (eventType == "raid_test") return "Raid Discord test — Deviated Playgrounds";
            return "Raid pressure detected — " + grid;
        }

        private string BuildDiscordMessage(RaidZone zone, string eventType, BasePlayer attacker, BaseEntity target, Dictionary<string, object> location, bool destroyed, string summaryText)
        {
            if (!string.IsNullOrWhiteSpace(summaryText))
                return summaryText + "\n" + BuildRaidFactLine(zone, attacker, target, location);

            string lead;
            if (eventType == "raid_entity_destroyed")
                lead = "Breach confirmed. Something just got deleted and the playground heard it.";
            else if (eventType == "raid_test")
                lead = "Raid test fired. If this lands, DiscordMind is listening.";
            else
                lead = "Raid pressure detected. Explosives are talking and the island is taking notes.";

            return lead + "\n" + BuildRaidFactLine(zone, attacker, target, location);
        }

        private string BuildRaidFactLine(RaidZone zone, BasePlayer attacker, BaseEntity target, Dictionary<string, object> location)
        {
            string grid = GetString(location, "grid", "unknown");
            string biome = GetString(location, "biome", "unknown");
            string monument = GetString(location, "nearestMonument", "Wilderness");
            string terrain = GetString(location, "terrain", "open island");
            string attackerName = attacker == null ? "unknown" : attacker.displayName;
            string targetName = target == null ? (zone.LastTarget ?? "unknown") : GetEntityName(target);

            return "Grid: " + grid + " | Biome: " + biome + " | Near: " + monument + " | Terrain: " + terrain +
                   "\nTarget: " + targetName + " | Weapon: " + (zone.LastWeapon ?? "unknown") + " | Hits: " + zone.HitCount + " | Destroyed: " + zone.DestroyedEntities +
                   "\nAttacker: " + attackerName + " | Attackers seen: " + zone.Attackers.Count + " | Damage: " + Mathf.RoundToInt(zone.TotalDamage);
        }

        private Dictionary<string, object> BuildLocationFacts(Vector3 pos)
        {
            var ctx = new Dictionary<string, object>();
            ctx["grid"] = GetGrid(pos);
            ctx["biome"] = GuessBiome(pos);
            ctx["terrain"] = GuessTerrainContext(pos);
            ctx["position"] = FormatVector(pos);

            var monument = FindNearestMonument(pos, _config.DiscordMind.MaxMonumentSearchMeters);
            ctx["nearestMonument"] = monument.Name;
            ctx["distanceToMonumentMeters"] = monument.Distance >= 0 ? Mathf.RoundToInt(monument.Distance) : -1;
            ctx["areaType"] = monument.Distance >= 0 && monument.Distance <= 125f ? "at monument" : monument.Distance >= 0 && monument.Distance <= 300f ? "near monument" : "wilderness";
            return ctx;
        }

        private string GetGrid(Vector3 position)
        {
            try
            {
                int size = GetWorldSize();
                int gridSize = Mathf.Max(1, _config.DiscordMind.GridCellSizeMeters);
                float half = size / 2f;
                int x = Mathf.Clamp(Mathf.FloorToInt((position.x + half) / gridSize), 0, 999);
                int z = Mathf.Clamp(Mathf.FloorToInt((half - position.z) / gridSize), 0, 999);
                return NumberToGridLetters(x) + (z + 1);
            }
            catch { return "unknown"; }
        }

        private int GetWorldSize()
        {
            try { if (ConVar.Server.worldsize > 0) return ConVar.Server.worldsize; } catch { }
            try { if (TerrainMeta.Size.x > 0) return Mathf.RoundToInt(TerrainMeta.Size.x); } catch { }
            return 4500;
        }

        private string NumberToGridLetters(int number)
        {
            number = Mathf.Max(0, number);
            string letters = string.Empty;
            do
            {
                letters = (char)('A' + (number % 26)) + letters;
                number = number / 26 - 1;
            } while (number >= 0);
            return letters;
        }

        private string GuessBiome(Vector3 position)
        {
            if (position.y < -2f) return "Ocean/Shoreline";
            int size = GetWorldSize();
            if (position.z > size * 0.22f) return "Snow/Arctic side";
            if (position.x > size * 0.22f) return "Desert/Arid side";
            return "Forest/Temperate side";
        }

        private string GuessTerrainContext(Vector3 position)
        {
            if (position.y < -5f) return "waterline/ocean danger";
            if (position.y < 4f) return "low ground with bad sightlines";
            if (position.y > 135f) return "high ground with long sightlines";
            if (position.y > 80f) return "raised terrain/ridge pressure";
            return "open ground";
        }

        private MonumentRead FindNearestMonument(Vector3 position, float maxDistance)
        {
            var best = new MonumentRead { Name = "Wilderness", Distance = -1f };
            try
            {
                var monuments = UnityEngine.Object.FindObjectsOfType<MonumentInfo>();
                if (monuments == null || monuments.Length == 0) return best;
                float bestDistance = maxDistance;
                foreach (var monument in monuments)
                {
                    if (monument == null) continue;
                    float distance = Vector3.Distance(position, monument.transform.position);
                    if (distance > bestDistance) continue;
                    bestDistance = distance;
                    best.Name = CleanMonumentName(monument.name);
                    best.Distance = distance;
                }
            }
            catch { }
            return best;
        }

        private string CleanMonumentName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Unknown monument";
            string text = raw.Replace("(Clone)", string.Empty).Replace("_", " ").Replace("/", " ").Trim();
            string lower = text.ToLowerInvariant();
            foreach (var pair in _config.DiscordMind.MonumentNameOverrides)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && lower.Contains(pair.Key.ToLowerInvariant())) return pair.Value;
            }
            return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
        }

        private class MonumentRead
        {
            public string Name;
            public float Distance;
        }

        private bool HookAccepted(object result)
        {
            if (result == null) return false;
            if (result is bool) return (bool)result;
            bool parsed;
            if (bool.TryParse(result.ToString(), out parsed)) return parsed;
            return true;
        }

        private string GetString(Dictionary<string, object> dict, string key, string fallback)
        {
            if (dict == null || string.IsNullOrWhiteSpace(key)) return fallback;
            object value;
            if (dict.TryGetValue(key, out value) && value != null) return value.ToString();
            return fallback;
        }

        private string CleanDiscordText(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            value = value.Replace("@everyone", "@\u200beveryone").Replace("@here", "@\u200bhere").Replace("\r", " ").Replace("\n", "\n").Trim();
            if (max > 0 && value.Length > max) value = value.Substring(0, Math.Max(0, max - 3)).TrimEnd() + "...";
            return value;
        }

        private string BuildPlayerWarningPrompt(BasePlayer player, RaidZone zone, float distance)
        {
            return
                "Write one short Deviated Playgrounds raid warning for the player. " +
                "Tone: tactical, hostile, Rust-aware, concise, and player-facing. Make raid pressure feel dangerous without inventing facts. " +
                "Use WarMode, kits, homes, Discord, VIP, or economy only if base WorldMind facts explicitly allow them. Keep it under 160 characters. " +
                $"Player={player.displayName}; DistanceMeters={Mathf.RoundToInt(distance)}; RaidZone={zone.Key}; Hits={zone.HitCount}; DestroyedEntities={zone.DestroyedEntities}; AttackersSeen={zone.Attackers.Count}; LastTarget={zone.LastTarget}; LastWeapon={zone.LastWeapon}.";
        }

        private string BuildZoneSummaryPrompt(RaidZone zone)
        {
            return
                "Write one concise Deviated Playgrounds raid activity summary. " +
                "Tone: tactical, sarcastic, hostile island intelligence. Do not invent attackers, loot, commands, or custom systems. Keep it under 180 characters. " +
                $"RaidZone={zone.Key}; Center={FormatVector(zone.Center)}; Hits={zone.HitCount}; TotalDamage={Mathf.RoundToInt(zone.TotalDamage)}; DestroyedEntities={zone.DestroyedEntities}; AttackersSeen={zone.Attackers.Count}; LikelyOwners={zone.LikelyOwners.Count}; LastTarget={zone.LastTarget}; LastWeapon={zone.LastWeapon}.";
        }

        private string CleanModelText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            text = text.Trim().Replace("\n", " ").Replace("\r", " ");
            if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase) || text.Equals("null", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            if (text.StartsWith("{") || text.StartsWith("[")) return string.Empty;
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            if (text.Length > _config.WorldMind.MaxResponseCharacters) text = text.Substring(0, _config.WorldMind.MaxResponseCharacters).TrimEnd() + "...";
            return text;
        }

        #endregion

        #region Maintenance

        private void CleanupExpiredZones()
        {
            var maxAge = Mathf.Max(60f, _config.RaidDetection.ZoneExpireSeconds);
            foreach (var kvp in _zones.ToList())
            {
                if (Now - kvp.Value.LastActivity > maxAge)
                {
                    _zones.Remove(kvp.Key);
                    _lastZoneWorldMindAsk.Remove(kvp.Key);
                }
            }
        }

        private void RunTest(BasePlayer player)
        {
            var fake = new RaidZone
            {
                Key = "test:zone",
                Center = player != null ? player.transform.position : Vector3.zero,
                FirstActivity = Now,
                LastActivity = Now,
                HitCount = 8,
                DestroyedEntities = 1,
                TotalDamage = 1200f,
                LastTarget = "wall.stone",
                LastWeapon = "explosive.timed"
            };

            fake.Attackers.Add(player != null ? player.userID : 1UL);

            if (player != null)
            {
                AskWorldMindForPlayerWarning(player, fake, 95f);
            }
            else
            {
                AskWorldMindForZoneSummary(fake);
            }
        }

        #endregion

        #region Config

        private class PluginConfig
        {
            [JsonProperty("General")]
            public GeneralConfig General = new GeneralConfig();

            [JsonProperty("Permissions")]
            public PermissionConfig Permissions = new PermissionConfig();

            [JsonProperty("Raid Detection")]
            public RaidDetectionConfig RaidDetection = new RaidDetectionConfig();

            [JsonProperty("Alerts")]
            public AlertConfig Alerts = new AlertConfig();

            [JsonProperty("WorldMind Integration")]
            public WorldMindConfig WorldMind = new WorldMindConfig();

            [JsonProperty("DiscordMind Integration")]
            public DiscordMindConfig DiscordMind = new DiscordMindConfig();

            [JsonProperty("Messages")]
            public MessageConfig Messages = new MessageConfig();
        }

        private class GeneralConfig
        {
            public bool Enabled = true;
            public bool PrintAsciiOnLoad = true;
            public bool Debug = false;
            public string ChatPrefix = "<color=#d8b56d>[WorldMind Raid]</color>";
        }

        private class PermissionConfig
        {
            public bool RequireUsePermissionForTracking = false;
            public bool RequireUsePermissionForAlerts = false;
        }

        private class RaidDetectionConfig
        {
            public bool ExplosiveDamageOnly = true;
            public float MinimumDamageToTrack = 5f;
            public float ZoneRadiusMeters = 75f;
            public float ZoneExpireSeconds = 900f;
            public float ZoneCleanupIntervalSeconds = 60f;
            public float SummaryIntervalSeconds = 45f;
            public int MinimumHitsBeforeSummary = 4;
            public bool TrackBuildingBlocks = true;
            public bool TrackDoors = true;
            public bool TrackExternalWalls = true;
            public bool TrackCupboards = true;
            public bool TrackDeployables = true;
            public List<string> DeployableKeywords = new List<string>
            {
                "box.wooden",
                "box.wooden.large",
                "furnace",
                "locker",
                "battery",
                "turret",
                "sam_site",
                "repairbench",
                "workbench"
            };
        }

        private class AlertConfig
        {
            public bool AlertNearbyPlayers = true;
            public bool AlertAttackers = false;
            public float AlertRadiusMeters = 120f;
            public float PlayerAlertCooldownSeconds = 180f;
            public bool BroadcastRaidSummaries = false;
        }

        private class WorldMindConfig
        {
            public bool UseWorldMindForPlayerWarnings = true;
            public bool UseWorldMindForRaidSummaries = true;
            public bool RecordRaidEvents = true;
            public float ZoneSummaryCooldownSeconds = 240f;
            public int MaxResponseCharacters = 220;
        }

        private class DiscordMindConfig
        {
            public bool Enabled = true;
            public string ChannelKey = "raid";
            public string Category = "raid";
            public bool SendDamageEvents = true;
            public bool SendDestroyedEvents = true;
            public bool SendSummaryEvents = true;
            public bool SendTestEvents = true;
            public double MinimumSecondsBetweenZoneDamagePosts = 60d;
            public int MaxDiscordMessageLength = 1800;
            public int GridCellSizeMeters = 150;
            public float MaxMonumentSearchMeters = 450f;
            public bool DebugDiscordRouting = false;
            public Dictionary<string, string> MonumentNameOverrides = new Dictionary<string, string>
            {
                ["launch"] = "Launch Site",
                ["airfield"] = "Airfield",
                ["military_tunnel"] = "Military Tunnels",
                ["military tunnel"] = "Military Tunnels",
                ["trainyard"] = "Train Yard",
                ["train yard"] = "Train Yard",
                ["powerplant"] = "Power Plant",
                ["power plant"] = "Power Plant",
                ["water_treatment"] = "Water Treatment",
                ["water treatment"] = "Water Treatment",
                ["junkyard"] = "Junkyard",
                ["satellite"] = "Satellite Dish",
                ["dome"] = "Dome",
                ["harbor"] = "Harbor",
                ["lighthouse"] = "Lighthouse",
                ["supermarket"] = "Supermarket",
                ["gas_station"] = "Gas Station",
                ["gas station"] = "Gas Station",
                ["excavator"] = "Giant Excavator",
                ["oilrig"] = "Oil Rig",
                ["oil rig"] = "Oil Rig",
                ["outpost"] = "Outpost",
                ["bandit"] = "Bandit Camp"
            };
        }

        private class MessageConfig
        {
            public string FallbackPlayerWarning = "Raid activity detected roughly {distance}m away. Stay aware.";
            public string NoActiveZones = "No active raid zones are currently being tracked.";
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
        }

        private void LoadConfiguration()
        {
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("Config was empty.");
                MergeMissingConfigSections();
                // Owner-safe: do not SaveConfig() on normal load.
                // This prevents owner-edited configs from being rewritten or reverted on reload.
            }
            catch (Exception ex)
            {
                PrintError($"Config load failed: {ex.Message}. Creating default config.");
                LoadDefaultConfig();
                SaveConfig();
            }
        }

        private void MergeMissingConfigSections()
        {
            if (_config.General == null) _config.General = new GeneralConfig();
            if (_config.Permissions == null) _config.Permissions = new PermissionConfig();
            if (_config.RaidDetection == null) _config.RaidDetection = new RaidDetectionConfig();
            if (_config.Alerts == null) _config.Alerts = new AlertConfig();
            if (_config.WorldMind == null) _config.WorldMind = new WorldMindConfig();
            if (_config.DiscordMind == null) _config.DiscordMind = new DiscordMindConfig();
            if (_config.Messages == null) _config.Messages = new MessageConfig();
            if (_config.RaidDetection.DeployableKeywords == null) _config.RaidDetection.DeployableKeywords = new RaidDetectionConfig().DeployableKeywords;
            if (_config.DiscordMind.MonumentNameOverrides == null) _config.DiscordMind.MonumentNameOverrides = new DiscordMindConfig().MonumentNameOverrides;
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        #endregion

        #region Helpers

        private bool HasAdmin(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin));
        }

        private bool HasUse(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermUse));
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null)
            {
                Puts(StripTags(message));
                return;
            }

            player.ChatMessage($"{_config.General.ChatPrefix} {message}");
        }

        private string BuildStatus()
        {
            return
                $"WorldMindRaidBrain status\n" +
                $"Enabled: {_config.General.Enabled}\n" +
                $"WorldMindV2 linked: {(WorldMindV2 != null ? "yes" : "no")}\n" +
                $"DiscordMind linked: {(WorldMindDiscordMind != null ? "yes" : "no")}\n" +
                $"Discord routing enabled: {_config.DiscordMind.Enabled}\n" +
                $"Discord channel key: {_config.DiscordMind.ChannelKey}\n" +
                $"Active raid zones: {_zones.Count}\n" +
                $"Alert radius: {_config.Alerts.AlertRadiusMeters}m\n" +
                $"Zone radius: {_config.RaidDetection.ZoneRadiusMeters}m";
        }

        private string BuildZonesList()
        {
            if (_zones.Count == 0) return _config.Messages.NoActiveZones;

            var lines = new List<string> { "Active raid zones:" };
            foreach (var zone in _zones.Values.OrderByDescending(z => z.LastActivity).Take(10))
            {
                lines.Add($"{zone.Key} | hits={zone.HitCount} destroyed={zone.DestroyedEntities} attackers={zone.Attackers.Count} last={Mathf.RoundToInt((float)(Now - zone.LastActivity))}s ago target={zone.LastTarget}");
            }
            return string.Join("\n", lines.ToArray());
        }

        private string FormatVector(Vector3 pos)
        {
            return $"{Mathf.RoundToInt(pos.x)},{Mathf.RoundToInt(pos.y)},{Mathf.RoundToInt(pos.z)}";
        }

        private string StripTags(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("<color=#d8b56d>", string.Empty).Replace("</color>", string.Empty);
        }

        private double Now => Interface.Oxide.Now;

        private class RaidZone
        {
            public string Key;
            public Vector3 Center;
            public double FirstActivity;
            public double LastActivity;
            public int HitCount;
            public int DestroyedEntities;
            public float TotalDamage;
            public string LastTarget;
            public string LastWeapon;
            public readonly HashSet<ulong> Attackers = new HashSet<ulong>();
            public readonly Dictionary<ulong, string> AttackerNames = new Dictionary<ulong, string>();
            public readonly HashSet<ulong> LikelyOwners = new HashSet<ulong>();
        }

        private const string Dv8dAscii = @"
DDDDDDDD      VV        VV     88888888      DDDDDDDD
DDDDDDDDD     VV        VV    8888888888     DDDDDDDDD
DD     DDD    VV        VV    88      88     DD     DDD
DD      DD    VV        VV    88      88     DD      DD
DD      DD     VV      VV      88888888      DD      DD
DD      DD     VV      VV     8888888888     DD      DD
DD      DD      VV    VV      88      88     DD      DD
DD     DDD       VV  VV       88      88     DD     DDD
DDDDDDDDD        VVVV         8888888888     DDDDDDDDD
DDDDDDDD          VV           88888888      DDDDDDDD
";

        #endregion
    }
}
