/*
DDDDDD     VV        VV    8888888     DDDDDD
DD   DD     VV      VV    88     88    DD   DD
DD    DD     VV    VV     88     88    DD    DD
DD    DD      VV  VV       8888888     DD    DD
DD    DD       VVVV       88     88    DD    DD
DD   DD         VV        88     88    DD   DD
DDDDDD          VV         8888888     DDDDDD

Made with love by Deviated Systems
Author: Devi8d0ne
*/

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindThreatSense", "Devi8d0ne", "1.1.0")]
    [Description("Deviated Playgrounds threat sense brain with WorldMind warnings and DiscordMind threat routing.")]
    public class WorldMindThreatSense : RustPlugin
    {
        private const string MadeWithLoveTag = "Made with love by Deviated Systems";
        private const string DV8DAsciiTag = @"
DDDDDD     VV        VV    8888888     DDDDDD
DD   DD     VV      VV    88     88    DD   DD
DD    DD     VV    VV     88     88    DD    DD
DD    DD      VV  VV       8888888     DD    DD
DD    DD       VVVV       88     88    DD    DD
DD   DD         VV        88     88    DD   DD
DDDDDD          VV         8888888     DDDDDD
";

        private const string PermissionAdmin = "worldmindthreatsense.admin";
        private const string PermissionUse = "worldmindthreatsense.use";

        [PluginReference] private Plugin WorldMindV2;
        [PluginReference] private Plugin WorldMindDiscordMind;

        private PluginConfig _config;
        private readonly Dictionary<string, double> _lastWarning = new Dictionary<string, double>();
        private readonly Dictionary<string, int> _warningCounts = new Dictionary<string, int>();

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermissionAdmin, this);
            permission.RegisterPermission(PermissionUse, this);
            LoadConfigValues();
            PrintStartup();
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
                if (_config == null) throw new Exception("Config was null after read.");
                _config.Normalize();
            }
            catch (Exception ex)
            {
                PrintError("Config read failed. Existing config was NOT overwritten. Runtime defaults are being used for this session only. Error: " + ex.Message);
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

        private void PrintStartup()
        {
            if (_config.General.PrintAsciiOnLoad)
                Puts(DV8DAsciiTag);

            Puts("WorldMindThreatSense loaded. " + MadeWithLoveTag);
            Puts("WorldMind bridge: " + (WorldMindV2 == null ? "not found" : "found") + ". DiscordMind bridge: " + (WorldMindDiscordMind == null ? "not found" : "found") + ". Admin command: /wmthreat");
        }

        #endregion

        #region Commands

        [ChatCommand("wmthreat")]
        private void CmdThreat(BasePlayer player, string command, string[] args)
        {
            if (player != null && !IsAdmin(player))
            {
                Reply(player, "No permission.");
                return;
            }

            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            if (sub == "reload")
            {
                LoadConfigValues();
                Reply(player, "WorldMindThreatSense config reloaded.");
                return;
            }

            if (sub == "test")
            {
                if (player == null)
                {
                    Puts("Run /wmthreat test in-game so the test has a player target.");
                    return;
                }

                ThreatContext context = new ThreatContext
                {
                    PlayerId = player.UserIDString,
                    PlayerName = player.displayName,
                    ThreatType = "manual_test",
                    DamageAmount = 12f,
                    DamageType = "test",
                    AttackerName = "test attacker",
                    AttackerId = "",
                    AttackerIsPlayer = false,
                    AttackerIsNpc = false,
                    Weapon = "unknown",
                    DistanceMeters = 18f,
                    PlayerHealth = player.health,
                    Location = DescribeLocation(player.transform.position),
                    Urgency = 2
                };

                SendWorldMindWarning(player, context, true);
                return;
            }

            if (sub == "testdiscord")
            {
                if (player == null)
                {
                    Puts("Run /wmthreat testdiscord in-game so the test has a player target.");
                    return;
                }

                ThreatContext context = new ThreatContext
                {
                    PlayerId = player.UserIDString,
                    PlayerName = player.displayName,
                    ThreatType = "manual_discord_test",
                    DamageAmount = 22f,
                    DamageType = "Bullet",
                    AttackerName = "Deviated Test Threat",
                    AttackerId = "",
                    AttackerIsPlayer = true,
                    AttackerIsNpc = false,
                    Weapon = "rifle.ak",
                    DistanceMeters = 31f,
                    PlayerHealth = player.health,
                    Location = DescribeLocation(player.transform.position),
                    Urgency = 4
                };

                bool sent = SendThreatToDiscord(context, "Threat test: hostile pressure detected near " + player.displayName + ".", true);
                Reply(player, sent ? "Discord threat test queued." : "Discord threat test failed or DiscordMind is not linked. Check /wmthreat status and worldminddiscord.status.");
                return;
            }

            Reply(player,
                "WorldMindThreatSense status\n" +
                "Enabled: " + _config.General.Enabled + "\n" +
                "WorldMind: " + (WorldMindV2 == null ? "not found" : "found") + "\n" +
                "DiscordMind: " + (WorldMindDiscordMind == null ? "not found" : "found") + "\n" +
                "Use WorldMind: " + _config.WorldMind.UseWorldMind + "\n" +
                "Discord routing enabled: " + _config.DiscordMind.Enabled + "\n" +
                "Discord channel key: " + _config.DiscordMind.ChannelKey + "\n" +
                "Require Use Permission: " + _config.General.RequirePermission + "\n" +
                "Cooldown Seconds: " + _config.General.WarningCooldownSeconds + "\n" +
                "Warnings This Session: " + TotalWarnings());
        }

        #endregion


        [ConsoleCommand("worldmindthreatsense.status")]
        private void CcmdStatus(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            arg.ReplyWith("WorldMindThreatSense status\n" +
                          "Enabled: " + _config.General.Enabled + "\n" +
                          "WorldMind: " + (WorldMindV2 == null ? "not found" : "found") + "\n" +
                          "DiscordMind: " + (WorldMindDiscordMind == null ? "not found" : "found") + "\n" +
                          "Discord routing enabled: " + _config.DiscordMind.Enabled + "\n" +
                          "Discord channel key: " + _config.DiscordMind.ChannelKey + "\n" +
                          "Warnings This Session: " + TotalWarnings());
        }

        [ConsoleCommand("worldmindthreatsense.testdiscord")]
        private void CcmdTestDiscord(ConsoleSystem.Arg arg)
        {
            ThreatContext context = new ThreatContext
            {
                PlayerId = "0",
                PlayerName = "Console Test",
                ThreatType = "console_discord_test",
                DamageAmount = 22f,
                DamageType = "Bullet",
                AttackerName = "Deviated Test Threat",
                AttackerId = "",
                AttackerIsPlayer = true,
                AttackerIsNpc = false,
                Weapon = "rifle.ak",
                DistanceMeters = 31f,
                PlayerHealth = 73f,
                Location = "Deviated Playgrounds test grid",
                Urgency = 4
            };

            bool sent = SendThreatToDiscord(context, "Threat test: DiscordMind bridge is receiving WorldMindThreatSense packets.", true);
            arg?.ReplyWith(sent ? "Discord threat test queued." : "Discord threat test failed or DiscordMind is not linked.");
        }

        #region Hooks

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (!_config.General.Enabled) return;
            if (entity == null || info == null) return;

            BasePlayer victim = entity as BasePlayer;
            if (victim == null || !victim.userID.IsSteamId()) return;
            if (victim.IsNpc && _config.Filters.IgnoreNpcVictims) return;
            if (_config.General.RequirePermission && !permission.UserHasPermission(victim.UserIDString, PermissionUse)) return;

            float totalDamage = info.damageTypes == null ? 0f : info.damageTypes.Total();
            if (totalDamage < _config.Filters.MinimumDamageToWarn) return;
            if (!CanWarn(victim.UserIDString)) return;

            ThreatContext context = BuildThreatContext(victim, info, totalDamage);
            if (!_config.Filters.WarnOnEnvironmentalDamage && !context.AttackerIsPlayer && context.AttackerName == "environment") return;
            if (!_config.Filters.WarnOnNpcDamage && context.AttackerIsNpc) return;
            if (!_config.Filters.WarnOnPlayerDamage && context.AttackerIsPlayer) return;

            SendWorldMindWarning(victim, context, false);
        }

        #endregion

        #region Threat Flow

        private ThreatContext BuildThreatContext(BasePlayer victim, HitInfo info, float totalDamage)
        {
            BasePlayer attacker = info.InitiatorPlayer;
            BaseEntity initiator = info.Initiator;

            string attackerName = "environment";
            string attackerId = "";
            bool attackerIsPlayer = false;
            bool attackerIsNpc = false;
            float distance = 0f;

            if (attacker != null)
            {
                attackerName = attacker.displayName;
                attackerId = attacker.UserIDString;
                attackerIsPlayer = attacker.userID.IsSteamId() && !attacker.IsNpc;
                attackerIsNpc = attacker.IsNpc;
                distance = Vector3.Distance(victim.transform.position, attacker.transform.position);
            }
            else if (initiator != null)
            {
                attackerName = initiator.ShortPrefabName ?? initiator.GetType().Name;
                distance = Vector3.Distance(victim.transform.position, initiator.transform.position);
            }

            string damageType = "unknown";
            if (info.damageTypes != null)
                damageType = info.damageTypes.GetMajorityDamageType().ToString();

            string weapon = GetWeaponName(info);
            int urgency = CalculateUrgency(victim, totalDamage, attackerIsPlayer, distance);

            return new ThreatContext
            {
                PlayerId = victim.UserIDString,
                PlayerName = victim.displayName,
                ThreatType = attackerIsPlayer ? "player_damage" : attackerIsNpc ? "npc_damage" : "environment_damage",
                DamageAmount = totalDamage,
                DamageType = damageType,
                AttackerName = attackerName,
                AttackerId = attackerId,
                AttackerIsPlayer = attackerIsPlayer,
                AttackerIsNpc = attackerIsNpc,
                Weapon = weapon,
                DistanceMeters = distance,
                PlayerHealth = victim.health,
                Location = DescribeLocation(victim.transform.position),
                Urgency = urgency
            };
        }

        private void SendWorldMindWarning(BasePlayer player, ThreatContext context, bool force)
        {
            if (player == null || context == null) return;
            MarkWarned(player.UserIDString);
            IncrementWarning(player.UserIDString);

            if (WorldMindV2 != null && _config.WorldMind.RecordThreatEvents)
                WorldMindV2.Call("WorldMind_RecordEvent", Name, "threat_detected", player.UserIDString, context.ToTruthDictionary());

            if (!_config.WorldMind.UseWorldMind || WorldMindV2 == null)
            {
                string fallback = BuildFallbackWarning(context);
                Reply(player, fallback);
                SendThreatToDiscord(context, fallback, force);
                return;
            }

            Dictionary<string, object> request = BuildWorldMindRequest(context);
            Action<string> callback = message =>
            {
                string warning = IsUsableMessage(message) ? CleanChat(message) : BuildFallbackWarning(context);
                Reply(player, warning);
                SendThreatToDiscord(context, warning, force);
            };

            object called = WorldMindV2.Call("WorldMind_AskText", request, callback);
            if (called == null)
            {
                string fallback = BuildFallbackWarning(context);
                Reply(player, fallback);
                SendThreatToDiscord(context, fallback, force);
            }
        }

        private bool SendThreatToDiscord(ThreatContext context, string warning, bool force)
        {
            if (context == null) return false;
            if (!_config.DiscordMind.Enabled && !force) return false;
            if (WorldMindDiscordMind == null)
            {
                if (_config.DiscordMind.DebugDiscordRouting)
                    Puts("DiscordMind routing skipped: WorldMindDiscordMind is not loaded.");
                return false;
            }

            if (!_config.DiscordMind.SendPlayerDamageThreats && context.AttackerIsPlayer) return false;
            if (!_config.DiscordMind.SendNpcDamageThreats && context.AttackerIsNpc) return false;
            if (!_config.DiscordMind.SendEnvironmentalThreats && !context.AttackerIsPlayer && !context.AttackerIsNpc) return false;
            if (context.Urgency < _config.DiscordMind.SendOnlyUrgencyAtOrAbove) return false;

            string cleanWarning = CleanDiscordText(IsUsableMessage(warning) ? warning : BuildFallbackWarning(context), _config.DiscordMind.MaxDiscordLineLength);
            string title = BuildDiscordTitle(context);
            string message = BuildDiscordMessage(context, cleanWarning);

            Dictionary<string, object> packet = new Dictionary<string, object>
            {
                ["source"] = Name,
                ["category"] = _config.DiscordMind.Category,
                ["channelKey"] = _config.DiscordMind.ChannelKey,
                ["title"] = title,
                ["message"] = message,
                ["threatType"] = context.ThreatType,
                ["playerName"] = context.PlayerName,
                ["attackerName"] = context.AttackerName,
                ["attackerIsPlayer"] = context.AttackerIsPlayer,
                ["attackerIsNpc"] = context.AttackerIsNpc,
                ["damageAmount"] = Mathf.Round(context.DamageAmount * 10f) / 10f,
                ["damageType"] = context.DamageType,
                ["weapon"] = context.Weapon,
                ["distanceMeters"] = Mathf.Round(context.DistanceMeters * 10f) / 10f,
                ["playerHealth"] = Mathf.Round(context.PlayerHealth * 10f) / 10f,
                ["location"] = context.Location,
                ["urgency"] = context.Urgency,
                ["warning"] = cleanWarning,
                ["timeUtc"] = DateTime.UtcNow.ToString("o")
            };

            try
            {
                object result = WorldMindDiscordMind.Call("WorldMindDiscordMind_SendThreatEvent", packet);
                if (AsBool(result)) return true;

                result = WorldMindDiscordMind.Call("WorldMindDiscordMind_SendEvent", packet);
                if (AsBool(result)) return true;

                result = WorldMindDiscordMind.Call("WorldMindDiscordMind_SendMessageToChannel", _config.DiscordMind.ChannelKey, title, message, _config.DiscordMind.Category);
                if (AsBool(result)) return true;

                if (_config.DiscordMind.DebugDiscordRouting)
                    Puts("DiscordMind threat route returned no success. Result: " + (result == null ? "null" : result.ToString()));
            }
            catch (Exception ex)
            {
                if (_config.DiscordMind.DebugDiscordRouting)
                    Puts("DiscordMind threat route failed: " + ex.Message);
            }

            return false;
        }

        private string BuildDiscordTitle(ThreatContext context)
        {
            if (context == null) return "WorldMind Threat";
            if (context.AttackerIsPlayer) return "Threat Sense: Player Damage";
            if (context.AttackerIsNpc) return "Threat Sense: NPC Damage";
            return "Threat Sense: Environmental Damage";
        }

        private string BuildDiscordMessage(ThreatContext context, string warning)
        {
            List<string> parts = new List<string>();
            parts.Add("**" + CleanDiscordText(context.PlayerName, 80) + "** took **" + Mathf.RoundToInt(context.DamageAmount) + "** " + CleanDiscordText(context.DamageType, 40) + " damage.");

            if (!string.IsNullOrWhiteSpace(context.AttackerName) && context.AttackerName != "environment")
                parts.Add("Attacker: `" + CleanDiscordText(context.AttackerName, 80) + "`");

            if (!string.IsNullOrWhiteSpace(context.Weapon) && context.Weapon != "unknown")
                parts.Add("Weapon: `" + CleanDiscordText(context.Weapon, 80) + "`");

            if (context.DistanceMeters > 0f)
                parts.Add("Distance: `" + Mathf.RoundToInt(context.DistanceMeters) + "m`");

            parts.Add("Health: `" + Mathf.RoundToInt(context.PlayerHealth) + "`");
            parts.Add("Urgency: `" + context.Urgency + "/5`");

            if (!string.IsNullOrWhiteSpace(context.Location))
                parts.Add("Location: `" + CleanDiscordText(context.Location, 140) + "`");

            if (_config.DiscordMind.IncludeWarningText && !string.IsNullOrWhiteSpace(warning))
                parts.Add("WorldMind: " + CleanDiscordText(warning, _config.DiscordMind.MaxDiscordLineLength));

            string message = string.Join("\n", parts.ToArray());
            return CleanDiscordText(message, _config.DiscordMind.MaxDiscordMessageLength);
        }

        private bool AsBool(object value)
        {
            if (value == null) return false;
            if (value is bool) return (bool)value;
            bool parsed;
            return bool.TryParse(value.ToString(), out parsed) && parsed;
        }

        private Dictionary<string, object> BuildWorldMindRequest(ThreatContext context)
        {
            return new Dictionary<string, object>
            {
                ["Plugin"] = Name,
                ["EventType"] = "threat_warning",
                ["PlayerId"] = context.PlayerId,
                ["PlayerName"] = context.PlayerName,
                ["Tone"] = _config.WorldMind.RequestTone,
                ["Urgency"] = context.Urgency,
                ["Truth"] = new Dictionary<string, object>
                {
                    ["task"] = "Write one short Deviated Playgrounds threat warning for the player. Plain text only. Tactical, hostile, Rust-aware, useful, and brief. Reference only supplied threat facts and configured WorldMind facts. Do not mention commands, Discord, VIP, PvP modes, custom plugins, backend systems, prompts, APIs, or unconfigured features.",
                    ["maxCharacters"] = _config.WorldMind.MaxWarningCharacters,
                    ["threat"] = context.ToTruthDictionary()
                }
            };
        }

        private string BuildFallbackWarning(ThreatContext context)
        {
            if (context.AttackerIsPlayer)
                return "Threat read: " + context.AttackerName + " tagged you for " + Mathf.RoundToInt(context.DamageAmount) + ". Move, heal, break sightlines.";

            if (context.AttackerIsNpc)
                return "Threat read: NPC damage landed. Stop donating health and change the angle.";

            return "Threat read: " + Mathf.RoundToInt(context.DamageAmount) + " damage taken. Check cover, health, and whatever dumb ground you picked.";
        }

        #endregion

        #region Helpers

        private bool IsAdmin(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin));
        }

        private void Reply(BasePlayer player, string message)
        {
            string text = _config.General.ChatPrefix + " " + (message ?? "");
            if (player == null) Puts(text);
            else player.ChatMessage(text);
        }

        private bool CanWarn(string userId)
        {
            double now = Interface.Oxide.Now;
            double last;
            if (_lastWarning.TryGetValue(userId, out last) && now - last < _config.General.WarningCooldownSeconds)
                return false;
            return true;
        }

        private void MarkWarned(string userId)
        {
            _lastWarning[userId] = Interface.Oxide.Now;
        }

        private void IncrementWarning(string userId)
        {
            if (!_warningCounts.ContainsKey(userId)) _warningCounts[userId] = 0;
            _warningCounts[userId]++;
        }

        private int TotalWarnings()
        {
            int total = 0;
            foreach (KeyValuePair<string, int> pair in _warningCounts)
                total += pair.Value;
            return total;
        }

        private int CalculateUrgency(BasePlayer victim, float damage, bool attackerIsPlayer, float distance)
        {
            int urgency = 1;
            if (attackerIsPlayer) urgency++;
            if (damage >= _config.Filters.HighDamageThreshold) urgency++;
            if (victim.health <= _config.Filters.LowHealthThreshold) urgency++;
            if (attackerIsPlayer && distance > 0f && distance <= _config.Filters.CloseRangeMeters) urgency++;
            return Mathf.Clamp(urgency, 1, 5);
        }

        private string GetWeaponName(HitInfo info)
        {
            try
            {
                if (info == null) return "unknown";
                if (info.WeaponPrefab != null && !string.IsNullOrEmpty(info.WeaponPrefab.ShortPrefabName))
                    return info.WeaponPrefab.ShortPrefabName;
                if (info.Weapon != null)
                {
                    Item item = info.Weapon.GetItem();
                    if (item != null && item.info != null)
                        return item.info.shortname;
                }
            }
            catch
            {
                // ignored
            }

            return "unknown";
        }

        private string DescribeLocation(Vector3 position)
        {
            object described = WorldMindV2 == null ? null : WorldMindV2.Call("WorldMind_DescribeLocation", position);
            if (described != null) return described.ToString();
            return "x " + Mathf.RoundToInt(position.x) + ", z " + Mathf.RoundToInt(position.z);
        }

        private string CleanChat(string value)
        {
            value = (value ?? "").Trim();
            value = value.Replace("\r", " ").Replace("\n", " ");
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            int max = Mathf.Max(40, _config.WorldMind.MaxWarningCharacters);
            if (value.Length > max) value = value.Substring(0, max).Trim() + "...";
            return value;
        }

        private string CleanDiscordText(string value, int max)
        {
            value = (value ?? "").Trim();
            value = value.Replace("@everyone", "@\u200beveryone").Replace("@here", "@\u200bhere");
            value = value.Replace("\r", " ").Replace("\n", "\n");
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            if (max > 0 && value.Length > max) value = value.Substring(0, Math.Max(0, max - 3)).Trim() + "...";
            return value;
        }

        private bool IsUsableMessage(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string t = value.Trim();
            if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) return false;
            if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            if (t.Equals("null", StringComparison.OrdinalIgnoreCase)) return false;
            if (t == "{}" || t == "[]") return false;
            return true;
        }

        #endregion

        #region Config / DTOs

        private class PluginConfig
        {
            [JsonProperty(Order = 1, PropertyName = "General")]
            public GeneralConfig General = new GeneralConfig();

            [JsonProperty(Order = 2, PropertyName = "Threat Filters")]
            public FilterConfig Filters = new FilterConfig();

            [JsonProperty(Order = 3, PropertyName = "WorldMind Bridge")]
            public WorldMindConfig WorldMind = new WorldMindConfig();

            [JsonProperty(Order = 4, PropertyName = "DiscordMind Integration")]
            public DiscordMindConfig DiscordMind = new DiscordMindConfig();

            public static PluginConfig Default()
            {
                return new PluginConfig();
            }

            public void Normalize()
            {
                if (General == null) General = new GeneralConfig();
                if (Filters == null) Filters = new FilterConfig();
                if (WorldMind == null) WorldMind = new WorldMindConfig();
                if (DiscordMind == null) DiscordMind = new DiscordMindConfig();
            }
        }

        private class GeneralConfig
        {
            public bool Enabled = true;
            public string ChatPrefix = "<color=#d6b36a>[ThreatSense]</color>";
            public bool RequirePermission = false;
            public float WarningCooldownSeconds = 35f;
            public bool PrintAsciiOnLoad = true;
        }

        private class FilterConfig
        {
            public bool IgnoreNpcVictims = true;
            public bool WarnOnPlayerDamage = true;
            public bool WarnOnNpcDamage = true;
            public bool WarnOnEnvironmentalDamage = false;
            public float MinimumDamageToWarn = 8f;
            public float HighDamageThreshold = 35f;
            public float LowHealthThreshold = 35f;
            public float CloseRangeMeters = 35f;
        }

        private class WorldMindConfig
        {
            public bool UseWorldMind = true;
            public bool RecordThreatEvents = true;
            public string RequestTone = "Short, hostile, tactical, Rust-aware, and Deviated Playgrounds focused. Warn fast, call out damage, attacker pressure, distance, health, cover, movement, and bad timing. Useful first, sarcastic when earned. Do not invent server facts.";
            public int MaxWarningCharacters = 200;
        }


        private class DiscordMindConfig
        {
            public bool Enabled = true;
            public string ChannelKey = "threat";
            public string Category = "threat";
            public bool SendPlayerDamageThreats = true;
            public bool SendNpcDamageThreats = true;
            public bool SendEnvironmentalThreats = false;
            public int SendOnlyUrgencyAtOrAbove = 3;
            public bool IncludeWarningText = true;
            public int MaxDiscordLineLength = 240;
            public int MaxDiscordMessageLength = 1800;
            public bool DebugDiscordRouting = false;
        }

        private class ThreatContext
        {
            public string PlayerId = "";
            public string PlayerName = "";
            public string ThreatType = "";
            public float DamageAmount;
            public string DamageType = "";
            public string AttackerName = "";
            public string AttackerId = "";
            public bool AttackerIsPlayer;
            public bool AttackerIsNpc;
            public string Weapon = "";
            public float DistanceMeters;
            public float PlayerHealth;
            public string Location = "";
            public int Urgency = 1;

            public Dictionary<string, object> ToTruthDictionary()
            {
                return new Dictionary<string, object>
                {
                    ["playerName"] = PlayerName,
                    ["threatType"] = ThreatType,
                    ["damageAmount"] = Mathf.Round(DamageAmount * 10f) / 10f,
                    ["damageType"] = DamageType,
                    ["attackerName"] = AttackerName,
                    ["attackerId"] = AttackerId,
                    ["attackerIsPlayer"] = AttackerIsPlayer,
                    ["attackerIsNpc"] = AttackerIsNpc,
                    ["weapon"] = Weapon,
                    ["distanceMeters"] = Mathf.Round(DistanceMeters * 10f) / 10f,
                    ["playerHealth"] = Mathf.Round(PlayerHealth * 10f) / 10f,
                    ["location"] = Location,
                    ["urgency"] = Urgency
                };
            }
        }

        #endregion
    }
}
