using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindReputationBrain", "Devi8d0ne", "1.1.0")]
    [Description("Deviated Playgrounds player reputation, title, behavior identity, and Discord routing layer for the WorldMind plugin ecosystem.")]
    public class WorldMindReputationBrain : RustPlugin
    {
        private const string PermissionAdmin = "worldmindreputationbrain.admin";
        private const string PermissionUse = "worldmindreputationbrain.use";
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
        [PluginReference] private Plugin WorldMindPlayerBrain;
        [PluginReference] private Plugin WorldMindAdminMind;
        [PluginReference] private Plugin WorldMindQuestBrain;
        [PluginReference] private Plugin WorldMindDiscordMind;

        private PluginConfig _config;
        private StoredData _data;

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
                Puts($"{MadeWithLoveTag} | Author: Devi8d0ne | Plugin: WorldMindReputationBrain");
            }

            timer.Every(Math.Max(60f, _config.General.AutoSaveSeconds), SaveData);

            if (_config.General.EnablePeriodicEvaluation)
                timer.Every(Math.Max(300f, _config.General.EvaluationIntervalMinutes * 60f), () => EvaluateOnlinePlayers());

            Puts("WorldMindReputationBrain loaded.");
        }

        private void Unload()
        {
            SaveData();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            TouchProfile(player.UserIDString, player.displayName);

            if (_config.General.ShowReputationOnConnect)
            {
                timer.Once(6f, () =>
                {
                    if (player != null && player.IsConnected)
                        ShowReputation(player, player.UserIDString);
                });
            }
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            BasePlayer player = entity as BasePlayer;
            if (player == null || item == null) return;
            AddSignal(player.UserIDString, player.displayName, "gather", Math.Max(1, item.amount / 100), item.info == null ? "" : item.info.shortname);
        }

        private void OnCollectiblePickup(Item item, BasePlayer player)
        {
            if (player == null || item == null) return;
            AddSignal(player.UserIDString, player.displayName, "collect", Math.Max(1, item.amount), item.info == null ? "" : item.info.shortname);
        }

        private void OnEntityBuilt(Planner planner, GameObject gameObject)
        {
            if (planner == null || gameObject == null) return;
            BasePlayer player = planner.GetOwnerPlayer();
            if (player == null) return;
            AddSignal(player.UserIDString, player.displayName, "build", 1, gameObject.name);
        }

        private void OnItemCraftFinished(ItemCraftTask task, Item item)
        {
            if (task == null || item == null) return;
            BasePlayer owner = GetCraftTaskOwner(task);
            if (owner == null) return;
            AddSignal(owner.UserIDString, owner.displayName, "craft", Math.Max(1, item.amount), item.info == null ? "" : item.info.shortname);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) return;

            BasePlayer victim = entity as BasePlayer;
            BasePlayer attacker = info == null ? null : info.InitiatorPlayer;

            if (victim != null)
            {
                AddSignal(victim.UserIDString, victim.displayName, "death", 1, attacker == null ? "environment" : attacker.displayName);

                if (attacker != null && attacker != victim)
                    AddSignal(attacker.UserIDString, attacker.displayName, "player_kill", 1, victim.displayName);

                return;
            }

            if (attacker != null)
            {
                string shortName = entity.ShortPrefabName ?? "";
                if (entity is NPCPlayer || shortName.ToLowerInvariant().Contains("scientist"))
                    AddSignal(attacker.UserIDString, attacker.displayName, "npc_kill", 1, shortName);
            }
        }

        #endregion

        #region Commands

        [ChatCommand("rep")]
        private void CmdRep(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!CanUse(player)) return;

            if (args == null || args.Length == 0)
            {
                ShowReputation(player, player.UserIDString);
                return;
            }

            string sub = args[0].ToLowerInvariant();

            if (sub == "profile")
            {
                ShowReputation(player, player.UserIDString);
                return;
            }

            if (sub == "ask")
            {
                string question = args.Length > 1 ? string.Join(" ", args.Skip(1).ToArray()) : "What is my reputation?";
                AskReputationQuestion(player, question);
                return;
            }

            ShowReputation(player, player.UserIDString);
        }

        [ChatCommand("wmrep")]
        private void CmdAdminRep(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!HasAdmin(player)) return;

            if (args == null || args.Length == 0)
            {
                Reply(player,
                    "WorldMindReputationBrain commands:\n" +
                    "/wmrep status\n" +
                    "/wmrep profile <steamId/name>\n" +
                    "/wmrep eval <steamId/name>\n" +
                    "/wmrep evalall\n" +
                    "/wmrep addtag <steamId/name> <tag>\n" +
                    "/wmrep removetag <steamId/name> <tag>\n" +
                    "/wmrep title <steamId/name> <title>\n" +
                    "/wmrep signal <steamId/name> <type> <amount> [notes]\n" +
                    "/wmrep top\n" +
                    "/wmrep ask <steamId/name> <question>\n" +
                    "/wmrep testdiscord [steamId/name]\n" +
                    "/wmrep reload\n" +
                    "/wmrep save\n" +
                    "/wmrep clear <steamId>");
                return;
            }

            string sub = args[0].ToLowerInvariant();

            if (sub == "status")
            {
                Reply(player, GetStatusText());
                return;
            }

            if (sub == "profile")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmrep profile <steamId/name>");
                    return;
                }

                Reply(player, BuildProfileText(ResolvePlayerId(args[1]), true));
                return;
            }

            if (sub == "eval")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmrep eval <steamId/name>");
                    return;
                }

                ReputationProfile profile = EvaluatePlayer(ResolvePlayerId(args[1]), true);
                Reply(player, profile == null ? "No profile found." : BuildProfileText(profile.PlayerId, true));
                return;
            }

            if (sub == "evalall")
            {
                int count = EvaluateOnlinePlayers();
                Reply(player, $"Evaluated {count} online players.");
                return;
            }

            if (sub == "addtag")
            {
                if (args.Length < 3)
                {
                    Reply(player, "Usage: /wmrep addtag <steamId/name> <tag>");
                    return;
                }

                ReputationProfile profile = GetOrCreateProfile(ResolvePlayerId(args[1]), args[1]);
                AddTag(profile, args[2], "manual");
                SaveData();
                Reply(player, $"Added tag {args[2]}.");
                return;
            }

            if (sub == "removetag")
            {
                if (args.Length < 3)
                {
                    Reply(player, "Usage: /wmrep removetag <steamId/name> <tag>");
                    return;
                }

                ReputationProfile profile = GetProfile(ResolvePlayerId(args[1]));
                if (profile == null)
                {
                    Reply(player, "Profile not found.");
                    return;
                }

                profile.Tags.RemoveAll(x => string.Equals(x.Tag, args[2], StringComparison.OrdinalIgnoreCase));
                SaveData();
                Reply(player, $"Removed tag {args[2]}.");
                return;
            }

            if (sub == "title")
            {
                if (args.Length < 3)
                {
                    Reply(player, "Usage: /wmrep title <steamId/name> <title>");
                    return;
                }

                ReputationProfile profile = GetOrCreateProfile(ResolvePlayerId(args[1]), args[1]);
                profile.Title = string.Join(" ", args.Skip(2).ToArray());
                profile.TitleSource = "manual";
                profile.UpdatedUtc = DateTime.UtcNow.ToString("o");
                SaveData();
                Reply(player, $"Title set: {profile.Title}");
                return;
            }

            if (sub == "signal")
            {
                if (args.Length < 4)
                {
                    Reply(player, "Usage: /wmrep signal <steamId/name> <type> <amount> [notes]");
                    return;
                }

                int amount;
                if (!int.TryParse(args[3], out amount))
                {
                    Reply(player, "Amount must be a whole number.");
                    return;
                }

                string notes = args.Length > 4 ? string.Join(" ", args.Skip(4).ToArray()) : "";
                string id = ResolvePlayerId(args[1]);
                AddSignal(id, args[1], args[2], amount, notes);
                Reply(player, "Signal recorded.");
                return;
            }

            if (sub == "top")
            {
                Reply(player, BuildTopText());
                return;
            }

            if (sub == "ask")
            {
                if (args.Length < 3)
                {
                    Reply(player, "Usage: /wmrep ask <steamId/name> <question>");
                    return;
                }

                string id = ResolvePlayerId(args[1]);
                string question = string.Join(" ", args.Skip(2).ToArray());
                AskAdminReputationQuestion(player, id, question);
                return;
            }

            if (sub == "testdiscord")
            {
                string id = args.Length >= 2 ? ResolvePlayerId(args[1]) : player.UserIDString;
                SendTestDiscordReputationEvent(id, player.displayName);
                Reply(player, "WorldMind reputation Discord test queued.");
                return;
            }

            if (sub == "reload")
            {
                LoadPluginConfig();
                LoadData();
                Reply(player, "WorldMindReputationBrain reloaded.");
                return;
            }

            if (sub == "save")
            {
                SaveData();
                Reply(player, "WorldMindReputationBrain data saved.");
                return;
            }

            if (sub == "clear")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmrep clear <steamId>");
                    return;
                }

                bool removed = _data.Profiles.Remove(args[1]);
                SaveData();
                Reply(player, removed ? "Profile cleared." : "Profile not found.");
                return;
            }

            Reply(player, "Unknown command. Use /wmrep for help.");
        }

        [ConsoleCommand("worldmindrep.status")]
        private void ConsoleStatus(ConsoleSystem.Arg arg)
        {
            arg?.ReplyWith(GetStatusText());
        }

        [ConsoleCommand("worldmindrep.reload")]
        private void ConsoleReload(ConsoleSystem.Arg arg)
        {
            LoadPluginConfig();
            LoadData();
            arg?.ReplyWith("WorldMindReputationBrain reloaded.");
        }

        [ConsoleCommand("worldmindrep.testdiscord")]
        private void ConsoleTestDiscord(ConsoleSystem.Arg arg)
        {
            string id = arg != null && arg.Args != null && arg.Args.Length > 0 ? ResolvePlayerId(arg.Args[0]) : "server-test";
            SendTestDiscordReputationEvent(id, ResolvePlayerName(id));
            arg?.ReplyWith("WorldMindReputationBrain Discord test queued.");
        }

        #endregion

        #region Public hooks

        private object WorldMindReputationBrain_AddSignal(string playerId, string playerName, string signalType, int amount, string notes)
        {
            AddSignal(playerId, playerName, signalType, amount, notes);
            return true;
        }

        private object WorldMindReputationBrain_GetProfile(string playerId)
        {
            return GetProfile(playerId);
        }

        private object WorldMindReputationBrain_GetSummary(string playerId)
        {
            return BuildProfileText(playerId, false);
        }

        private object WorldMindReputationBrain_Evaluate(string playerId)
        {
            return EvaluatePlayer(playerId, false);
        }

        private object WorldMindReputationBrain_GetTags(string playerId)
        {
            ReputationProfile profile = GetProfile(playerId);
            return profile == null ? new List<ReputationTag>() : profile.Tags;
        }

        private object WorldMindReputationBrain_GetTitle(string playerId)
        {
            ReputationProfile profile = GetProfile(playerId);
            return profile == null ? "" : profile.Title;
        }

        private object WorldMindReputationBrain_GetTopProfiles(int count)
        {
            return _data.Profiles.Values
                .OrderByDescending(x => x.ReputationScore)
                .Take(Mathf.Clamp(count, 1, 100))
                .ToList();
        }

        #endregion

        #region Core

        private void TouchProfile(string playerId, string playerName)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return;

            ReputationProfile profile = GetOrCreateProfile(playerId, playerName);
            profile.PlayerName = string.IsNullOrWhiteSpace(playerName) ? profile.PlayerName : playerName;
            profile.LastSeenUtc = DateTime.UtcNow.ToString("o");
            SaveData();
        }

        private ReputationProfile GetOrCreateProfile(string playerId, string playerName)
        {
            if (string.IsNullOrWhiteSpace(playerId))
                playerId = playerName ?? "unknown";

            ReputationProfile profile;
            if (!_data.Profiles.TryGetValue(playerId, out profile))
            {
                profile = new ReputationProfile
                {
                    PlayerId = playerId,
                    PlayerName = playerName ?? "",
                    CreatedUtc = DateTime.UtcNow.ToString("o"),
                    LastSeenUtc = DateTime.UtcNow.ToString("o"),
                    UpdatedUtc = DateTime.UtcNow.ToString("o")
                };
                _data.Profiles[playerId] = profile;
            }

            if (!string.IsNullOrWhiteSpace(playerName))
                profile.PlayerName = playerName;

            return profile;
        }

        private ReputationProfile GetProfile(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return null;

            ReputationProfile profile;
            return _data.Profiles.TryGetValue(playerId, out profile) ? profile : null;
        }

        private void AddSignal(string playerId, string playerName, string signalType, int amount, string notes)
        {
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(signalType)) return;
            amount = Math.Max(1, amount);

            ReputationProfile profile = GetOrCreateProfile(playerId, playerName);

            string key = signalType.ToLowerInvariant();
            int current;
            profile.Signals.TryGetValue(key, out current);
            profile.Signals[key] = current + amount;

            profile.RecentSignals.Add(new ReputationSignal
            {
                Type = key,
                Amount = amount,
                Notes = notes ?? "",
                TimestampUtc = DateTime.UtcNow.ToString("o")
            });

            while (profile.RecentSignals.Count > _config.Reporting.KeepRecentSignalsPerPlayer)
                profile.RecentSignals.RemoveAt(0);

            profile.UpdatedUtc = DateTime.UtcNow.ToString("o");

            EvaluateProfile(profile, false);

            SaveData();
            RecordWorldMindEvent("reputation_signal", new Dictionary<string, object>
            {
                ["playerId"] = playerId,
                ["playerName"] = playerName,
                ["signalType"] = key,
                ["amount"] = amount,
                ["notes"] = notes
            });

            SendDiscordReputationSignal(profile, key, amount, notes);
        }

        private int EvaluateOnlinePlayers()
        {
            int count = 0;

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null) continue;

                TouchProfile(player.UserIDString, player.displayName);
                EvaluatePlayer(player.UserIDString, false);
                count++;
            }

            return count;
        }

        private ReputationProfile EvaluatePlayer(string playerId, bool allowWorldMind)
        {
            ReputationProfile profile = GetProfile(playerId);
            if (profile == null) return null;

            EvaluateProfile(profile, allowWorldMind);
            SaveData();
            return profile;
        }

        private void EvaluateProfile(ReputationProfile profile, bool allowWorldMind)
        {
            if (profile == null) return;

            profile.Tags.Clear();

            int kills = GetSignal(profile, "player_kill");
            int deaths = GetSignal(profile, "death");
            int npcKills = GetSignal(profile, "npc_kill");
            int gather = GetSignal(profile, "gather");
            int build = GetSignal(profile, "build");
            int craft = GetSignal(profile, "craft");
            int collect = GetSignal(profile, "collect");

            profile.ReputationScore =
                kills * _config.Scoring.PlayerKillWeight +
                npcKills * _config.Scoring.NpcKillWeight +
                gather * _config.Scoring.GatherWeight +
                build * _config.Scoring.BuildWeight +
                craft * _config.Scoring.CraftWeight +
                collect * _config.Scoring.CollectWeight -
                deaths * _config.Scoring.DeathPenalty;

            if (kills >= _config.Thresholds.PvpMindedKills) AddTag(profile, "pvp_minded", "automatic");
            if (deaths >= _config.Thresholds.DeathProneDeaths) AddTag(profile, "death_prone", "automatic");
            if (npcKills >= _config.Thresholds.NpcHunterKills) AddTag(profile, "npc_hunter", "automatic");
            if (gather >= _config.Thresholds.FarmerGatherScore) AddTag(profile, "farmer", "automatic");
            if (build >= _config.Thresholds.BuilderBuilds) AddTag(profile, "builder", "automatic");
            if (craft >= _config.Thresholds.CrafterCrafts) AddTag(profile, "crafter", "automatic");

            profile.Title = PickTitle(profile);
            profile.TitleSource = "automatic";
            profile.UpdatedUtc = DateTime.UtcNow.ToString("o");

            if (allowWorldMind && _config.WorldMindIntegration.AllowWorldMindTitleGeneration)
                TryGenerateWorldMindTitle(profile);
        }

        private int GetSignal(ReputationProfile profile, string key)
        {
            int value;
            return profile.Signals.TryGetValue(key, out value) ? value : 0;
        }

        private void AddTag(ReputationProfile profile, string tag, string source)
        {
            if (profile == null || string.IsNullOrWhiteSpace(tag)) return;

            if (profile.Tags.Any(x => string.Equals(x.Tag, tag, StringComparison.OrdinalIgnoreCase)))
                return;

            profile.Tags.Add(new ReputationTag
            {
                Tag = tag,
                Source = source ?? "unknown",
                AddedUtc = DateTime.UtcNow.ToString("o")
            });
        }

        private string PickTitle(ReputationProfile profile)
        {
            if (profile == null) return "Unknown Survivor";

            int kills = GetSignal(profile, "player_kill");
            int deaths = GetSignal(profile, "death");
            int npcKills = GetSignal(profile, "npc_kill");
            int gather = GetSignal(profile, "gather");
            int build = GetSignal(profile, "build");
            int craft = GetSignal(profile, "craft");

            if (kills >= _config.Thresholds.PvpMindedKills && deaths >= _config.Thresholds.DeathProneDeaths)
                return "Volatile Survivor";

            if (kills >= _config.Thresholds.PvpMindedKills)
                return "Pressure Seeker";

            if (npcKills >= _config.Thresholds.NpcHunterKills)
                return "Scientist Bully";

            if (build >= _config.Thresholds.BuilderBuilds)
                return "Builder Brain";

            if (gather >= _config.Thresholds.FarmerGatherScore)
                return "Resource Grinder";

            if (craft >= _config.Thresholds.CrafterCrafts)
                return "Workbench Regular";

            if (deaths >= _config.Thresholds.DeathProneDeaths)
                return "Loot Donor";

            return "Fresh Survivor";
        }

        private void TryGenerateWorldMindTitle(ReputationProfile profile)
        {
            if (profile == null || WorldMindV2 == null) return;

            string prompt =
                "You are WorldMind generating a short Deviated Playgrounds Rust player reputation title.\n" +
                "Use configured server facts only. Deviated Playgrounds voice is sharp, Rust-aware, sarcastic, and player-facing.\n" +
                "Return only a title, 2-4 words. No explanation. No slurs or real-world hate.\n" +
                $"Profile JSON:\n{JsonConvert.SerializeObject(profile, Formatting.Indented)}";

            try
            {
                object result = WorldMindV2.Call("WorldMind_AskText", prompt, "WorldMindReputationBrain", "title_generation");
                string title = result == null ? "" : result.ToString().Trim();

                if (!string.IsNullOrWhiteSpace(title) && title.Length <= _config.Display.MaxTitleLength)
                {
                    profile.Title = title.Replace("\n", " ").Replace("\r", " ");
                    profile.TitleSource = "worldmind";
                }
            }
            catch (Exception ex)
            {
                if (_config.General.Debug)
                    Puts($"WorldMind title generation failed: {ex.Message}");
            }
        }


        private void SendTestDiscordReputationEvent(string playerId, string fallbackName)
        {
            ReputationProfile profile = GetOrCreateProfile(playerId, string.IsNullOrWhiteSpace(fallbackName) ? "Deviated Test Survivor" : fallbackName);
            AddTag(profile, "discord_test", "test");
            profile.ReputationScore += 1;
            profile.Title = string.IsNullOrWhiteSpace(profile.Title) ? "Playground Variable" : profile.Title;
            profile.UpdatedUtc = DateTime.UtcNow.ToString("o");

            SendDiscordReputationEvent("reputation_test", profile, new Dictionary<string, object>
            {
                ["reason"] = "manual Discord routing test",
                ["signalType"] = "test",
                ["amount"] = 1
            }, true);
        }

        private void SendDiscordReputationSignal(ReputationProfile profile, string signalType, int amount, string notes)
        {
            if (profile == null || _config == null || _config.DiscordMindIntegration == null) return;
            if (!_config.DiscordMindIntegration.Enabled) return;
            if (!_config.DiscordMindIntegration.SendSignalEvents) return;

            string key = (signalType ?? "").ToLowerInvariant();
            if (_config.DiscordMindIntegration.OnlySendConfiguredSignalTypes &&
                (_config.DiscordMindIntegration.SignificantSignalTypes == null ||
                 !_config.DiscordMindIntegration.SignificantSignalTypes.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase))))
                return;

            if (amount < _config.DiscordMindIntegration.MinimumSignalAmountToPost)
                return;

            SendDiscordReputationEvent("reputation_signal", profile, new Dictionary<string, object>
            {
                ["signalType"] = key,
                ["amount"] = amount,
                ["notes"] = notes ?? ""
            }, false);
        }

        private void SendDiscordReputationEvent(string eventType, ReputationProfile profile, Dictionary<string, object> extraFacts, bool force)
        {
            if (profile == null || _config == null || _config.DiscordMindIntegration == null) return;
            if (!_config.DiscordMindIntegration.Enabled && !force) return;

            try
            {
                string tags = profile.Tags == null || profile.Tags.Count == 0
                    ? "none"
                    : string.Join(", ", profile.Tags.Select(x => x.Tag).ToArray());

                string title = _config.DiscordMindIntegration.TitlePrefix + " " + profile.PlayerName;
                string message =
                    "**" + CleanDiscord(profile.PlayerName, 80) + "** [" + CleanDiscord(profile.Title, 80) + "]\n" +
                    "Score: `" + profile.ReputationScore + "` | Tags: `" + CleanDiscord(tags, 220) + "`";

                if (extraFacts != null && extraFacts.ContainsKey("signalType"))
                    message += "\nSignal: `" + CleanDiscord(Convert.ToString(extraFacts["signalType"]), 60) + "` +" + Convert.ToString(extraFacts.ContainsKey("amount") ? extraFacts["amount"] : 1);

                if (extraFacts != null && extraFacts.ContainsKey("reason"))
                    message += "\nReason: " + CleanDiscord(Convert.ToString(extraFacts["reason"]), 220);

                if (extraFacts != null && extraFacts.ContainsKey("notes") && !string.IsNullOrWhiteSpace(Convert.ToString(extraFacts["notes"])))
                    message += "\nNotes: " + CleanDiscord(Convert.ToString(extraFacts["notes"]), 220);

                Dictionary<string, object> packet = new Dictionary<string, object>
                {
                    ["title"] = title,
                    ["message"] = TruncateDiscord(message, _config.DiscordMindIntegration.MaxDiscordMessageLength),
                    ["category"] = _config.DiscordMindIntegration.Category,
                    ["channelKey"] = _config.DiscordMindIntegration.ChannelKey,
                    ["eventType"] = eventType,
                    ["plugin"] = Name,
                    ["playerId"] = profile.PlayerId,
                    ["playerName"] = profile.PlayerName,
                    ["reputationScore"] = profile.ReputationScore,
                    ["reputationTitle"] = profile.Title,
                    ["titleSource"] = profile.TitleSource,
                    ["tags"] = tags,
                    ["updatedUtc"] = profile.UpdatedUtc,
                    ["extraFacts"] = extraFacts ?? new Dictionary<string, object>()
                };

                if (_config.DiscordMindIntegration.IncludeFullProfileJson)
                    packet["profileJson"] = JsonConvert.SerializeObject(profile);

                object result = Interface.CallHook("WorldMindDiscordMind_SendReputationEvent", packet);
                if (HookAccepted(result)) return;

                result = Interface.CallHook("WorldMindDiscordMind_SendEvent", packet);
                if (HookAccepted(result)) return;

                result = Interface.CallHook("WorldMindDiscordMind_SendMessageToChannel",
                    _config.DiscordMindIntegration.ChannelKey,
                    title,
                    TruncateDiscord(message, _config.DiscordMindIntegration.MaxDiscordMessageLength),
                    _config.DiscordMindIntegration.Category);

                if (_config.DiscordMindIntegration.DebugDiscordRouting)
                    Puts("DiscordMind reputation route result: " + (result == null ? "null" : result.ToString()));
            }
            catch (Exception ex)
            {
                if (_config.General.Debug || _config.DiscordMindIntegration.DebugDiscordRouting)
                    Puts("Discord reputation route failed: " + ex.Message);
            }
        }

        private bool HookAccepted(object result)
        {
            if (result == null) return false;
            if (result is bool) return (bool)result;
            string text = result.ToString();
            return text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("queued", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("ok", StringComparison.OrdinalIgnoreCase);
        }

        private string CleanDiscord(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string clean = value.Replace("@everyone", "@\u200beveryone").Replace("@here", "@\u200bhere");
            clean = clean.Replace("\r", " ").Replace("\n", " ").Trim();
            return TruncateDiscord(clean, max);
        }

        private string TruncateDiscord(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (max <= 0 || value.Length <= max) return value;
            return value.Substring(0, Math.Max(0, max - 3)).TrimEnd() + "...";
        }

        private string ResolvePlayerId(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return "";

            BasePlayer player = BasePlayer.activePlayerList.FirstOrDefault(x =>
                x != null &&
                (
                    x.UserIDString == query ||
                    (!string.IsNullOrWhiteSpace(x.displayName) && x.displayName.ToLowerInvariant().Contains(query.ToLowerInvariant()))
                ));

            return player == null ? query : player.UserIDString;
        }

        private string ResolvePlayerName(string playerId)
        {
            BasePlayer player = BasePlayer.activePlayerList.FirstOrDefault(x => x != null && x.UserIDString == playerId);
            if (player != null) return player.displayName;

            ReputationProfile profile = GetProfile(playerId);
            return profile == null ? playerId : profile.PlayerName;
        }

        #endregion

        #region WorldMind ask/events

        private void AskReputationQuestion(BasePlayer player, string question)
        {
            if (WorldMindV2 == null)
            {
                ShowReputation(player, player.UserIDString);
                return;
            }

            ReputationProfile profile = GetProfile(player.UserIDString);
            if (profile == null)
            {
                Reply(player, "No reputation profile yet.");
                return;
            }

            string prompt =
                "You are WorldMind answering a player question about their generic Rust reputation profile.\n" +
                "Do not assume VIP, Discord, WarMode, kits, homes, teleport, factions, custom economy, DeepSea, or server-specific systems.\n" +
                "Keep it short and player-facing.\n" +
                $"Question: {question}\n" +
                $"Profile JSON:\n{JsonConvert.SerializeObject(profile, Formatting.Indented)}";

            try
            {
                object result = WorldMindV2.Call("WorldMind_AskText", prompt, "WorldMindReputationBrain", "player_reputation_question");
                string message = result == null ? "" : result.ToString();
                Reply(player, string.IsNullOrWhiteSpace(message) ? BuildProfileText(player.UserIDString, false) : message);
            }
            catch
            {
                ShowReputation(player, player.UserIDString);
            }
        }

        private void AskAdminReputationQuestion(BasePlayer admin, string playerId, string question)
        {
            if (WorldMindV2 == null)
            {
                Reply(admin, "WorldMindV2 is not loaded.");
                return;
            }

            ReputationProfile profile = GetProfile(playerId);
            if (profile == null)
            {
                Reply(admin, "No reputation profile found.");
                return;
            }

            string prompt =
                "You are WorldMind answering an admin-only reputation question for a generic Rust server.\n" +
                "Do not assume VIP, Discord, WarMode, kits, homes, teleport, factions, custom economy, DeepSea, or server-specific systems.\n" +
                "Do not recommend punishment. Summarize behavior identity only.\n" +
                $"Question: {question}\n" +
                $"Profile JSON:\n{JsonConvert.SerializeObject(profile, Formatting.Indented)}";

            try
            {
                object result = WorldMindV2.Call("WorldMind_AskText", prompt, "WorldMindReputationBrain", "admin_reputation_question");
                string message = result == null ? "" : result.ToString();
                Reply(admin, string.IsNullOrWhiteSpace(message) ? BuildProfileText(playerId, true) : message);
            }
            catch (Exception ex)
            {
                Reply(admin, $"WorldMind reputation question failed: {ex.Message}");
            }
        }

        private void RecordWorldMindEvent(string eventType, object payload)
        {
            if (!_config.WorldMindIntegration.RecordEventsToWorldMind || WorldMindV2 == null) return;

            try
            {
                Dictionary<string, object> packet = new Dictionary<string, object>
                {
                    ["plugin"] = "WorldMindReputationBrain",
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

        private void ShowReputation(BasePlayer viewer, string playerId)
        {
            Reply(viewer, BuildProfileText(playerId, false));
        }

        private string BuildProfileText(string playerId, bool admin)
        {
            ReputationProfile profile = GetProfile(playerId);
            if (profile == null)
                return "No reputation profile found yet.";

            List<string> lines = new List<string>
            {
                $"{profile.PlayerName} [{profile.Title}]",
                $"Score: {profile.ReputationScore}",
                $"Tags: {(profile.Tags.Count == 0 ? "none" : string.Join(", ", profile.Tags.Select(x => x.Tag).ToArray()))}"
            };

            if (admin)
            {
                lines.Add("Signals:");
                foreach (KeyValuePair<string, int> kvp in profile.Signals.OrderByDescending(x => x.Value).Take(10))
                    lines.Add($"- {kvp.Key}: {kvp.Value}");

                lines.Add($"Updated: {profile.UpdatedUtc}");
            }

            return string.Join("\n", lines.ToArray());
        }

        private string BuildTopText()
        {
            List<ReputationProfile> top = _data.Profiles.Values
                .OrderByDescending(x => x.ReputationScore)
                .Take(_config.Reporting.TopListLimit)
                .ToList();

            if (top.Count == 0)
                return "No reputation profiles yet.";

            List<string> lines = new List<string> { "Top reputation profiles:" };

            foreach (ReputationProfile p in top)
                lines.Add($"- {p.PlayerName} [{p.Title}] score={p.ReputationScore}");

            return string.Join("\n", lines.ToArray());
        }

        private string GetStatusText()
        {
            return
                "WorldMindReputationBrain status\n" +
                $"WorldMindV2 linked: {(WorldMindV2 != null ? "yes" : "no")}\n" +
                $"PlayerBrain linked: {(WorldMindPlayerBrain != null ? "yes" : "no")}\n" +
                $"AdminMind linked: {(WorldMindAdminMind != null ? "yes" : "no")}\n" +
                $"DiscordMind linked: {(WorldMindDiscordMind != null ? "yes" : "no")}\n" +
                $"Discord routing enabled: {_config.DiscordMindIntegration.Enabled}\n" +
                $"Profiles: {_data.Profiles.Count}\n" +
                $"Periodic evaluation: {_config.General.EnablePeriodicEvaluation}";
        }

        #endregion

        #region Helpers

        private BasePlayer GetCraftTaskOwner(ItemCraftTask task)
        {
            if (task == null) return null;

            object value = GetFieldOrProperty(task, "owner") ??
                           GetFieldOrProperty(task, "Owner") ??
                           GetFieldOrProperty(task, "player") ??
                           GetFieldOrProperty(task, "Player");

            return value as BasePlayer;
        }

        private object GetFieldOrProperty(object target, string name)
        {
            if (target == null || string.IsNullOrWhiteSpace(name)) return null;

            Type type = target.GetType();

            System.Reflection.FieldInfo field = type.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (field != null)
                return field.GetValue(target);

            System.Reflection.PropertyInfo property = type.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (property != null)
                return property.GetValue(target, null);

            return null;
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null || string.IsNullOrWhiteSpace(message)) return;
            player.ChatMessage($"<color=#d7b46a>[WorldMind Reputation]</color> {message}");
        }

        private bool HasAdmin(BasePlayer player)
        {
            if (player == null) return false;

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin))
                return true;

            Reply(player, "You do not have permission to use that command.");
            return false;
        }

        private bool CanUse(BasePlayer player)
        {
            if (player == null) return false;

            if (!_config.General.RequireUsePermission)
                return true;

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionUse))
                return true;

            Reply(player, "You do not have permission to view reputation.");
            return false;
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
                if (_config == null)
                    throw new Exception("Config was null.");

                _config.EnsureDefaults();
                // Owner-safe rule: do not write config after a normal load.
                // Existing owner edits, cleared lists, custom values, and ordering stay untouched.
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
                if (_data == null)
                    _data = new StoredData();

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

            [JsonProperty("Scoring")]
            public ScoringSettings Scoring = new ScoringSettings();

            [JsonProperty("Thresholds")]
            public ThresholdSettings Thresholds = new ThresholdSettings();

            [JsonProperty("Display")]
            public DisplaySettings Display = new DisplaySettings();

            [JsonProperty("Reporting")]
            public ReportingSettings Reporting = new ReportingSettings();

            [JsonProperty("WorldMind Integration")]
            public WorldMindIntegrationSettings WorldMindIntegration = new WorldMindIntegrationSettings();

            [JsonProperty("DiscordMind Integration")]
            public DiscordMindIntegrationSettings DiscordMindIntegration = new DiscordMindIntegrationSettings();

            public static PluginConfig Default()
            {
                PluginConfig config = new PluginConfig();
                config.EnsureDefaults();
                return config;
            }

            public void EnsureDefaults()
            {
                if (General == null) General = new GeneralSettings();
                if (Scoring == null) Scoring = new ScoringSettings();
                if (Thresholds == null) Thresholds = new ThresholdSettings();
                if (Display == null) Display = new DisplaySettings();
                if (Reporting == null) Reporting = new ReportingSettings();
                if (WorldMindIntegration == null) WorldMindIntegration = new WorldMindIntegrationSettings();
                if (DiscordMindIntegration == null) DiscordMindIntegration = new DiscordMindIntegrationSettings();
                if (DiscordMindIntegration.SignificantSignalTypes == null) DiscordMindIntegration.SignificantSignalTypes = new DiscordMindIntegrationSettings().SignificantSignalTypes;
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

            [JsonProperty("ShowReputationOnConnect")]
            public bool ShowReputationOnConnect = false;

            [JsonProperty("EnablePeriodicEvaluation")]
            public bool EnablePeriodicEvaluation = true;

            [JsonProperty("EvaluationIntervalMinutes")]
            public float EvaluationIntervalMinutes = 15f;
        }

        private class ScoringSettings
        {
            public int PlayerKillWeight = 20;
            public int NpcKillWeight = 8;
            public int GatherWeight = 1;
            public int BuildWeight = 3;
            public int CraftWeight = 1;
            public int CollectWeight = 1;
            public int DeathPenalty = 4;
        }

        private class ThresholdSettings
        {
            public int PvpMindedKills = 5;
            public int DeathProneDeaths = 8;
            public int NpcHunterKills = 10;
            public int FarmerGatherScore = 100;
            public int BuilderBuilds = 25;
            public int CrafterCrafts = 40;
        }

        private class DisplaySettings
        {
            public int MaxTitleLength = 40;
        }

        private class ReportingSettings
        {
            public int KeepRecentSignalsPerPlayer = 40;
            public int TopListLimit = 10;
        }

        private class WorldMindIntegrationSettings
        {
            public bool RecordEventsToWorldMind = true;
            public bool AllowWorldMindTitleGeneration = false;
        }

        private class DiscordMindIntegrationSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Channel Key")]
            public string ChannelKey = "reputation";

            [JsonProperty("Category")]
            public string Category = "reputation";

            [JsonProperty("Title Prefix")]
            public string TitlePrefix = "Reputation Shift";

            [JsonProperty("Send Signal Events")]
            public bool SendSignalEvents = true;

            [JsonProperty("Only Send Configured Signal Types")]
            public bool OnlySendConfiguredSignalTypes = true;

            [JsonProperty("Significant Signal Types")]
            public List<string> SignificantSignalTypes = new List<string>
            {
                "player_kill",
                "death",
                "npc_kill",
                "manual",
                "reputation_test"
            };

            [JsonProperty("Minimum Signal Amount To Post")]
            public int MinimumSignalAmountToPost = 1;

            [JsonProperty("Include Full Profile Json")]
            public bool IncludeFullProfileJson = false;

            [JsonProperty("Max Discord Message Length")]
            public int MaxDiscordMessageLength = 1600;

            [JsonProperty("Debug Discord Routing")]
            public bool DebugDiscordRouting = false;
        }

        private class StoredData
        {
            [JsonProperty("Profiles")]
            public Dictionary<string, ReputationProfile> Profiles = new Dictionary<string, ReputationProfile>();

            public void EnsureDefaults()
            {
                if (Profiles == null)
                    Profiles = new Dictionary<string, ReputationProfile>();
            }
        }

        public class ReputationProfile
        {
            public string PlayerId = "";
            public string PlayerName = "";
            public string Title = "Fresh Survivor";
            public string TitleSource = "automatic";
            public int ReputationScore = 0;
            public Dictionary<string, int> Signals = new Dictionary<string, int>();
            public List<ReputationTag> Tags = new List<ReputationTag>();
            public List<ReputationSignal> RecentSignals = new List<ReputationSignal>();
            public string CreatedUtc = "";
            public string LastSeenUtc = "";
            public string UpdatedUtc = "";
        }

        public class ReputationTag
        {
            public string Tag = "";
            public string Source = "";
            public string AddedUtc = "";
        }

        public class ReputationSignal
        {
            public string Type = "";
            public int Amount = 0;
            public string Notes = "";
            public string TimestampUtc = "";
        }

        #endregion
    }
}
