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
    [Info("WorldMindProviderBrain", "Devi8d0ne", "1.0.0")]
    [Description("Optional external provider health, cache, and lookup bridge for the WorldMind plugin ecosystem.")]
    public class WorldMindProviderBrain : RustPlugin
    {
        private const string PermissionAdmin = "worldmindproviderbrain.admin";
        private const string PermissionUse = "worldmindproviderbrain.use";
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
        [PluginReference] private Plugin WorldMindItemBrain;
        [PluginReference] private Plugin WorldMindMapBrain;
        [PluginReference] private Plugin WorldMindDiscordMind;

        private PluginConfig _config;
        private StoredData _data;
        private bool _healthCheckRunning;

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
                Puts($"{MadeWithLoveTag} | Author: Devi8d0ne | Plugin: WorldMindProviderBrain");
            }

            if (_config.General.RunHealthCheckOnLoad)
                timer.Once(5f, RunHealthChecks);

            if (_config.General.EnablePeriodicHealthChecks && _config.General.HealthCheckIntervalMinutes > 0)
                timer.Every(Math.Max(300f, _config.General.HealthCheckIntervalMinutes * 60f), RunHealthChecks);

            timer.Every(Math.Max(60f, _config.General.AutoSaveSeconds), SaveData);

            Puts("WorldMindProviderBrain loaded.");
        }

        private void Unload()
        {
            SaveData();
        }

        #endregion

        #region Commands

        [ChatCommand("wmprovider")]
        private void CmdProvider(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (args == null || args.Length == 0)
            {
                if (!HasAdmin(player)) return;
                Reply(player,
                    "WorldMindProviderBrain commands:\n" +
                    "/wmprovider status\n" +
                    "/wmprovider health\n" +
                    "/wmprovider check\n" +
                    "/wmprovider cache\n" +
                    "/wmprovider clearcache\n" +
                    "/wmprovider item <shortname>\n" +
                    "/wmprovider rusttools <path>\n" +
                    "/wmprovider rustitemapi <path>\n" +
                    "/wmprovider rustio <path>\n" +
                    "/wmprovider reload\n" +
                    "/wmprovider save");
                return;
            }

            string sub = args[0].ToLowerInvariant();

            if (sub == "item")
            {
                if (!CanUse(player)) return;

                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmprovider item <shortname>");
                    return;
                }

                LookupItem(player, args[1]);
                return;
            }

            if (!HasAdmin(player)) return;

            if (sub == "status")
            {
                Reply(player, GetStatusText());
                return;
            }

            if (sub == "health")
            {
                Reply(player, BuildHealthText());
                return;
            }

            if (sub == "check")
            {
                RunHealthChecks();
                Reply(player, "Provider health checks started.");
                return;
            }

            if (sub == "cache")
            {
                Reply(player, BuildCacheText());
                return;
            }

            if (sub == "clearcache")
            {
                _data.Cache.Clear();
                SaveData();
                Reply(player, "Provider cache cleared.");
                return;
            }

            if (sub == "rusttools")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmprovider rusttools <path>");
                    return;
                }

                string path = string.Join(" ", args.Skip(1).ToArray());
                RequestProviderPath(player, "RustTools", _config.Providers.RustTools, path);
                return;
            }

            if (sub == "rustitemapi")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmprovider rustitemapi <path>");
                    return;
                }

                string path = string.Join(" ", args.Skip(1).ToArray());
                RequestProviderPath(player, "RustItemApi", _config.Providers.RustItemApi, path);
                return;
            }

            if (sub == "rustio")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmprovider rustio <path>");
                    return;
                }

                string path = string.Join(" ", args.Skip(1).ToArray());
                RequestProviderPath(player, "RustIO", _config.Providers.RustIo, path);
                return;
            }

            if (sub == "reload")
            {
                LoadPluginConfig();
                LoadData();
                Reply(player, "WorldMindProviderBrain reloaded.");
                return;
            }

            if (sub == "save")
            {
                SaveData();
                Reply(player, "WorldMindProviderBrain data saved.");
                return;
            }

            Reply(player, "Unknown command. Use /wmprovider for help.");
        }

        [ConsoleCommand("worldmindprovider.status")]
        private void ConsoleStatus(ConsoleSystem.Arg arg)
        {
            arg?.ReplyWith(GetStatusText());
        }

        [ConsoleCommand("worldmindprovider.health")]
        private void ConsoleHealth(ConsoleSystem.Arg arg)
        {
            arg?.ReplyWith(BuildHealthText());
        }

        [ConsoleCommand("worldmindprovider.check")]
        private void ConsoleCheck(ConsoleSystem.Arg arg)
        {
            RunHealthChecks();
            arg?.ReplyWith("Provider health checks started.");
        }

        [ConsoleCommand("worldmindprovider.reload")]
        private void ConsoleReload(ConsoleSystem.Arg arg)
        {
            LoadPluginConfig();
            LoadData();
            arg?.ReplyWith("WorldMindProviderBrain reloaded.");
        }

        #endregion

        #region Public hooks

        private object WorldMindProviderBrain_GetStatus()
        {
            return BuildStatusPacket();
        }

        private object WorldMindProviderBrain_GetProviderHealth(string providerName)
        {
            ProviderHealth health;
            return _data.Health.TryGetValue(NormalizeProvider(providerName), out health) ? health : null;
        }

        private object WorldMindProviderBrain_RunHealthChecks()
        {
            RunHealthChecks();
            return true;
        }

        private object WorldMindProviderBrain_GetCached(string key)
        {
            CacheEntry entry;
            if (!_data.Cache.TryGetValue(key ?? "", out entry)) return null;
            if (IsCacheExpired(entry)) return null;
            return entry.Body;
        }

        private object WorldMindProviderBrain_SetCached(string key, string body, int ttlMinutes)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;

            _data.Cache[key] = new CacheEntry
            {
                Key = key,
                Body = body ?? "",
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                ExpiresUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, ttlMinutes)).ToString("o")
            };

            SaveData();
            return true;
        }

        private object WorldMindProviderBrain_LookupItem(string shortname)
        {
            return LookupItemPacket(shortname);
        }

        private object WorldMindProviderBrain_RequestProviderPath(string providerName, string path)
        {
            ProviderSettings settings = GetProviderSettings(providerName);
            if (settings == null) return null;

            return RequestProviderPathSync(providerName, settings, path);
        }

        #endregion

        #region Health / requests

        private void RunHealthChecks()
        {
            if (_healthCheckRunning) return;

            _healthCheckRunning = true;

            List<Action<Action>> checks = new List<Action<Action>>();

            if (_config.Providers.Steam.Enabled)
                checks.Add(done => CheckProvider("Steam", _config.Providers.Steam, done));

            if (_config.Providers.RustIo.Enabled)
                checks.Add(done => CheckProvider("RustIO", _config.Providers.RustIo, done));

            if (_config.Providers.RustTools.Enabled)
                checks.Add(done => CheckProvider("RustTools", _config.Providers.RustTools, done));

            if (_config.Providers.RustItemApi.Enabled)
                checks.Add(done => CheckProvider("RustItemApi", _config.Providers.RustItemApi, done));

            if (checks.Count == 0)
            {
                _healthCheckRunning = false;
                SaveData();
                return;
            }

            RunChecksSequentially(checks, 0);
        }

        private void RunChecksSequentially(List<Action<Action>> checks, int index)
        {
            if (index >= checks.Count)
            {
                _healthCheckRunning = false;
                _data.LastHealthCheckUtc = DateTime.UtcNow.ToString("o");
                SaveData();
                RecordWorldMindEvent("provider_health_checked", BuildStatusPacket());
                return;
            }

            checks[index](() => RunChecksSequentially(checks, index + 1));
        }

        private void CheckProvider(string name, ProviderSettings settings, Action done)
        {
            string normalized = NormalizeProvider(name);

            if (settings == null || !settings.Enabled)
            {
                SetHealth(normalized, false, "disabled", 0, "");
                done?.Invoke();
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            {
                SetHealth(normalized, false, "missing BaseUrl", 0, "");
                done?.Invoke();
                return;
            }

            string url = BuildUrl(settings.BaseUrl, settings.HealthPath);

            Dictionary<string, string> headers = BuildHeaders(settings);

            webrequest.Enqueue(url, null, (code, response) =>
            {
                bool ok = code >= 200 && code < 400;
                string message = ok ? "ok" : $"HTTP {code}";
                SetHealth(normalized, ok, message, code, response);

                if (_config.General.Debug)
                    Puts($"Health {name}: {message}");

                done?.Invoke();
            }, this, RequestMethod.GET, headers, _config.General.RequestTimeoutSeconds);
        }

        private void SetHealth(string providerName, bool ok, string message, int httpCode, string response)
        {
            _data.Health[providerName] = new ProviderHealth
            {
                ProviderName = providerName,
                Enabled = true,
                Healthy = ok,
                Message = message ?? "",
                HttpCode = httpCode,
                LastCheckedUtc = DateTime.UtcNow.ToString("o"),
                LastResponsePreview = CleanText(response ?? "", _config.General.ResponsePreviewLength)
            };
        }

        private void RequestProviderPath(BasePlayer player, string providerName, ProviderSettings settings, string path)
        {
            if (settings == null)
            {
                Reply(player, "Provider settings not found.");
                return;
            }

            if (!settings.Enabled)
            {
                Reply(player, $"{providerName} is disabled.");
                return;
            }

            string cacheKey = $"{NormalizeProvider(providerName)}:{path}";
            CacheEntry cached;
            if (_config.Cache.UseCache && _data.Cache.TryGetValue(cacheKey, out cached) && !IsCacheExpired(cached))
            {
                Reply(player, $"Cached response:\n{CleanText(cached.Body, 1200)}");
                return;
            }

            string url = BuildUrl(settings.BaseUrl, path);
            Dictionary<string, string> headers = BuildHeaders(settings);

            webrequest.Enqueue(url, null, (code, response) =>
            {
                if (code >= 200 && code < 400)
                {
                    SetCache(cacheKey, response, settings.CacheMinutes);
                    Reply(player, $"HTTP {code}\n{CleanText(response, 1200)}");
                }
                else
                {
                    Reply(player, $"Request failed: HTTP {code}\n{CleanText(response, 500)}");
                }
            }, this, RequestMethod.GET, headers, _config.General.RequestTimeoutSeconds);
        }

        private string RequestProviderPathSync(string providerName, ProviderSettings settings, string path)
        {
            // Oxide webrequests are async. This hook returns cached data if present and queues refresh if missing.
            if (settings == null || !settings.Enabled) return null;

            string cacheKey = $"{NormalizeProvider(providerName)}:{path}";
            CacheEntry cached;
            if (_data.Cache.TryGetValue(cacheKey, out cached) && !IsCacheExpired(cached))
                return cached.Body;

            string url = BuildUrl(settings.BaseUrl, path);
            Dictionary<string, string> headers = BuildHeaders(settings);

            webrequest.Enqueue(url, null, (code, response) =>
            {
                if (code >= 200 && code < 400)
                    SetCache(cacheKey, response, settings.CacheMinutes);
            }, this, RequestMethod.GET, headers, _config.General.RequestTimeoutSeconds);

            return null;
        }

        private Dictionary<string, string> BuildHeaders(ProviderSettings settings)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();

            if (!string.IsNullOrWhiteSpace(settings.ApiKeyHeaderName) && !string.IsNullOrWhiteSpace(settings.ApiKey))
                headers[settings.ApiKeyHeaderName] = settings.ApiKey;

            if (!string.IsNullOrWhiteSpace(settings.BearerToken))
                headers["Authorization"] = $"Bearer {settings.BearerToken}";

            if (!string.IsNullOrWhiteSpace(settings.UserAgent))
                headers["User-Agent"] = settings.UserAgent;

            return headers;
        }

        private string BuildUrl(string baseUrl, string path)
        {
            baseUrl = (baseUrl ?? "").Trim();
            path = (path ?? "").Trim();

            if (string.IsNullOrWhiteSpace(path))
                return baseUrl;

            if (path.StartsWith("http://") || path.StartsWith("https://"))
                return path;

            if (baseUrl.EndsWith("/") && path.StartsWith("/"))
                return baseUrl.TrimEnd('/') + path;

            if (!baseUrl.EndsWith("/") && !path.StartsWith("/"))
                return baseUrl + "/" + path;

            return baseUrl + path;
        }

        private void SetCache(string key, string body, int ttlMinutes)
        {
            if (string.IsNullOrWhiteSpace(key)) return;

            if (_data.Cache.Count >= _config.Cache.MaxEntries)
            {
                string oldestKey = _data.Cache.Values.OrderBy(x => x.CreatedUtc).Select(x => x.Key).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(oldestKey))
                    _data.Cache.Remove(oldestKey);
            }

            _data.Cache[key] = new CacheEntry
            {
                Key = key,
                Body = body ?? "",
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                ExpiresUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, ttlMinutes)).ToString("o")
            };

            SaveData();
        }

        private bool IsCacheExpired(CacheEntry entry)
        {
            if (entry == null) return true;
            DateTime expires;
            if (!DateTime.TryParse(entry.ExpiresUtc, out expires)) return true;
            return DateTime.UtcNow >= expires;
        }

        #endregion

        #region Item lookup

        private object LookupItemPacket(string shortname)
        {
            if (string.IsNullOrWhiteSpace(shortname)) return null;

            if (WorldMindItemBrain != null)
            {
                try
                {
                    object result = WorldMindItemBrain.Call("WorldMindItemBrain_LookupItem", shortname);
                    if (result != null) return result;
                }
                catch { }
            }

            if (_config.Providers.RustItemApi.Enabled && _config.Providers.RustItemApi.UseForItemLookup)
            {
                string path = _config.Providers.RustItemApi.ItemLookupPath.Replace("{shortname}", Uri.EscapeDataString(shortname));
                object cached = WorldMindProviderBrain_RequestProviderPath("RustItemApi", path);
                if (cached != null) return cached;
            }

            if (_config.Providers.RustTools.Enabled && _config.Providers.RustTools.UseForItemLookup)
            {
                string path = _config.Providers.RustTools.ItemLookupPath.Replace("{shortname}", Uri.EscapeDataString(shortname));
                object cached = WorldMindProviderBrain_RequestProviderPath("RustTools", path);
                if (cached != null) return cached;
            }

            ItemDefinition def = ItemManager.FindItemDefinition(shortname);
            if (def == null) return null;

            return new Dictionary<string, object>
            {
                ["shortname"] = def.shortname,
                ["displayName"] = def.displayName == null ? def.shortname : def.displayName.english,
                ["itemId"] = def.itemid,
                ["category"] = def.category.ToString(),
                ["stackable"] = def.stackable,
                ["source"] = "local ItemManager fallback"
            };
        }

        private void LookupItem(BasePlayer player, string shortname)
        {
            object result = LookupItemPacket(shortname);

            if (result == null)
            {
                Reply(player, $"No item data found for {shortname}. If an external provider was enabled, a cache refresh may have been queued.");
                return;
            }

            Reply(player, CleanText(JsonConvert.SerializeObject(result, Formatting.Indented), 1400));
        }

        #endregion

        #region WorldMind

        private void RecordWorldMindEvent(string eventType, object payload)
        {
            if (!_config.WorldMindIntegration.RecordEventsToWorldMind || WorldMindV2 == null) return;

            try
            {
                Dictionary<string, object> packet = new Dictionary<string, object>
                {
                    ["plugin"] = "WorldMindProviderBrain",
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

        #region Reporting/helpers

        private string GetStatusText()
        {
            ProviderStatusPacket packet = BuildStatusPacket();

            return
                "WorldMindProviderBrain status\n" +
                $"WorldMindV2 linked: {(WorldMindV2 != null ? "yes" : "no")}\n" +
                $"ItemBrain linked: {(WorldMindItemBrain != null ? "yes" : "no")}\n" +
                $"MapBrain linked: {(WorldMindMapBrain != null ? "yes" : "no")}\n" +
                $"Providers enabled: {packet.EnabledProviderCount}\n" +
                $"Health records: {_data.Health.Count}\n" +
                $"Cache entries: {_data.Cache.Count}\n" +
                $"Last health check UTC: {(string.IsNullOrWhiteSpace(_data.LastHealthCheckUtc) ? "none" : _data.LastHealthCheckUtc)}";
        }

        private string BuildHealthText()
        {
            if (_data.Health.Count == 0)
                return "No provider health records yet. Run /wmprovider check.";

            List<string> lines = new List<string> { "Provider health:" };

            foreach (ProviderHealth health in _data.Health.Values.OrderBy(x => x.ProviderName))
            {
                lines.Add($"- {health.ProviderName}: {(health.Healthy ? "healthy" : "unhealthy")} | {health.Message} | HTTP {health.HttpCode} | {health.LastCheckedUtc}");
            }

            return string.Join("\n", lines.ToArray());
        }

        private string BuildCacheText()
        {
            if (_data.Cache.Count == 0)
                return "Provider cache is empty.";

            List<string> lines = new List<string> { $"Cache entries: {_data.Cache.Count}" };

            foreach (CacheEntry entry in _data.Cache.Values.OrderByDescending(x => x.CreatedUtc).Take(20))
            {
                lines.Add($"- {entry.Key} | expires={entry.ExpiresUtc} | chars={entry.Body?.Length ?? 0}");
            }

            return string.Join("\n", lines.ToArray());
        }

        private ProviderStatusPacket BuildStatusPacket()
        {
            List<ProviderConfigSummary> providers = new List<ProviderConfigSummary>
            {
                ProviderSummary("Steam", _config.Providers.Steam),
                ProviderSummary("RustIO", _config.Providers.RustIo),
                ProviderSummary("RustTools", _config.Providers.RustTools),
                ProviderSummary("RustItemApi", _config.Providers.RustItemApi)
            };

            return new ProviderStatusPacket
            {
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                WorldMindLinked = WorldMindV2 != null,
                ItemBrainLinked = WorldMindItemBrain != null,
                MapBrainLinked = WorldMindMapBrain != null,
                EnabledProviderCount = providers.Count(x => x.Enabled),
                Providers = providers,
                Health = _data.Health.Values.ToList(),
                CacheEntries = _data.Cache.Count,
                LastHealthCheckUtc = _data.LastHealthCheckUtc
            };
        }

        private ProviderConfigSummary ProviderSummary(string name, ProviderSettings settings)
        {
            if (settings == null) settings = new ProviderSettings();

            return new ProviderConfigSummary
            {
                Name = name,
                Enabled = settings.Enabled,
                HasBaseUrl = !string.IsNullOrWhiteSpace(settings.BaseUrl),
                HasApiKey = !string.IsNullOrWhiteSpace(settings.ApiKey) || !string.IsNullOrWhiteSpace(settings.BearerToken),
                UseForItemLookup = settings.UseForItemLookup,
                UseForMapLookup = settings.UseForMapLookup,
                UseForIcons = settings.UseForIcons,
                CacheMinutes = settings.CacheMinutes
            };
        }

        private ProviderSettings GetProviderSettings(string providerName)
        {
            string normalized = NormalizeProvider(providerName);

            if (normalized == "steam") return _config.Providers.Steam;
            if (normalized == "rustio") return _config.Providers.RustIo;
            if (normalized == "rusttools") return _config.Providers.RustTools;
            if (normalized == "rustitemapi") return _config.Providers.RustItemApi;

            return null;
        }

        private string NormalizeProvider(string providerName)
        {
            return (providerName ?? "").Trim().ToLowerInvariant().Replace(":", "").Replace("_", "").Replace("-", "");
        }

        private string CleanText(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";

            string clean = value.Trim();

            if (maxLength > 0 && clean.Length > maxLength)
                clean = clean.Substring(0, maxLength - 3) + "...";

            return clean;
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null || string.IsNullOrWhiteSpace(message)) return;
            player.ChatMessage($"<color=#d7b46a>[WorldMind ProviderBrain]</color> {message}");
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

            Reply(player, "You do not have permission to use this.");
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

            [JsonProperty("Cache")]
            public CacheSettings Cache = new CacheSettings();

            [JsonProperty("Providers - all disabled by default")]
            public ProviderGroupSettings Providers = new ProviderGroupSettings();

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
                if (Cache == null) Cache = new CacheSettings();
                if (Providers == null) Providers = new ProviderGroupSettings();
                if (WorldMindIntegration == null) WorldMindIntegration = new WorldMindIntegrationSettings();
                Providers.EnsureDefaults();
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

            [JsonProperty("RunHealthCheckOnLoad")]
            public bool RunHealthCheckOnLoad = false;

            [JsonProperty("EnablePeriodicHealthChecks")]
            public bool EnablePeriodicHealthChecks = false;

            [JsonProperty("HealthCheckIntervalMinutes")]
            public float HealthCheckIntervalMinutes = 60f;

            [JsonProperty("AutoSaveSeconds")]
            public float AutoSaveSeconds = 300f;

            [JsonProperty("RequestTimeoutSeconds")]
            public float RequestTimeoutSeconds = 10f;

            [JsonProperty("ResponsePreviewLength")]
            public int ResponsePreviewLength = 500;
        }

        private class CacheSettings
        {
            [JsonProperty("UseCache")]
            public bool UseCache = true;

            [JsonProperty("MaxEntries")]
            public int MaxEntries = 250;
        }

        private class ProviderGroupSettings
        {
            [JsonProperty("Steam")]
            public ProviderSettings Steam = new ProviderSettings
            {
                Enabled = false,
                BaseUrl = "https://api.steampowered.com",
                HealthPath = "",
                ApiKeyHeaderName = "",
                ApiKey = "",
                CacheMinutes = 1440
            };

            [JsonProperty("RustIO")]
            public ProviderSettings RustIo = new ProviderSettings
            {
                Enabled = false,
                BaseUrl = "",
                HealthPath = "",
                ApiKeyHeaderName = "",
                ApiKey = "",
                UseForMapLookup = false,
                CacheMinutes = 15
            };

            [JsonProperty("RustTools")]
            public ProviderSettings RustTools = new ProviderSettings
            {
                Enabled = false,
                BaseUrl = "https://rusttools.xyz",
                HealthPath = "",
                ApiKeyHeaderName = "",
                ApiKey = "",
                UseForItemLookup = false,
                UseForMapLookup = false,
                UseForIcons = false,
                ItemLookupPath = "",
                CacheMinutes = 1440
            };

            [JsonProperty("RustItemApi")]
            public ProviderSettings RustItemApi = new ProviderSettings
            {
                Enabled = false,
                BaseUrl = "",
                HealthPath = "",
                ApiKeyHeaderName = "",
                ApiKey = "",
                UseForItemLookup = false,
                ItemLookupPath = "",
                CacheMinutes = 1440
            };

            public void EnsureDefaults()
            {
                if (Steam == null) Steam = new ProviderSettings();
                if (RustIo == null) RustIo = new ProviderSettings();
                if (RustTools == null) RustTools = new ProviderSettings();
                if (RustItemApi == null) RustItemApi = new ProviderSettings();

                if (string.IsNullOrWhiteSpace(RustTools.BaseUrl))
                    RustTools.BaseUrl = "https://rusttools.xyz";
            }
        }

        private class ProviderSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = false;

            [JsonProperty("BaseUrl")]
            public string BaseUrl = "";

            [JsonProperty("HealthPath")]
            public string HealthPath = "";

            [JsonProperty("ApiKeyHeaderName")]
            public string ApiKeyHeaderName = "";

            [JsonProperty("ApiKey")]
            public string ApiKey = "";

            [JsonProperty("BearerToken")]
            public string BearerToken = "";

            [JsonProperty("UserAgent")]
            public string UserAgent = "WorldMindProviderBrain/1.0";

            [JsonProperty("UseForItemLookup")]
            public bool UseForItemLookup = false;

            [JsonProperty("UseForMapLookup")]
            public bool UseForMapLookup = false;

            [JsonProperty("UseForIcons")]
            public bool UseForIcons = false;

            [JsonProperty("ItemLookupPath - may use {shortname}")]
            public string ItemLookupPath = "";

            [JsonProperty("CacheMinutes")]
            public int CacheMinutes = 60;
        }

        private class WorldMindIntegrationSettings
        {
            [JsonProperty("RecordEventsToWorldMind")]
            public bool RecordEventsToWorldMind = true;
        }

        private class StoredData
        {
            [JsonProperty("LastHealthCheckUtc")]
            public string LastHealthCheckUtc = "";

            [JsonProperty("Health")]
            public Dictionary<string, ProviderHealth> Health = new Dictionary<string, ProviderHealth>();

            [JsonProperty("Cache")]
            public Dictionary<string, CacheEntry> Cache = new Dictionary<string, CacheEntry>();

            public void EnsureDefaults()
            {
                if (Health == null) Health = new Dictionary<string, ProviderHealth>();
                if (Cache == null) Cache = new Dictionary<string, CacheEntry>();
            }
        }

        public class ProviderHealth
        {
            public string ProviderName = "";
            public bool Enabled = false;
            public bool Healthy = false;
            public string Message = "";
            public int HttpCode = 0;
            public string LastCheckedUtc = "";
            public string LastResponsePreview = "";
        }

        public class CacheEntry
        {
            public string Key = "";
            public string Body = "";
            public string CreatedUtc = "";
            public string ExpiresUtc = "";
        }

        public class ProviderStatusPacket
        {
            public string TimestampUtc = "";
            public bool WorldMindLinked = false;
            public bool ItemBrainLinked = false;
            public bool MapBrainLinked = false;
            public int EnabledProviderCount = 0;
            public List<ProviderConfigSummary> Providers = new List<ProviderConfigSummary>();
            public List<ProviderHealth> Health = new List<ProviderHealth>();
            public int CacheEntries = 0;
            public string LastHealthCheckUtc = "";
        }

        public class ProviderConfigSummary
        {
            public string Name = "";
            public bool Enabled = false;
            public bool HasBaseUrl = false;
            public bool HasApiKey = false;
            public bool UseForItemLookup = false;
            public bool UseForMapLookup = false;
            public bool UseForIcons = false;
            public int CacheMinutes = 0;
        }

        #endregion
    }
}
