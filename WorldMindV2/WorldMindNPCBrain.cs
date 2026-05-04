using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindNPCBrain", "Devi8d0ne", "2.5.0")]
    [Description("Deviated Playgrounds NPC personality brain with owner-safe config loading, location-aware WorldMind reactions, biome reads, monuments, DiscordMind routing, and fallback lines.")]
    public class WorldMindNPCBrain : RustPlugin
    {
        private const string MadeWithLoveTag = "Made with love by Deviated Systems";
        private const string PermissionUse = "worldmindnpcbrain.use";

        private PluginConfig _config;
        private StoredData _data;

        private readonly Dictionary<ulong, double> _playerApproachCooldowns = new Dictionary<ulong, double>();
        private readonly Dictionary<uint, double> _npcCombatCooldowns = new Dictionary<uint, double>();
        private readonly Dictionary<string, double> _eventCooldowns = new Dictionary<string, double>();
        private readonly System.Random _random = new System.Random();

        #region Oxide Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermissionUse, this);
            LoadDataFile();
        }

        private void OnServerInitialized()
        {
            if (_config == null) LoadConfig();
            if (!_config.General.Enabled) return;

            if (_config.General.PrintDV8DAsciiOnLoad)
                PrintDv8dAscii();

            timer.Every(Mathf.Max(1f, _config.Cooldowns.ApproachScanIntervalSeconds), ScanNpcApproaches);

            DebugLog("WorldMindNPCBrain initialized. " + MadeWithLoveTag);
        }

        private void Unload()
        {
            SaveDataFile();
        }

        #endregion

        #region Hooks

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (!_config.General.Enabled || !_config.NPCReactionSettings.ReactWhenNPCIsAttacked) return;

            var npc = entity as BasePlayer;
            if (!IsAllowedNpc(npc)) return;

            var attacker = info?.InitiatorPlayer;
            if (attacker == null || attacker.IsNpc || attacker.userID == 0) return;

            if (!PassNpcCombatCooldown(npc, "attacked")) return;

            var facts = BuildFacts("attacked", npc, attacker, info);
            ReactToNpcEvent("Attacked", npc, attacker, facts, attacker);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (!_config.General.Enabled) return;

            var npc = entity as BasePlayer;
            if (!IsAllowedNpc(npc)) return;

            var attacker = info?.InitiatorPlayer;
            if (_config.NPCReactionSettings.ReactWhenNPCDies)
            {
                BasePlayer recipient = attacker != null && !attacker.IsNpc ? attacker : FindNearestHuman(npc.transform.position, 40f);
                if (recipient != null)
                {
                    var facts = BuildFacts("died", npc, recipient, info);
                    ReactToNpcEvent("Death", npc, recipient, facts, recipient);
                }
            }
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (!_config.General.Enabled || !_config.NPCReactionSettings.ReactWhenNPCKillsPlayer) return;
            if (player == null || player.IsNpc || player.userID == 0) return;

            var npc = info?.InitiatorPlayer;
            if (!IsAllowedNpc(npc)) return;

            if (!PassNpcCombatCooldown(npc, "killed_player")) return;

            var facts = BuildFacts("killed_player", npc, player, info);
            ReactToNpcEvent("KillPlayer", npc, player, facts, player);
        }

        #endregion

        #region Approach Scan

        private void ScanNpcApproaches()
        {
            if (!_config.General.Enabled || !_config.NPCReactionSettings.ReactWhenPlayerApproachesNPC) return;
            if (BasePlayer.activePlayerList == null || BasePlayer.activePlayerList.Count == 0) return;

            var players = BasePlayer.activePlayerList.Where(p => p != null && p.IsConnected && !p.IsNpc && p.userID != 0).ToList();
            if (players.Count == 0) return;

            foreach (var npc in BasePlayer.activePlayerList)
            {
                if (!IsAllowedNpc(npc)) continue;

                foreach (var player in players)
                {
                    if (!CanReceiveLine(player)) continue;
                    if (Vector3.Distance(player.transform.position, npc.transform.position) > _config.NPCReactionSettings.ApproachRadiusMeters) continue;
                    if (!PassPlayerApproachCooldown(player)) continue;
                    if (_random.NextDouble() > Mathf.Clamp01(_config.NPCReactionSettings.ApproachLineChance01)) continue;

                    var facts = BuildFacts("approach", npc, player, null);
                    ReactToNpcEvent("Approach", npc, player, facts, player);
                    break;
                }
            }
        }

        #endregion

        #region Reaction Engine

        private void ReactToNpcEvent(string eventKey, BasePlayer npc, BasePlayer targetPlayer, Dictionary<string, object> facts, BasePlayer preferredRecipient)
        {
            if (npc == null) return;

            var profile = ResolveNpcProfile(npc);
            var prompt = BuildPrompt(eventKey, npc, targetPlayer, profile, facts);
            var line = RequestWorldMindLine(prompt, facts);

            if (!IsUsableLine(line))
            {
                line = PickFallbackLine(eventKey, profile);
            }

            line = SanitizeLine(line, _config.NPCReactionSettings.MaxLineLength);
            if (!IsUsableLine(line)) return;

            var speakerName = !string.IsNullOrWhiteSpace(profile.SpeakerName) ? profile.SpeakerName : _config.NPCReactionSettings.DefaultSpeakerName;
            SendNpcLine(npc.transform.position, preferredRecipient, speakerName, line);
            RecordNpcEvent(eventKey, npc, targetPlayer, line, facts);
            SendDiscordNpcEvent(eventKey, npc, targetPlayer, speakerName, line, facts);
        }

        private string RequestWorldMindLine(string prompt, Dictionary<string, object> facts)
        {
            if (!_config.WorldMindV2HookSettings.UseWorldMindV2Hook) return null;

            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["source"] = Name,
                    ["kind"] = "npc_reaction",
                    ["prompt"] = prompt,
                    ["facts"] = facts,
                    ["maxLength"] = _config.NPCReactionSettings.MaxLineLength,
                    ["tone"] = _config.NPCReactionSettings.Tone,
                    ["allowProfanity"] = _config.NPCReactionSettings.AllowProfanity
                };

                foreach (var hookName in _config.WorldMindV2HookSettings.RequestHookNames)
                {
                    if (string.IsNullOrWhiteSpace(hookName)) continue;

                    var result = Interface.CallHook(hookName, prompt, payload);
                    var text = ExtractTextFromHookResult(result);
                    if (IsUsableLine(text)) return text;

                    result = Interface.CallHook(hookName, payload);
                    text = ExtractTextFromHookResult(result);
                    if (IsUsableLine(text)) return text;
                }
            }
            catch (Exception ex)
            {
                DebugLog("WorldMind request failed: " + ex.Message);
            }

            return null;
        }

        private string ExtractTextFromHookResult(object result)
        {
            if (result == null) return null;

            var text = result as string;
            if (!string.IsNullOrWhiteSpace(text)) return text;

            var dict = result as Dictionary<string, object>;
            if (dict != null)
            {
                foreach (var key in new[] { "line", "text", "message", "content", "response" })
                {
                    if (dict.ContainsKey(key) && dict[key] != null)
                        return dict[key].ToString();
                }
            }

            return result.ToString();
        }

        private string BuildPrompt(string eventKey, BasePlayer npc, BasePlayer targetPlayer, NpcProfile profile, Dictionary<string, object> facts)
        {
            var template = GetPromptTemplate(eventKey);
            var npcName = SafeName(npc);
            var playerName = targetPlayer != null ? SafeName(targetPlayer) : "unknown survivor";
            var profileVoice = profile != null ? profile.Voice : "hostile, Rust-aware, short";
            var serverIdentity = _config.NPCReactionSettings.ServerIdentity;

            return template
                .Replace("{server}", serverIdentity)
                .Replace("{speaker}", profile.SpeakerName ?? _config.NPCReactionSettings.DefaultSpeakerName)
                .Replace("{npc}", npcName)
                .Replace("{player}", playerName)
                .Replace("{tone}", _config.NPCReactionSettings.Tone)
                .Replace("{profileVoice}", profileVoice)
                .Replace("{maxLength}", _config.NPCReactionSettings.MaxLineLength.ToString())
                + "\nFacts: " + JsonConvert.SerializeObject(facts) +
                  "\nRules: One sentence only. Rust chat length. Player-facing. No admin voice. No tutorial tone. No config, AI, model, prompt, API, backend, plugin, or hidden-system talk. Do not invent commands, rewards, events, lore, or server mechanics. No slurs or real-world hate.";
        }

        private string GetPromptTemplate(string eventKey)
        {
            if (_config.PromptTemplates == null) return DefaultPrompt(eventKey);

            string value;
            if (_config.PromptTemplates.TryGetValue(eventKey, out value) && !string.IsNullOrWhiteSpace(value)) return value;

            return DefaultPrompt(eventKey);
        }

        private string DefaultPrompt(string eventKey)
        {
            switch (eventKey)
            {
                case "Approach":
                    return "Write ONE short {server} NPC line when {player} gets close to {speaker}. Tone: {profileVoice}. Make the player feel watched, heard, and slightly stupid for getting close. Max {maxLength} chars.";
                case "Attacked":
                    return "Write ONE short {server} NPC line after {player} attacks {speaker}. Tone: {profileVoice}. Mock the bad choice, sloppy aim, greed, or confidence. Max {maxLength} chars.";
                case "Death":
                    return "Write ONE short final {server} NPC line when {speaker} dies. Tone: bitter, hostile, atmospheric, Rust-aware. Make the player feel they won loot, not safety. Max {maxLength} chars.";
                case "KillPlayer":
                    return "Write ONE short {server} NPC line after {speaker} kills {player}. Tone: cold, sarcastic, tactical. Make the death feel like punishment for bad positioning, greed, noise, or dumb timing. Max {maxLength} chars.";
                default:
                    return "Write ONE short {server} NPC reaction line. Tone: {profileVoice}. Max {maxLength} chars.";
            }
        }

        #endregion

        #region Chat + Safety

        private void SendNpcLine(Vector3 origin, BasePlayer preferredRecipient, string speakerName, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            var speaker = SanitizeLine(speakerName, 40);
            var message = $"<color={_config.NPCReactionSettings.SpeakerColor}>{speaker}</color>: <color={_config.NPCReactionSettings.LineColor}>{line}</color>";

            if (_config.Delivery.SendToNearbyPlayers)
            {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    if (player == null || !player.IsConnected || player.IsNpc) continue;
                    if (!CanReceiveLine(player)) continue;
                    if (Vector3.Distance(player.transform.position, origin) > _config.Delivery.NearbyChatRadiusMeters) continue;
                    player.ChatMessage(message);
                }
                return;
            }

            if (preferredRecipient != null && preferredRecipient.IsConnected && CanReceiveLine(preferredRecipient))
                preferredRecipient.ChatMessage(message);
        }

        private bool IsUsableLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;

            var t = line.Trim();
            if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) return false;
            if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            if (t.Equals("null", StringComparison.OrdinalIgnoreCase)) return false;
            if (t.StartsWith("{") || t.StartsWith("[")) return false;
            if (t.IndexOf("api", StringComparison.OrdinalIgnoreCase) >= 0 && t.IndexOf("key", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (t.IndexOf("prompt", StringComparison.OrdinalIgnoreCase) >= 0 && t.IndexOf("config", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        private string SanitizeLine(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var clean = value.Replace("\n", " ").Replace("\r", " ").Trim();
            clean = StripOuterQuotes(clean);

            if (!_config.NPCReactionSettings.AllowProfanity)
                clean = SoftProfanityFilter(clean);

            if (clean.Length > maxLength)
                clean = clean.Substring(0, Mathf.Max(0, maxLength - 1)).TrimEnd() + "…";

            return clean;
        }

        private string StripOuterQuotes(string value)
        {
            if (value.Length >= 2 && ((value[0] == '"' && value[value.Length - 1] == '"') || (value[0] == '\'' && value[value.Length - 1] == '\'')))
                return value.Substring(1, value.Length - 2).Trim();
            return value;
        }

        private string SoftProfanityFilter(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            var blocked = new[] { "fuck", "shit", "bitch", "asshole", "dick" };
            foreach (var word in blocked)
            {
                text = System.Text.RegularExpressions.Regex.Replace(text, word, "****", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            return text;
        }

        #endregion

        #region NPC Profiles + Fallbacks

        private NpcProfile ResolveNpcProfile(BasePlayer npc)
        {
            var haystack = ((npc?.displayName ?? "") + " " + (npc?.ShortPrefabName ?? "") + " " + (npc?.PrefabName ?? "")).ToLowerInvariant();

            foreach (var pair in _config.NPCPersonalityProfiles)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                if (haystack.Contains(pair.Key.ToLowerInvariant())) return pair.Value;
            }

            return new NpcProfile
            {
                SpeakerName = _config.NPCReactionSettings.DefaultSpeakerName,
                Voice = _config.NPCReactionSettings.Tone
            };
        }

        private string PickFallbackLine(string eventKey, NpcProfile profile)
        {
            List<string> lines;
            if (profile != null && profile.FallbackLines != null && profile.FallbackLines.TryGetValue(eventKey, out lines) && lines != null && lines.Count > 0)
                return lines[_random.Next(lines.Count)];

            if (_config.FallbackLines != null && _config.FallbackLines.TryGetValue(eventKey, out lines) && lines != null && lines.Count > 0)
                return lines[_random.Next(lines.Count)];

            return "DV8D Control saw that mistake before you did.";
        }

        #endregion

        #region Facts + Memory

        private Dictionary<string, object> BuildFacts(string eventType, BasePlayer npc, BasePlayer player, HitInfo info)
        {
            var facts = new Dictionary<string, object>
            {
                ["eventType"] = eventType,
                ["serverIdentity"] = _config.NPCReactionSettings.ServerIdentity,
                ["npcName"] = SafeName(npc),
                ["npcPrefab"] = npc?.ShortPrefabName ?? "unknown",
                ["playerName"] = player != null ? SafeName(player) : "unknown",
                ["distanceMeters"] = player != null && npc != null ? Math.Round(Vector3.Distance(player.transform.position, npc.transform.position), 1) : 0,
                ["npcHealth"] = npc != null ? Math.Round(npc.health, 1) : 0,
                ["timeOfDay"] = TOD_Sky.Instance != null ? Math.Round(TOD_Sky.Instance.Cycle.Hour, 1).ToString() : "unknown"
            };

            if (_config.LocationAwareness.Enabled)
            {
                facts["location"] = BuildLocationContext(npc != null ? npc.transform.position : (player != null ? player.transform.position : Vector3.zero), npc, player);
            }

            if (info != null)
            {
                facts["damageType"] = info.damageTypes?.GetMajorityDamageType().ToString() ?? "unknown";
                facts["weapon"] = info.WeaponPrefab != null ? info.WeaponPrefab.ShortPrefabName : "unknown";
                facts["isHeadshot"] = info.isHeadshot;
            }

            return facts;
        }


        private Dictionary<string, object> BuildLocationContext(Vector3 position, BasePlayer npc, BasePlayer player)
        {
            var ctx = new Dictionary<string, object>
            {
                ["grid"] = _config.LocationAwareness.IncludeGrid ? GetGrid(position) : "disabled",
                ["biome"] = _config.LocationAwareness.IncludeBiome ? GuessBiome(position) : "disabled",
                ["height"] = Math.Round(position.y, 1),
                ["position"] = new Dictionary<string, object>
                {
                    ["x"] = Math.Round(position.x, 1),
                    ["y"] = Math.Round(position.y, 1),
                    ["z"] = Math.Round(position.z, 1)
                }
            };

            if (_config.LocationAwareness.IncludeNearestMonument)
            {
                var monument = FindNearestMonument(position, _config.LocationAwareness.MaxMonumentSearchMeters);
                ctx["nearestMonument"] = monument.Name;
                ctx["distanceToMonumentMeters"] = monument.Distance >= 0 ? Math.Round(monument.Distance, 1) : -1;
                ctx["areaType"] = DetermineAreaType(position, monument);
            }
            else
            {
                ctx["nearestMonument"] = "disabled";
                ctx["distanceToMonumentMeters"] = -1;
                ctx["areaType"] = "unknown";
            }

            if (_config.LocationAwareness.IncludeWorldReads)
            {
                ctx["safezone"] = IsPlayerInSafeZone(npc) || IsPlayerInSafeZone(player);
                ctx["terrain"] = GuessTerrainContext(position);
                ctx["threatFlavor"] = BuildThreatFlavor(ctx);
            }

            return ctx;
        }

        private string GetGrid(Vector3 position)
        {
            try
            {
                var size = GetWorldSize();
                if (size <= 0) return "unknown";

                var gridSize = Mathf.Max(1, _config.LocationAwareness.GridCellSizeMeters);
                var half = size / 2f;
                var x = Mathf.Clamp(Mathf.FloorToInt((position.x + half) / gridSize), 0, 999);
                var z = Mathf.Clamp(Mathf.FloorToInt((half - position.z) / gridSize), 0, 999);

                return NumberToGridLetters(x) + (z + 1);
            }
            catch
            {
                return "unknown";
            }
        }

        private int GetWorldSize()
        {
            try
            {
                if (ConVar.Server.worldsize > 0) return ConVar.Server.worldsize;
            }
            catch { }

            try
            {
                if (TerrainMeta.Size.x > 0) return Mathf.RoundToInt(TerrainMeta.Size.x);
            }
            catch { }

            return 4500;
        }

        private string NumberToGridLetters(int number)
        {
            number = Mathf.Max(0, number);
            var letters = string.Empty;
            do
            {
                letters = (char)('A' + (number % 26)) + letters;
                number = number / 26 - 1;
            } while (number >= 0);
            return letters;
        }

        private string GuessBiome(Vector3 position)
        {
            var reflected = TryReadBiomeByReflection(position);
            if (!string.IsNullOrWhiteSpace(reflected) && reflected != "unknown") return reflected;

            // Safe fallback. Rust biome APIs have changed over time, so this keeps the plugin compiling.
            // It is not perfect, but it gives the model useful island flavor instead of nothing.
            if (position.y < -2f) return "Ocean/shoreline";
            if (position.z > GetWorldSize() * 0.22f) return "Snow/Arctic side";
            if (position.x > GetWorldSize() * 0.22f) return "Desert/Arid side";
            return "Forest/Temperate side";
        }

        private string TryReadBiomeByReflection(Vector3 position)
        {
            try
            {
                var terrainMeta = typeof(TerrainMeta);
                object biomeMap = null;

                var field = terrainMeta.GetField("BiomeMap", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) biomeMap = field.GetValue(null);

                if (biomeMap == null)
                {
                    var prop = terrainMeta.GetProperty("BiomeMap", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (prop != null) biomeMap = prop.GetValue(null, null);
                }

                if (biomeMap == null) return "unknown";

                var methods = biomeMap.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name == "GetBiome")
                    .ToList();

                foreach (var method in methods)
                {
                    var parameters = method.GetParameters();
                    object value = null;

                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Vector3))
                        value = method.Invoke(biomeMap, new object[] { position });
                    else if (parameters.Length == 2 && parameters[0].ParameterType == typeof(Vector3))
                        value = method.Invoke(biomeMap, new object[] { position, 0 });
                    else
                        continue;

                    var parsed = ParseBiomeValue(value);
                    if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
                }
            }
            catch (Exception ex)
            {
                DebugLog("Biome reflection failed: " + ex.Message);
            }

            return "unknown";
        }

        private string ParseBiomeValue(object value)
        {
            if (value == null) return null;

            var text = value.ToString();
            if (string.IsNullOrWhiteSpace(text)) return null;

            var lower = text.ToLowerInvariant();
            if (lower.Contains("arid") || lower.Contains("desert")) return "Desert/Arid";
            if (lower.Contains("arctic") || lower.Contains("snow")) return "Snow/Arctic";
            if (lower.Contains("tundra")) return "Tundra";
            if (lower.Contains("temperate") || lower.Contains("forest")) return "Forest/Temperate";

            float numeric;
            if (float.TryParse(text, out numeric))
            {
                var i = Mathf.RoundToInt(numeric);
                if ((i & 8) == 8) return "Snow/Arctic";
                if ((i & 4) == 4) return "Tundra";
                if ((i & 2) == 2) return "Forest/Temperate";
                if ((i & 1) == 1) return "Desert/Arid";
            }

            return text;
        }

        private MonumentRead FindNearestMonument(Vector3 position, float maxDistance)
        {
            var best = new MonumentRead { Name = "Wilderness", Distance = -1f };

            try
            {
                var monuments = UnityEngine.Object.FindObjectsOfType<MonumentInfo>();
                if (monuments == null || monuments.Length == 0) return best;

                var bestDistance = maxDistance;
                foreach (var monument in monuments)
                {
                    if (monument == null) continue;
                    var distance = Vector3.Distance(position, monument.transform.position);
                    if (distance > bestDistance) continue;

                    bestDistance = distance;
                    best.Name = CleanMonumentName(monument.name);
                    best.Distance = distance;
                    best.Position = monument.transform.position;
                }
            }
            catch (Exception ex)
            {
                DebugLog("Nearest monument read failed: " + ex.Message);
            }

            return best;
        }

        private string CleanMonumentName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Unknown monument";

            var text = raw.Replace("(Clone)", string.Empty)
                .Replace("_", " ")
                .Replace("/", " ")
                .Trim();

            var lower = text.ToLowerInvariant();
            foreach (var pair in _config.LocationAwareness.MonumentNameOverrides)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && lower.Contains(pair.Key.ToLowerInvariant()))
                    return pair.Value;
            }

            return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
        }

        private string DetermineAreaType(Vector3 position, MonumentRead monument)
        {
            if (monument.Distance >= 0 && monument.Distance <= _config.LocationAwareness.InsideMonumentMeters) return "inside/at monument";
            if (monument.Distance >= 0 && monument.Distance <= _config.LocationAwareness.NearMonumentMeters) return "near monument";
            if (position.y < -2f) return "shoreline/ocean edge";
            if (position.y < 5f) return "low ground";
            if (position.y > 120f) return "high ground/ridge";
            return "open island";
        }

        private string GuessTerrainContext(Vector3 position)
        {
            if (position.y < -5f) return "waterline/ocean danger";
            if (position.y < 4f) return "low ground with bad sightlines";
            if (position.y > 135f) return "high ground with long sightlines";
            if (position.y > 80f) return "raised terrain/ridge pressure";
            return "open ground";
        }

        private bool IsPlayerInSafeZone(BasePlayer player)
        {
            if (player == null) return false;
            try
            {
                var method = player.GetType().GetMethod("InSafeZone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method != null)
                {
                    var result = method.Invoke(player, null);
                    if (result is bool) return (bool)result;
                }
            }
            catch { }
            return false;
        }

        private string BuildThreatFlavor(Dictionary<string, object> ctx)
        {
            var biome = ctx.ContainsKey("biome") ? (ctx["biome"] ?? "unknown").ToString() : "unknown";
            var area = ctx.ContainsKey("areaType") ? (ctx["areaType"] ?? "unknown").ToString() : "unknown";
            var monument = ctx.ContainsKey("nearestMonument") ? (ctx["nearestMonument"] ?? "Wilderness").ToString() : "Wilderness";

            if (area.Contains("monument")) return monument + " pressure: loot, angles, third parties, and bad exits.";
            if (biome.IndexOf("Snow", StringComparison.OrdinalIgnoreCase) >= 0) return "Snow pressure: white sightlines, cold mistakes, long angles.";
            if (biome.IndexOf("Desert", StringComparison.OrdinalIgnoreCase) >= 0 || biome.IndexOf("Arid", StringComparison.OrdinalIgnoreCase) >= 0) return "Desert pressure: open ground, loud movement, nowhere to hide.";
            if (biome.IndexOf("Forest", StringComparison.OrdinalIgnoreCase) >= 0 || biome.IndexOf("Temperate", StringComparison.OrdinalIgnoreCase) >= 0) return "Forest pressure: trees hide movement until it is too close.";
            if (area.Contains("shoreline") || biome.IndexOf("Ocean", StringComparison.OrdinalIgnoreCase) >= 0) return "Shore pressure: water behind you, rifles in front of you.";
            return "Island pressure: movement, greed, and timing decide who gets looted.";
        }

        private class MonumentRead
        {
            public string Name;
            public float Distance;
            public Vector3 Position;
        }

        private void RecordNpcEvent(string eventKey, BasePlayer npc, BasePlayer player, string line, Dictionary<string, object> facts)
        {
            if (_data == null) return;

            var entry = new NpcMemoryEvent
            {
                TimeUtc = DateTime.UtcNow.ToString("o"),
                Event = eventKey,
                Npc = SafeName(npc),
                Player = player != null ? SafeName(player) : "unknown",
                Line = line,
                Facts = facts
            };

            _data.Events.Add(entry);
            while (_data.Events.Count > _config.Memory.MaxStoredEvents)
                _data.Events.RemoveAt(0);

            if (_config.WorldMindV2HookSettings.RecordNPCEventsToWorldMind)
            {
                try
                {
                    var payload = new Dictionary<string, object>
                    {
                        ["source"] = Name,
                        ["kind"] = "npc_event",
                        ["event"] = entry.Event,
                        ["npc"] = entry.Npc,
                        ["player"] = entry.Player,
                        ["line"] = entry.Line,
                        ["facts"] = entry.Facts,
                        ["timeUtc"] = entry.TimeUtc
                    };

                    foreach (var hookName in _config.WorldMindV2HookSettings.RecordHookNames)
                    {
                        if (!string.IsNullOrWhiteSpace(hookName))
                            Interface.CallHook(hookName, payload);
                    }
                }
                catch (Exception ex)
                {
                    DebugLog("WorldMind record hook failed: " + ex.Message);
                }
            }
        }

        private void SendDiscordNpcEvent(string eventKey, BasePlayer npc, BasePlayer player, string speakerName, string line, Dictionary<string, object> facts)
        {
            if (_config == null || _config.DiscordMindIntegration == null || !_config.DiscordMindIntegration.Enabled) return;
            if (string.IsNullOrWhiteSpace(line)) return;
            if (!ShouldSendDiscordEvent(eventKey)) return;

            try
            {
                var category = string.IsNullOrWhiteSpace(_config.DiscordMindIntegration.Category) ? "npc" : _config.DiscordMindIntegration.Category;
                var channelKey = string.IsNullOrWhiteSpace(_config.DiscordMindIntegration.ChannelKey) ? category : _config.DiscordMindIntegration.ChannelKey;
                var title = BuildDiscordTitle(eventKey, speakerName);
                var message = BuildDiscordMessage(eventKey, npc, player, speakerName, line, facts);

                if (!IsUsableLine(message)) return;

                var packet = new Dictionary<string, object>
                {
                    ["source"] = Name,
                    ["kind"] = "npc_event",
                    ["category"] = category,
                    ["channelKey"] = channelKey,
                    ["title"] = title,
                    ["message"] = message,
                    ["event"] = eventKey,
                    ["speaker"] = speakerName,
                    ["line"] = line,
                    ["npc"] = SafeName(npc),
                    ["player"] = player != null ? SafeName(player) : "unknown",
                    ["facts"] = facts,
                    ["timeUtc"] = DateTime.UtcNow.ToString("o")
                };

                object result = Interface.CallHook("WorldMindDiscordMind_SendNpcEvent", packet);
                if (IsTruthyHookResult(result))
                {
                    DebugLog("DiscordMind NPC event queued through WorldMindDiscordMind_SendNpcEvent.");
                    return;
                }

                result = Interface.CallHook("WorldMindDiscordMind_SendEvent", packet);
                if (IsTruthyHookResult(result))
                {
                    DebugLog("DiscordMind NPC event queued through WorldMindDiscordMind_SendEvent.");
                    return;
                }

                result = Interface.CallHook("WorldMindDiscordMind_SendMessageToChannel", channelKey, title, message, category);
                if (IsTruthyHookResult(result))
                {
                    DebugLog("DiscordMind NPC event queued through WorldMindDiscordMind_SendMessageToChannel.");
                    return;
                }

                if (_config.DiscordMindIntegration.DebugDiscordRouting)
                    Puts("DiscordMind NPC event was not accepted by any hook. Is WorldMindDiscordMind loaded and updated?");
            }
            catch (Exception ex)
            {
                DebugLog("DiscordMind NPC event failed: " + ex.Message);
            }
        }

        private bool ShouldSendDiscordEvent(string eventKey)
        {
            if (_config == null || _config.DiscordMindIntegration == null) return false;
            if (string.IsNullOrWhiteSpace(eventKey)) return false;

            switch (eventKey)
            {
                case "Approach": return _config.DiscordMindIntegration.SendApproachEvents;
                case "Attacked": return _config.DiscordMindIntegration.SendAttackedEvents;
                case "Death": return _config.DiscordMindIntegration.SendNpcDeathEvents;
                case "KillPlayer": return _config.DiscordMindIntegration.SendNpcKilledPlayerEvents;
                default: return _config.DiscordMindIntegration.SendOtherNpcEvents;
            }
        }

        private string BuildDiscordTitle(string eventKey, string speakerName)
        {
            var server = string.IsNullOrWhiteSpace(_config.NPCReactionSettings.ServerIdentity) ? "WorldMind" : _config.NPCReactionSettings.ServerIdentity;
            var speaker = string.IsNullOrWhiteSpace(speakerName) ? _config.NPCReactionSettings.DefaultSpeakerName : speakerName;

            switch (eventKey)
            {
                case "Approach": return server + " NPC Approach";
                case "Attacked": return speaker + " Attacked";
                case "Death": return speaker + " Down";
                case "KillPlayer": return speaker + " Killed Player";
                default: return server + " NPC Event";
            }
        }

        private string BuildDiscordMessage(string eventKey, BasePlayer npc, BasePlayer player, string speakerName, string line, Dictionary<string, object> facts)
        {
            var parts = new List<string>();
            var speaker = string.IsNullOrWhiteSpace(speakerName) ? _config.NPCReactionSettings.DefaultSpeakerName : speakerName;
            var playerName = player != null ? SafeName(player) : "unknown survivor";
            var npcName = npc != null ? SafeName(npc) : speaker;

            parts.Add("**" + CleanDiscordText(speaker, 60) + "**: " + CleanDiscordText(line, _config.DiscordMindIntegration.MaxDiscordLineLength));

            if (_config.DiscordMindIntegration.IncludeEventFacts)
            {
                parts.Add("Event: `" + CleanDiscordText(eventKey, 32) + "` | NPC: `" + CleanDiscordText(npcName, 48) + "` | Player: `" + CleanDiscordText(playerName, 48) + "`");

                var locationText = ExtractDiscordLocation(facts);
                if (!string.IsNullOrWhiteSpace(locationText))
                    parts.Add(locationText);

                var weaponText = ExtractDiscordWeaponFacts(facts);
                if (!string.IsNullOrWhiteSpace(weaponText))
                    parts.Add(weaponText);
            }

            return CleanDiscordText(string.Join("\n", parts.ToArray()), _config.DiscordMindIntegration.MaxDiscordMessageLength);
        }

        private string ExtractDiscordLocation(Dictionary<string, object> facts)
        {
            if (facts == null || !facts.ContainsKey("location") || facts["location"] == null) return "";

            var dict = facts["location"] as Dictionary<string, object>;
            if (dict == null)
            {
                try
                {
                    dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(facts["location"]));
                }
                catch { }
            }

            if (dict == null) return "";

            var grid = GetDictString(dict, "grid", "unknown");
            var biome = GetDictString(dict, "biome", "unknown");
            var monument = GetDictString(dict, "nearestMonument", "Wilderness");
            var areaType = GetDictString(dict, "areaType", "unknown");
            var threat = GetDictString(dict, "threatFlavor", "");

            var line = "Location: `" + CleanDiscordText(grid, 16) + "` | `" + CleanDiscordText(biome, 32) + "` | `" + CleanDiscordText(monument, 48) + "` | `" + CleanDiscordText(areaType, 32) + "`";
            if (!string.IsNullOrWhiteSpace(threat))
                line += "\nRead: " + CleanDiscordText(threat, 180);
            return line;
        }

        private string ExtractDiscordWeaponFacts(Dictionary<string, object> facts)
        {
            if (facts == null) return "";

            var bits = new List<string>();
            var weapon = GetDictString(facts, "weapon", "");
            var damageType = GetDictString(facts, "damageType", "");
            var distance = GetDictString(facts, "distanceMeters", "");
            var headshot = GetDictString(facts, "isHeadshot", "");

            if (!string.IsNullOrWhiteSpace(weapon) && weapon != "unknown") bits.Add("Weapon: `" + CleanDiscordText(weapon, 48) + "`");
            if (!string.IsNullOrWhiteSpace(damageType) && damageType != "unknown") bits.Add("Damage: `" + CleanDiscordText(damageType, 32) + "`");
            if (!string.IsNullOrWhiteSpace(distance) && distance != "0") bits.Add("Distance: `" + CleanDiscordText(distance, 16) + "m`");
            if (headshot.Equals("True", StringComparison.OrdinalIgnoreCase) || headshot.Equals("true", StringComparison.OrdinalIgnoreCase)) bits.Add("Headshot: `yes`");

            return bits.Count == 0 ? "" : string.Join(" | ", bits.ToArray());
        }

        private string GetDictString(Dictionary<string, object> dict, string key, string fallback)
        {
            if (dict == null || string.IsNullOrWhiteSpace(key)) return fallback;
            object value;
            if (dict.TryGetValue(key, out value) && value != null)
                return value.ToString();
            return fallback;
        }

        private bool IsTruthyHookResult(object result)
        {
            if (result == null) return false;
            if (result is bool) return (bool)result;
            var text = result.ToString();
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Equals("queued", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Equals("sent", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private string CleanDiscordText(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var clean = value
                .Replace("@everyone", "@\u200beveryone")
                .Replace("@here", "@\u200bhere")
                .Replace("\r", " ")
                .Trim();

            if (maxLength > 0 && clean.Length > maxLength)
                clean = clean.Substring(0, Math.Max(0, maxLength - 3)).TrimEnd() + "...";

            return clean;
        }

        #endregion

        #region Helpers

        private bool IsAllowedNpc(BasePlayer npc)
        {
            if (npc == null || !npc.IsNpc) return false;
            if (_config.NPCFilters.IncludeAllNPCPlayers) return true;

            var haystack = ((npc.displayName ?? "") + " " + (npc.ShortPrefabName ?? "") + " " + (npc.PrefabName ?? "")).ToLowerInvariant();
            foreach (var token in _config.NPCFilters.AllowedNPCNameOrPrefabContains)
            {
                if (!string.IsNullOrWhiteSpace(token) && haystack.Contains(token.ToLowerInvariant())) return true;
            }

            return false;
        }

        private bool CanReceiveLine(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return false;
            if (!_config.Permissions.RequireUsePermissionToReceiveNPCLines) return true;
            return permission.UserHasPermission(player.UserIDString, PermissionUse);
        }

        private bool PassPlayerApproachCooldown(BasePlayer player)
        {
            if (player == null) return false;

            var now = Now();
            double until;
            if (_playerApproachCooldowns.TryGetValue(player.userID, out until) && until > now) return false;

            _playerApproachCooldowns[player.userID] = now + _config.Cooldowns.PlayerApproachLineCooldownSeconds;
            return true;
        }

        private bool PassNpcCombatCooldown(BasePlayer npc, string bucket)
        {
            if (npc == null) return false;

            var id = npc.net != null ? npc.net.ID.Value : 0;
            var now = Now();
            var key = id + ":" + bucket;

            double until;
            if (_eventCooldowns.TryGetValue(key, out until) && until > now) return false;

            _eventCooldowns[key] = now + _config.Cooldowns.NPCCombatLineCooldownSeconds;
            return true;
        }

        private BasePlayer FindNearestHuman(Vector3 origin, float radius)
        {
            BasePlayer best = null;
            var bestDist = radius;

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsNpc || !player.IsConnected) continue;
                if (!CanReceiveLine(player)) continue;

                var dist = Vector3.Distance(origin, player.transform.position);
                if (dist < bestDist)
                {
                    best = player;
                    bestDist = dist;
                }
            }

            return best;
        }

        private string SafeName(BasePlayer player)
        {
            if (player == null) return "unknown";
            return string.IsNullOrWhiteSpace(player.displayName) ? (player.ShortPrefabName ?? "unknown") : player.displayName;
        }

        private double Now()
        {
            return Interface.Oxide.Now;
        }

        private void DebugLog(string message)
        {
            if (_config != null && _config.General.Debug)
                Puts(message);
        }

        private void PrintDv8dAscii()
        {
            Puts(@"
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
" + MadeWithLoveTag);
        }

        #endregion

        #region Config + Data

        protected override void LoadDefaultConfig()
        {
            _config = PluginConfig.DefaultConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("Config read returned null");

                // Owner-safe rule:
                // Load what the owner already edited, fill runtime nulls only, and DO NOT
                // write the file back on every reload. This prevents plugin updates from
                // reverting owner-edited values, reordered lists, cleared lists, or custom text.
                _config.MergeMissingDefaults();
            }
            catch (Exception ex)
            {
                PrintWarning("Config error, creating a safe default config because the current config could not be read: " + ex.Message);
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void LoadDataFile()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
                if (_data == null) _data = new StoredData();
            }
            catch
            {
                _data = new StoredData();
            }
        }

        private void SaveDataFile()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, _data ?? new StoredData());
        }

        private class PluginConfig
        {
            [JsonProperty("General")]
            public GeneralSettings General = new GeneralSettings();

            [JsonProperty("WorldMind V2 Hook Settings")]
            public WorldMindHookSettings WorldMindV2HookSettings = new WorldMindHookSettings();

            [JsonProperty("Permissions")]
            public PermissionSettings Permissions = new PermissionSettings();

            [JsonProperty("NPC Filters")]
            public NpcFilterSettings NPCFilters = new NpcFilterSettings();

            [JsonProperty("NPC Reaction Settings")]
            public NpcReactionSettings NPCReactionSettings = new NpcReactionSettings();

            [JsonProperty("NPC Personality Profiles")]
            public Dictionary<string, NpcProfile> NPCPersonalityProfiles = DefaultProfiles();

            [JsonProperty("Prompt Templates")]
            public Dictionary<string, string> PromptTemplates = DefaultPromptTemplates();

            [JsonProperty("Fallback Lines")]
            public Dictionary<string, List<string>> FallbackLines = DefaultFallbackLines();

            [JsonProperty("Delivery")]
            public DeliverySettings Delivery = new DeliverySettings();

            [JsonProperty("DiscordMind Integration")]
            public DiscordMindSettings DiscordMindIntegration = new DiscordMindSettings();

            [JsonProperty("Location Awareness")]
            public LocationAwarenessSettings LocationAwareness = new LocationAwarenessSettings();

            [JsonProperty("Cooldowns")]
            public CooldownSettings Cooldowns = new CooldownSettings();

            [JsonProperty("Memory")]
            public MemorySettings Memory = new MemorySettings();

            public static PluginConfig DefaultConfig()
            {
                return new PluginConfig();
            }

            public void MergeMissingDefaults()
            {
                if (General == null) General = new GeneralSettings();
                if (WorldMindV2HookSettings == null) WorldMindV2HookSettings = new WorldMindHookSettings();
                if (Permissions == null) Permissions = new PermissionSettings();
                if (NPCFilters == null) NPCFilters = new NpcFilterSettings();
                if (NPCReactionSettings == null) NPCReactionSettings = new NpcReactionSettings();
                // Null means the section failed to deserialize or is absent at runtime.
                // Empty dictionaries/lists can be intentional owner edits, so never replace them.
                if (NPCPersonalityProfiles == null) NPCPersonalityProfiles = DefaultProfiles();
                if (PromptTemplates == null) PromptTemplates = DefaultPromptTemplates();
                if (FallbackLines == null) FallbackLines = DefaultFallbackLines();
                if (Delivery == null) Delivery = new DeliverySettings();
                if (DiscordMindIntegration == null) DiscordMindIntegration = new DiscordMindSettings();
                if (LocationAwareness == null) LocationAwareness = new LocationAwarenessSettings();
                if (Cooldowns == null) Cooldowns = new CooldownSettings();
                if (Memory == null) Memory = new MemorySettings();
            }
        }

        private class GeneralSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Print DV8D ASCII On Load")]
            public bool PrintDV8DAsciiOnLoad = true;

            [JsonProperty("Debug")]
            public bool Debug = false;
        }

        private class WorldMindHookSettings
        {
            [JsonProperty("Use WorldMindV2 Hook")]
            public bool UseWorldMindV2Hook = true;

            [JsonProperty("Record NPC Events To WorldMind")]
            public bool RecordNPCEventsToWorldMind = true;

            [JsonProperty("Request Hook Names")]
            public List<string> RequestHookNames = new List<string>
            {
                "WorldMindV2_RequestNpcLine",
                "WorldMindV2_RequestLine",
                "WorldMind_RequestLine",
                "WorldMindV2_Ask"
            };

            [JsonProperty("Record Hook Names")]
            public List<string> RecordHookNames = new List<string>
            {
                "WorldMindV2_RecordNpcEvent",
                "WorldMindV2_RecordEvent",
                "WorldMind_RecordEvent"
            };
        }

        private class PermissionSettings
        {
            [JsonProperty("Require worldmindnpcbrain.use To Receive NPC Lines")]
            public bool RequireUsePermissionToReceiveNPCLines = false;
        }

        private class NpcFilterSettings
        {
            [JsonProperty("Include All NPC Players")]
            public bool IncludeAllNPCPlayers = true;

            [JsonProperty("Allowed NPC Name Or Prefab Contains")]
            public List<string> AllowedNPCNameOrPrefabContains = new List<string>
            {
                "scientist",
                "npc",
                "murderer",
                "tunneldweller",
                "dweller",
                "heavy",
                "peacekeeper",
                "scarecrow",
                "bandit",
                "patrol"
            };
        }

        private class NpcReactionSettings
        {
            [JsonProperty("Server Identity")]
            public string ServerIdentity = "Deviated Playgrounds";

            [JsonProperty("Default Speaker Name")]
            public string DefaultSpeakerName = "DV8D Control";

            [JsonProperty("Tone")]
            public string Tone = "Deviated Playgrounds NPC voice: adult Rust chaos, hostile island intelligence, sarcastic pressure, and tactical bite. This server is a hybrid PvE/PvP sandbox with WarMode tension, risk-heavy progression, loot greed, kits, homes, raids, monuments, and player-made disasters. NPCs should feel owned by the playground: violent, amused, unimpressed, and aware of bad movement, loud footsteps, naked confidence, sloppy peeks, greed, panic looting, poor cover, and terrible survival instincts. Lines must be short, player-facing, useful, and mean enough to sting. No admin voice. No tutorial tone. No generic scientist voice. No backend, config, AI, plugin, command, API, model, or hidden-system talk. No slurs or real-world hate.";

            [JsonProperty("Allow Profanity")]
            public bool AllowProfanity = true;

            [JsonProperty("Speaker Color")]
            public string SpeakerColor = "#d6b36a";

            [JsonProperty("Line Color")]
            public string LineColor = "#e8e0cf";

            [JsonProperty("Max Line Length")]
            public int MaxLineLength = 160;

            [JsonProperty("React When Player Approaches NPC")]
            public bool ReactWhenPlayerApproachesNPC = true;

            [JsonProperty("Approach Radius Meters")]
            public float ApproachRadiusMeters = 22f;

            [JsonProperty("Approach Line Chance 0-1")]
            public float ApproachLineChance01 = 0.35f;

            [JsonProperty("React When NPC Is Attacked")]
            public bool ReactWhenNPCIsAttacked = true;

            [JsonProperty("React When NPC Dies")]
            public bool ReactWhenNPCDies = true;

            [JsonProperty("React When NPC Kills Player")]
            public bool ReactWhenNPCKillsPlayer = true;
        }

        private class DeliverySettings
        {
            [JsonProperty("Send To Nearby Players")]
            public bool SendToNearbyPlayers = true;

            [JsonProperty("Nearby Chat Radius Meters")]
            public float NearbyChatRadiusMeters = 35f;
        }

        private class DiscordMindSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Channel Key")]
            public string ChannelKey = "npc";

            [JsonProperty("Category")]
            public string Category = "npc";

            [JsonProperty("Send Approach Events")]
            public bool SendApproachEvents = false;

            [JsonProperty("Send Attacked Events")]
            public bool SendAttackedEvents = true;

            [JsonProperty("Send NPC Death Events")]
            public bool SendNpcDeathEvents = true;

            [JsonProperty("Send NPC Killed Player Events")]
            public bool SendNpcKilledPlayerEvents = true;

            [JsonProperty("Send Other NPC Events")]
            public bool SendOtherNpcEvents = true;

            [JsonProperty("Include Event Facts")]
            public bool IncludeEventFacts = true;

            [JsonProperty("Max Discord Line Length")]
            public int MaxDiscordLineLength = 240;

            [JsonProperty("Max Discord Message Length")]
            public int MaxDiscordMessageLength = 1800;

            [JsonProperty("Debug Discord Routing")]
            public bool DebugDiscordRouting = false;
        }


        private class LocationAwarenessSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Include Grid")]
            public bool IncludeGrid = true;

            [JsonProperty("Include Biome")]
            public bool IncludeBiome = true;

            [JsonProperty("Include Nearest Monument")]
            public bool IncludeNearestMonument = true;

            [JsonProperty("Include World Reads")]
            public bool IncludeWorldReads = true;

            [JsonProperty("Grid Cell Size Meters")]
            public int GridCellSizeMeters = 150;

            [JsonProperty("Max Monument Search Meters")]
            public float MaxMonumentSearchMeters = 450f;

            [JsonProperty("Inside Monument Meters")]
            public float InsideMonumentMeters = 125f;

            [JsonProperty("Near Monument Meters")]
            public float NearMonumentMeters = 300f;

            [JsonProperty("Use Location In Prompts")]
            public bool UseLocationInPrompts = true;

            [JsonProperty("Location Prompt Instruction")]
            public string LocationPromptInstruction = "Use provided location facts when useful: grid, biome, nearest monument, terrain, safezone, and threat flavor. Mention the place only if it makes the line sharper. Do not invent locations.";

            [JsonProperty("Monument Name Overrides")]
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
                ["junk yard"] = "Junkyard",
                ["satellite"] = "Satellite Dish",
                ["dome"] = "Dome",
                ["harbor"] = "Harbor",
                ["lighthouse"] = "Lighthouse",
                ["supermarket"] = "Supermarket",
                ["gas_station"] = "Gas Station",
                ["gas station"] = "Gas Station",
                ["mining_quarry"] = "Mining Quarry",
                ["mining quarry"] = "Mining Quarry",
                ["excavator"] = "Giant Excavator",
                ["oilrig"] = "Oil Rig",
                ["oil rig"] = "Oil Rig",
                ["underwater"] = "Underwater Labs",
                ["fishing_village"] = "Fishing Village",
                ["fishing village"] = "Fishing Village",
                ["outpost"] = "Outpost",
                ["bandit"] = "Bandit Camp",
                ["compound"] = "Compound"
            };
        }

        private class CooldownSettings
        {
            [JsonProperty("Approach Scan Interval Seconds")]
            public float ApproachScanIntervalSeconds = 3f;

            [JsonProperty("Player Approach Line Cooldown Seconds")]
            public double PlayerApproachLineCooldownSeconds = 120d;

            [JsonProperty("NPC Combat Line Cooldown Seconds")]
            public double NPCCombatLineCooldownSeconds = 14d;
        }

        private class MemorySettings
        {
            [JsonProperty("Max Stored Events")]
            public int MaxStoredEvents = 250;
        }

        private class NpcProfile
        {
            [JsonProperty("Speaker Name")]
            public string SpeakerName = "Playground Warden";

            [JsonProperty("Voice")]
            public string Voice = "hostile, sarcastic, tactical, Rust-aware, short";

            [JsonProperty("Fallback Lines")]
            public Dictionary<string, List<string>> FallbackLines = new Dictionary<string, List<string>>();
        }

        private static Dictionary<string, NpcProfile> DefaultProfiles()
        {
            return new Dictionary<string, NpcProfile>
            {
                ["scientist"] = new NpcProfile
                {
                    SpeakerName = "DV8D Warden",
                    Voice = "Deviated Playgrounds scientist: cold, sarcastic, tactical, hostile, Rust-aware, amused by dumb player choices"
                },
                ["heavy"] = new NpcProfile
                {
                    SpeakerName = "Heavy Warden",
                    Voice = "Deviated Playgrounds heavy NPC: brutal, militarized, direct, oppressive, no patience, short kill-threat energy"
                },
                ["murderer"] = new NpcProfile
                {
                    SpeakerName = "Playground Freak",
                    Voice = "Deviated Playgrounds murderer: feral, twitchy, creepy, chaotic, darkly funny, violent, short"
                },
                ["tunneldweller"] = new NpcProfile
                {
                    SpeakerName = "Tunnel Rat",
                    Voice = "Deviated Playgrounds tunnel dweller: paranoid, dirty, hostile, jumpy, scrap-hungry, rusted-out sewer menace"
                },
                ["dweller"] = new NpcProfile
                {
                    SpeakerName = "Tunnel Rat",
                    Voice = "Deviated Playgrounds tunnel dweller: paranoid, dirty, hostile, jumpy, scrap-hungry, rusted-out sewer menace"
                },
                ["peacekeeper"] = new NpcProfile
                {
                    SpeakerName = "Playground Peacekeeper",
                    Voice = "Deviated Playgrounds peacekeeper: dry, smug, controlled, threatening, safezone authority with a mean streak"
                },
                ["scarecrow"] = new NpcProfile
                {
                    SpeakerName = "Playground Creeper",
                    Voice = "Deviated Playgrounds scarecrow: strange, feral, hostile, cursed, darkly funny, not fully sane"
                },
                ["bandit"] = new NpcProfile
                {
                    SpeakerName = "Scrap Clerk",
                    Voice = "Deviated Playgrounds bandit: greedy, dry, crooked, amused, talks like every player is a walking transaction"
                },
                ["patrol"] = new NpcProfile
                {
                    SpeakerName = "Road Warden",
                    Voice = "Deviated Playgrounds patrol NPC: tactical, predatory, road-hardened, watches movement and punishes bad routes"
                },
                ["bradley"] = new NpcProfile
                {
                    SpeakerName = "Yard Boss",
                    Voice = "Deviated Playgrounds armored yard boss: mechanical, brutal, territorial, short warnings, zero mercy"
                }
            };
        }

        private static Dictionary<string, string> DefaultPromptTemplates()
        {
            return new Dictionary<string, string>
            {
                ["Approach"] = "Write ONE short Deviated Playgrounds NPC line when {player} gets close to {speaker}. Speaker vibe: {profileVoice}. Use location facts if useful: grid, biome, monument, terrain, safezone, sightlines, weathered island pressure. Make the player feel watched, heard, and judged by the playground. Mean, tactical, Rust-aware. Max {maxLength} chars.",
                ["Attacked"] = "Write ONE short Deviated Playgrounds NPC line after {player} attacks {speaker}. Speaker vibe: {profileVoice}. Use location facts if useful: monument, biome, grid, terrain, cover, bad angle, open ground. Mock the player's bad choice, aim, ego, greed, timing, or cover. Max {maxLength} chars.",
                ["Death"] = "Write ONE short final Deviated Playgrounds NPC line when {speaker} dies. Speaker vibe: {profileVoice}. Use location facts if useful: monument, biome, grid, terrain, escape route, third-party danger. The player won the body, not the island. Max {maxLength} chars.",
                ["KillPlayer"] = "Write ONE short Deviated Playgrounds NPC line after {speaker} kills {player}. Speaker vibe: {profileVoice}. Use location facts if useful: biome, monument, terrain, sightlines, grid, bad cover, open ground. Make the death feel earned by bad movement, greed, noise, poor cover, panic, or dumb timing. Cold, sarcastic, tactical. Max {maxLength} chars."
            };
        }

        private static Dictionary<string, List<string>> DefaultFallbackLines()
        {
            return new Dictionary<string, List<string>>
            {
                ["Approach"] = new List<string>
                {
                    "DV8D hears those boots. Subtle as a chainsaw in a loot room.",
                    "Easy, tourist. Deviated Playgrounds bites back.",
                    "That gear sounds expensive. Shame about the survival instincts.",
                    "Keep walking loud. The playground loves confident donations.",
                    "You are close enough for the Warden to start judging.",
                    "I hear panic, metal, and a plan held together with hope.",
                    "Slow down, champion. The respawn screen is not going anywhere.",
                    "You smell like farm, fear, and bad route planning.",
                    "The playground heard you before it respected you.",
                    "Step softer. Even the rocks think you are loud.",
                    "That backpack is screaming loot run with no exit plan.",
                    "Move like that in WarMode and the island files your obituary early.",
                    "You are not sneaking. You are announcing a delivery.",
                    "The playground sees you drifting into regret.",
                    "Careful. Curiosity gets farmed here.",
                    "That confidence better have backup ammo.",
                    "You came this close with that plan? Bold little donation.",
                    "Deviated Playgrounds does not hand out safety. It invoices mistakes."
                },
                ["Attacked"] = new List<string>
                {
                    "Bad choice. DV8D just marked you as equipment.",
                    "That shot had confidence. The aim filed a complaint.",
                    "Congratulations, you upgraded from visitor to target.",
                    "That trigger pull sounded like regret arriving early.",
                    "You just turned a loot run into a lesson.",
                    "Careful. Your courage is leaking through your armor.",
                    "The playground accepts your complaint and returns fire.",
                    "Now Deviated gets to play too.",
                    "You hit me like you were asking permission.",
                    "That bullet had ambition. Shame about the shooter.",
                    "You brought smoke. I brought consequences.",
                    "Swing again. Maybe the second mistake comes with wisdom.",
                    "That was not aggression. That was volunteering.",
                    "Your aim is brave. Your future is not.",
                    "Deviated Playgrounds thanks you for starting the paperwork.",
                    "You shot first and still look like the victim.",
                    "This is where your route plan becomes loot confetti.",
                    "Bad timing, loud gun, worse odds. Classic playground behavior."
                },
                ["Death"] = new List<string>
                {
                    "Take the loot. Deviated already charged interest.",
                    "You won the body. Try winning the walk home.",
                    "Loot fast. Something worse heard the celebration.",
                    "One Warden down. The playground still has teeth.",
                    "Enjoy the gun. Paranoia comes free.",
                    "You earned the corpse. Keeping it is the real exam.",
                    "Fine. Take my kit. The playground will want it back.",
                    "You killed me. The island remains undefeated.",
                    "Pocket the scrap and start lying to yourself about safety.",
                    "Good shot. Now survive the witnesses.",
                    "Nice work. Now pretend that noise did not invite company.",
                    "Grab fast, breathe later, die somewhere else.",
                    "That corpse is bait with better lighting.",
                    "You got paid. Now the playground gets curious.",
                    "Do not flex yet. Loot makes people stupid.",
                    "Enjoy the moment. Deviated Playgrounds loves short celebrations.",
                    "One less problem. Same bad island.",
                    "You won this square. The map still hates you."
                },
                ["KillPlayer"] = new List<string>
                {
                    "Threat removed. Inventory redistributed.",
                    "Deviated Playgrounds thanks you for the donation.",
                    "That was not a fight. That was paperwork.",
                    "Respawn lesson delivered.",
                    "You peeked like hope was armor.",
                    "Gear recovered from a poor decision.",
                    "Another survivor audited and deleted.",
                    "Bad cover, loud boots, free kit. Classic.",
                    "The island saw the mistake before you did.",
                    "You died exactly where confidence goes to rot.",
                    "WarMode or not, bad positioning still collects.",
                    "Your loot had better survival instincts than you.",
                    "That death was sponsored by greed and no cover.",
                    "You pushed like the respawn button owed you money.",
                    "DV8D Control confirms: skill issue with accessories.",
                    "You brought a kit to a lesson and left both behind.",
                    "The playground cleaned up your route planning problem.",
                    "Next time bring cover instead of main-character energy."
                }
            };
        }

        private class StoredData
        {
            [JsonProperty("Events")]
            public List<NpcMemoryEvent> Events = new List<NpcMemoryEvent>();
        }

        private class NpcMemoryEvent
        {
            public string TimeUtc;
            public string Event;
            public string Npc;
            public string Player;
            public string Line;
            public Dictionary<string, object> Facts;
        }

        #endregion
    }
}
