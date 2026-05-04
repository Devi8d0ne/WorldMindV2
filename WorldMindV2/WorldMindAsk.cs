using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindAsk", "Devi8d0ne", "1.0.0")]
    [Description("Player-facing WorldMind companion plugin. Adds a clean /ask command that asks WorldMindV2 without hardcoded server-specific lingo.")]
    public class WorldMindAsk : RustPlugin
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

        private const string PermissionUse = "worldmindask.use";
        private const string PermissionAdmin = "worldmindask.admin";

        [PluginReference] private Plugin WorldMindV2;

        private PluginConfig _config;
        private StoredData _data;
        private readonly Dictionary<string, double> _cooldowns = new Dictionary<string, double>();

        #region Oxide Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermissionUse, this);
            permission.RegisterPermission(PermissionAdmin, this);
            LoadConfigValues();
            LoadData();
            PrintWarning(DV8DAsciiTag);
            PrintWarning(MadeWithLoveTag + " | WorldMindAsk loaded. Command: /" + _config.Command.CommandName);
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
                if (_config == null)
                    throw new Exception("Config was null after read.");

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
                if (_data == null)
                    _data = new StoredData();
            }
            catch (Exception ex)
            {
                PrintError("Data read failed. Existing data JSON was NOT overwritten. Runtime data is empty for this session only. Error: " + ex.Message);
                _data = new StoredData();
            }

            if (_data.PlayerQuestionCounts == null) _data.PlayerQuestionCounts = new Dictionary<string, int>();
            if (_data.RecentQuestions == null) _data.RecentQuestions = new List<QuestionLogRecord>();
        }

        private void SaveData()
        {
            if (_data == null) return;
            Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        #endregion

        #region Commands

        [ChatCommand("ask")]
        private void CmdMind(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (!_config.Command.Enabled)
            {
                Reply(player, "WorldMind ask is disabled.");
                return;
            }

            if (_config.Command.RequirePermission && !HasUsePermission(player))
            {
                Reply(player, "You do not have permission to use WorldMind.");
                return;
            }

            if (args == null || args.Length == 0)
            {
                Reply(player, BuildHelpText(player));
                return;
            }

            string sub = args[0].ToLowerInvariant();

            if (sub == "help")
            {
                Reply(player, BuildHelpText(player));
                return;
            }

            if (sub == "status")
            {
                if (!IsAdmin(player))
                {
                    Reply(player, "You do not have permission to view WorldMind ask status.");
                    return;
                }

                Reply(player, BuildStatusText());
                return;
            }

            if (sub == "reload")
            {
                if (!IsAdmin(player))
                {
                    Reply(player, "You do not have permission to reload WorldMind ask.");
                    return;
                }

                LoadConfigValues();
                LoadData();
                Reply(player, "WorldMindAsk config and data reloaded.");
                return;
            }

            string question = string.Join(" ", args);
            AskWorldMind(player, question);
        }

        [ConsoleCommand("worldmindask.status")]
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

        [ConsoleCommand("worldmindask.reload")]
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
            arg.ReplyWith("WorldMindAsk config and data reloaded.");
        }

        #endregion

        #region Ask Flow

        private void AskWorldMind(BasePlayer player, string question)
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                Reply(player, BuildHelpText(player));
                return;
            }

            question = question.Trim();
            if (question.Length > _config.Limits.MaxQuestionCharacters)
                question = question.Substring(0, _config.Limits.MaxQuestionCharacters);

            if (!CanAsk(player))
            {
                Reply(player, _config.Messages.CooldownMessage);
                return;
            }

            if (WorldMindV2 == null)
            {
                Reply(player, "WorldMindV2 is not loaded. Load WorldMindV2.cs first.");
                return;
            }

            Dictionary<string, object> request = BuildWorldMindRequest(player, question);
            Action<string> callback = answer =>
            {
                if (player == null || !player.IsConnected) return;

                string clean = CleanAnswer(answer);
                if (string.IsNullOrEmpty(clean))
                    clean = _config.Messages.EmptyAnswerMessage;

                Reply(player, _config.Messages.AnswerPrefix + clean);
            };

            object called = WorldMindV2.Call("WorldMind_AskText", request, callback);
            if (called == null)
            {
                Reply(player, "WorldMindV2 did not answer. Make sure the base plugin is version 2.0.3 or newer.");
                return;
            }

            RecordQuestion(player, question);

            if (_config.WorldMind.RecordQuestionEvents)
            {
                Dictionary<string, object> truth = new Dictionary<string, object>
                {
                    ["question"] = question,
                    ["source"] = Name,
                    ["position"] = FormatPosition(player.transform.position)
                };
                WorldMindV2.Call("WorldMind_RecordEvent", Name, "ask_question", player.UserIDString, truth);
            }
        }

        private Dictionary<string, object> BuildWorldMindRequest(BasePlayer player, string question)
        {
            Dictionary<string, object> truth = new Dictionary<string, object>
            {
                ["question"] = question,
                ["command"] = "/" + _config.Command.CommandName,
                ["playerIsAdmin"] = IsAdmin(player),
                ["playerId"] = player.UserIDString,
                ["playerName"] = player.displayName
            };

            if (_config.Context.IncludePosition)
            {
                truth["position"] = FormatPosition(player.transform.position);
                object location = WorldMindV2 == null ? null : WorldMindV2.Call("WorldMind_DescribeLocation", player.transform.position);
                if (location != null) truth["locationDescription"] = Convert.ToString(location);
            }

            if (_config.Context.IncludeBasicPlayerState)
            {
                truth["health"] = Math.Round(player.health, 1);
                truth["isSleeping"] = player.IsSleeping();
                truth["isWounded"] = player.IsWounded();
                truth["isConnected"] = player.IsConnected;
            }

            if (_config.Context.IncludeHeldItem)
            {
                Item held = player.GetActiveItem();
                if (held != null && held.info != null)
                {
                    truth["heldItemShortname"] = held.info.shortname;
                    truth["heldItemName"] = held.info.displayName == null ? held.info.shortname : held.info.displayName.english;
                }
            }

            if (_config.WorldMind.ExtraInstructions.Count > 0)
                truth["askInstructions"] = string.Join("\n", _config.WorldMind.ExtraInstructions.ToArray());

            return new Dictionary<string, object>
            {
                ["Plugin"] = Name,
                ["EventType"] = "server_ask_question",
                ["PlayerId"] = player.UserIDString,
                ["PlayerName"] = player.displayName,
                ["Tone"] = _config.WorldMind.Tone,
                ["Urgency"] = 1,
                ["Truth"] = truth
            };
        }

        #endregion

        #region Helpers

        private bool CanAsk(BasePlayer player)
        {
            if (player == null) return false;
            if (IsAdmin(player) && _config.Limits.AdminBypassCooldown) return true;

            double now = Interface.Oxide.Now;
            double last;
            if (_cooldowns.TryGetValue(player.UserIDString, out last))
            {
                if (now - last < _config.Limits.CooldownSeconds)
                    return false;
            }

            _cooldowns[player.UserIDString] = now;
            return true;
        }

        private void RecordQuestion(BasePlayer player, string question)
        {
            if (player == null || _data == null) return;

            int count;
            _data.PlayerQuestionCounts.TryGetValue(player.UserIDString, out count);
            _data.PlayerQuestionCounts[player.UserIDString] = count + 1;

            if (_config.Logging.KeepRecentQuestionLog)
            {
                _data.RecentQuestions.Add(new QuestionLogRecord
                {
                    SteamId = player.UserIDString,
                    PlayerName = player.displayName,
                    Question = question,
                    CreatedUtc = DateTime.UtcNow.ToString("o")
                });

                while (_data.RecentQuestions.Count > _config.Logging.MaxRecentQuestions)
                    _data.RecentQuestions.RemoveAt(0);
            }

            SaveData();
        }

        private string BuildHelpText(BasePlayer player)
        {
            string text = _config.Messages.HelpText.Replace("{command}", "/" + _config.Command.CommandName);
            if (IsAdmin(player))
                text += "\nAdmin: /" + _config.Command.CommandName + " status | /" + _config.Command.CommandName + " reload";
            return text;
        }

        private string BuildStatusText()
        {
            return string.Join("\n", new[]
            {
                "WorldMindAsk",
                "Enabled: " + _config.Command.Enabled,
                "Command: /" + _config.Command.CommandName,
                "WorldMindV2 loaded: " + (WorldMindV2 != null),
                "Requires permission: " + _config.Command.RequirePermission,
                "Cooldown seconds: " + _config.Limits.CooldownSeconds,
                "Stored player question counts: " + (_data == null || _data.PlayerQuestionCounts == null ? 0 : _data.PlayerQuestionCounts.Count)
            });
        }

        private string CleanAnswer(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer)) return "";
            string clean = answer.Trim();
            clean = clean.Replace("\r", " ").Replace("\n", " ");
            while (clean.Contains("  ")) clean = clean.Replace("  ", " ");

            if (clean.Length > _config.Limits.MaxAnswerCharacters)
                clean = clean.Substring(0, _config.Limits.MaxAnswerCharacters).TrimEnd() + "...";

            return clean;
        }

        private string FormatPosition(Vector3 position)
        {
            return "x=" + Math.Round(position.x, 1) + ", y=" + Math.Round(position.y, 1) + ", z=" + Math.Round(position.z, 1);
        }

        private bool HasUsePermission(BasePlayer player)
        {
            return player != null && (permission.UserHasPermission(player.UserIDString, PermissionUse) || IsAdmin(player));
        }

        private bool IsAdmin(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin));
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null || string.IsNullOrEmpty(message)) return;
            SendReply(player, _config.Messages.ChatPrefix + message);
        }

        #endregion

        #region Config/Data Classes

        private class PluginConfig
        {
            [JsonProperty(Order = 1, PropertyName = "Command")]
            public CommandConfig Command = new CommandConfig();

            [JsonProperty(Order = 2, PropertyName = "WorldMind Request Behavior")]
            public WorldMindConfig WorldMind = new WorldMindConfig();

            [JsonProperty(Order = 3, PropertyName = "Player Context Sent To WorldMind")]
            public ContextConfig Context = new ContextConfig();

            [JsonProperty(Order = 4, PropertyName = "Limits")]
            public LimitsConfig Limits = new LimitsConfig();

            [JsonProperty(Order = 5, PropertyName = "Messages")]
            public MessageConfig Messages = new MessageConfig();

            [JsonProperty(Order = 6, PropertyName = "Logging")]
            public LoggingConfig Logging = new LoggingConfig();

            public static PluginConfig Default()
            {
                return new PluginConfig
                {
                    Command = new CommandConfig(),
                    WorldMind = new WorldMindConfig(),
                    Context = new ContextConfig(),
                    Limits = new LimitsConfig(),
                    Messages = new MessageConfig(),
                    Logging = new LoggingConfig()
                };
            }

            public void Normalize()
            {
                if (Command == null) Command = new CommandConfig();
                if (WorldMind == null) WorldMind = new WorldMindConfig();
                if (Context == null) Context = new ContextConfig();
                if (Limits == null) Limits = new LimitsConfig();
                if (Messages == null) Messages = new MessageConfig();
                if (Logging == null) Logging = new LoggingConfig();

                Command.Normalize();
                WorldMind.Normalize();
                Limits.Normalize();
                Messages.Normalize();
                Logging.Normalize();
            }
        }

        private class CommandConfig
        {
            public bool Enabled = true;
            public string CommandName = "ask";
            public bool RequirePermission = false;

            public void Normalize()
            {
                if (string.IsNullOrWhiteSpace(CommandName)) CommandName = "ask";
                CommandName = CommandName.Trim().TrimStart('/').ToLowerInvariant();
            }
        }

        private class WorldMindConfig
        {
            public string Tone = "Helpful, immersive, concise, and Rust-aware.";
            public bool RecordQuestionEvents = true;
            public List<string> ExtraInstructions = new List<string>
            {
                "Answer only using configured WorldMind server identity, enabled features, enabled commands, and known server facts.",
                "If a command or feature is not configured, say the owner has not configured that information yet.",
                "Keep player answers short enough for Rust chat."
            };

            public void Normalize()
            {
                if (string.IsNullOrWhiteSpace(Tone)) Tone = "Helpful, immersive, concise, and Rust-aware.";
                if (ExtraInstructions == null) ExtraInstructions = new List<string>();
            }
        }

        private class ContextConfig
        {
            public bool IncludePosition = true;
            public bool IncludeBasicPlayerState = true;
            public bool IncludeHeldItem = true;
        }

        private class LimitsConfig
        {
            public float CooldownSeconds = 15f;
            public bool AdminBypassCooldown = true;
            public int MaxQuestionCharacters = 220;
            public int MaxAnswerCharacters = 420;

            public void Normalize()
            {
                if (CooldownSeconds < 0f) CooldownSeconds = 0f;
                if (MaxQuestionCharacters < 40) MaxQuestionCharacters = 40;
                if (MaxAnswerCharacters < 80) MaxAnswerCharacters = 80;
            }
        }

        private class MessageConfig
        {
            public string ChatPrefix = "<color=#d6b46a>[WorldMind]</color> ";
            public string AnswerPrefix = "";
            public string CooldownMessage = "WorldMind is cooling down. Try again shortly.";
            public string EmptyAnswerMessage = "WorldMind returned no answer.";
            public string HelpText = "Ask WorldMind with {command} <question>. Example: {command} what can I do here?";

            public void Normalize()
            {
                if (ChatPrefix == null) ChatPrefix = "";
                if (AnswerPrefix == null) AnswerPrefix = "";
                if (string.IsNullOrWhiteSpace(CooldownMessage)) CooldownMessage = "WorldMind is cooling down. Try again shortly.";
                if (string.IsNullOrWhiteSpace(EmptyAnswerMessage)) EmptyAnswerMessage = "WorldMind returned no answer.";
                if (string.IsNullOrWhiteSpace(HelpText)) HelpText = "Ask WorldMind with {command} <question>.";
            }
        }

        private class LoggingConfig
        {
            public bool KeepRecentQuestionLog = true;
            public int MaxRecentQuestions = 50;

            public void Normalize()
            {
                if (MaxRecentQuestions < 0) MaxRecentQuestions = 0;
                if (MaxRecentQuestions > 500) MaxRecentQuestions = 500;
            }
        }

        private class StoredData
        {
            public Dictionary<string, int> PlayerQuestionCounts = new Dictionary<string, int>();
            public List<QuestionLogRecord> RecentQuestions = new List<QuestionLogRecord>();
        }

        private class QuestionLogRecord
        {
            public string SteamId = "";
            public string PlayerName = "";
            public string Question = "";
            public string CreatedUtc = "";
        }

        #endregion
    }
}
