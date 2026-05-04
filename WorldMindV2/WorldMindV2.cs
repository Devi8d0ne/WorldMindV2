using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindV2", "Devi8d0ne", "2.0.6")]
    [Description("Generic WorldMind V2 intelligence layer for Rust servers. Provides model access, memory hooks, optional data providers, and shared APIs for companion plugins.")]
    public class WorldMindV2 : RustPlugin
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
        private const string PermissionAdmin = "worldmindv2.admin";
        private const string DataFolder = "WorldMindV2";

        private PluginConfig _config;
        private StoredData _data;
        private readonly Dictionary<string, double> _lastAskByPlayer = new Dictionary<string, double>();

        #region Oxide Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermissionAdmin, this);
            LoadConfigValues();
            LoadData();
            PrintCleanStartupSummary();
        }

        private void Unload()
        {
            SaveData();
        }

        protected override void LoadDefaultConfig()
        {
            // Only used when the config file does not exist yet.
            // Existing owner-edited config JSON must never be replaced during reload/start.
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

                // Normalize in memory only. Do not SaveConfig() here.
                // This preserves manual JSON edits and prevents reload/start from rewriting owner values.
                _config.Normalize();
            }
            catch (Exception ex)
            {
                // Do not overwrite a broken/hand-edited config with defaults.
                // Keep the file intact so the owner can fix it. Use safe runtime defaults only for this load.
                PrintError("Config read failed. Your existing config file was NOT overwritten. Fix the JSON and reload. Runtime defaults are being used for this session only. Error: " + ex.Message);
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
            // Important: load only. Do not save on normal reload/start.
            // Owner-edited JSON must survive plugin reloads and server restarts.
            LoadConfig();
        }

        private void LoadData()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFolder + "/WorldMindData");
                if (_data == null)
                    _data = new StoredData();
            }
            catch (Exception ex)
            {
                // Do not write defaults over a broken/hand-edited data file during load.
                // Runtime data is empty for this session until the JSON is fixed.
                PrintError("WorldMind data read failed. Existing data JSON was NOT overwritten during load. Error: " + ex.Message);
                _data = new StoredData();
            }

            if (_data.PlayerMemory == null) _data.PlayerMemory = new Dictionary<string, PlayerMemoryRecord>();
            if (_data.AgentMemory == null) _data.AgentMemory = new Dictionary<string, AgentMemoryRecord>();
            if (_data.ServerFacts == null) _data.ServerFacts = new Dictionary<string, string>();
            if (_data.EventTimeline == null) _data.EventTimeline = new List<WorldMindEventRecord>();
            if (_data.ItemCache == null) _data.ItemCache = new Dictionary<string, ItemInfo>();
            if (_data.ProviderHealth == null) _data.ProviderHealth = new Dictionary<string, ProviderHealthRecord>();
        }

        private void SaveData()
        {
            if (_data == null) return;
            Interface.Oxide.DataFileSystem.WriteObject(DataFolder + "/WorldMindData", _data);
        }

        #endregion

        #region Commands

        [ChatCommand("worldmind")]
        private void CmdWorldMind(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (args == null || args.Length == 0)
            {
                Reply(player,
                    "WorldMind V2 is installed. Use /worldmind status. Admins can use /worldmind reload or /wmask <question>.");
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
                if (!IsAdmin(player))
                {
                    Reply(player, "You do not have permission to reload WorldMind.");
                    return;
                }

                LoadConfigValues();
                LoadData();
                Reply(player, "WorldMind V2 config and data reloaded.");
                return;
            }

            if (sub == "ask")
            {
                Reply(player, "Use /ask <question> for player questions. Admins can use /wmask <question> for direct WorldMind testing.");
                return;
            }

            Reply(player, "Unknown WorldMind command. Use /worldmind status. Admins can use /worldmind reload or /wmask <question>.");
        }

        [ChatCommand("wmask")]
        private void CmdWorldMindAdminAsk(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (!IsAdmin(player))
            {
                Reply(player, "You do not have permission to use admin WorldMind ask.");
                return;
            }

            if (!_config.WorldMind.Enabled)
            {
                Reply(player, "WorldMind is disabled in config.");
                return;
            }

            if (args == null || args.Length == 0)
            {
                Reply(player, "Usage: /wmask <admin question>");
                return;
            }

            string question = string.Join(" ", args);
            WorldMindRequest request = new WorldMindRequest
            {
                Plugin = Name,
                EventType = "admin_question",
                PlayerId = player.UserIDString,
                PlayerName = player.displayName,
                Tone = _config.WorldMind.DefaultTone,
                Urgency = 1,
                Truth = new Dictionary<string, object>
                {
                    ["question"] = question,
                    ["adminOnly"] = true,
                    ["position"] = FormatPosition(player.transform.position),
                    ["serverName"] = _config.ServerIdentity.ServerName,
                    ["gameplayStyle"] = _config.ServerIdentity.GameplayStyle,
                    ["enabledCommandCount"] = _config.CommandRegistry.Count(x => x.Enabled),
                    ["timelineEvents"] = _data.EventTimeline.Count,
                    ["playerMemoryRecords"] = _data.PlayerMemory.Count
                }
            };

            AskWorldMind(request, response =>
            {
                if (player == null || !player.IsConnected) return;
                Reply(player, string.IsNullOrEmpty(response.Message) ? "WorldMind returned no message." : response.Message);
            });
        }

        [ConsoleCommand("worldmind.status")]
        private void CcmdStatus(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            if (arg.Player() != null && !IsAdmin(arg.Player()))
            {
                arg.ReplyWith("No permission.");
                return;
            }

            arg.ReplyWith(BuildStatusText());
        }

        [ConsoleCommand("worldmind.reload")]
        private void CcmdReload(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            if (arg.Player() != null && !IsAdmin(arg.Player()))
            {
                arg.ReplyWith("No permission.");
                return;
            }

            LoadConfigValues();
            LoadData();
            arg.ReplyWith("WorldMind V2 config and data reloaded.");
        }

        #endregion

        #region Shared WorldMind API Hooks

        // Companion plugin usage:
        // Interface.CallHook("WorldMind_Ask", requestObject, callbackAction);
        private object WorldMind_Ask(object requestObject, Action<WorldMindResponse> callback)
        {
            WorldMindRequest request = ConvertRequest(requestObject);
            AskWorldMind(request, callback);
            return true;
        }

        // Safer companion plugin hook. Uses only string callbacks so companion plugins do not need
        // to reference WorldMindV2 nested response classes.
        // Interface.CallHook("WorldMind_AskText", requestObject, Action<string> callbackAction);
        private object WorldMind_AskText(object requestObject, Action<string> callback)
        {
            WorldMindRequest request = ConvertRequest(requestObject);
            AskWorldMind(request, response =>
            {
                if (callback == null) return;
                callback(response == null ? "" : response.Message);
            });
            return true;
        }

        // Companion plugin usage:
        // Interface.CallHook("WorldMind_RecordEvent", pluginName, eventType, playerId, truthDictionary);
        private object WorldMind_RecordEvent(string pluginName, string eventType, string playerId, Dictionary<string, object> truth)
        {
            RecordEvent(pluginName, eventType, playerId, truth);
            return true;
        }

        private object WorldMind_GetPlayerMemory(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return null;
            PlayerMemoryRecord memory;
            return _data.PlayerMemory.TryGetValue(playerId, out memory) ? memory : null;
        }

        private object WorldMind_SetPlayerMemoryFact(string playerId, string key, string value)
        {
            if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(key)) return false;
            PlayerMemoryRecord memory = GetOrCreatePlayerMemory(playerId, "");
            memory.Facts[key] = value ?? "";
            memory.UpdatedUtc = DateTime.UtcNow.ToString("o");
            SaveData();
            return true;
        }

        private object WorldMind_GetServerFact(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string value;
            return _data.ServerFacts.TryGetValue(key, out value) ? value : null;
        }

        private object WorldMind_SetServerFact(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return false;
            _data.ServerFacts[key] = value ?? "";
            SaveData();
            return true;
        }

        private object WorldMind_LookupItem(string shortname)
        {
            if (string.IsNullOrEmpty(shortname)) return null;
            ItemInfo info;
            if (_data.ItemCache.TryGetValue(shortname, out info)) return info;
            return GetFallbackItemInfo(shortname);
        }

        private object WorldMind_DescribeLocation(Vector3 position)
        {
            return DescribeLocation(position);
        }

        private object WorldMind_GetConfigSummary()
        {
            return new Dictionary<string, object>
            {
                ["Plugin"] = Name,
                ["Version"] = Version.ToString(),
                ["ServerName"] = _config.ServerIdentity.ServerName,
                ["WorldMindEnabled"] = _config.WorldMind.Enabled,
                ["LmEndpointConfigured"] = !string.IsNullOrEmpty(_config.RequiredSetup.LmEndpoint),
                ["Model"] = _config.RequiredSetup.Model,
                ["SteamConfigured"] = !string.IsNullOrEmpty(_config.RequiredSetup.SteamApiKey),
                ["RustIoEnabled"] = _config.OptionalExternalDataProviders.RustIo.Enabled,
                ["RustToolsEnabled"] = _config.OptionalExternalDataProviders.RustTools.Enabled,
                ["RustItemApiEnabled"] = _config.OptionalExternalDataProviders.RustItemApi.Enabled,
                ["EnabledCommandCount"] = _config.CommandRegistry.Count(x => x.Enabled)
            };
        }

        #endregion

        #region Model Bridge

        private void AskWorldMind(WorldMindRequest request, Action<WorldMindResponse> callback)
        {
            if (callback == null) callback = response => { };

            if (request == null)
            {
                callback(WorldMindResponse.Error("Invalid WorldMind request."));
                return;
            }

            if (!_config.WorldMind.Enabled)
            {
                callback(WorldMindResponse.Error("WorldMind is disabled."));
                return;
            }

            if (string.IsNullOrEmpty(_config.RequiredSetup.LmEndpoint))
            {
                callback(WorldMindResponse.Error("LM endpoint is not configured."));
                return;
            }

            string systemPrompt = BuildSystemPrompt();
            string userPrompt = BuildUserPrompt(request);

            Dictionary<string, object> payload;
            if (_config.RequiredSetup.RequestFormat == "responses")
            {
                payload = new Dictionary<string, object>
                {
                    ["model"] = _config.RequiredSetup.Model,
                    ["input"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["role"] = "system",
                            ["content"] = systemPrompt
                        },
                        new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["content"] = userPrompt
                        }
                    },
                    ["max_output_tokens"] = _config.ModelBehavior.MaxOutputTokens,
                    ["temperature"] = _config.ModelBehavior.Temperature
                };
            }
            else
            {
                payload = new Dictionary<string, object>
                {
                    ["model"] = _config.RequiredSetup.Model,
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["role"] = "system",
                            ["content"] = systemPrompt
                        },
                        new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["content"] = userPrompt
                        }
                    },
                    ["max_tokens"] = _config.ModelBehavior.MaxOutputTokens,
                    ["temperature"] = _config.ModelBehavior.Temperature
                };
            }

            string body = JsonConvert.SerializeObject(payload);
            Dictionary<string, string> headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json"
            };

            if (!string.IsNullOrEmpty(_config.RequiredSetup.BearerToken))
                headers["Authorization"] = "Bearer " + _config.RequiredSetup.BearerToken;

            webrequest.Enqueue(_config.RequiredSetup.LmEndpoint, body, (code, response) =>
            {
                if (code < 200 || code >= 300 || string.IsNullOrEmpty(response))
                {
                    UpdateProviderHealth("Model", false, "HTTP " + code + ": " + Truncate(response, 300));
                    callback(WorldMindResponse.Error("Model request failed: HTTP " + code));
                    return;
                }

                UpdateProviderHealth("Model", true, "OK");
                callback(ParseModelResponse(response));
            }, this, RequestMethod.POST, headers, _config.ModelBehavior.TimeoutSeconds);
        }

        private WorldMindResponse ParseModelResponse(string raw)
        {
            try
            {
                Dictionary<string, object> parsed = JsonConvert.DeserializeObject<Dictionary<string, object>>(raw);
                string message = ExtractModelText(parsed);
                return new WorldMindResponse
                {
                    Message = CleanModelText(message),
                    Intent = "message",
                    Urgency = "normal",
                    SuggestedAction = "none",
                    MemoryUpdates = new Dictionary<string, object>()
                };
            }
            catch
            {
                return new WorldMindResponse
                {
                    Message = CleanModelText(raw),
                    Intent = "message",
                    Urgency = "normal",
                    SuggestedAction = "none",
                    MemoryUpdates = new Dictionary<string, object>()
                };
            }
        }

        private string ExtractModelText(Dictionary<string, object> parsed)
        {
            if (parsed == null) return "";

            object value;

            if (parsed.TryGetValue("output_text", out value))
                return Convert.ToString(value);

            if (parsed.TryGetValue("choices", out value))
            {
                string json = JsonConvert.SerializeObject(value);
                List<ChatChoice> choices = JsonConvert.DeserializeObject<List<ChatChoice>>(json);
                if (choices != null && choices.Count > 0 && choices[0].Message != null)
                    return choices[0].Message.Content ?? "";
            }

            if (parsed.TryGetValue("output", out value))
            {
                string json = JsonConvert.SerializeObject(value);
                List<ResponseOutput> outputs = JsonConvert.DeserializeObject<List<ResponseOutput>>(json);
                if (outputs != null)
                {
                    foreach (ResponseOutput output in outputs)
                    {
                        if (output == null || output.Content == null) continue;
                        foreach (ResponseContent content in output.Content)
                        {
                            if (content == null) continue;
                            if (!string.IsNullOrEmpty(content.Text)) return content.Text;
                            if (!string.IsNullOrEmpty(content.Content)) return content.Content;
                        }
                    }
                }
            }

            return JsonConvert.SerializeObject(parsed);
        }

        #endregion

        #region Prompt Builder

        private string BuildSystemPrompt()
        {
            List<string> enabledCommands = _config.CommandRegistry
                .Where(x => x.Enabled)
                .Select(x => x.Name + " = " + x.Command + " (" + x.Description + ")")
                .ToList();

            return string.Join("\n", new[]
            {
                "You are WorldMind, a generic Rust server intelligence layer.",
                "You are not an admin, not a tutorial popup, and not a rule enforcer unless the request comes from an admin-only system.",
                "Developer identity: " + MadeWithLoveTag + ". Author: Devi8d0ne.",
                "Server name: " + Safe(_config.ServerIdentity.ServerName),
                "Server description: " + Safe(_config.ServerIdentity.ServerDescription),
                "Gameplay style: " + Safe(_config.ServerIdentity.GameplayStyle),
                "Brand voice: " + Safe(_config.ServerIdentity.BrandVoice),
                "Configured tone: " + Safe(_config.WorldMind.DefaultTone),
                "Sarcasm level: " + Safe(_config.WorldMind.SarcasmLevel),
                "Profanity allowed: " + _config.WorldMind.ProfanityAllowed,
                "Trash talk allowed: " + _config.WorldMind.TrashTalkAllowed,
                "Enabled commands: " + (enabledCommands.Count == 0 ? "None configured." : string.Join("; ", enabledCommands.ToArray())),
                "Rules:",
                "1. Do not mention commands unless they are enabled in the command registry.",
                "2. Do not mention VIP, Discord, websites, economy systems, kits, homes, teleport, custom events, custom plugins, or PvP/PvE flagging unless the owner explicitly added and enabled them in the command registry or server facts.",
                "3. Do not invent URLs, commands, features, rules, plugins, prices, or server lore.",
                "4. Do not reference any specific server identity unless configured in ServerIdentity.",
                "5. Keep responses short enough for Rust chat unless a companion plugin requests a summary.",
                "6. No slurs, protected-class harassment, sexual content involving minors, or instructions for cheating/exploits.",
                "7. If information is missing, say what is missing instead of pretending it exists."
            });
        }

        private string BuildUserPrompt(WorldMindRequest request)
        {
            Dictionary<string, object> packet = new Dictionary<string, object>
            {
                ["plugin"] = request.Plugin ?? "unknown",
                ["eventType"] = request.EventType ?? "unknown",
                ["playerId"] = request.PlayerId ?? "",
                ["playerName"] = request.PlayerName ?? "",
                ["tone"] = request.Tone ?? _config.WorldMind.DefaultTone,
                ["urgency"] = request.Urgency,
                ["truth"] = request.Truth ?? new Dictionary<string, object>(),
                ["serverFacts"] = _data.ServerFacts,
                ["knownPlayerMemory"] = GetMemoryFactsForPrompt(request.PlayerId)
            };

            return "Respond to this Rust server event using only configured facts and enabled features. Return plain text only.\n" +
                   JsonConvert.SerializeObject(packet, Formatting.Indented);
        }

        #endregion

        #region Event Intake / Memory

        private void RecordEvent(string pluginName, string eventType, string playerId, Dictionary<string, object> truth)
        {
            WorldMindEventRecord record = new WorldMindEventRecord
            {
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                Plugin = pluginName ?? "unknown",
                EventType = eventType ?? "unknown",
                PlayerId = playerId ?? "",
                TruthJson = JsonConvert.SerializeObject(truth ?? new Dictionary<string, object>())
            };

            _data.EventTimeline.Add(record);

            int max = Math.Max(50, _config.Memory.MaxTimelineEvents);
            while (_data.EventTimeline.Count > max)
                _data.EventTimeline.RemoveAt(0);

            if (!string.IsNullOrEmpty(playerId))
            {
                PlayerMemoryRecord memory = GetOrCreatePlayerMemory(playerId, "");
                memory.LastSeenUtc = DateTime.UtcNow.ToString("o");
                memory.UpdatedUtc = DateTime.UtcNow.ToString("o");
                Increment(memory.Counters, eventType ?? "unknown");
            }

            SaveData();
        }

        private PlayerMemoryRecord GetOrCreatePlayerMemory(string playerId, string playerName)
        {
            PlayerMemoryRecord memory;
            if (!_data.PlayerMemory.TryGetValue(playerId, out memory))
            {
                memory = new PlayerMemoryRecord
                {
                    PlayerId = playerId,
                    PlayerName = playerName ?? "",
                    CreatedUtc = DateTime.UtcNow.ToString("o"),
                    UpdatedUtc = DateTime.UtcNow.ToString("o"),
                    LastSeenUtc = DateTime.UtcNow.ToString("o"),
                    Facts = new Dictionary<string, string>(),
                    Counters = new Dictionary<string, int>(),
                    Tags = new List<string>()
                };
                _data.PlayerMemory[playerId] = memory;
            }

            if (!string.IsNullOrEmpty(playerName)) memory.PlayerName = playerName;
            return memory;
        }

        private Dictionary<string, object> GetMemoryFactsForPrompt(string playerId)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            if (string.IsNullOrEmpty(playerId)) return result;

            PlayerMemoryRecord memory;
            if (!_data.PlayerMemory.TryGetValue(playerId, out memory)) return result;

            result["facts"] = memory.Facts;
            result["counters"] = memory.Counters;
            result["tags"] = memory.Tags;
            return result;
        }

        #endregion

        #region Optional Provider Services - Safe Stubs

        private ItemInfo GetFallbackItemInfo(string shortname)
        {
            return new ItemInfo
            {
                Shortname = shortname,
                DisplayName = shortname,
                Category = "unknown",
                Source = "fallback",
                UpdatedUtc = DateTime.UtcNow.ToString("o")
            };
        }

        private string DescribeLocation(Vector3 position)
        {
            string grid = GetGrid(position);
            return "Grid " + grid + " at " + FormatPosition(position);
        }

        private string GetGrid(Vector3 position)
        {
            // Generic fallback only. Rust:IO/RustTools/MapBrain providers can replace this with true map-aware location context.
            int x = Mathf.Clamp(Mathf.FloorToInt((position.x + 4500f) / 150f), 0, 25);
            int z = Mathf.Clamp(Mathf.FloorToInt((4500f - position.z) / 150f), 0, 25);
            char letter = (char)('A' + x);
            return letter + z.ToString();
        }

        private void UpdateProviderHealth(string provider, bool healthy, string message)
        {
            if (string.IsNullOrEmpty(provider)) return;
            _data.ProviderHealth[provider] = new ProviderHealthRecord
            {
                Provider = provider,
                Healthy = healthy,
                Message = message ?? "",
                CheckedUtc = DateTime.UtcNow.ToString("o")
            };
            SaveData();
        }

        #endregion

        #region Helpers

        private bool IsAdmin(BasePlayer player)
        {
            if (player == null) return false;
            return player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin);
        }

        private bool CanPlayerAsk(BasePlayer player)
        {
            if (player == null) return false;
            double now = Interface.Oxide.Now;
            double last;
            if (_lastAskByPlayer.TryGetValue(player.UserIDString, out last))
            {
                if (now - last < _config.WorldMind.PlayerAskCooldownSeconds) return false;
            }

            _lastAskByPlayer[player.UserIDString] = now;
            return true;
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null) return;
            SendReply(player, "<color=#00F0FF>[WorldMind]</color> " + message);
        }

        private string BuildStatusText()
        {
            List<string> parts = new List<string>
            {
                "WorldMind V2 status:",
                "Enabled: " + _config.WorldMind.Enabled,
                "LM endpoint configured: " + !string.IsNullOrEmpty(_config.RequiredSetup.LmEndpoint),
                "Model: " + Safe(_config.RequiredSetup.Model),
                "Steam API configured: " + !string.IsNullOrEmpty(_config.RequiredSetup.SteamApiKey),
                "RustIO enabled: " + _config.OptionalExternalDataProviders.RustIo.Enabled,
                "RustTools enabled: " + _config.OptionalExternalDataProviders.RustTools.Enabled,
                "RustItemApi enabled: " + _config.OptionalExternalDataProviders.RustItemApi.Enabled,
                "Enabled commands: " + _config.CommandRegistry.Count(x => x.Enabled),
                "Player memory records: " + _data.PlayerMemory.Count,
                "Timeline events: " + _data.EventTimeline.Count
            };
            return string.Join("\n", parts.ToArray());
        }

        private void PrintCleanStartupSummary()
        {
            Puts(DV8DAsciiTag);
            Puts("WorldMind V2 loaded. " + MadeWithLoveTag + ".");
            Puts("Required setup values live at the top of the config: LM endpoint, model, optional bearer token, and Steam API key.");
            Puts("No server-specific commands or features are assumed. Enable features and commands in config before WorldMind can mention them.");
        }

        private string FormatPosition(Vector3 position)
        {
            return Mathf.RoundToInt(position.x) + ", " + Mathf.RoundToInt(position.y) + ", " + Mathf.RoundToInt(position.z);
        }

        private string Safe(string value)
        {
            return string.IsNullOrEmpty(value) ? "not configured" : value;
        }

        private string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Length <= max) return value;
            return value.Substring(0, max) + "...";
        }

        private string CleanModelText(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length > _config.WorldMind.MaxChatCharacters)
                value = value.Substring(0, _config.WorldMind.MaxChatCharacters).Trim() + "...";
            return value;
        }

        private void Increment(Dictionary<string, int> counters, string key)
        {
            if (counters == null || string.IsNullOrEmpty(key)) return;
            if (!counters.ContainsKey(key)) counters[key] = 0;
            counters[key]++;
        }

        private WorldMindRequest ConvertRequest(object requestObject)
        {
            if (requestObject == null) return null;
            WorldMindRequest direct = requestObject as WorldMindRequest;
            if (direct != null) return direct;

            try
            {
                string json = JsonConvert.SerializeObject(requestObject);
                return JsonConvert.DeserializeObject<WorldMindRequest>(json);
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Config Classes

        private class PluginConfig
        {
            [JsonProperty(Order = 1, PropertyName = "REQUIRED SETUP - owner supplied values")]
            public RequiredSetupConfig RequiredSetup = new RequiredSetupConfig();

            [JsonProperty(Order = 2, PropertyName = "Server Identity - generic by default")]
            public ServerIdentityConfig ServerIdentity = new ServerIdentityConfig();

            [JsonProperty(Order = 3, PropertyName = "Command Registry - WorldMind may only mention enabled commands")]
            public List<CommandRegistryEntry> CommandRegistry = new List<CommandRegistryEntry>();

            [JsonProperty(Order = 4, PropertyName = "Optional External Data Providers - disabled by default")]
            public OptionalExternalDataProvidersConfig OptionalExternalDataProviders = new OptionalExternalDataProvidersConfig();

            [JsonProperty(Order = 5, PropertyName = "WorldMind Personality")]
            public WorldMindPersonalityConfig WorldMind = new WorldMindPersonalityConfig();

            [JsonProperty(Order = 6, PropertyName = "Model Behavior")]
            public ModelBehaviorConfig ModelBehavior = new ModelBehaviorConfig();

            [JsonProperty(Order = 7, PropertyName = "Memory Settings")]
            public MemoryConfig Memory = new MemoryConfig();

            [JsonProperty(Order = 8, PropertyName = "Admin UI / Shared UI Expectations")]
            public AdminUiConfig AdminUi = new AdminUiConfig();

            public static PluginConfig Default()
            {
                PluginConfig config = new PluginConfig();
                config.CommandRegistry = new List<CommandRegistryEntry>
                {
                    new CommandRegistryEntry { Name = "Homes", Enabled = false, Command = "/homes", Description = "Shows home commands or home menu." },
                    new CommandRegistryEntry { Name = "Kits", Enabled = false, Command = "/kit", Description = "Opens the server kit menu if installed." },
                    new CommandRegistryEntry { Name = "Discord", Enabled = false, Command = "/discord", Description = "Shows the server Discord link if configured." },
                    new CommandRegistryEntry { Name = "Website", Enabled = false, Command = "/website", Description = "Shows the server website link if configured." }
                };
                return config;
            }

            public void Normalize()
            {
                if (RequiredSetup == null) RequiredSetup = new RequiredSetupConfig();
                if (ServerIdentity == null) ServerIdentity = new ServerIdentityConfig();
                // If owner intentionally clears this list, keep it empty.
                // Only seed default commands when the config property is missing/null.
                if (CommandRegistry == null) CommandRegistry = Default().CommandRegistry;
                if (OptionalExternalDataProviders == null) OptionalExternalDataProviders = new OptionalExternalDataProvidersConfig();
                if (WorldMind == null) WorldMind = new WorldMindPersonalityConfig();
                if (ModelBehavior == null) ModelBehavior = new ModelBehaviorConfig();
                if (Memory == null) Memory = new MemoryConfig();
                if (AdminUi == null) AdminUi = new AdminUiConfig();
                RequiredSetup.Normalize();
                ModelBehavior.Normalize();
                WorldMind.Normalize();
            }
        }

        private class RequiredSetupConfig
        {
            public string LmEndpoint = "";
            public string Model = "";
            public string BearerToken = "";
            public string SteamApiKey = "";
            public string RequestFormat = "chat_completions";

            public void Normalize()
            {
                if (RequestFormat != "responses") RequestFormat = "chat_completions";
            }
        }

        private class ServerIdentityConfig
        {
            public string ServerName = "My Rust Server";
            public string ServerDescription = "A Rust server with custom gameplay.";
            public string GameplayStyle = "Vanilla, modded, PvP, PvE, or hybrid.";
            public string WebsiteUrl = "";
            public string DiscordUrl = "";
            public string BrandVoice = "Helpful, immersive, and Rust-aware.";
            public string OwnerNotes = "Add your own server identity, gameplay style, rules tone, and player-facing context here.";
        }

        private class CommandRegistryEntry
        {
            public string Name = "";
            public bool Enabled = false;
            public string Command = "";
            public string Description = "";
        }

        private class OptionalExternalDataProvidersConfig
        {
            public SteamProviderConfig Steam = new SteamProviderConfig();
            public RustIoProviderConfig RustIo = new RustIoProviderConfig();
            public RustToolsProviderConfig RustTools = new RustToolsProviderConfig();
            public RustItemApiProviderConfig RustItemApi = new RustItemApiProviderConfig();
        }

        private class SteamProviderConfig
        {
            public bool Enabled = false;
            public bool UseForPlayerProfileContext = true;
            public int CacheHours = 24;
        }

        private class RustIoProviderConfig
        {
            public bool Enabled = false;
            public string ApiKey = "";
            public string MapUrl = "";
            public bool UseForLocationContext = false;
            public bool UseForEventDirector = false;
            public int CacheMinutes = 15;
        }

        private class RustToolsProviderConfig
        {
            public bool Enabled = false;
            public string BaseUrl = "https://rusttools.xyz";
            public string ApiKey = "";
            public bool UseForItemLookup = false;
            public bool UseForMapLookup = false;
            public bool UseForIcons = false;
            public int CacheHours = 24;
        }

        private class RustItemApiProviderConfig
        {
            public bool Enabled = false;
            public string BaseUrl = "";
            public bool CacheItemData = true;
            public int RefreshHours = 24;
        }

        private class WorldMindPersonalityConfig
        {
            public bool Enabled = true;
            public string DefaultTone = "Immersive";
            public string SarcasmLevel = "Low";
            public bool ProfanityAllowed = false;
            public bool TrashTalkAllowed = false;
            public int PlayerAskCooldownSeconds = 20;
            public int MaxChatCharacters = 280;

            public void Normalize()
            {
                PlayerAskCooldownSeconds = Math.Max(1, PlayerAskCooldownSeconds);
                MaxChatCharacters = Math.Max(80, MaxChatCharacters);
            }
        }

        private class ModelBehaviorConfig
        {
            public int TimeoutSeconds = 20;
            public int MaxOutputTokens = 120;
            public float Temperature = 0.7f;

            public void Normalize()
            {
                TimeoutSeconds = Math.Max(5, TimeoutSeconds);
                MaxOutputTokens = Math.Max(20, MaxOutputTokens);
                Temperature = Mathf.Clamp(Temperature, 0f, 2f);
            }
        }

        private class MemoryConfig
        {
            public bool EnablePlayerMemory = true;
            public bool EnableAgentMemory = true;
            public bool EnableServerFacts = true;
            public bool EnableWorldEventTimeline = true;
            public int MaxTimelineEvents = 500;
        }

        private class AdminUiConfig
        {
            public string ThemeDefault = "Rust earth tones";
            public string[] SelectableBiomeColorProfiles = { "forest", "desert", "snow", "wasteland" };
            public bool PreferSharedWorldMindUiPlugin = true;
            public bool UseKitsStyleReplaceEntryEditor = true;
            public bool UseScrollSafePages = true;
            public bool ShowPromptPreviewPage = true;
            public bool ShowProviderHealthPage = true;
        }

        #endregion

        #region Data Classes

        private class StoredData
        {
            public Dictionary<string, PlayerMemoryRecord> PlayerMemory = new Dictionary<string, PlayerMemoryRecord>();
            public Dictionary<string, AgentMemoryRecord> AgentMemory = new Dictionary<string, AgentMemoryRecord>();
            public Dictionary<string, string> ServerFacts = new Dictionary<string, string>();
            public List<WorldMindEventRecord> EventTimeline = new List<WorldMindEventRecord>();
            public Dictionary<string, ItemInfo> ItemCache = new Dictionary<string, ItemInfo>();
            public Dictionary<string, ProviderHealthRecord> ProviderHealth = new Dictionary<string, ProviderHealthRecord>();
        }

        private class PlayerMemoryRecord
        {
            public string PlayerId = "";
            public string PlayerName = "";
            public string CreatedUtc = "";
            public string UpdatedUtc = "";
            public string LastSeenUtc = "";
            public Dictionary<string, string> Facts = new Dictionary<string, string>();
            public Dictionary<string, int> Counters = new Dictionary<string, int>();
            public List<string> Tags = new List<string>();
        }

        private class AgentMemoryRecord
        {
            public string AgentName = "";
            public Dictionary<string, string> Facts = new Dictionary<string, string>();
            public string UpdatedUtc = "";
        }

        private class WorldMindEventRecord
        {
            public string TimestampUtc = "";
            public string Plugin = "";
            public string EventType = "";
            public string PlayerId = "";
            public string TruthJson = "";
        }

        private class ItemInfo
        {
            public string Shortname = "";
            public string DisplayName = "";
            public string Category = "";
            public string IconUrl = "";
            public string Source = "";
            public string UpdatedUtc = "";
        }

        private class ProviderHealthRecord
        {
            public string Provider = "";
            public bool Healthy = false;
            public string Message = "";
            public string CheckedUtc = "";
        }

        #endregion

        #region Request / Response DTOs

        public class WorldMindRequest
        {
            public string Plugin = "";
            public string EventType = "";
            public string PlayerId = "";
            public string PlayerName = "";
            public string Tone = "";
            public int Urgency = 1;
            public Dictionary<string, object> Truth = new Dictionary<string, object>();
        }

        public class WorldMindResponse
        {
            public string Message = "";
            public string Intent = "";
            public string Urgency = "";
            public string SuggestedAction = "";
            public Dictionary<string, object> MemoryUpdates = new Dictionary<string, object>();

            public static WorldMindResponse Error(string message)
            {
                return new WorldMindResponse
                {
                    Message = message,
                    Intent = "error",
                    Urgency = "normal",
                    SuggestedAction = "none",
                    MemoryUpdates = new Dictionary<string, object>()
                };
            }
        }

        private class ChatChoice
        {
            [JsonProperty("message")]
            public ChatMessage Message;
        }

        private class ChatMessage
        {
            [JsonProperty("content")]
            public string Content;
        }

        private class ResponseOutput
        {
            [JsonProperty("content")]
            public List<ResponseContent> Content;
        }

        private class ResponseContent
        {
            [JsonProperty("text")]
            public string Text;

            [JsonProperty("content")]
            public string Content;
        }

        #endregion
    }
}
