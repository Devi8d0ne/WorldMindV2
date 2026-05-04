using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindQuestBrain", "Devi8d0ne", "1.0.1")]
    [Description("Generic objective and mission layer for the WorldMind plugin ecosystem.")]
    public class WorldMindQuestBrain : RustPlugin
    {
        private const string PermissionAdmin = "worldmindquestbrain.admin";
        private const string PermissionUse = "worldmindquestbrain.use";
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
        [PluginReference] private Plugin WorldMindMapBrain;
        [PluginReference] private Plugin WorldMindItemBrain;
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
                Puts($"{MadeWithLoveTag} | Author: Devi8d0ne | Plugin: WorldMindQuestBrain");
            }

            timer.Every(Math.Max(60f, _config.General.AutoSaveSeconds), SaveData);

            if (_config.General.EnablePeriodicObjectiveCleanup)
                timer.Every(Math.Max(300f, _config.General.CleanupIntervalMinutes * 60f), CleanupExpiredObjectives);

            Puts("WorldMindQuestBrain loaded.");
        }

        private void Unload()
        {
            SaveData();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;

            if (_config.General.NotifyPlayerOfActiveObjectivesOnConnect)
            {
                timer.Once(5f, () =>
                {
                    if (player != null && player.IsConnected)
                        ShowPlayerObjectives(player);
                });
            }
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            BasePlayer player = entity as BasePlayer;
            if (player == null || item == null || item.info == null) return;

            AddProgress(player.UserIDString, "gather", item.info.shortname, item.amount, player.displayName);
        }

        private void OnCollectiblePickup(Item item, BasePlayer player)
        {
            if (player == null || item == null || item.info == null) return;

            AddProgress(player.UserIDString, "collect", item.info.shortname, item.amount, player.displayName);
        }

        private void OnItemCraftFinished(ItemCraftTask task, Item item)
        {
            if (task == null || item == null || item.info == null) return;

            BasePlayer owner = GetCraftTaskOwner(task);
            if (owner == null) return;

            AddProgress(owner.UserIDString, "craft", item.info.shortname, item.amount, owner.displayName);
        }

        private BasePlayer GetCraftTaskOwner(ItemCraftTask task)
        {
            if (task == null) return null;

            try
            {
                object value = GetFieldOrProperty(task, "owner");
                BasePlayer player = value as BasePlayer;
                if (player != null) return player;
            }
            catch { }

            try
            {
                object value = GetFieldOrProperty(task, "Owner");
                BasePlayer player = value as BasePlayer;
                if (player != null) return player;
            }
            catch { }

            try
            {
                object value = GetFieldOrProperty(task, "player");
                BasePlayer player = value as BasePlayer;
                if (player != null) return player;
            }
            catch { }

            try
            {
                object value = GetFieldOrProperty(task, "Player");
                BasePlayer player = value as BasePlayer;
                if (player != null) return player;
            }
            catch { }

            return null;
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

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;

            BasePlayer attacker = info.InitiatorPlayer;
            if (attacker == null) return;

            BasePlayer victimPlayer = entity as BasePlayer;
            if (victimPlayer != null && victimPlayer != attacker)
            {
                AddProgress(attacker.UserIDString, "kill_player", "player", 1, attacker.displayName);
                return;
            }

            if (entity is NPCPlayer || entity.ShortPrefabName.ToLowerInvariant().Contains("scientist"))
            {
                AddProgress(attacker.UserIDString, "kill_npc", entity.ShortPrefabName, 1, attacker.displayName);
            }
        }

        #endregion

        #region Commands

        [ChatCommand("quest")]
        private void CmdQuest(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (!CanUse(player)) return;

            if (args == null || args.Length == 0)
            {
                ShowPlayerObjectives(player);
                return;
            }

            string sub = args[0].ToLowerInvariant();

            if (sub == "list")
            {
                ShowPlayerObjectives(player);
                return;
            }

            if (sub == "abandon")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /quest abandon <objectiveId>");
                    return;
                }

                AbandonObjective(player, args[1]);
                return;
            }

            if (sub == "info")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /quest info <objectiveId>");
                    return;
                }

                ShowObjectiveInfo(player, args[1]);
                return;
            }

            if (sub == "ask")
            {
                string question = args.Length > 1 ? string.Join(" ", args.Skip(1).ToArray()) : "What should I work on next?";
                AskQuestQuestion(player, question);
                return;
            }

            ShowPlayerObjectives(player);
        }

        [ChatCommand("wmquest")]
        private void CmdAdminQuest(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!HasAdmin(player)) return;

            if (args == null || args.Length == 0)
            {
                Reply(player,
                    "WorldMindQuestBrain commands:\n" +
                    "/wmquest status\n" +
                    "/wmquest templates\n" +
                    "/wmquest active\n" +
                    "/wmquest create <title>\n" +
                    "/wmquest assign <playerId/name> <templateId/objectiveId>\n" +
                    "/wmquest complete <playerId/name> <objectiveId>\n" +
                    "/wmquest generate <playerId/name> <notes>\n" +
                    "/wmquest recap <playerId/name>\n" +
                    "/wmquest reload\n" +
                    "/wmquest save\n" +
                    "/wmquest clear");
                return;
            }

            string sub = args[0].ToLowerInvariant();

            if (sub == "status")
            {
                Reply(player, GetStatusText());
                return;
            }

            if (sub == "templates")
            {
                Reply(player, BuildTemplatesText());
                return;
            }

            if (sub == "active")
            {
                Reply(player, BuildActiveText());
                return;
            }

            if (sub == "create")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmquest create <title>");
                    return;
                }

                string title = string.Join(" ", args.Skip(1).ToArray());
                QuestTemplate template = CreateManualTemplate(title, player.displayName);
                _data.Templates[template.TemplateId] = template;
                SaveData();
                Reply(player, $"Template created: {template.TemplateId}");
                return;
            }

            if (sub == "assign")
            {
                if (args.Length < 3)
                {
                    Reply(player, "Usage: /wmquest assign <playerId/name> <templateId/objectiveId>");
                    return;
                }

                AssignToPlayerCommand(player, args[1], args[2]);
                return;
            }

            if (sub == "complete")
            {
                if (args.Length < 3)
                {
                    Reply(player, "Usage: /wmquest complete <playerId/name> <objectiveId>");
                    return;
                }

                CompleteObjectiveCommand(player, args[1], args[2]);
                return;
            }

            if (sub == "generate")
            {
                if (args.Length < 3)
                {
                    Reply(player, "Usage: /wmquest generate <playerId/name> <notes>");
                    return;
                }

                string target = args[1];
                string notes = string.Join(" ", args.Skip(2).ToArray());
                GenerateObjectiveForPlayer(player, target, notes);
                return;
            }

            if (sub == "recap")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmquest recap <playerId/name>");
                    return;
                }

                GenerateQuestRecap(player, args[1]);
                return;
            }

            if (sub == "reload")
            {
                LoadPluginConfig();
                LoadData();
                Reply(player, "WorldMindQuestBrain reloaded.");
                return;
            }

            if (sub == "save")
            {
                SaveData();
                Reply(player, "WorldMindQuestBrain data saved.");
                return;
            }

            if (sub == "clear")
            {
                _data = new StoredData();
                SeedDefaultTemplates();
                SaveData();
                Reply(player, "WorldMindQuestBrain data cleared and default templates restored.");
                return;
            }

            Reply(player, "Unknown command. Use /wmquest for help.");
        }

        [ConsoleCommand("worldmindquest.status")]
        private void ConsoleStatus(ConsoleSystem.Arg arg)
        {
            arg?.ReplyWith(GetStatusText());
        }

        [ConsoleCommand("worldmindquest.reload")]
        private void ConsoleReload(ConsoleSystem.Arg arg)
        {
            LoadPluginConfig();
            LoadData();
            arg?.ReplyWith("WorldMindQuestBrain reloaded.");
        }

        #endregion

        #region Public hooks

        private object WorldMindQuestBrain_CreateObjective(Dictionary<string, object> packet)
        {
            QuestTemplate template = TemplateFromPacket(packet);
            _data.Templates[template.TemplateId] = template;
            SaveData();
            return template.TemplateId;
        }

        private object WorldMindQuestBrain_AssignObjective(string playerId, string playerName, string templateId)
        {
            ActiveObjective objective = AssignObjective(playerId, playerName, templateId, "external-hook");
            return objective == null ? null : objective.ObjectiveId;
        }

        private object WorldMindQuestBrain_AddProgress(string playerId, string progressType, string target, int amount)
        {
            return AddProgress(playerId, progressType, target, amount, "");
        }

        private object WorldMindQuestBrain_CompleteObjective(string playerId, string objectiveId, string reason)
        {
            return CompleteObjective(playerId, objectiveId, reason);
        }

        private object WorldMindQuestBrain_GetPlayerObjectives(string playerId)
        {
            return GetPlayerObjectives(playerId);
        }

        private object WorldMindQuestBrain_GetQuestSummary(string playerId)
        {
            return BuildPlayerQuestSummary(playerId);
        }

        #endregion

        #region Core

        private void SeedDefaultTemplates()
        {
            if (_data.Templates.Count > 0) return;

            AddDefaultTemplate("gather_wood", "Gather wood", "Gather 5,000 wood.", "gather", "wood", 5000, "resource");
            AddDefaultTemplate("gather_stones", "Gather stones", "Gather 5,000 stones.", "gather", "stones", 5000, "resource");
            AddDefaultTemplate("craft_bandages", "Prepare supplies", "Craft 5 bandages.", "craft", "bandage", 5, "survival");
            AddDefaultTemplate("kill_scientists", "Clear hostile NPCs", "Kill 3 hostile NPCs.", "kill_npc", "scientist", 3, "combat");
            AddDefaultTemplate("survive_objective", "Stay alive", "Stay active and make progress without requiring a specific server system.", "generic", "survive", 1, "survival");
        }

        private void AddDefaultTemplate(string id, string title, string description, string progressType, string target, int amount, string category)
        {
            _data.Templates[id] = new QuestTemplate
            {
                TemplateId = id,
                Title = title,
                Description = description,
                Category = category,
                ProgressType = progressType,
                Target = target,
                RequiredAmount = amount,
                TimeLimitMinutes = _config.Objectives.DefaultTimeLimitMinutes,
                CreatedBy = "WorldMindQuestBrain default"
            };
        }

        private QuestTemplate CreateManualTemplate(string title, string adminName)
        {
            string id = $"manual_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{UnityEngine.Random.Range(1000, 9999)}";

            return new QuestTemplate
            {
                TemplateId = id,
                Title = title,
                Description = title,
                Category = "manual",
                ProgressType = "manual",
                Target = "manual",
                RequiredAmount = 1,
                TimeLimitMinutes = _config.Objectives.DefaultTimeLimitMinutes,
                CreatedBy = adminName ?? "admin"
            };
        }

        private ActiveObjective AssignObjective(string playerId, string playerName, string templateId, string source)
        {
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(templateId))
                return null;

            QuestTemplate template;
            if (!_data.Templates.TryGetValue(templateId, out template))
                return null;

            string objectiveId = $"Q-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{UnityEngine.Random.Range(1000, 9999)}";

            ActiveObjective objective = new ActiveObjective
            {
                ObjectiveId = objectiveId,
                TemplateId = template.TemplateId,
                PlayerId = playerId,
                PlayerName = playerName ?? "",
                Title = template.Title,
                Description = template.Description,
                Category = template.Category,
                ProgressType = template.ProgressType,
                Target = template.Target,
                RequiredAmount = template.RequiredAmount,
                CurrentAmount = 0,
                AssignedUtc = DateTime.UtcNow.ToString("o"),
                ExpiresUtc = template.TimeLimitMinutes <= 0 ? "" : DateTime.UtcNow.AddMinutes(template.TimeLimitMinutes).ToString("o"),
                Status = "active",
                Source = source ?? ""
            };

            _data.ActiveObjectives[objective.ObjectiveId] = objective;

            RecordWorldMindEvent("objective_assigned", objective);
            SaveData();

            BasePlayer player = FindPlayerById(playerId);
            if (player != null)
                Reply(player, $"New objective: {objective.Title}\n{objective.Description}");

            return objective;
        }

        private bool AddProgress(string playerId, string progressType, string target, int amount, string playerName)
        {
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(progressType) || amount <= 0)
                return false;

            bool changed = false;
            string lowerType = progressType.ToLowerInvariant();
            string lowerTarget = (target ?? "").ToLowerInvariant();

            foreach (ActiveObjective objective in _data.ActiveObjectives.Values.ToList())
            {
                if (objective.PlayerId != playerId || objective.Status != "active") continue;
                if (!ProgressMatches(objective, lowerType, lowerTarget)) continue;

                objective.CurrentAmount += amount;
                objective.LastProgressUtc = DateTime.UtcNow.ToString("o");
                if (!string.IsNullOrWhiteSpace(playerName)) objective.PlayerName = playerName;

                changed = true;

                if (objective.CurrentAmount >= objective.RequiredAmount)
                    CompleteObjective(playerId, objective.ObjectiveId, "progress_complete");
            }

            if (changed)
                SaveData();

            return changed;
        }

        private bool ProgressMatches(ActiveObjective objective, string progressType, string target)
        {
            if (objective == null) return false;

            string objType = (objective.ProgressType ?? "").ToLowerInvariant();
            string objTarget = (objective.Target ?? "").ToLowerInvariant();

            if (objType != progressType) return false;
            if (string.IsNullOrWhiteSpace(objTarget) || objTarget == "any") return true;
            if (target == objTarget) return true;
            if (target.Contains(objTarget)) return true;
            if (objTarget.Contains(target)) return true;

            return false;
        }

        private bool CompleteObjective(string playerId, string objectiveId, string reason)
        {
            ActiveObjective objective;
            if (!_data.ActiveObjectives.TryGetValue(objectiveId, out objective))
                return false;

            if (!string.Equals(objective.PlayerId, playerId, StringComparison.OrdinalIgnoreCase))
                return false;

            objective.Status = "completed";
            objective.CompletedUtc = DateTime.UtcNow.ToString("o");
            objective.CompletionReason = reason ?? "";

            _data.CompletedObjectives[objective.ObjectiveId] = objective;
            _data.ActiveObjectives.Remove(objective.ObjectiveId);

            RecordWorldMindEvent("objective_completed", objective);
            SaveData();

            BasePlayer player = FindPlayerById(playerId);
            if (player != null)
                Reply(player, $"Objective complete: {objective.Title}");

            if (_config.Discord.SendQuestCompletionsToDiscord && WorldMindDiscordMind != null)
                WorldMindDiscordMind.Call("WorldMindDiscordMind_SendEventSummary", $"{objective.PlayerName} completed objective: {objective.Title}");

            return true;
        }

        private void AbandonObjective(BasePlayer player, string objectiveId)
        {
            ActiveObjective objective;
            if (!_data.ActiveObjectives.TryGetValue(objectiveId, out objective) || objective.PlayerId != player.UserIDString)
            {
                Reply(player, "Objective not found.");
                return;
            }

            objective.Status = "abandoned";
            objective.CompletedUtc = DateTime.UtcNow.ToString("o");
            objective.CompletionReason = "abandoned";

            _data.CompletedObjectives[objective.ObjectiveId] = objective;
            _data.ActiveObjectives.Remove(objective.ObjectiveId);
            RecordWorldMindEvent("objective_abandoned", objective);
            SaveData();

            Reply(player, $"Objective abandoned: {objective.Title}");
        }

        private void CleanupExpiredObjectives()
        {
            DateTime now = DateTime.UtcNow;
            int expired = 0;

            foreach (ActiveObjective objective in _data.ActiveObjectives.Values.ToList())
            {
                if (string.IsNullOrWhiteSpace(objective.ExpiresUtc)) continue;

                DateTime expires;
                if (!DateTime.TryParse(objective.ExpiresUtc, out expires)) continue;
                if (expires > now) continue;

                objective.Status = "expired";
                objective.CompletedUtc = now.ToString("o");
                objective.CompletionReason = "expired";

                _data.CompletedObjectives[objective.ObjectiveId] = objective;
                _data.ActiveObjectives.Remove(objective.ObjectiveId);
                expired++;
            }

            if (expired > 0)
            {
                RecordWorldMindEvent("objectives_expired", new Dictionary<string, object> { ["count"] = expired });
                SaveData();
            }
        }

        private void AssignToPlayerCommand(BasePlayer admin, string targetQuery, string templateId)
        {
            BasePlayer target = FindPlayer(targetQuery);
            string playerId = target == null ? targetQuery : target.UserIDString;
            string playerName = target == null ? targetQuery : target.displayName;

            ActiveObjective objective = AssignObjective(playerId, playerName, templateId, $"admin:{admin.displayName}");

            if (objective == null)
            {
                Reply(admin, "Could not assign objective. Check player and template ID.");
                return;
            }

            Reply(admin, $"Assigned {objective.Title} to {playerName}. Objective ID: {objective.ObjectiveId}");
        }

        private void CompleteObjectiveCommand(BasePlayer admin, string targetQuery, string objectiveId)
        {
            BasePlayer target = FindPlayer(targetQuery);
            string playerId = target == null ? targetQuery : target.UserIDString;

            bool ok = CompleteObjective(playerId, objectiveId, $"manual_admin:{admin.displayName}");
            Reply(admin, ok ? "Objective completed." : "Objective not found or player mismatch.");
        }

        #endregion

        #region WorldMind generation

        private void GenerateObjectiveForPlayer(BasePlayer admin, string targetQuery, string notes)
        {
            BasePlayer target = FindPlayer(targetQuery);
            string playerId = target == null ? targetQuery : target.UserIDString;
            string playerName = target == null ? targetQuery : target.displayName;

            if (WorldMindV2 == null)
            {
                Reply(admin, "WorldMindV2 is not loaded.");
                return;
            }

            string prompt =
                "You are WorldMind creating one generic Rust server objective.\n" +
                "Do not assume VIP, Discord, WarMode, kits, homes, teleport, factions, custom economy, rewards, or server-specific systems.\n" +
                "Return only compact JSON with keys: title, description, category, progressType, target, requiredAmount.\n" +
                "Supported progressType values: gather, collect, craft, kill_npc, kill_player, manual, generic.\n" +
                "Use generic Rust item shortnames when possible. Keep the objective achievable.\n" +
                $"Player: {playerName} ({playerId})\n" +
                $"Admin notes: {notes}\n" +
                $"Existing quest summary: {BuildPlayerQuestSummary(playerId)}";

            try
            {
                object result = WorldMindV2.Call("WorldMind_AskText", prompt, "WorldMindQuestBrain", "objective_generation");
                string text = result == null ? "" : result.ToString();

                QuestTemplate template = TryParseGeneratedTemplate(text, admin.displayName);
                if (template == null)
                {
                    Reply(admin, "WorldMind did not return valid objective JSON.");
                    return;
                }

                _data.Templates[template.TemplateId] = template;
                ActiveObjective objective = AssignObjective(playerId, playerName, template.TemplateId, $"worldmind_generated_by:{admin.displayName}");
                Reply(admin, objective == null ? "Generated objective but assignment failed." : $"Generated and assigned: {objective.Title} ({objective.ObjectiveId})");
            }
            catch (Exception ex)
            {
                Reply(admin, $"WorldMind objective generation failed: {ex.Message}");
            }
        }

        private QuestTemplate TryParseGeneratedTemplate(string text, string adminName)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            try
            {
                int start = text.IndexOf('{');
                int end = text.LastIndexOf('}');
                if (start >= 0 && end > start)
                    text = text.Substring(start, end - start + 1);

                GeneratedObjective gen = JsonConvert.DeserializeObject<GeneratedObjective>(text);
                if (gen == null || string.IsNullOrWhiteSpace(gen.title))
                    return null;

                int required = gen.requiredAmount <= 0 ? 1 : gen.requiredAmount;

                return new QuestTemplate
                {
                    TemplateId = $"ai_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{UnityEngine.Random.Range(1000, 9999)}",
                    Title = gen.title.Trim(),
                    Description = string.IsNullOrWhiteSpace(gen.description) ? gen.title.Trim() : gen.description.Trim(),
                    Category = string.IsNullOrWhiteSpace(gen.category) ? "generated" : gen.category.Trim(),
                    ProgressType = string.IsNullOrWhiteSpace(gen.progressType) ? "manual" : gen.progressType.Trim().ToLowerInvariant(),
                    Target = string.IsNullOrWhiteSpace(gen.target) ? "any" : gen.target.Trim().ToLowerInvariant(),
                    RequiredAmount = required,
                    TimeLimitMinutes = _config.Objectives.DefaultTimeLimitMinutes,
                    CreatedBy = $"WorldMind via {adminName}"
                };
            }
            catch
            {
                return null;
            }
        }

        private void GenerateQuestRecap(BasePlayer admin, string targetQuery)
        {
            if (WorldMindV2 == null)
            {
                Reply(admin, "WorldMindV2 is not loaded.");
                return;
            }

            BasePlayer target = FindPlayer(targetQuery);
            string playerId = target == null ? targetQuery : target.UserIDString;
            string playerName = target == null ? targetQuery : target.displayName;

            string prompt =
                "You are WorldMind creating a concise admin-facing quest progress recap for a generic Rust server.\n" +
                "Do not assume VIP, Discord, WarMode, kits, homes, teleport, custom economy, factions, or rewards.\n" +
                "Summarize active/completed objectives only from the data.\n" +
                $"Player: {playerName} ({playerId})\n" +
                $"Quest data:\n{JsonConvert.SerializeObject(GetPlayerQuestPacket(playerId), Formatting.Indented)}";

            try
            {
                object result = WorldMindV2.Call("WorldMind_AskText", prompt, "WorldMindQuestBrain", "quest_recap");
                string message = result == null ? "" : result.ToString();
                Reply(admin, string.IsNullOrWhiteSpace(message) ? BuildPlayerQuestSummary(playerId) : message);
            }
            catch (Exception ex)
            {
                Reply(admin, $"WorldMind quest recap failed: {ex.Message}");
            }
        }

        private void AskQuestQuestion(BasePlayer player, string question)
        {
            if (WorldMindV2 == null)
            {
                Reply(player, BuildPlayerQuestSummary(player.UserIDString));
                return;
            }

            string prompt =
                "You are WorldMind answering a player's question about their generic Rust server objectives.\n" +
                "Do not mention VIP, Discord, WarMode, kits, homes, teleport, custom economy, factions, or rewards unless present in the objective data.\n" +
                "Keep it concise and useful.\n" +
                $"Player question: {question}\n" +
                $"Quest data:\n{JsonConvert.SerializeObject(GetPlayerQuestPacket(player.UserIDString), Formatting.Indented)}";

            try
            {
                object result = WorldMindV2.Call("WorldMind_AskText", prompt, "WorldMindQuestBrain", "player_quest_question");
                string message = result == null ? "" : result.ToString();
                Reply(player, string.IsNullOrWhiteSpace(message) ? BuildPlayerQuestSummary(player.UserIDString) : message);
            }
            catch
            {
                Reply(player, BuildPlayerQuestSummary(player.UserIDString));
            }
        }

        private void RecordWorldMindEvent(string eventType, object payload)
        {
            if (!_config.WorldMindIntegration.RecordEventsToWorldMind || WorldMindV2 == null) return;

            try
            {
                Dictionary<string, object> packet = new Dictionary<string, object>
                {
                    ["plugin"] = "WorldMindQuestBrain",
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

        #region Formatting/reporting

        private void ShowPlayerObjectives(BasePlayer player)
        {
            List<ActiveObjective> objectives = GetPlayerObjectives(player.UserIDString);

            if (objectives.Count == 0)
            {
                Reply(player, "You have no active objectives.");
                return;
            }

            List<string> lines = new List<string> { "Active objectives:" };

            foreach (ActiveObjective objective in objectives.Take(_config.Objectives.MaxObjectivesShownToPlayer))
            {
                lines.Add($"- {objective.ObjectiveId}: {objective.Title} [{objective.CurrentAmount}/{objective.RequiredAmount}]");
            }

            Reply(player, string.Join("\n", lines.ToArray()));
        }

        private void ShowObjectiveInfo(BasePlayer player, string objectiveId)
        {
            ActiveObjective objective = GetPlayerObjectives(player.UserIDString).FirstOrDefault(x => string.Equals(x.ObjectiveId, objectiveId, StringComparison.OrdinalIgnoreCase));

            if (objective == null)
            {
                Reply(player, "Objective not found.");
                return;
            }

            Reply(player,
                $"{objective.Title}\n" +
                $"{objective.Description}\n" +
                $"Progress: {objective.CurrentAmount}/{objective.RequiredAmount}\n" +
                $"Type: {objective.ProgressType}\n" +
                $"Target: {objective.Target}\n" +
                $"Expires: {(string.IsNullOrWhiteSpace(objective.ExpiresUtc) ? "none" : objective.ExpiresUtc)}");
        }

        private string BuildTemplatesText()
        {
            if (_data.Templates.Count == 0) return "No templates found.";

            List<string> lines = new List<string> { "Quest templates:" };

            foreach (QuestTemplate template in _data.Templates.Values.OrderBy(x => x.TemplateId).Take(30))
            {
                lines.Add($"- {template.TemplateId}: {template.Title} | {template.ProgressType}:{template.Target} [{template.RequiredAmount}]");
            }

            return string.Join("\n", lines.ToArray());
        }

        private string BuildActiveText()
        {
            if (_data.ActiveObjectives.Count == 0) return "No active objectives.";

            List<string> lines = new List<string> { "Active objectives:" };

            foreach (ActiveObjective objective in _data.ActiveObjectives.Values.OrderByDescending(x => x.AssignedUtc).Take(30))
            {
                lines.Add($"- {objective.ObjectiveId}: {objective.PlayerName} | {objective.Title} [{objective.CurrentAmount}/{objective.RequiredAmount}]");
            }

            return string.Join("\n", lines.ToArray());
        }

        private string BuildPlayerQuestSummary(string playerId)
        {
            QuestPlayerPacket packet = GetPlayerQuestPacket(playerId);

            List<string> lines = new List<string>
            {
                $"Quest summary for {packet.PlayerName} ({packet.PlayerId})",
                $"Active: {packet.Active.Count}",
                $"Completed: {packet.Completed.Count}",
                "Active objectives:"
            };

            if (packet.Active.Count == 0)
            {
                lines.Add("- none");
            }
            else
            {
                foreach (ActiveObjective objective in packet.Active.Take(8))
                    lines.Add($"- {objective.Title}: {objective.CurrentAmount}/{objective.RequiredAmount}");
            }

            return string.Join("\n", lines.ToArray());
        }

        private QuestPlayerPacket GetPlayerQuestPacket(string playerId)
        {
            List<ActiveObjective> active = _data.ActiveObjectives.Values
                .Where(x => x.PlayerId == playerId && x.Status == "active")
                .OrderByDescending(x => x.AssignedUtc)
                .ToList();

            List<ActiveObjective> completed = _data.CompletedObjectives.Values
                .Where(x => x.PlayerId == playerId)
                .OrderByDescending(x => x.CompletedUtc)
                .Take(20)
                .ToList();

            string name = "";
            if (active.Count > 0) name = active[0].PlayerName;
            else if (completed.Count > 0) name = completed[0].PlayerName;

            return new QuestPlayerPacket
            {
                PlayerId = playerId,
                PlayerName = name,
                Active = active,
                Completed = completed
            };
        }

        private List<ActiveObjective> GetPlayerObjectives(string playerId)
        {
            return _data.ActiveObjectives.Values
                .Where(x => x.PlayerId == playerId && x.Status == "active")
                .OrderByDescending(x => x.AssignedUtc)
                .ToList();
        }

        private string GetStatusText()
        {
            return
                "WorldMindQuestBrain status\n" +
                $"WorldMindV2 linked: {(WorldMindV2 != null ? "yes" : "no")}\n" +
                $"PlayerBrain linked: {(WorldMindPlayerBrain != null ? "yes" : "no")}\n" +
                $"MapBrain linked: {(WorldMindMapBrain != null ? "yes" : "no")}\n" +
                $"ItemBrain linked: {(WorldMindItemBrain != null ? "yes" : "no")}\n" +
                $"Templates: {_data.Templates.Count}\n" +
                $"Active objectives: {_data.ActiveObjectives.Count}\n" +
                $"Completed/closed objectives: {_data.CompletedObjectives.Count}";
        }

        #endregion

        #region Helpers

        private QuestTemplate TemplateFromPacket(Dictionary<string, object> packet)
        {
            if (packet == null) packet = new Dictionary<string, object>();

            string id = GetString(packet, "templateId", "");
            if (string.IsNullOrWhiteSpace(id))
                id = $"external_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{UnityEngine.Random.Range(1000, 9999)}";

            return new QuestTemplate
            {
                TemplateId = id,
                Title = GetString(packet, "title", "Objective"),
                Description = GetString(packet, "description", "Complete the objective."),
                Category = GetString(packet, "category", "external"),
                ProgressType = GetString(packet, "progressType", "manual").ToLowerInvariant(),
                Target = GetString(packet, "target", "manual").ToLowerInvariant(),
                RequiredAmount = GetInt(packet, "requiredAmount", 1),
                TimeLimitMinutes = GetInt(packet, "timeLimitMinutes", _config.Objectives.DefaultTimeLimitMinutes),
                CreatedBy = GetString(packet, "createdBy", "external")
            };
        }

        private string GetString(Dictionary<string, object> packet, string key, string fallback)
        {
            object value;
            return packet != null && packet.TryGetValue(key, out value) && value != null ? value.ToString() : fallback;
        }

        private int GetInt(Dictionary<string, object> packet, string key, int fallback)
        {
            object value;
            if (packet == null || !packet.TryGetValue(key, out value) || value == null) return fallback;

            int parsed;
            return int.TryParse(value.ToString(), out parsed) ? parsed : fallback;
        }

        private BasePlayer FindPlayer(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null) continue;
                if (player.UserIDString == query) return player;
                if (!string.IsNullOrWhiteSpace(player.displayName) && player.displayName.ToLowerInvariant().Contains(query.ToLowerInvariant()))
                    return player;
            }

            return null;
        }

        private BasePlayer FindPlayerById(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return null;

            return BasePlayer.activePlayerList.FirstOrDefault(x => x != null && x.UserIDString == playerId);
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null || string.IsNullOrWhiteSpace(message)) return;
            player.ChatMessage($"<color=#d7b46a>[WorldMind QuestBrain]</color> {message}");
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

            Reply(player, "You do not have permission to use quests.");
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
                SeedDefaultTemplates();
            }
            catch
            {
                _data = new StoredData();
                SeedDefaultTemplates();
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

            [JsonProperty("Objectives")]
            public ObjectiveSettings Objectives = new ObjectiveSettings();

            [JsonProperty("WorldMind Integration")]
            public WorldMindIntegrationSettings WorldMindIntegration = new WorldMindIntegrationSettings();

            [JsonProperty("Discord Integration")]
            public DiscordSettings Discord = new DiscordSettings();

            public static PluginConfig Default()
            {
                PluginConfig config = new PluginConfig();
                config.EnsureDefaults();
                return config;
            }

            public void EnsureDefaults()
            {
                if (General == null) General = new GeneralSettings();
                if (Objectives == null) Objectives = new ObjectiveSettings();
                if (WorldMindIntegration == null) WorldMindIntegration = new WorldMindIntegrationSettings();
                if (Discord == null) Discord = new DiscordSettings();
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

            [JsonProperty("NotifyPlayerOfActiveObjectivesOnConnect")]
            public bool NotifyPlayerOfActiveObjectivesOnConnect = true;

            [JsonProperty("EnablePeriodicObjectiveCleanup")]
            public bool EnablePeriodicObjectiveCleanup = true;

            [JsonProperty("CleanupIntervalMinutes")]
            public float CleanupIntervalMinutes = 10f;
        }

        private class ObjectiveSettings
        {
            [JsonProperty("DefaultTimeLimitMinutes")]
            public int DefaultTimeLimitMinutes = 1440;

            [JsonProperty("MaxActiveObjectivesPerPlayer")]
            public int MaxActiveObjectivesPerPlayer = 5;

            [JsonProperty("MaxObjectivesShownToPlayer")]
            public int MaxObjectivesShownToPlayer = 8;
        }

        private class WorldMindIntegrationSettings
        {
            [JsonProperty("RecordEventsToWorldMind")]
            public bool RecordEventsToWorldMind = true;
        }

        private class DiscordSettings
        {
            [JsonProperty("SendQuestCompletionsToDiscord")]
            public bool SendQuestCompletionsToDiscord = false;
        }

        private class StoredData
        {
            [JsonProperty("Templates")]
            public Dictionary<string, QuestTemplate> Templates = new Dictionary<string, QuestTemplate>();

            [JsonProperty("ActiveObjectives")]
            public Dictionary<string, ActiveObjective> ActiveObjectives = new Dictionary<string, ActiveObjective>();

            [JsonProperty("CompletedObjectives")]
            public Dictionary<string, ActiveObjective> CompletedObjectives = new Dictionary<string, ActiveObjective>();

            public void EnsureDefaults()
            {
                if (Templates == null) Templates = new Dictionary<string, QuestTemplate>();
                if (ActiveObjectives == null) ActiveObjectives = new Dictionary<string, ActiveObjective>();
                if (CompletedObjectives == null) CompletedObjectives = new Dictionary<string, ActiveObjective>();
            }
        }

        public class QuestTemplate
        {
            public string TemplateId = "";
            public string Title = "";
            public string Description = "";
            public string Category = "";
            public string ProgressType = "";
            public string Target = "";
            public int RequiredAmount = 1;
            public int TimeLimitMinutes = 1440;
            public string CreatedBy = "";
        }

        public class ActiveObjective
        {
            public string ObjectiveId = "";
            public string TemplateId = "";
            public string PlayerId = "";
            public string PlayerName = "";
            public string Title = "";
            public string Description = "";
            public string Category = "";
            public string ProgressType = "";
            public string Target = "";
            public int RequiredAmount = 1;
            public int CurrentAmount = 0;
            public string AssignedUtc = "";
            public string LastProgressUtc = "";
            public string ExpiresUtc = "";
            public string CompletedUtc = "";
            public string Status = "active";
            public string CompletionReason = "";
            public string Source = "";
        }

        private class GeneratedObjective
        {
            public string title;
            public string description;
            public string category;
            public string progressType;
            public string target;
            public int requiredAmount;
        }

        public class QuestPlayerPacket
        {
            public string PlayerId = "";
            public string PlayerName = "";
            public List<ActiveObjective> Active = new List<ActiveObjective>();
            public List<ActiveObjective> Completed = new List<ActiveObjective>();
        }

        #endregion
    }
}
