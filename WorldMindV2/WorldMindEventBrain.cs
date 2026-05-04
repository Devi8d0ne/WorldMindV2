using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindEventBrain", "Devi8d0ne", "1.1.0")]
    [Description("Deviated Playgrounds WorldMind event brain with DiscordMind routing, AI-flavored event announcements, updates, and recaps.")]
    public class WorldMindEventBrain : RustPlugin
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

        private const string PermAdmin = "worldmindeventbrain.admin";
        private const string PermUse = "worldmindeventbrain.use";

        [PluginReference] private Plugin WorldMindV2;
        [PluginReference] private Plugin WorldMindDiscordMind;

        private PluginConfig _config;
        private StoredData _data;
        private Timer _tickTimer;

        #region Oxide

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermUse, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            if (_config.General.PrintAsciiOnLoad)
                PrintWarning("\n" + Dv8dAscii + "\nAuthor: Devi8d0ne\n" + MadeWithLoveTag);

            StartTickTimer();
            Puts($"WorldMindEventBrain loaded. {MadeWithLoveTag}");
        }

        private void Unload()
        {
            _tickTimer?.Destroy();
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
                PrintError("Config read failed. Existing config was NOT overwritten. Runtime defaults are being used. Error: " + ex.Message);
                _config = PluginConfig.Default();
                _config.Normalize();
            }
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void LoadData()
        {
            LoadConfig();
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
                if (_data == null) _data = new StoredData();
            }
            catch (Exception ex)
            {
                PrintError("Data read failed. Existing data was NOT overwritten. Runtime data is empty. Error: " + ex.Message);
                _data = new StoredData();
            }

            if (_data.ActiveEvents == null) _data.ActiveEvents = new Dictionary<string, EventRecord>();
            if (_data.EventHistory == null) _data.EventHistory = new List<EventHistoryRecord>();
        }

        private void SaveData()
        {
            if (_data == null) return;
            Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        #endregion

        #region Commands

        [ChatCommand("wmevent")]
        private void CmdEvent(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (!IsAdmin(player))
            {
                Reply(player, "You do not have permission to use WorldMindEventBrain.");
                return;
            }

            if (args == null || args.Length == 0)
            {
                Reply(player, BuildHelp());
                return;
            }

            HandleCommand(player, args, msg => Reply(player, msg));
        }

        [ConsoleCommand("wmevent")]
        private void CcmdEvent(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            BasePlayer player = arg.Player();
            if (player != null && !IsAdmin(player))
            {
                arg.ReplyWith("No permission.");
                return;
            }

            string[] args = arg.Args ?? new string[0];
            if (args.Length == 0)
            {
                arg.ReplyWith(BuildHelp());
                return;
            }

            HandleCommand(player, args, msg => arg.ReplyWith(msg));
        }

        private void HandleCommand(BasePlayer player, string[] args, Action<string> reply)
        {
            string sub = args[0].ToLowerInvariant();

            if (sub == "status")
            {
                reply(BuildStatus());
                return;
            }

            if (sub == "reload")
            {
                LoadData();
                StartTickTimer();
                reply("WorldMindEventBrain config/data reloaded.");
                return;
            }

            if (sub == "help")
            {
                reply(BuildHelp());
                return;
            }

            if (sub == "list")
            {
                reply(BuildEventList());
                return;
            }

            if (sub == "clear")
            {
                _data.ActiveEvents.Clear();
                SaveData();
                reply("All active WorldMind events cleared.");
                return;
            }

            if (sub == "stop")
            {
                if (args.Length < 2)
                {
                    reply("Usage: /wmevent stop <eventId>");
                    return;
                }

                string id = args[1].ToLowerInvariant();
                EventRecord record;
                if (!_data.ActiveEvents.TryGetValue(id, out record))
                {
                    reply("No active event found with id: " + id);
                    return;
                }

                _data.ActiveEvents.Remove(id);
                SaveData();
                string stopMessage = $"Event ended: {record.Title}";
                Announce(stopMessage);
                SendDiscordEvent("event_stopped", record.Title, stopMessage, "server", new Dictionary<string, object>
                {
                    ["eventId"] = id,
                    ["title"] = record.Title,
                    ["timeUtc"] = DateTime.UtcNow.ToString("o")
                });
                RecordToWorldMind("event_stopped", "server", new Dictionary<string, object>
                {
                    ["eventId"] = id,
                    ["title"] = record.Title
                });
                reply("Stopped event: " + record.Title);
                return;
            }

            if (sub == "announce")
            {
                if (args.Length < 2)
                {
                    reply("Usage: /wmevent announce <message>");
                    return;
                }

                string input = JoinArgs(args, 1);
                GenerateAndAnnounce("manual_announcement", input, null, player, false);
                reply("WorldMind event announcement requested.");
                return;
            }

            if (sub == "start")
            {
                if (args.Length < 2)
                {
                    reply("Usage: /wmevent start <event title> [minutes]");
                    return;
                }

                int minutes = _config.Events.DefaultDurationMinutes;
                string title = JoinArgs(args, 1);
                int parsed;
                string last = args[args.Length - 1];
                if (args.Length >= 3 && int.TryParse(last, out parsed))
                {
                    minutes = Mathf.Clamp(parsed, 1, _config.Events.MaxDurationMinutes);
                    title = JoinArgs(args, 1, args.Length - 1);
                }

                StartEvent(title, minutes, player, reply);
                return;
            }

            if (sub == "update")
            {
                if (args.Length < 3)
                {
                    reply("Usage: /wmevent update <eventId> <update text>");
                    return;
                }

                string id = args[1].ToLowerInvariant();
                EventRecord record;
                if (!_data.ActiveEvents.TryGetValue(id, out record))
                {
                    reply("No active event found with id: " + id);
                    return;
                }

                string update = JoinArgs(args, 2);
                GenerateAndAnnounce("event_update", update, record, player, false);
                record.LastUpdateUtc = DateTime.UtcNow.ToString("o");
                SaveData();
                reply("WorldMind event update requested for: " + record.Title);
                return;
            }

            if (sub == "recap")
            {
                if (args.Length < 2)
                {
                    reply("Usage: /wmevent recap <eventId or recap notes>");
                    return;
                }

                string idOrNotes = JoinArgs(args, 1);
                EventRecord record;
                _data.ActiveEvents.TryGetValue(args[1].ToLowerInvariant(), out record);
                GenerateAndAnnounce("event_recap", idOrNotes, record, player, true);
                reply("WorldMind event recap requested.");
                return;
            }

            if (sub == "test")
            {
                GenerateAndAnnounce("event_test", "Generate a short test event announcement for Deviated Playgrounds.", null, player, false);
                reply("WorldMind event test requested.");
                return;
            }

            if (sub == "testdiscord")
            {
                string message = "DV8D event bridge test: Deviated Playgrounds is talking to DiscordMind.";
                bool sent = SendDiscordEvent("event_test", "WorldMind Event Test", message, player == null ? "server" : player.UserIDString, new Dictionary<string, object>
                {
                    ["test"] = true,
                    ["requestedBy"] = player == null ? "console" : player.displayName,
                    ["timeUtc"] = DateTime.UtcNow.ToString("o")
                });
                reply(sent ? "DiscordMind event test queued." : "DiscordMind event test failed or DiscordMind integration is disabled.");
                return;
            }

            reply("Unknown subcommand. Use /wmevent help.");
        }

        [ConsoleCommand("worldmindeventbrain.status")]
        private void CcmdStatus(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            arg.ReplyWith(BuildStatus());
        }

        [ConsoleCommand("worldmindeventbrain.testdiscord")]
        private void CcmdTestDiscord(ConsoleSystem.Arg arg)
        {
            string message = "DV8D event bridge test: Deviated Playgrounds is talking to DiscordMind.";
            bool sent = SendDiscordEvent("event_test", "WorldMind Event Test", message, "server", new Dictionary<string, object>
            {
                ["test"] = true,
                ["requestedBy"] = "console",
                ["timeUtc"] = DateTime.UtcNow.ToString("o")
            });

            arg.ReplyWith(sent ? "DiscordMind event test queued." : "DiscordMind event test failed or DiscordMind integration is disabled.");
        }

        #endregion

        #region Event Logic

        private void StartEvent(string title, int minutes, BasePlayer player, Action<string> reply)
        {
            title = CleanInput(title, _config.Events.MaxTitleLength);
            if (string.IsNullOrWhiteSpace(title))
            {
                reply("Event title cannot be empty.");
                return;
            }

            string id = MakeEventId(title);
            DateTime now = DateTime.UtcNow;
            EventRecord record = new EventRecord
            {
                Id = id,
                Title = title,
                CreatedBy = player == null ? "console" : player.displayName,
                CreatedById = player == null ? "server" : player.UserIDString,
                StartedUtc = now.ToString("o"),
                EndsUtc = now.AddMinutes(minutes).ToString("o"),
                LastUpdateUtc = now.ToString("o"),
                DurationMinutes = minutes
            };

            _data.ActiveEvents[id] = record;
            _data.EventHistory.Add(new EventHistoryRecord
            {
                EventId = id,
                Title = title,
                Type = "started",
                Message = title,
                TimestampUtc = now.ToString("o")
            });
            TrimHistory();
            SaveData();

            string input = $"Start a Deviated Playgrounds Rust server event titled '{title}'. Duration: {minutes} minutes. Announce it with Deviated Playgrounds energy: sharp, Rust-aware, tactical, and player-facing. Mention only configured facts; do not invent commands, rewards, mechanics, or server systems.";
            GenerateAndAnnounce("event_started", input, record, player, false);

            RecordToWorldMind("event_started", record.CreatedById, new Dictionary<string, object>
            {
                ["eventId"] = id,
                ["title"] = title,
                ["durationMinutes"] = minutes,
                ["createdBy"] = record.CreatedBy
            });

            reply($"Started event '{title}' with id '{id}' for {minutes} minutes.");
        }

        private void GenerateAndAnnounce(string eventType, string input, EventRecord record, BasePlayer player, bool recap)
        {
            if (!_config.General.Enabled)
                return;

            string fallback = BuildFallback(eventType, input, record);
            string prompt = BuildPrompt(eventType, input, record, player, recap);

            AskWorldMind(prompt, eventType, player, record, response =>
            {
                string message = CleanOutput(string.IsNullOrWhiteSpace(response) || IsGarbageOutput(response) ? fallback : response, _config.Events.MaxAnnouncementLength);
                if (string.IsNullOrWhiteSpace(message) || IsGarbageOutput(message)) message = fallback;

                Announce(message);

                string id = record == null ? "manual" : record.Id;
                SendDiscordEvent(eventType, record == null ? "WorldMind Event" : record.Title, message, player == null ? "server" : player.UserIDString, new Dictionary<string, object>
                {
                    ["eventId"] = id,
                    ["eventTitle"] = record == null ? "manual" : record.Title,
                    ["eventType"] = eventType,
                    ["input"] = input,
                    ["requestedBy"] = player == null ? "server" : player.displayName,
                    ["recap"] = recap,
                    ["timeUtc"] = DateTime.UtcNow.ToString("o")
                });

                _data.EventHistory.Add(new EventHistoryRecord
                {
                    EventId = id,
                    Title = record == null ? eventType : record.Title,
                    Type = eventType,
                    Message = message,
                    TimestampUtc = DateTime.UtcNow.ToString("o")
                });
                TrimHistory();
                SaveData();

                RecordToWorldMind(eventType, player == null ? "server" : player.UserIDString, new Dictionary<string, object>
                {
                    ["eventId"] = id,
                    ["eventTitle"] = record == null ? "manual" : record.Title,
                    ["message"] = message,
                    ["input"] = input
                });
            });
        }

        private string BuildPrompt(string eventType, string input, EventRecord record, BasePlayer player, bool recap)
        {
            string actor = player == null ? "server console" : player.displayName;
            string eventInfo = record == null
                ? "No active event record provided."
                : $"Active event id: {record.Id}. Title: {record.Title}. Started UTC: {record.StartedUtc}. Ends UTC: {record.EndsUtc}.";

            return
                "You are WorldMind EventBrain for Deviated Playgrounds. " +
                "Write one short player-facing Rust chat message with sharp Deviated Playgrounds energy: immersive, tactical, sarcastic when useful, and alive. " +
                "Treat events like island intelligence: danger, loot, movement, pressure, consequence, bad decisions, and opportunity. " +
                "Mention WarMode, kits, homes, VIP, Discord, economy, plugins, rewards, or custom commands only when supplied as configured facts. " +
                "Do not invent rewards, mechanics, commands, rules, lore, winners, player counts, or admin-only details. " +
                "No backend, config, AI, model, API, or prompt talk. Keep it readable in Rust chat. " +
                "Event type: " + eventType + ". " +
                "Requested by: " + actor + ". " +
                eventInfo + " " +
                "Input/notes: " + input + ". " +
                (recap ? "This is a recap, so summarize only known facts and pressure, not fake outcomes. " : "This is an announcement/update, so make it active, clear, and worth reacting to. ") +
                "Maximum length: " + _config.Events.MaxAnnouncementLength + " characters.";
        }

        private void AskWorldMind(string prompt, string eventType, BasePlayer player, EventRecord record, Action<string> callback)
        {
            if (!_config.WorldMind.UseWorldMindV2 || WorldMindV2 == null)
            {
                callback(null);
                return;
            }

            bool answered = false;
            try
            {
                Dictionary<string, object> truth = new Dictionary<string, object>
                {
                    ["prompt"] = prompt,
                    ["source"] = Name,
                    ["eventType"] = eventType,
                    ["maxCharacters"] = _config.Events.MaxAnnouncementLength
                };

                if (record != null)
                {
                    truth["eventId"] = record.Id;
                    truth["eventTitle"] = record.Title;
                    truth["eventEndsUtc"] = record.EndsUtc;
                }

                if (player != null)
                {
                    truth["playerId"] = player.UserIDString;
                    truth["playerName"] = player.displayName;
                    truth["playerPosition"] = FormatPosition(player.transform.position);
                    object location = WorldMindV2.Call("WorldMind_DescribeLocation", player.transform.position);
                    if (location != null) truth["locationDescription"] = Convert.ToString(location);
                }

                Dictionary<string, object> request = new Dictionary<string, object>
                {
                    ["Plugin"] = Name,
                    ["EventType"] = eventType,
                    ["PlayerId"] = player == null ? "server" : player.UserIDString,
                    ["PlayerName"] = player == null ? "Server" : player.displayName,
                    ["Tone"] = _config.WorldMind.Tone,
                    ["Urgency"] = 2,
                    ["Truth"] = truth
                };

                Action<string> wmCallback = answer =>
                {
                    answered = true;
                    callback(answer);
                };

                object called = WorldMindV2.Call("WorldMind_AskText", request, wmCallback);
                if (called == null)
                {
                    if (_config.General.Debug) PrintWarning("WorldMind_AskText returned null. Using fallback.");
                    callback(null);
                    return;
                }

                timer.Once(_config.WorldMind.ResponseTimeoutSeconds, () =>
                {
                    if (!answered)
                        callback(null);
                });
            }
            catch (Exception ex)
            {
                if (_config.General.Debug)
                    PrintWarning("WorldMind call failed: " + ex.Message);
                callback(null);
            }
        }

        private void Announce(string message)
        {
            message = CleanOutput(message, _config.Events.MaxAnnouncementLength);
            if (string.IsNullOrWhiteSpace(message)) return;

            string formatted = _config.Chat.Prefix + message;

            if (_config.Chat.BroadcastToServer)
            {
                PrintToChat(formatted);
                return;
            }

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                if (_config.General.RequireUsePermission && !HasPerm(player, PermUse)) continue;
                PrintToChat(player, formatted);
            }
        }

        private void StartTickTimer()
        {
            _tickTimer?.Destroy();
            if (!_config.General.Enabled || !_config.Events.AutoEndEvents)
                return;

            _tickTimer = timer.Every(Math.Max(30f, _config.Events.TickSeconds), CheckEvents);
        }

        private void CheckEvents()
        {
            if (_data == null || _data.ActiveEvents == null || _data.ActiveEvents.Count == 0) return;

            DateTime now = DateTime.UtcNow;
            List<string> ended = new List<string>();
            foreach (KeyValuePair<string, EventRecord> kvp in _data.ActiveEvents)
            {
                DateTime end;
                if (!DateTime.TryParse(kvp.Value.EndsUtc, out end)) continue;
                if (now >= end) ended.Add(kvp.Key);
            }

            foreach (string id in ended)
            {
                EventRecord record = _data.ActiveEvents[id];
                _data.ActiveEvents.Remove(id);
                GenerateAndAnnounce("event_auto_ended", "The configured event duration expired. Give a short generic end notice.", record, null, false);
            }

            if (ended.Count > 0) SaveData();
        }

        private bool SendDiscordEvent(string eventType, string title, string message, string playerId, Dictionary<string, object> facts)
        {
            if (_config == null || _config.DiscordMind == null || !_config.DiscordMind.Enabled) return false;
            if (string.IsNullOrWhiteSpace(message)) return false;

            string lower = (eventType ?? "event").ToLowerInvariant();
            if (!_config.DiscordMind.SendTestEvents && lower.Contains("test")) return false;
            if (!_config.DiscordMind.SendManualAnnouncements && (lower.Contains("manual") || lower.Contains("announce"))) return false;
            if (!_config.DiscordMind.SendEventStartStop && (lower.Contains("started") || lower.Contains("stopped") || lower.Contains("ended"))) return false;
            if (!_config.DiscordMind.SendEventUpdates && lower.Contains("update")) return false;
            if (!_config.DiscordMind.SendEventRecaps && lower.Contains("recap")) return false;

            string cleanMessage = CleanOutput(message, _config.DiscordMind.MaxDiscordMessageLength);
            if (!IsUsableDiscordText(cleanMessage)) return false;

            var packet = new Dictionary<string, object>
            {
                ["category"] = string.IsNullOrWhiteSpace(_config.DiscordMind.Category) ? "event" : _config.DiscordMind.Category,
                ["channelKey"] = string.IsNullOrWhiteSpace(_config.DiscordMind.ChannelKey) ? "event" : _config.DiscordMind.ChannelKey,
                ["title"] = CleanInput(string.IsNullOrWhiteSpace(title) ? "WorldMind Event" : title, _config.DiscordMind.MaxDiscordTitleLength),
                ["message"] = cleanMessage,
                ["eventType"] = eventType ?? "event",
                ["playerId"] = playerId ?? "server",
                ["source"] = Name,
                ["serverIdentity"] = _config.DiscordMind.ServerIdentity,
                ["timeUtc"] = DateTime.UtcNow.ToString("o")
            };

            if (_config.DiscordMind.IncludeEventFacts && facts != null)
                packet["facts"] = facts;

            bool sent = false;
            try
            {
                object result = Interface.CallHook("WorldMindDiscordMind_SendEvent", packet);
                sent = HookResultTrue(result);

                if (!sent)
                {
                    result = Interface.CallHook("WorldMindDiscordMind_SendMessageToChannel", packet["channelKey"].ToString(), packet["title"].ToString(), cleanMessage, packet["category"].ToString());
                    sent = HookResultTrue(result);
                }
            }
            catch (Exception ex)
            {
                if (_config.General.Debug || _config.DiscordMind.DebugDiscordRouting)
                    PrintWarning("DiscordMind event routing failed: " + ex.Message);
            }

            if (_config.DiscordMind.DebugDiscordRouting || _config.General.Debug)
                Puts("DiscordMind event route " + (sent ? "queued" : "not queued") + ": " + eventType + " / " + cleanMessage);

            return sent;
        }

        private bool HookResultTrue(object result)
        {
            if (result == null) return false;
            if (result is bool) return (bool)result;
            bool parsed;
            if (bool.TryParse(result.ToString(), out parsed)) return parsed;
            return false;
        }

        private bool IsGarbageOutput(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            string t = value.Trim();
            if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("null", StringComparison.OrdinalIgnoreCase)) return true;
            if (t == "{}" || t == "[]") return true;
            if (t.StartsWith("{") || t.StartsWith("[")) return true;
            return false;
        }

        private bool IsUsableDiscordText(string value)
        {
            if (IsGarbageOutput(value)) return false;
            return true;
        }

        private void RecordToWorldMind(string eventType, string playerId, Dictionary<string, object> truth)
        {
            if (!_config.WorldMind.RecordEventsToWorldMind || WorldMindV2 == null) return;

            try
            {
                WorldMindV2.Call("WorldMind_RecordEvent", Name, eventType, playerId ?? "server", truth ?? new Dictionary<string, object>());
            }
            catch (Exception ex)
            {
                if (_config.General.Debug)
                    PrintWarning("WorldMind_RecordEvent failed: " + ex.Message);
            }
        }

        #endregion

        #region Text / Helpers

        private string BuildHelp()
        {
            return string.Join("\n", new[]
            {
                "WorldMindEventBrain commands:",
                "/wmevent status",
                "/wmevent start <event title> [minutes]",
                "/wmevent announce <message>",
                "/wmevent update <eventId> <update text>",
                "/wmevent recap <eventId or notes>",
                "/wmevent list",
                "/wmevent stop <eventId>",
                "/wmevent clear",
                "/wmevent reload",
                "/wmevent test",
                "/wmevent testdiscord"
            });
        }

        private string BuildStatus()
        {
            return $"WorldMindEventBrain v1.1.0\n" +
                   $"Enabled: {_config.General.Enabled}\n" +
                   $"WorldMindV2 loaded: {(WorldMindV2 != null)}\n" +
                   $"DiscordMind loaded: {(WorldMindDiscordMind != null)}\n" +
                   $"Use WorldMindV2: {_config.WorldMind.UseWorldMindV2}\n" +
                   $"DiscordMind enabled: {_config.DiscordMind.Enabled}\n" +
                   $"Discord channel key: {_config.DiscordMind.ChannelKey}\n" +
                   $"Active events: {_data.ActiveEvents.Count}\n" +
                   $"History records: {_data.EventHistory.Count}";
        }

        private string BuildEventList()
        {
            if (_data.ActiveEvents.Count == 0)
                return "No active WorldMind events.";

            List<string> lines = new List<string> { "Active WorldMind events:" };
            foreach (EventRecord record in _data.ActiveEvents.Values.OrderBy(x => x.StartedUtc))
            {
                lines.Add($"{record.Id} | {record.Title} | ends UTC {record.EndsUtc}");
            }
            return string.Join("\n", lines.ToArray());
        }

        private string BuildFallback(string eventType, string input, EventRecord record)
        {
            if (eventType == "event_started" && record != null)
                return $"Event started: {record.Title}.";
            if (eventType == "event_update" && record != null)
                return $"Event update: {record.Title}. {input}";
            if (eventType == "event_recap")
                return "Event recap: " + input;
            if (eventType == "event_auto_ended" && record != null)
                return $"Event ended: {record.Title}.";
            return input;
        }

        private string MakeEventId(string title)
        {
            string clean = new string((title ?? "event").ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
            clean = string.Join("-", clean.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(clean)) clean = "event";
            if (clean.Length > 24) clean = clean.Substring(0, 24).Trim('-');

            string id = clean;
            int i = 2;
            while (_data.ActiveEvents.ContainsKey(id))
            {
                id = clean + "-" + i;
                i++;
            }
            return id;
        }

        private string JoinArgs(string[] args, int start)
        {
            return JoinArgs(args, start, args.Length);
        }

        private string JoinArgs(string[] args, int start, int endExclusive)
        {
            if (args == null || args.Length <= start) return string.Empty;
            List<string> parts = new List<string>();
            for (int i = start; i < endExclusive && i < args.Length; i++)
                parts.Add(args[i]);
            return string.Join(" ", parts.ToArray());
        }

        private string CleanInput(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            value = value.Replace("\n", " ").Replace("\r", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            if (max > 0 && value.Length > max) value = value.Substring(0, max).Trim();
            return value;
        }

        private string CleanOutput(string value, int max)
        {
            value = CleanInput(value, max);
            if (value.StartsWith("WorldMind:", StringComparison.OrdinalIgnoreCase))
                value = value.Substring("WorldMind:".Length).Trim();
            if (value.StartsWith("Announcement:", StringComparison.OrdinalIgnoreCase))
                value = value.Substring("Announcement:".Length).Trim();
            return value;
        }

        private void TrimHistory()
        {
            int max = Math.Max(0, _config.Events.MaxHistoryRecords);
            if (max <= 0)
            {
                _data.EventHistory.Clear();
                return;
            }

            while (_data.EventHistory.Count > max)
                _data.EventHistory.RemoveAt(0);
        }

        private string FormatPosition(Vector3 pos)
        {
            return $"x={Math.Round(pos.x, 1)}, y={Math.Round(pos.y, 1)}, z={Math.Round(pos.z, 1)}";
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null) return;
            PrintToChat(player, _config.Chat.Prefix + message);
        }

        private bool IsAdmin(BasePlayer player)
        {
            if (player == null) return false;
            return player.IsAdmin || HasPerm(player, PermAdmin);
        }

        private bool HasPerm(BasePlayer player, string perm)
        {
            return player != null && permission.UserHasPermission(player.UserIDString, perm);
        }

        #endregion

        #region Config / Data

        private class PluginConfig
        {
            [JsonProperty("General")]
            public GeneralConfig General = new GeneralConfig();

            [JsonProperty("Chat")]
            public ChatConfig Chat = new ChatConfig();

            [JsonProperty("WorldMind V2")]
            public WorldMindConfig WorldMind = new WorldMindConfig();

            [JsonProperty("Event Settings")]
            public EventSettings Events = new EventSettings();

            [JsonProperty("DiscordMind Integration")]
            public DiscordMindSettings DiscordMind = new DiscordMindSettings();

            public static PluginConfig Default()
            {
                return new PluginConfig();
            }

            public void Normalize()
            {
                if (General == null) General = new GeneralConfig();
                if (Chat == null) Chat = new ChatConfig();
                if (WorldMind == null) WorldMind = new WorldMindConfig();
                if (Events == null) Events = new EventSettings();
                if (DiscordMind == null) DiscordMind = new DiscordMindSettings();
                if (string.IsNullOrWhiteSpace(Chat.Prefix)) Chat.Prefix = "<color=#d7b46a>[WorldMind Event]</color> ";
                if (Events.DefaultDurationMinutes <= 0) Events.DefaultDurationMinutes = 15;
                if (Events.MaxDurationMinutes <= 0) Events.MaxDurationMinutes = 180;
                if (Events.MaxAnnouncementLength <= 0) Events.MaxAnnouncementLength = 220;
                if (Events.MaxTitleLength <= 0) Events.MaxTitleLength = 80;
                if (Events.TickSeconds < 30f) Events.TickSeconds = 30f;
                if (WorldMind.ResponseTimeoutSeconds <= 0f) WorldMind.ResponseTimeoutSeconds = 8f;
            }
        }

        private class GeneralConfig
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Print DV8D ASCII On Load")]
            public bool PrintAsciiOnLoad = true;

            [JsonProperty("Require Use Permission For Receiving Non-Broadcast Event Messages")]
            public bool RequireUsePermission = false;

            [JsonProperty("Debug")]
            public bool Debug = false;
        }

        private class ChatConfig
        {
            [JsonProperty("Chat Prefix")]
            public string Prefix = "<color=#d7b46a>[WorldMind Event]</color> ";

            [JsonProperty("Broadcast To Entire Server")]
            public bool BroadcastToServer = true;
        }

        private class WorldMindConfig
        {
            [JsonProperty("Use WorldMindV2")]
            public bool UseWorldMindV2 = true;

            [JsonProperty("Record Events To WorldMind Timeline")]
            public bool RecordEventsToWorldMind = true;

            [JsonProperty("Tone")]
            public string Tone = "Deviated Playgrounds event voice: sharp, tactical, immersive, sarcastic when useful, and Rust-aware. Turn events into player-facing island intelligence: danger, opportunity, movement, pressure, consequence, loot greed, and bad decisions. Keep it short for Rust chat. Use configured server facts only; never invent commands, rewards, rules, winners, mechanics, or lore.";

            [JsonProperty("Response Timeout Seconds")]
            public float ResponseTimeoutSeconds = 8f;
        }

        private class DiscordMindSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Server Identity")]
            public string ServerIdentity = "Deviated Playgrounds";

            [JsonProperty("Channel Key")]
            public string ChannelKey = "event";

            [JsonProperty("Category")]
            public string Category = "event";

            [JsonProperty("Send Manual Announcements")]
            public bool SendManualAnnouncements = true;

            [JsonProperty("Send Event Start Stop")]
            public bool SendEventStartStop = true;

            [JsonProperty("Send Event Updates")]
            public bool SendEventUpdates = true;

            [JsonProperty("Send Event Recaps")]
            public bool SendEventRecaps = true;

            [JsonProperty("Send Test Events")]
            public bool SendTestEvents = true;

            [JsonProperty("Include Event Facts")]
            public bool IncludeEventFacts = true;

            [JsonProperty("Max Discord Title Length")]
            public int MaxDiscordTitleLength = 180;

            [JsonProperty("Max Discord Message Length")]
            public int MaxDiscordMessageLength = 1800;

            [JsonProperty("Debug Discord Routing")]
            public bool DebugDiscordRouting = false;
        }

        private class EventSettings
        {
            [JsonProperty("Default Event Duration Minutes")]
            public int DefaultDurationMinutes = 15;

            [JsonProperty("Maximum Event Duration Minutes")]
            public int MaxDurationMinutes = 180;

            [JsonProperty("Maximum Event Title Length")]
            public int MaxTitleLength = 80;

            [JsonProperty("Maximum Announcement Length")]
            public int MaxAnnouncementLength = 220;

            [JsonProperty("Auto End Events When Duration Expires")]
            public bool AutoEndEvents = true;

            [JsonProperty("Event Check Tick Seconds")]
            public float TickSeconds = 60f;

            [JsonProperty("Maximum Stored History Records")]
            public int MaxHistoryRecords = 100;
        }

        private class StoredData
        {
            public Dictionary<string, EventRecord> ActiveEvents = new Dictionary<string, EventRecord>();
            public List<EventHistoryRecord> EventHistory = new List<EventHistoryRecord>();
        }

        private class EventRecord
        {
            public string Id;
            public string Title;
            public string CreatedBy;
            public string CreatedById;
            public string StartedUtc;
            public string EndsUtc;
            public string LastUpdateUtc;
            public int DurationMinutes;
        }

        private class EventHistoryRecord
        {
            public string EventId;
            public string Title;
            public string Type;
            public string Message;
            public string TimestampUtc;
        }

        #endregion
    }
}
