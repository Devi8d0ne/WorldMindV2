using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindDeathRecap", "Devi8d0ne", "1.1.0")]
    [Description("WorldMind companion plugin that turns Rust death events into short death recaps and routes death intelligence to DiscordMind.")]
    public class WorldMindDeathRecap : RustPlugin
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

        private const string PermissionAdmin = "worldminddeathrecap.admin";
        private const string PermissionUse = "worldminddeathrecap.use";

        [PluginReference] private Plugin WorldMindV2;
        [PluginReference] private Plugin WorldMindDiscordMind;

        private PluginConfig _config;
        private StoredData _data;
        private readonly Dictionary<string, double> _lastRecapByVictim = new Dictionary<string, double>();

        #region Oxide Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermissionAdmin, this);
            permission.RegisterPermission(PermissionUse, this);
            LoadConfigValues();
            LoadData();
            Puts(DV8DAsciiTag);
            Puts(MadeWithLoveTag + " | WorldMindDeathRecap loaded. Death recaps and Discord death routing ready.");
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
                if (_config == null) throw new Exception("Config was null after read.");
                _config.Normalize();
            }
            catch (Exception ex)
            {
                PrintError("Config read failed. Existing config file was NOT overwritten. Runtime defaults are being used for this session only. Error: " + ex.Message);
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
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
                if (_data == null) _data = new StoredData();
            }
            catch (Exception ex)
            {
                PrintError("Data read failed. Existing data JSON was NOT overwritten. Runtime data is empty for this session only. Error: " + ex.Message);
                _data = new StoredData();
            }

            if (_data.PlayerDeathCounts == null) _data.PlayerDeathCounts = new Dictionary<string, int>();
            if (_data.PlayerKillCounts == null) _data.PlayerKillCounts = new Dictionary<string, int>();
            if (_data.RecentDeaths == null) _data.RecentDeaths = new List<DeathLogRecord>();
        }

        private void SaveData()
        {
            if (_data == null) return;
            Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        #endregion

        #region Commands

        [ChatCommand("wmdeath")]
        private void CmdDeathRecap(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player))
            {
                Reply(player, "You do not have permission to use this command.");
                return;
            }

            if (args == null || args.Length == 0)
            {
                Reply(player, BuildStatusText());
                return;
            }

            string sub = args[0].ToLowerInvariant();
            if (sub == "status")
            {
                Reply(player, BuildStatusText());
                return;
            }

            if (sub == "reload")
            {
                LoadConfigValues();
                LoadData();
                Reply(player, "WorldMindDeathRecap config and data reloaded.");
                return;
            }

            if (sub == "test")
            {
                SendTestRecap(player);
                return;
            }

            if (sub == "testdiscord")
            {
                SendTestDiscord(player);
                return;
            }

            Reply(player, "Usage: /wmdeath, /wmdeath status, /wmdeath reload, /wmdeath test, /wmdeath testdiscord");
        }

        [ConsoleCommand("worldminddeathrecap.status")]
        private void CcmdStatus(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            BasePlayer player = arg.Player();
            if (player != null && !IsAdmin(player))
            {
                arg.ReplyWith("No permission.");
                return;
            }

            arg.ReplyWith(BuildStatusText());
        }

        [ConsoleCommand("worldminddeathrecap.reload")]
        private void CcmdReload(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            BasePlayer player = arg.Player();
            if (player != null && !IsAdmin(player))
            {
                arg.ReplyWith("No permission.");
                return;
            }

            LoadConfigValues();
            LoadData();
            arg.ReplyWith("WorldMindDeathRecap config and data reloaded.");
        }

        [ConsoleCommand("worldminddeathrecap.testdiscord")]
        private void CcmdTestDiscord(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            BasePlayer player = arg.Player();
            if (player != null && !IsAdmin(player))
            {
                arg.ReplyWith("No permission.");
                return;
            }

            DeathContext context = BuildSyntheticDeathContext(player);
            string recap = BuildFallbackRecap(context);
            bool sent = SendDeathToDiscord(context, recap, "console_test");
            arg.ReplyWith(sent ? "WorldMindDeathRecap Discord test queued." : "WorldMindDeathRecap Discord test failed or Discord routing is disabled. Check config/status.");
        }

        #endregion

        #region Death Hook

        private void OnPlayerDeath(BasePlayer victim, HitInfo info)
        {
            if (!_config.General.Enabled) return;
            if (victim == null) return;
            if (_config.Filters.IgnoreNpcVictims && victim.IsNpc) return;
            if (_config.Filters.RequirePermission && !permission.UserHasPermission(victim.UserIDString, PermissionUse)) return;
            if (!CanSendRecap(victim.UserIDString)) return;

            DeathContext context = BuildDeathContext(victim, info);
            RecordLocalDeath(context);

            if (_config.WorldMind.RecordDeathEvents && WorldMindV2 != null)
                WorldMindV2.Call("WorldMind_RecordEvent", Name, "player_death", victim.UserIDString, context.ToTruthDictionary());

            if (!_config.WorldMind.UseWorldMind || WorldMindV2 == null)
            {
                CompleteDeathRecap(context, BuildFallbackRecap(context), "fallback_no_worldmind");
                return;
            }

            Dictionary<string, object> request = BuildWorldMindRequest(context);
            Action<string> callback = message =>
            {
                string recap = string.IsNullOrWhiteSpace(message) ? BuildFallbackRecap(context) : message.Trim();
                CompleteDeathRecap(context, recap, string.IsNullOrWhiteSpace(message) ? "fallback_empty_worldmind" : "worldmind");
            };

            object called = WorldMindV2.Call("WorldMind_AskText", request, callback);
            if (called == null)
                CompleteDeathRecap(context, BuildFallbackRecap(context), "fallback_call_failed");
        }

        #endregion

        #region Recap Flow

        private DeathContext BuildDeathContext(BasePlayer victim, HitInfo info)
        {
            BasePlayer killer = info == null ? null : info.InitiatorPlayer;
            bool selfInflicted = killer != null && killer.userID == victim.userID;
            bool playerKill = killer != null && killer.userID != victim.userID && !killer.IsNpc;
            bool npcKill = killer != null && killer.IsNpc;

            string damageType = "unknown";
            if (info != null && info.damageTypes != null)
                damageType = info.damageTypes.GetMajorityDamageType().ToString();

            string weapon = GetWeaponName(info);
            Vector3 victimPos = victim.transform.position;
            Vector3 killerPos = killer == null ? Vector3.zero : killer.transform.position;
            float distance = killer == null ? 0f : Vector3.Distance(victimPos, killerPos);

            object location = WorldMindV2 == null ? null : WorldMindV2.Call("WorldMind_DescribeLocation", victimPos);

            DeathContext context = new DeathContext
            {
                VictimId = victim.UserIDString,
                VictimName = victim.displayName,
                VictimWasNpc = victim.IsNpc,
                KillerId = killer == null ? "" : killer.UserIDString,
                KillerName = killer == null ? "" : killer.displayName,
                KillerWasNpc = npcKill,
                SelfInflicted = selfInflicted,
                PlayerKill = playerKill,
                DamageType = damageType,
                Weapon = weapon,
                DistanceMeters = Mathf.RoundToInt(distance),
                VictimPosition = FormatPosition(victimPos),
                KillerPosition = killer == null ? "" : FormatPosition(killerPos),
                LocationDescription = location == null ? "" : Convert.ToString(location),
                TimeUtc = DateTime.UtcNow.ToString("o")
            };

            if (_config.Context.IncludeHeldItem && victim.GetActiveItem() != null && victim.GetActiveItem().info != null)
            {
                Item active = victim.GetActiveItem();
                context.VictimHeldItemShortname = active.info.shortname;
                context.VictimHeldItemName = active.info.displayName == null ? active.info.shortname : active.info.displayName.english;
            }

            if (_config.Context.IncludeInventorySummary)
                context.InventorySummary = BuildInventorySummary(victim);

            return context;
        }

        private Dictionary<string, object> BuildWorldMindRequest(DeathContext context)
        {
            Dictionary<string, object> truth = context.ToTruthDictionary();
            truth["recapInstructions"] = string.Join("\n", _config.WorldMind.RecapInstructions.ToArray());
            truth["showToVictim"] = _config.Output.ShowToVictim;
            truth["showToKiller"] = _config.Output.ShowToKiller;
            truth["maxChatCharacters"] = _config.Output.MaxChatCharacters;
            truth["discordRoutingEnabled"] = _config.Discord.Enabled;
            truth["discordChannelKey"] = _config.Discord.ChannelKey;

            return new Dictionary<string, object>
            {
                ["Plugin"] = Name,
                ["EventType"] = "death_recap",
                ["PlayerId"] = context.VictimId,
                ["PlayerName"] = context.VictimName,
                ["Tone"] = _config.WorldMind.Tone,
                ["Urgency"] = context.PlayerKill ? 3 : 2,
                ["Truth"] = truth
            };
        }

        private void CompleteDeathRecap(DeathContext context, string recap, string source)
        {
            if (string.IsNullOrWhiteSpace(recap))
                recap = BuildFallbackRecap(context);

            recap = SanitizeGeneratedRecap(recap);
            if (string.IsNullOrWhiteSpace(recap))
                recap = BuildFallbackRecap(context);

            SendRecapMessages(context, recap);
            SendDeathToDiscord(context, recap, source);
        }

        private void SendRecapMessages(DeathContext context, string recap)
        {
            if (string.IsNullOrWhiteSpace(recap)) return;
            recap = Truncate(recap.Trim(), _config.Output.MaxChatCharacters);

            BasePlayer victim = BasePlayer.FindByID(ConvertToUlong(context.VictimId));
            if (_config.Output.ShowToVictim && victim != null && victim.IsConnected)
                Reply(victim, recap);

            if (_config.Output.ShowToKiller && context.PlayerKill && !string.IsNullOrEmpty(context.KillerId))
            {
                BasePlayer killer = BasePlayer.FindByID(ConvertToUlong(context.KillerId));
                if (killer != null && killer.IsConnected)
                    Reply(killer, "Kill recap: " + recap);
            }

            if (_config.Output.BroadcastPlayerKills && context.PlayerKill)
                PrintToChat(_config.Output.ChatPrefix + " " + recap);
        }

        private string BuildFallbackRecap(DeathContext context)
        {
            if (context.PlayerKill)
            {
                string weapon = string.IsNullOrEmpty(context.Weapon) ? "unknown weapon" : context.Weapon;
                string range = context.DistanceMeters > 0 ? " from " + context.DistanceMeters + "m" : "";
                return context.KillerName + " killed " + context.VictimName + " with " + weapon + range + ".";
            }

            if (context.SelfInflicted)
                return context.VictimName + " died to their own decisions. Damage type: " + context.DamageType + ".";

            if (context.KillerWasNpc)
                return context.VictimName + " was killed by " + context.KillerName + ". Damage type: " + context.DamageType + ".";

            return context.VictimName + " died. Damage type: " + context.DamageType + ".";
        }

        private void SendTestRecap(BasePlayer player)
        {
            DeathContext context = BuildSyntheticDeathContext(player);

            if (!_config.WorldMind.UseWorldMind || WorldMindV2 == null)
            {
                Reply(player, BuildFallbackRecap(context));
                return;
            }

            Action<string> callback = message => Reply(player, string.IsNullOrWhiteSpace(message) ? BuildFallbackRecap(context) : SanitizeGeneratedRecap(message.Trim()));
            object called = WorldMindV2.Call("WorldMind_AskText", BuildWorldMindRequest(context), callback);
            if (called == null) Reply(player, BuildFallbackRecap(context));
        }

        private void SendTestDiscord(BasePlayer player)
        {
            DeathContext context = BuildSyntheticDeathContext(player);
            string recap = BuildFallbackRecap(context);
            bool sent = SendDeathToDiscord(context, recap, "chat_test");
            Reply(player, sent ? "Discord death test queued." : "Discord death test failed or routing is disabled. Check /wmdiscord status and this config.");
        }

        private DeathContext BuildSyntheticDeathContext(BasePlayer player)
        {
            Vector3 pos = player == null ? Vector3.zero : player.transform.position;
            return new DeathContext
            {
                VictimId = player == null ? "0" : player.UserIDString,
                VictimName = player == null ? "Deviated Survivor" : player.displayName,
                KillerName = "Test Attacker",
                KillerId = "0",
                PlayerKill = true,
                DamageType = "Bullet",
                Weapon = "rifle.ak",
                DistanceMeters = 42,
                VictimPosition = FormatPosition(pos),
                KillerPosition = FormatPosition(pos + new Vector3(42f, 0f, 0f)),
                LocationDescription = "Deviated Playgrounds test location",
                TimeUtc = DateTime.UtcNow.ToString("o")
            };
        }

        #endregion

        #region DiscordMind Routing

        private bool SendDeathToDiscord(DeathContext context, string recap, string source)
        {
            if (context == null || !_config.Discord.Enabled) return false;
            if (context.SelfInflicted && !_config.Discord.SendSelfInflictedDeaths) return false;
            if (context.KillerWasNpc && !_config.Discord.SendNpcDeaths) return false;
            if (context.PlayerKill && !_config.Discord.SendPlayerKills) return false;
            if (!context.PlayerKill && !context.KillerWasNpc && !context.SelfInflicted && !_config.Discord.SendOtherDeaths) return false;

            Dictionary<string, object> packet = BuildDiscordDeathPacket(context, recap, source);
            object result = Interface.CallHook("WorldMindDiscordMind_SendDeathEvent", packet);

            if (result == null || IsFalseResult(result))
                result = Interface.CallHook("WorldMindDiscordMind_SendEvent", packet);

            if (result == null || IsFalseResult(result))
                result = Interface.CallHook("WorldMindDiscordMind_SendMessageToChannel", _config.Discord.ChannelKey, GetDiscordTitle(context), GetDiscordMessage(context, recap), "death");

            bool sent = result != null && !IsFalseResult(result);
            if (_config.General.Debug)
                Puts("Discord death route " + (sent ? "queued" : "failed") + " | victim=" + context.VictimName + " | source=" + source);

            return sent;
        }

        private Dictionary<string, object> BuildDiscordDeathPacket(DeathContext context, string recap, string source)
        {
            Dictionary<string, object> truth = context.ToTruthDictionary();
            truth["source"] = source ?? "unknown";
            truth["serverIdentity"] = _config.Discord.ServerIdentity;

            return new Dictionary<string, object>
            {
                ["category"] = "death",
                ["channelKey"] = _config.Discord.ChannelKey,
                ["title"] = GetDiscordTitle(context),
                ["message"] = GetDiscordMessage(context, recap),
                ["server"] = _config.Discord.ServerIdentity,
                ["plugin"] = Name,
                ["eventType"] = "player_death",
                ["victimId"] = context.VictimId,
                ["victimName"] = context.VictimName,
                ["killerId"] = context.KillerId,
                ["killerName"] = context.KillerName,
                ["weapon"] = context.Weapon,
                ["distanceMeters"] = context.DistanceMeters,
                ["damageType"] = context.DamageType,
                ["location"] = context.LocationDescription,
                ["timeUtc"] = context.TimeUtc,
                ["truth"] = truth
            };
        }

        private string GetDiscordTitle(DeathContext context)
        {
            string prefix = string.IsNullOrWhiteSpace(_config.Discord.TitlePrefix) ? "Death Recap" : _config.Discord.TitlePrefix;
            if (context == null) return prefix;
            if (context.PlayerKill && !string.IsNullOrWhiteSpace(context.KillerName))
                return prefix + ": " + context.VictimName + " got dropped by " + context.KillerName;
            if (context.KillerWasNpc && !string.IsNullOrWhiteSpace(context.KillerName))
                return prefix + ": " + context.VictimName + " got deleted by " + context.KillerName;
            if (context.SelfInflicted)
                return prefix + ": " + context.VictimName + " self-owned";
            return prefix + ": " + context.VictimName + " died";
        }

        private string GetDiscordMessage(DeathContext context, string recap)
        {
            if (context == null) return recap ?? "";

            List<string> lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(recap)) lines.Add(recap.Trim());

            if (_config.Discord.IncludeDeathFacts)
            {
                string killer = string.IsNullOrWhiteSpace(context.KillerName) ? "Unknown" : context.KillerName;
                string weapon = string.IsNullOrWhiteSpace(context.Weapon) ? "unknown" : context.Weapon;
                string distance = context.DistanceMeters > 0 ? context.DistanceMeters + "m" : "unknown range";
                lines.Add("Victim: " + context.VictimName + " | Killer: " + killer + " | Weapon: " + weapon + " | Range: " + distance);

                if (!string.IsNullOrWhiteSpace(context.LocationDescription))
                    lines.Add("Location: " + context.LocationDescription);
            }

            return Truncate(string.Join("\n", lines.ToArray()), _config.Discord.MaxDiscordMessageCharacters);
        }

        private bool IsFalseResult(object result)
        {
            if (result == null) return true;
            if (result is bool) return !(bool)result;
            string text = result.ToString();
            return string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase);
        }

        private string SanitizeGeneratedRecap(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string text = value.Trim();
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) return "";
            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) return "";
            if (string.Equals(text, "null", StringComparison.OrdinalIgnoreCase)) return "";
            if (text == "{}" || text == "[]") return "";
            return Truncate(text, _config.Output.MaxChatCharacters);
        }

        #endregion

        #region Helpers

        private string GetWeaponName(HitInfo info)
        {
            if (info == null) return "";

            try
            {
                if (info.WeaponPrefab != null)
                    return info.WeaponPrefab.ShortPrefabName;
            }
            catch { }

            try
            {
                if (info.Initiator != null)
                    return info.Initiator.ShortPrefabName;
            }
            catch { }

            return "";
        }

        private string BuildInventorySummary(BasePlayer player)
        {
            if (player == null || player.inventory == null) return "";

            int itemCount = 0;
            int weaponLikeCount = 0;
            int medicalCount = 0;
            int resourceStackCount = 0;

            CountContainer(player.inventory.containerMain, ref itemCount, ref weaponLikeCount, ref medicalCount, ref resourceStackCount);
            CountContainer(player.inventory.containerBelt, ref itemCount, ref weaponLikeCount, ref medicalCount, ref resourceStackCount);
            CountContainer(player.inventory.containerWear, ref itemCount, ref weaponLikeCount, ref medicalCount, ref resourceStackCount);

            return "items=" + itemCount + ", weaponLike=" + weaponLikeCount + ", medical=" + medicalCount + ", resourceStacks=" + resourceStackCount;
        }

        private void CountContainer(ItemContainer container, ref int itemCount, ref int weaponLikeCount, ref int medicalCount, ref int resourceStackCount)
        {
            if (container == null || container.itemList == null) return;

            foreach (Item item in container.itemList)
            {
                if (item == null || item.info == null) continue;
                itemCount++;
                string shortname = item.info.shortname ?? "";
                string category = item.info.category.ToString();

                if (category == "Weapon" || shortname.Contains("rifle") || shortname.Contains("pistol") || shortname.Contains("smg") || shortname.Contains("bow"))
                    weaponLikeCount++;
                if (category == "Medical" || shortname.Contains("syringe") || shortname.Contains("medkit") || shortname.Contains("bandage"))
                    medicalCount++;
                if (category == "Resources")
                    resourceStackCount++;
            }
        }

        private void RecordLocalDeath(DeathContext context)
        {
            Increment(_data.PlayerDeathCounts, context.VictimId);
            if (context.PlayerKill && !string.IsNullOrEmpty(context.KillerId))
                Increment(_data.PlayerKillCounts, context.KillerId);

            _data.RecentDeaths.Add(new DeathLogRecord
            {
                TimeUtc = context.TimeUtc,
                VictimId = context.VictimId,
                VictimName = context.VictimName,
                KillerId = context.KillerId,
                KillerName = context.KillerName,
                DamageType = context.DamageType,
                Weapon = context.Weapon,
                DistanceMeters = context.DistanceMeters,
                LocationDescription = context.LocationDescription
            });

            int max = Math.Max(25, _config.Data.MaxRecentDeathsStored);
            while (_data.RecentDeaths.Count > max)
                _data.RecentDeaths.RemoveAt(0);

            SaveData();
        }

        private bool CanSendRecap(string victimId)
        {
            if (string.IsNullOrEmpty(victimId)) return false;
            double now = Interface.Oxide.Now;
            double last;
            if (_lastRecapByVictim.TryGetValue(victimId, out last))
            {
                if (now - last < _config.Output.PerVictimCooldownSeconds) return false;
            }
            _lastRecapByVictim[victimId] = now;
            return true;
        }

        private bool IsAdmin(BasePlayer player)
        {
            if (player == null) return false;
            return player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin);
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null || string.IsNullOrEmpty(message)) return;
            SendReply(player, _config.Output.ChatPrefix + " " + message);
        }

        private string BuildStatusText()
        {
            return "WorldMindDeathRecap status:\n" +
                   "Enabled: " + _config.General.Enabled + "\n" +
                   "Use WorldMind: " + _config.WorldMind.UseWorldMind + "\n" +
                   "WorldMindV2 loaded: " + (WorldMindV2 != null) + "\n" +
                   "Show to victim: " + _config.Output.ShowToVictim + "\n" +
                   "Show to killer: " + _config.Output.ShowToKiller + "\n" +
                   "Broadcast player kills: " + _config.Output.BroadcastPlayerKills + "\n" +
                   "Discord enabled: " + _config.Discord.Enabled + "\n" +
                   "DiscordMind loaded: " + (WorldMindDiscordMind != null) + "\n" +
                   "Discord channel key: " + _config.Discord.ChannelKey + "\n" +
                   "Stored deaths: " + _data.RecentDeaths.Count;
        }

        private void Increment(Dictionary<string, int> dictionary, string key)
        {
            if (dictionary == null || string.IsNullOrEmpty(key)) return;
            if (!dictionary.ContainsKey(key)) dictionary[key] = 0;
            dictionary[key]++;
        }

        private string FormatPosition(Vector3 position)
        {
            return Mathf.RoundToInt(position.x) + ", " + Mathf.RoundToInt(position.y) + ", " + Mathf.RoundToInt(position.z);
        }

        private string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (max <= 0) return value;
            if (value.Length <= max) return value;
            return value.Substring(0, max).Trim() + "...";
        }

        private ulong ConvertToUlong(string value)
        {
            ulong result;
            return ulong.TryParse(value, out result) ? result : 0UL;
        }

        #endregion

        #region Config Classes

        private class PluginConfig
        {
            [JsonProperty(Order = 1, PropertyName = "General")]
            public GeneralConfig General = new GeneralConfig();

            [JsonProperty(Order = 2, PropertyName = "WorldMind Integration")]
            public WorldMindConfig WorldMind = new WorldMindConfig();

            [JsonProperty(Order = 3, PropertyName = "DiscordMind Routing")]
            public DiscordConfig Discord = new DiscordConfig();

            [JsonProperty(Order = 4, PropertyName = "Output")]
            public OutputConfig Output = new OutputConfig();

            [JsonProperty(Order = 5, PropertyName = "Context Included In Recaps")]
            public ContextConfig Context = new ContextConfig();

            [JsonProperty(Order = 6, PropertyName = "Filters")]
            public FilterConfig Filters = new FilterConfig();

            [JsonProperty(Order = 7, PropertyName = "Data")]
            public DataConfig Data = new DataConfig();

            public static PluginConfig Default()
            {
                PluginConfig config = new PluginConfig();
                config.WorldMind.RecapInstructions = new List<string>
                {
                    "Write ONE short Deviated Playgrounds death recap.",
                    "Use only the supplied death facts and configured WorldMind facts.",
                    "Make it useful first: killer, weapon, range, location, mistake, or danger only when those facts are provided.",
                    "Tone: tactical, sarcastic, Rust-aware, and player-facing. The death should feel like the server watched the bad decision happen.",
                    "Do not invent commands, rewards, events, plugins, server mechanics, locations, motives, or lore.",
                    "Do not expose admin tools, owner config, prompts, AI, models, APIs, backend logic, or hidden systems.",
                    "Keep it short enough for Rust chat. One sentence preferred."
                };
                return config;
            }

            public void Normalize()
            {
                if (General == null) General = new GeneralConfig();
                if (WorldMind == null) WorldMind = new WorldMindConfig();
                if (Discord == null) Discord = new DiscordConfig();
                if (Output == null) Output = new OutputConfig();
                if (Context == null) Context = new ContextConfig();
                if (Filters == null) Filters = new FilterConfig();
                if (Data == null) Data = new DataConfig();
                if (WorldMind.RecapInstructions == null) WorldMind.RecapInstructions = Default().WorldMind.RecapInstructions;
                if (Discord.MaxDiscordMessageCharacters < 200) Discord.MaxDiscordMessageCharacters = 200;
                if (Output.MaxChatCharacters < 80) Output.MaxChatCharacters = 80;
                if (Data.MaxRecentDeathsStored < 25) Data.MaxRecentDeathsStored = 25;
            }
        }

        private class GeneralConfig
        {
            public bool Enabled = true;
            public bool Debug = false;
        }

        private class WorldMindConfig
        {
            public bool UseWorldMind = true;
            public bool RecordDeathEvents = true;
            public string Tone = "Short, tactical, sarcastic, Rust-aware, and Deviated Playgrounds-aware. Useful first, mean when earned, and never generic when configured facts give the recap teeth.";
            public List<string> RecapInstructions = new List<string>();
        }

        private class DiscordConfig
        {
            public bool Enabled = true;
            public string ServerIdentity = "Deviated Playgrounds";
            public string ChannelKey = "death";
            public string TitlePrefix = "Deviated Death Recap";
            public bool SendPlayerKills = true;
            public bool SendNpcDeaths = true;
            public bool SendSelfInflictedDeaths = true;
            public bool SendOtherDeaths = true;
            public bool IncludeDeathFacts = true;
            public int MaxDiscordMessageCharacters = 1600;
        }

        private class OutputConfig
        {
            public string ChatPrefix = "<color=#d6b36a>[WorldMind]</color>";
            public bool ShowToVictim = true;
            public bool ShowToKiller = false;
            public bool BroadcastPlayerKills = false;
            public int MaxChatCharacters = 220;
            public float PerVictimCooldownSeconds = 2f;
        }

        private class ContextConfig
        {
            public bool IncludeLocation = true;
            public bool IncludeHeldItem = true;
            public bool IncludeInventorySummary = false;
        }

        private class FilterConfig
        {
            public bool IgnoreNpcVictims = true;
            public bool RequirePermission = false;
        }

        private class DataConfig
        {
            public int MaxRecentDeathsStored = 200;
        }

        #endregion

        #region Data / DTO Classes

        private class StoredData
        {
            public Dictionary<string, int> PlayerDeathCounts = new Dictionary<string, int>();
            public Dictionary<string, int> PlayerKillCounts = new Dictionary<string, int>();
            public List<DeathLogRecord> RecentDeaths = new List<DeathLogRecord>();
        }

        private class DeathLogRecord
        {
            public string TimeUtc = "";
            public string VictimId = "";
            public string VictimName = "";
            public string KillerId = "";
            public string KillerName = "";
            public string DamageType = "";
            public string Weapon = "";
            public int DistanceMeters = 0;
            public string LocationDescription = "";
        }

        private class DeathContext
        {
            public string VictimId = "";
            public string VictimName = "";
            public bool VictimWasNpc = false;
            public string KillerId = "";
            public string KillerName = "";
            public bool KillerWasNpc = false;
            public bool SelfInflicted = false;
            public bool PlayerKill = false;
            public string DamageType = "";
            public string Weapon = "";
            public int DistanceMeters = 0;
            public string VictimPosition = "";
            public string KillerPosition = "";
            public string LocationDescription = "";
            public string VictimHeldItemShortname = "";
            public string VictimHeldItemName = "";
            public string InventorySummary = "";
            public string TimeUtc = "";

            public Dictionary<string, object> ToTruthDictionary()
            {
                Dictionary<string, object> truth = new Dictionary<string, object>
                {
                    ["victimId"] = VictimId,
                    ["victimName"] = VictimName,
                    ["victimWasNpc"] = VictimWasNpc,
                    ["killerId"] = KillerId,
                    ["killerName"] = KillerName,
                    ["killerWasNpc"] = KillerWasNpc,
                    ["selfInflicted"] = SelfInflicted,
                    ["playerKill"] = PlayerKill,
                    ["damageType"] = DamageType,
                    ["weapon"] = Weapon,
                    ["distanceMeters"] = DistanceMeters,
                    ["victimPosition"] = VictimPosition,
                    ["killerPosition"] = KillerPosition,
                    ["locationDescription"] = LocationDescription,
                    ["victimHeldItemShortname"] = VictimHeldItemShortname,
                    ["victimHeldItemName"] = VictimHeldItemName,
                    ["inventorySummary"] = InventorySummary,
                    ["timeUtc"] = TimeUtc
                };
                return truth;
            }
        }

        #endregion
    }
}
