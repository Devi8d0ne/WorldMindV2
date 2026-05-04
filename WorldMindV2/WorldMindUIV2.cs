using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindUIV2", "Devi8d0ne", "2.0.1")]
    [Description("Shared WorldMind V2 admin dashboard and Kits-style full configuration UI foundation.")]
    public class WorldMindUIV2 : RustPlugin
    {
        private const string MadeWithLoveTag = "Made with love by Deviated Systems";
        private const string PermissionAdmin = "worldmindui.admin";
        private const string Panel = "WorldMindUIV2.Panel";
        private const string CoreConfigFileName = "WorldMindV2";
        private const string CoreDataFileName = "WorldMindV2/WorldMindData";

        [PluginReference] private Plugin WorldMindV2;

        private UiConfig _config;
        private readonly Dictionary<string, UiSession> _sessions = new Dictionary<string, UiSession>();

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermissionAdmin, this);
            LoadConfigValues();
        }

        protected override void LoadDefaultConfig()
        {
            _config = UiConfig.Default();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<UiConfig>();
                if (_config == null) throw new Exception("Config was null after read.");
                _config.Normalize();
            }
            catch (Exception ex)
            {
                PrintError("WorldMindUIV2 config read failed. Existing UI config was NOT overwritten. Runtime defaults are being used. Error: " + ex.Message);
                _config = UiConfig.Default();
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

        private void Unload()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                DestroyUi(player);
        }

        #endregion

        #region Commands

        [ChatCommand("wmui")]
        private void CmdWmUi(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player))
            {
                Reply(player, "No permission.");
                return;
            }

            string page = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "dashboard";
            int scroll = 0;
            if (args != null && args.Length > 1) int.TryParse(args[1], out scroll);
            OpenUi(player, page, scroll);
        }

        [ConsoleCommand("wmui.open")]
        private void CcmdOpen(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !IsAdmin(player)) return;
            string page = arg.Args != null && arg.Args.Length > 0 ? arg.Args[0].ToLowerInvariant() : "dashboard";
            int scroll = 0;
            if (arg.Args != null && arg.Args.Length > 1) int.TryParse(arg.Args[1], out scroll);
            OpenUi(player, page, scroll);
        }

        [ConsoleCommand("wmui.close")]
        private void CcmdClose(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null) return;
            DestroyUi(player);
        }

        [ConsoleCommand("wmui.refresh")]
        private void CcmdRefresh(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !IsAdmin(player)) return;
            UiSession session = GetSession(player);
            OpenUi(player, session.Page, session.Scroll);
        }

        [ConsoleCommand("wmui.set")]
        private void CcmdSet(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 2)
            {
                Reply(player, "Missing value.");
                return;
            }

            string path = arg.Args[0];
            string value = JoinArgs(arg.Args, 1);
            bool ok = SetCoreConfigValue(path, value, false);
            Reply(player, ok ? "Saved: " + path : "Failed to save: " + path);
            Refresh(player);
        }

        [ConsoleCommand("wmui.clear")]
        private void CcmdClear(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 1) return;
            string path = arg.Args[0];
            bool ok = SetCoreConfigValue(path, "", true);
            Reply(player, ok ? "Cleared: " + path : "Failed to clear: " + path);
            Refresh(player);
        }

        [ConsoleCommand("wmui.toggle")]
        private void CcmdToggle(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 1) return;
            string path = arg.Args[0];
            bool ok = ToggleCoreConfigBool(path);
            Reply(player, ok ? "Toggled: " + path : "Failed to toggle: " + path);
            Refresh(player);
        }

        [ConsoleCommand("wmui.number")]
        private void CcmdNumber(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            string path = arg.Args[0];
            string action = arg.Args[1];
            string exact = arg.Args.Length > 2 ? JoinArgs(arg.Args, 2) : "";
            bool ok = ChangeCoreNumber(path, action, exact);
            Reply(player, ok ? "Updated number: " + path : "Failed to update number: " + path);
            Refresh(player);
        }

        [ConsoleCommand("wmui.command.remove")]
        private void CcmdCommandRemove(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 1) return;
            int index;
            if (!int.TryParse(arg.Args[0], out index)) return;
            bool ok = RemoveCommandRegistryEntry(index);
            Reply(player, ok ? "Removed command registry entry." : "Failed to remove command registry entry.");
            Refresh(player);
        }

        [ConsoleCommand("wmui.command.toggle")]
        private void CcmdCommandToggle(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 1) return;
            int index;
            if (!int.TryParse(arg.Args[0], out index)) return;
            bool ok = ToggleCommandRegistryEntry(index);
            Reply(player, ok ? "Toggled command registry entry." : "Failed to toggle command registry entry.");
            Refresh(player);
        }

        [ConsoleCommand("wmui.command.add")]
        private void CcmdCommandAdd(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 1) return;
            string value = JoinArgs(arg.Args, 0);
            bool ok = AddCommandRegistryEntry(value);
            Reply(player, ok ? "Added command registry entry." : "Use: Name|/command|Description|true-or-false");
            Refresh(player);
        }

        [ConsoleCommand("wmui.fact.remove")]
        private void CcmdFactRemove(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 1) return;
            string key = JoinArgs(arg.Args, 0);
            bool ok = RemoveServerFact(key);
            Reply(player, ok ? "Removed server fact." : "Failed to remove server fact.");
            Refresh(player);
        }

        [ConsoleCommand("wmui.fact.add")]
        private void CcmdFactAdd(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 1) return;
            string value = JoinArgs(arg.Args, 0);
            bool ok = AddServerFact(value);
            Reply(player, ok ? "Saved server fact." : "Use: key=value");
            Refresh(player);
        }

        [ConsoleCommand("wmui.theme")]
        private void CcmdTheme(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 1) return;
            string profile = arg.Args[0].ToLowerInvariant();
            if (!_config.ThemeProfiles.ContainsKey(profile))
            {
                Reply(player, "Unknown theme profile.");
                return;
            }
            _config.SelectedThemeProfile = profile;
            SaveConfig();
            Reply(player, "Theme set to " + profile + ".");
            Refresh(player);
        }

        #endregion

        #region UI

        private void OpenUi(BasePlayer player, string page, int scroll)
        {
            if (player == null) return;
            UiSession session = GetSession(player);
            session.Page = NormalizePage(page);
            session.Scroll = Mathf.Max(0, scroll);
            DestroyUi(player);

            Theme t = GetTheme();
            JObject coreConfig = ReadCoreConfig();
            JObject coreData = ReadCoreData();
            int maxScroll = GetMaxScroll(session.Page, coreConfig, coreData);
            session.Scroll = Mathf.Clamp(session.Scroll, 0, maxScroll);

            CuiElementContainer c = new CuiElementContainer();
            c.Add(new CuiPanel
            {
                Image = { Color = t.Backdrop },
                RectTransform = { AnchorMin = "0.08 0.06", AnchorMax = "0.92 0.94" },
                CursorEnabled = true
            }, "Overlay", Panel);

            AddHeader(c, t, session.Page);
            AddNav(c, t, session.Page);

            if (session.Page == "dashboard") DrawDashboard(c, t, coreConfig, coreData, session.Scroll);
            else if (session.Page == "setup") DrawSetup(c, t, coreConfig, session.Scroll);
            else if (session.Page == "identity") DrawIdentity(c, t, coreConfig, session.Scroll);
            else if (session.Page == "commands") DrawCommands(c, t, coreConfig, session.Scroll);
            else if (session.Page == "facts") DrawFacts(c, t, coreData, session.Scroll);
            else if (session.Page == "personality") DrawPersonality(c, t, coreConfig, session.Scroll);
            else if (session.Page == "diagnostics") DrawDiagnostics(c, t, coreConfig, coreData, session.Scroll);
            else DrawDashboard(c, t, coreConfig, coreData, session.Scroll);

            AddScrollControls(c, t, session.Page, session.Scroll, maxScroll);
            CuiHelper.AddUi(player, c);
        }

        private void DestroyUi(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, Panel);
        }

        private void Refresh(BasePlayer player)
        {
            timer.Once(0.05f, () =>
            {
                if (player == null || !player.IsConnected) return;
                UiSession session = GetSession(player);
                OpenUi(player, session.Page, session.Scroll);
            });
        }

        private void AddHeader(CuiElementContainer c, Theme t, string page)
        {
            AddLabel(c, Panel, "WorldMind UI V2", 20, TextAnchor.MiddleLeft, "0.03 0.93", "0.45 0.985", t.Title);
            AddLabel(c, Panel, MadeWithLoveTag + " · Devi8d0ne", 11, TextAnchor.MiddleLeft, "0.03 0.895", "0.45 0.93", t.Muted);
            AddLabel(c, Panel, "Page: " + PrettyPage(page), 13, TextAnchor.MiddleRight, "0.55 0.93", "0.88 0.985", t.Text);
            AddButton(c, Panel, "X", "wmui.close", "0.94 0.935", "0.985 0.985", t.Danger, t.Title, 16);
        }

        private void AddNav(CuiElementContainer c, Theme t, string active)
        {
            string[] pages = { "dashboard", "setup", "identity", "commands", "facts", "personality", "diagnostics" };
            float x = 0.03f;
            float w = 0.122f;
            for (int i = 0; i < pages.Length; i++)
            {
                string page = pages[i];
                string color = page == active ? t.Accent : t.Panel;
                AddButton(c, Panel, PrettyPage(page), "wmui.open " + page + " 0", x + " 0.845", (x + w) + " 0.885", color, t.Title, 11);
                x += w + 0.007f;
            }
        }

        private void DrawDashboard(CuiElementContainer c, Theme t, JObject coreConfig, JObject coreData, int scroll)
        {
            AddSectionTitle(c, t, "Core Proof / Connection Reality", 0.795f);
            Dictionary<string, object> proof = GetCoreProof();
            int y = 0;
            AddInfoRow(c, t, y++, "WorldMindV2 PluginReference", WorldMindV2 == null ? "not loaded" : "loaded - not proof by itself");
            AddInfoRow(c, t, y++, "Endpoint configured", HasCoreValue(coreConfig, "REQUIRED SETUP - owner supplied values", "LmEndpoint") ? "yes" : "no");
            AddInfoRow(c, t, y++, "Model", GetCoreValue(coreConfig, "REQUIRED SETUP - owner supplied values", "Model"));
            AddInfoRow(c, t, y++, "Proof object", proof.Count == 0 ? "not available - update WorldMindV2 Core first" : "available");
            AddInfoRow(c, t, y++, "Last error", ProofValue(proof, "LastError", "none / unavailable"));
            AddInfoRow(c, t, y++, "Last cleaned response", Truncate(ProofValue(proof, "LastCleanedResponsePreview", "none"), 90));
            AddInfoRow(c, t, y++, "Counters", BuildCounterSummary(proof));

            AddSectionTitle(c, t, "Registered Companion Plugins", 0.435f);
            List<string> plugins = GetPluginStatusLines();
            if (plugins.Count == 0) plugins.Add("No companion plugin status returned yet.");
            DrawPagedTextList(c, t, plugins, scroll, 8, 0.395f, 0.055f);
        }

        private void DrawSetup(CuiElementContainer c, Theme t, JObject coreConfig, int scroll)
        {
            AddSectionTitle(c, t, "Core Setup - owner supplied values at the top", 0.795f);
            string section = "REQUIRED SETUP - owner supplied values";
            int y = 0;
            DrawEditableString(c, t, y++, section, "LmEndpoint", GetCoreValue(coreConfig, section, "LmEndpoint"), "LM Endpoint");
            DrawEditableString(c, t, y++, section, "Model", GetCoreValue(coreConfig, section, "Model"), "Model");
            DrawEditableString(c, t, y++, section, "BearerToken", MaskSecret(GetCoreValue(coreConfig, section, "BearerToken")), "Bearer Token");
            DrawEditableString(c, t, y++, section, "SteamApiKey", MaskSecret(GetCoreValue(coreConfig, section, "SteamApiKey")), "Steam API Key");
            DrawEditableString(c, t, y++, section, "RequestFormat", GetCoreValue(coreConfig, section, "RequestFormat"), "Request Format");
            AddLabel(c, Panel, "RequestFormat accepts chat_completions or responses. Remove clears the line value; replacement saves only this selected field.", 11, TextAnchor.MiddleLeft, "0.055 0.175", "0.88 0.215", t.Muted);
        }

        private void DrawIdentity(CuiElementContainer c, Theme t, JObject coreConfig, int scroll)
        {
            AddSectionTitle(c, t, "Server Identity - generic defaults, owner-specific when configured", 0.795f);
            string section = "Server Identity - generic by default";
            int y = 0;
            DrawEditableString(c, t, y++, section, "ServerName", GetCoreValue(coreConfig, section, "ServerName"), "Server Name");
            DrawEditableString(c, t, y++, section, "ServerDescription", GetCoreValue(coreConfig, section, "ServerDescription"), "Description");
            DrawEditableString(c, t, y++, section, "GameplayStyle", GetCoreValue(coreConfig, section, "GameplayStyle"), "Gameplay Style");
            DrawEditableString(c, t, y++, section, "WebsiteUrl", GetCoreValue(coreConfig, section, "WebsiteUrl"), "Website URL");
            DrawEditableString(c, t, y++, section, "DiscordUrl", GetCoreValue(coreConfig, section, "DiscordUrl"), "Discord URL");
            DrawEditableString(c, t, y++, section, "BrandVoice", GetCoreValue(coreConfig, section, "BrandVoice"), "Brand Voice");
            DrawEditableString(c, t, y++, section, "OwnerNotes", GetCoreValue(coreConfig, section, "OwnerNotes"), "Owner Notes");
        }

        private void DrawCommands(CuiElementContainer c, Theme t, JObject coreConfig, int scroll)
        {
            AddSectionTitle(c, t, "Command Registry - remove, re-add, toggle, and replace lines", 0.795f);
            JArray arr = GetCoreArray(coreConfig, "Command Registry - WorldMind may only mention enabled commands");
            List<JObject> items = arr.Children<JObject>().ToList();
            int maxRows = 7;
            int start = Mathf.Clamp(scroll, 0, Mathf.Max(0, items.Count - 1));
            int y = 0;
            for (int i = start; i < items.Count && y < maxRows; i++, y++)
            {
                JObject o = items[i];
                float top = 0.755f - y * 0.083f;
                float bottom = top - 0.072f;
                AddBox(c, Panel, "0.045 " + bottom, "0.885 " + top, t.Row);
                string label = "#" + i + " " + JString(o, "Name") + " · " + JString(o, "Command") + " · Enabled: " + JBool(o, "Enabled");
                AddLabel(c, Panel, Truncate(label, 82), 11, TextAnchor.MiddleLeft, "0.055 " + (bottom + 0.035f), "0.68 " + top, t.Text);
                AddLabel(c, Panel, Truncate(JString(o, "Description"), 90), 10, TextAnchor.MiddleLeft, "0.055 " + bottom, "0.68 " + (bottom + 0.035f), t.Muted);
                AddButton(c, Panel, JBool(o, "Enabled") ? "Disable" : "Enable", "wmui.command.toggle " + i, "0.695 " + (bottom + 0.01f), "0.765 " + (top - 0.01f), t.Accent, t.Title, 10);
                AddButton(c, Panel, "Remove", "wmui.command.remove " + i, "0.775 " + (bottom + 0.01f), "0.875 " + (top - 0.01f), t.Danger, t.Title, 10);
            }

            AddLabel(c, Panel, "Add / re-add command line: Name|/command|Description|true", 11, TextAnchor.MiddleLeft, "0.045 0.125", "0.44 0.16", t.Muted);
            AddInput(c, Panel, "Kits|/kit|Opens the kit menu.|true", "wmui.command.add", "0.045 0.075", "0.68 0.118", t.Input, t.Text, 11);
            AddLabel(c, Panel, "Enter text then press Enter", 10, TextAnchor.MiddleLeft, "0.695 0.075", "0.875 0.118", t.Muted);
        }

        private void DrawFacts(CuiElementContainer c, Theme t, JObject coreData, int scroll)
        {
            AddSectionTitle(c, t, "Server Facts - key/value memory the model may use", 0.795f);
            JObject facts = coreData["ServerFacts"] as JObject;
            if (facts == null) facts = new JObject();
            List<JProperty> props = facts.Properties().ToList();
            int maxRows = 8;
            int start = Mathf.Clamp(scroll, 0, Mathf.Max(0, props.Count - 1));
            int y = 0;
            for (int i = start; i < props.Count && y < maxRows; i++, y++)
            {
                JProperty p = props[i];
                float top = 0.755f - y * 0.07f;
                float bottom = top - 0.058f;
                AddBox(c, Panel, "0.045 " + bottom, "0.885 " + top, t.Row);
                AddLabel(c, Panel, p.Name + " = " + Truncate(Convert.ToString(p.Value), 85), 11, TextAnchor.MiddleLeft, "0.055 " + bottom, "0.745 " + top, t.Text);
                AddButton(c, Panel, "Remove", "wmui.fact.remove " + EscapeArg(p.Name), "0.765 " + (bottom + 0.006f), "0.875 " + (top - 0.006f), t.Danger, t.Title, 10);
            }

            AddLabel(c, Panel, "Add / re-add fact: key=value", 11, TextAnchor.MiddleLeft, "0.045 0.125", "0.35 0.16", t.Muted);
            AddInput(c, Panel, "discord=https://discord.gg/example", "wmui.fact.add", "0.045 0.075", "0.68 0.118", t.Input, t.Text, 11);
        }

        private void DrawPersonality(CuiElementContainer c, Theme t, JObject coreConfig, int scroll)
        {
            AddSectionTitle(c, t, "WorldMind Personality - controlled without becoming soulless", 0.795f);
            string section = "WorldMind Personality";
            int y = 0;
            DrawBool(c, t, y++, section, "Enabled", GetCoreBool(coreConfig, section, "Enabled"));
            DrawEditableString(c, t, y++, section, "DefaultTone", GetCoreValue(coreConfig, section, "DefaultTone"), "Default Tone");
            DrawEditableString(c, t, y++, section, "SarcasmLevel", GetCoreValue(coreConfig, section, "SarcasmLevel"), "Sarcasm Level");
            DrawBool(c, t, y++, section, "ProfanityAllowed", GetCoreBool(coreConfig, section, "ProfanityAllowed"));
            DrawBool(c, t, y++, section, "TrashTalkAllowed", GetCoreBool(coreConfig, section, "TrashTalkAllowed"));
            DrawNumber(c, t, y++, section, "PlayerAskCooldownSeconds", GetCoreValue(coreConfig, section, "PlayerAskCooldownSeconds"));
            DrawNumber(c, t, y++, section, "MaxChatCharacters", GetCoreValue(coreConfig, section, "MaxChatCharacters"));
        }

        private void DrawDiagnostics(CuiElementContainer c, Theme t, JObject coreConfig, JObject coreData, int scroll)
        {
            AddSectionTitle(c, t, "Diagnostics - proof, not vibes", 0.795f);
            Dictionary<string, object> proof = GetCoreProof();
            List<string> lines = new List<string>();
            if (proof.Count == 0)
            {
                lines.Add("No proof object returned. Install/update WorldMindV2 Core with WorldMind_GetProof.");
            }
            else
            {
                foreach (KeyValuePair<string, object> kv in proof.OrderBy(x => x.Key))
                    lines.Add(kv.Key + ": " + Truncate(Convert.ToString(kv.Value), 120));
            }

            lines.Add("Core config present: " + (coreConfig.HasValues ? "yes" : "no"));
            lines.Add("Core data present: " + (coreData.HasValues ? "yes" : "no"));
            lines.Add("Timeline events: " + CountArray(coreData, "EventTimeline"));
            lines.Add("Player memory records: " + CountObject(coreData, "PlayerMemory"));
            DrawPagedTextList(c, t, lines, scroll, 10, 0.755f, 0.06f);

            AddSectionTitle(c, t, "UI Theme Profiles", 0.135f);
            float x = 0.045f;
            foreach (string key in _config.ThemeProfiles.Keys.ToList())
            {
                AddButton(c, Panel, key, "wmui.theme " + key, x + " 0.075", (x + 0.12f) + " 0.115", key == _config.SelectedThemeProfile ? t.Accent : t.Panel, t.Title, 10);
                x += 0.13f;
            }
        }

        private int GetMaxScroll(string page, JObject coreConfig, JObject coreData)
        {
            if (page == "dashboard")
            {
                List<string> plugins = GetPluginStatusLines();
                int count = plugins.Count == 0 ? 1 : plugins.Count;
                return Mathf.Max(0, count - 8);
            }

            if (page == "commands")
            {
                JArray arr = GetCoreArray(coreConfig, "Command Registry - WorldMind may only mention enabled commands");
                return Mathf.Max(0, arr.Count - 7);
            }

            if (page == "facts")
            {
                JObject facts = coreData["ServerFacts"] as JObject;
                int count = facts == null ? 0 : facts.Properties().Count();
                return Mathf.Max(0, count - 8);
            }

            if (page == "diagnostics")
            {
                Dictionary<string, object> proof = GetCoreProof();
                int count = proof.Count == 0 ? 1 : proof.Count;
                count += 4;
                return Mathf.Max(0, count - 10);
            }

            return 0;
        }

        private void AddScrollControls(CuiElementContainer c, Theme t, string page, int scroll, int maxScroll)
        {
            AddButton(c, Panel, "⟳", "wmui.refresh", "0.895 0.845", "0.925 0.885", t.Accent, t.Title, 12);

            float railMinX = 0.902f;
            float railMaxX = 0.918f;
            float railBottom = 0.125f;
            float railTop = 0.82f;

            AddLabel(c, Panel, "SCROLL", 8, TextAnchor.MiddleCenter, "0.887 0.805", "0.933 0.835", t.Muted);
            AddBox(c, Panel, railMinX + " " + railBottom, railMaxX + " " + railTop, t.Panel);

            if (maxScroll <= 0)
            {
                AddBox(c, Panel, (railMinX + 0.002f) + " " + (railBottom + 0.006f), (railMaxX - 0.002f) + " " + (railTop - 0.006f), t.Muted);
                AddLabel(c, Panel, "0/0", 8, TextAnchor.MiddleCenter, "0.885 0.09", "0.935 0.115", t.Muted);
                return;
            }

            int previous = Mathf.Max(0, scroll - _config.ScrollStep);
            int next = Mathf.Min(maxScroll, scroll + _config.ScrollStep);

            AddButton(c, Panel, "", "wmui.open " + page + " " + previous, railMinX + " " + ((railBottom + railTop) * 0.5f), railMaxX + " " + railTop, "0 0 0 0.001", t.Title, 1);
            AddButton(c, Panel, "", "wmui.open " + page + " " + next, railMinX + " " + railBottom, railMaxX + " " + ((railBottom + railTop) * 0.5f), "0 0 0 0.001", t.Title, 1);

            float railHeight = railTop - railBottom;
            float thumbHeight = Mathf.Clamp(railHeight * 0.22f, 0.095f, 0.18f);
            float travel = railHeight - thumbHeight - 0.012f;
            float ratio = Mathf.Clamp01((float)scroll / (float)maxScroll);
            float thumbTop = railTop - 0.006f - ratio * travel;
            float thumbBottom = thumbTop - thumbHeight;

            AddBox(c, Panel, (railMinX + 0.002f) + " " + thumbBottom, (railMaxX - 0.002f) + " " + thumbTop, t.Accent);
            AddLabel(c, Panel, scroll + "/" + maxScroll, 8, TextAnchor.MiddleCenter, "0.885 0.09", "0.935 0.115", t.Muted);
        }

        private void DrawEditableString(CuiElementContainer c, Theme t, int row, string section, string key, string value, string label)
        {
            float top = 0.755f - row * 0.083f;
            float bottom = top - 0.072f;
            string path = EncodePath(section, key);
            AddBox(c, Panel, "0.045 " + bottom, "0.885 " + top, t.Row);
            AddLabel(c, Panel, label + ":", 11, TextAnchor.MiddleLeft, "0.055 " + (bottom + 0.035f), "0.22 " + top, t.Title);
            AddLabel(c, Panel, Truncate(value, 72), 10, TextAnchor.MiddleLeft, "0.22 " + (bottom + 0.035f), "0.67 " + top, t.Text);
            AddInput(c, Panel, "replacement value", "wmui.set " + path, "0.22 " + (bottom + 0.008f), "0.67 " + (bottom + 0.034f), t.Input, t.Text, 10);
            AddButton(c, Panel, "Clear", "wmui.clear " + path, "0.695 " + (bottom + 0.01f), "0.765 " + (top - 0.01f), t.Danger, t.Title, 10);
            AddLabel(c, Panel, "Enter saves replacement", 9, TextAnchor.MiddleCenter, "0.775 " + (bottom + 0.01f), "0.875 " + (top - 0.01f), t.Muted);
        }

        private void DrawBool(CuiElementContainer c, Theme t, int row, string section, string key, bool value)
        {
            float top = 0.755f - row * 0.083f;
            float bottom = top - 0.072f;
            string path = EncodePath(section, key);
            AddBox(c, Panel, "0.045 " + bottom, "0.885 " + top, t.Row);
            AddLabel(c, Panel, key + ": " + (value ? "ON" : "OFF"), 12, TextAnchor.MiddleLeft, "0.055 " + bottom, "0.55 " + top, t.Text);
            AddButton(c, Panel, value ? "Turn Off" : "Turn On", "wmui.toggle " + path, "0.695 " + (bottom + 0.01f), "0.875 " + (top - 0.01f), value ? t.Accent : t.Panel, t.Title, 10);
        }

        private void DrawNumber(CuiElementContainer c, Theme t, int row, string section, string key, string value)
        {
            float top = 0.755f - row * 0.083f;
            float bottom = top - 0.072f;
            string path = EncodePath(section, key);
            AddBox(c, Panel, "0.045 " + bottom, "0.885 " + top, t.Row);
            AddLabel(c, Panel, key + ": " + value, 12, TextAnchor.MiddleLeft, "0.055 " + (bottom + 0.035f), "0.35 " + top, t.Text);
            AddButton(c, Panel, "-", "wmui.number " + path + " dec", "0.37 " + (bottom + 0.01f), "0.42 " + (top - 0.01f), t.Panel, t.Title, 14);
            AddButton(c, Panel, "+", "wmui.number " + path + " inc", "0.43 " + (bottom + 0.01f), "0.48 " + (top - 0.01f), t.Panel, t.Title, 14);
            AddInput(c, Panel, "exact value", "wmui.number " + path + " exact", "0.50 " + (bottom + 0.012f), "0.68 " + (top - 0.012f), t.Input, t.Text, 10);
            AddButton(c, Panel, "Clear", "wmui.clear " + path, "0.695 " + (bottom + 0.01f), "0.765 " + (top - 0.01f), t.Danger, t.Title, 10);
        }

        private void DrawPagedTextList(CuiElementContainer c, Theme t, List<string> lines, int scroll, int maxRows, float startTop, float rowHeight)
        {
            int start = Mathf.Clamp(scroll, 0, Mathf.Max(0, lines.Count - 1));
            int y = 0;
            for (int i = start; i < lines.Count && y < maxRows; i++, y++)
            {
                float top = startTop - y * rowHeight;
                float bottom = top - (rowHeight - 0.008f);
                AddBox(c, Panel, "0.045 " + bottom, "0.885 " + top, t.Row);
                AddLabel(c, Panel, Truncate(lines[i], 130), 10, TextAnchor.MiddleLeft, "0.055 " + bottom, "0.875 " + top, t.Text);
            }
        }

        private void AddInfoRow(CuiElementContainer c, Theme t, int row, string key, string value)
        {
            float top = 0.755f - row * 0.05f;
            float bottom = top - 0.042f;
            AddBox(c, Panel, "0.045 " + bottom, "0.885 " + top, t.Row);
            AddLabel(c, Panel, key, 10, TextAnchor.MiddleLeft, "0.055 " + bottom, "0.33 " + top, t.Title);
            AddLabel(c, Panel, value, 10, TextAnchor.MiddleLeft, "0.34 " + bottom, "0.875 " + top, t.Text);
        }

        private void AddSectionTitle(CuiElementContainer c, Theme t, string text, float y)
        {
            AddLabel(c, Panel, text, 14, TextAnchor.MiddleLeft, "0.045 " + (y - 0.035f), "0.885 " + y, t.Title);
        }

        private void AddBox(CuiElementContainer c, string parent, string min, string max, string color)
        {
            c.Add(new CuiPanel { Image = { Color = color }, RectTransform = { AnchorMin = min, AnchorMax = max }, CursorEnabled = false }, parent);
        }

        private void AddLabel(CuiElementContainer c, string parent, string text, int size, TextAnchor align, string min, string max, string color)
        {
            c.Add(new CuiLabel
            {
                Text = { Text = text ?? "", FontSize = size, Align = align, Color = color },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);
        }

        private void AddButton(CuiElementContainer c, string parent, string text, string command, string min, string max, string buttonColor, string textColor, int fontSize)
        {
            c.Add(new CuiButton
            {
                Button = { Color = buttonColor, Command = command },
                RectTransform = { AnchorMin = min, AnchorMax = max },
                Text = { Text = text, FontSize = fontSize, Align = TextAnchor.MiddleCenter, Color = textColor }
            }, parent);
        }

        private void AddInput(CuiElementContainer c, string parent, string placeholder, string command, string min, string max, string color, string textColor, int fontSize)
        {
            CuiElement e = new CuiElement { Parent = parent };
            e.Components.Add(new CuiImageComponent { Color = color });
            e.Components.Add(new CuiRectTransformComponent { AnchorMin = min, AnchorMax = max });
            e.Components.Add(new CuiInputFieldComponent
            {
                Text = placeholder,
                Command = command,
                FontSize = fontSize,
                Align = TextAnchor.MiddleLeft,
                Color = textColor,
                CharsLimit = 512
            });
            c.Add(e);
        }

        #endregion

        #region Config/Data Editing

        private JObject ReadCoreConfig()
        {
            try
            {
                DynamicConfigFile file = Interface.Oxide.ConfigFileSystem.GetFile(CoreConfigFileName);
                JObject root = file.ReadObject<JObject>();
                return root ?? new JObject();
            }
            catch (Exception ex)
            {
                PrintWarning("Could not read WorldMindV2 config: " + ex.Message);
                return new JObject();
            }
        }

        private bool WriteCoreConfig(JObject root)
        {
            try
            {
                DynamicConfigFile file = Interface.Oxide.ConfigFileSystem.GetFile(CoreConfigFileName);
                file.WriteObject(root, true);
                return true;
            }
            catch (Exception ex)
            {
                PrintWarning("Could not write WorldMindV2 config: " + ex.Message);
                return false;
            }
        }

        private JObject ReadCoreData()
        {
            try
            {
                JObject root = Interface.Oxide.DataFileSystem.ReadObject<JObject>(CoreDataFileName);
                return root ?? new JObject();
            }
            catch
            {
                return new JObject();
            }
        }

        private bool WriteCoreData(JObject root)
        {
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject(CoreDataFileName, root);
                return true;
            }
            catch (Exception ex)
            {
                PrintWarning("Could not write WorldMindV2 data: " + ex.Message);
                return false;
            }
        }

        private bool SetCoreConfigValue(string encodedPath, string value, bool clear)
        {
            string section, key;
            if (!DecodePath(encodedPath, out section, out key)) return false;
            JObject root = ReadCoreConfig();
            JObject sec = root[section] as JObject;
            if (sec == null)
            {
                sec = new JObject();
                root[section] = sec;
            }

            if (clear)
            {
                sec[key] = "";
            }
            else
            {
                JToken old = sec[key];
                if (old != null && old.Type == JTokenType.Boolean)
                {
                    bool b;
                    if (bool.TryParse(value, out b)) sec[key] = b;
                    else sec[key] = value;
                }
                else if (old != null && (old.Type == JTokenType.Integer || old.Type == JTokenType.Float))
                {
                    double d;
                    if (double.TryParse(value, out d)) sec[key] = old.Type == JTokenType.Integer ? new JValue((int)d) : new JValue(d);
                    else sec[key] = value;
                }
                else
                {
                    sec[key] = value ?? "";
                }
            }

            return WriteCoreConfig(root);
        }

        private bool ToggleCoreConfigBool(string encodedPath)
        {
            string section, key;
            if (!DecodePath(encodedPath, out section, out key)) return false;
            JObject root = ReadCoreConfig();
            JObject sec = root[section] as JObject;
            if (sec == null) return false;
            bool current = sec[key] != null && sec[key].Type == JTokenType.Boolean && sec[key].Value<bool>();
            sec[key] = !current;
            return WriteCoreConfig(root);
        }

        private bool ChangeCoreNumber(string encodedPath, string action, string exact)
        {
            string section, key;
            if (!DecodePath(encodedPath, out section, out key)) return false;
            JObject root = ReadCoreConfig();
            JObject sec = root[section] as JObject;
            if (sec == null) return false;
            double current = 0;
            if (sec[key] != null) double.TryParse(Convert.ToString(sec[key]), out current);
            if (action == "inc") current += 1;
            else if (action == "dec") current -= 1;
            else if (action == "exact")
            {
                double parsed;
                if (!double.TryParse(exact, out parsed)) return false;
                current = parsed;
            }
            sec[key] = Math.Abs(current - Math.Round(current)) < 0.0001 ? new JValue((int)Math.Round(current)) : new JValue(current);
            return WriteCoreConfig(root);
        }

        private bool RemoveCommandRegistryEntry(int index)
        {
            JObject root = ReadCoreConfig();
            JArray arr = GetCoreArray(root, "Command Registry - WorldMind may only mention enabled commands");
            if (index < 0 || index >= arr.Count) return false;
            arr.RemoveAt(index);
            root["Command Registry - WorldMind may only mention enabled commands"] = arr;
            return WriteCoreConfig(root);
        }

        private bool ToggleCommandRegistryEntry(int index)
        {
            JObject root = ReadCoreConfig();
            JArray arr = GetCoreArray(root, "Command Registry - WorldMind may only mention enabled commands");
            if (index < 0 || index >= arr.Count) return false;
            JObject o = arr[index] as JObject;
            if (o == null) return false;
            o["Enabled"] = !JBool(o, "Enabled");
            root["Command Registry - WorldMind may only mention enabled commands"] = arr;
            return WriteCoreConfig(root);
        }

        private bool AddCommandRegistryEntry(string line)
        {
            string[] parts = (line ?? "").Split('|');
            if (parts.Length < 3) return false;
            bool enabled = false;
            if (parts.Length >= 4) bool.TryParse(parts[3], out enabled);
            JObject root = ReadCoreConfig();
            JArray arr = GetCoreArray(root, "Command Registry - WorldMind may only mention enabled commands");
            JObject o = new JObject();
            o["Name"] = parts[0].Trim();
            o["Enabled"] = enabled;
            o["Command"] = parts[1].Trim();
            o["Description"] = parts[2].Trim();
            arr.Add(o);
            root["Command Registry - WorldMind may only mention enabled commands"] = arr;
            return WriteCoreConfig(root);
        }

        private bool RemoveServerFact(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            JObject root = ReadCoreData();
            JObject facts = root["ServerFacts"] as JObject;
            if (facts == null) return false;
            facts.Remove(key);
            root["ServerFacts"] = facts;
            return WriteCoreData(root);
        }

        private bool AddServerFact(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            int idx = line.IndexOf('=');
            if (idx <= 0) return false;
            string key = line.Substring(0, idx).Trim();
            string val = line.Substring(idx + 1).Trim();
            if (string.IsNullOrEmpty(key)) return false;
            JObject root = ReadCoreData();
            JObject facts = root["ServerFacts"] as JObject;
            if (facts == null)
            {
                facts = new JObject();
                root["ServerFacts"] = facts;
            }
            facts[key] = val;
            return WriteCoreData(root);
        }

        #endregion

        #region Core Hook Readers

        private Dictionary<string, object> GetCoreProof()
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            try
            {
                object raw = Interface.CallHook("WorldMind_GetProof");
                if (raw == null) return result;
                string json = JsonConvert.SerializeObject(raw);
                Dictionary<string, object> parsed = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (parsed != null) result = parsed;
            }
            catch { }
            return result;
        }

        private List<string> GetPluginStatusLines()
        {
            List<string> lines = new List<string>();
            try
            {
                object raw = Interface.CallHook("WorldMind_GetPluginStatus");
                if (raw == null) return lines;
                string json = JsonConvert.SerializeObject(raw);
                JToken token = JToken.Parse(json);
                if (token.Type == JTokenType.Array)
                {
                    foreach (JToken item in token.Children())
                        lines.Add(FlattenStatus(item));
                }
                else if (token.Type == JTokenType.Object)
                {
                    JObject obj = (JObject)token;
                    foreach (JProperty p in obj.Properties())
                        lines.Add(p.Name + ": " + FlattenStatus(p.Value));
                }
                else
                {
                    lines.Add(Convert.ToString(raw));
                }
            }
            catch { }
            return lines;
        }

        private string FlattenStatus(JToken token)
        {
            if (token == null) return "";
            if (token.Type != JTokenType.Object) return Convert.ToString(token);
            JObject o = (JObject)token;
            List<string> parts = new List<string>();
            foreach (JProperty p in o.Properties())
                parts.Add(p.Name + "=" + Truncate(Convert.ToString(p.Value), 35));
            return string.Join(" · ", parts.ToArray());
        }

        #endregion

        #region Helpers

        private bool IsAdmin(BasePlayer player)
        {
            if (player == null) return false;
            return player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin);
        }

        private UiSession GetSession(BasePlayer player)
        {
            UiSession s;
            if (!_sessions.TryGetValue(player.UserIDString, out s))
            {
                s = new UiSession();
                _sessions[player.UserIDString] = s;
            }
            return s;
        }

        private string NormalizePage(string page)
        {
            page = (page ?? "dashboard").ToLowerInvariant();
            if (page == "core") return "setup";
            if (page == "cmd" || page == "command") return "commands";
            if (page == "diag") return "diagnostics";
            string[] pages = { "dashboard", "setup", "identity", "commands", "facts", "personality", "diagnostics" };
            return pages.Contains(page) ? page : "dashboard";
        }

        private string PrettyPage(string page)
        {
            if (page == "dashboard") return "Dashboard";
            if (page == "setup") return "Core Setup";
            if (page == "identity") return "Identity";
            if (page == "commands") return "Commands";
            if (page == "facts") return "Facts";
            if (page == "personality") return "Personality";
            if (page == "diagnostics") return "Diagnostics";
            return page;
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null) return;
            SendReply(player, "<color=#00F0FF>[WorldMindUI]</color> " + message);
        }

        private string JoinArgs(string[] args, int start)
        {
            if (args == null || args.Length <= start) return "";
            return string.Join(" ", args.Skip(start).ToArray());
        }

        private string EncodePath(string section, string key)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(section + "|" + key));
        }

        private bool DecodePath(string encoded, out string section, out string key)
        {
            section = "";
            key = "";
            try
            {
                string raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                int idx = raw.LastIndexOf('|');
                if (idx <= 0) return false;
                section = raw.Substring(0, idx);
                key = raw.Substring(idx + 1);
                return true;
            }
            catch { return false; }
        }

        private string EscapeArg(string value)
        {
            if (value == null) return "";
            return value.Replace("\"", "");
        }

        private string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Length <= max) return value;
            return value.Substring(0, max) + "...";
        }

        private string MaskSecret(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Length <= 6) return "configured";
            return value.Substring(0, 3) + "..." + value.Substring(value.Length - 3);
        }

        private JObject Section(JObject root, string section)
        {
            return root == null ? null : root[section] as JObject;
        }

        private string GetCoreValue(JObject root, string section, string key)
        {
            JObject sec = Section(root, section);
            if (sec == null || sec[key] == null) return "";
            return Convert.ToString(sec[key]);
        }

        private bool GetCoreBool(JObject root, string section, string key)
        {
            JObject sec = Section(root, section);
            if (sec == null || sec[key] == null) return false;
            bool b;
            return bool.TryParse(Convert.ToString(sec[key]), out b) && b;
        }

        private bool HasCoreValue(JObject root, string section, string key)
        {
            return !string.IsNullOrEmpty(GetCoreValue(root, section, key));
        }

        private JArray GetCoreArray(JObject root, string key)
        {
            if (root == null) return new JArray();
            JArray arr = root[key] as JArray;
            if (arr == null) arr = new JArray();
            return arr;
        }

        private string JString(JObject o, string key)
        {
            return o != null && o[key] != null ? Convert.ToString(o[key]) : "";
        }

        private bool JBool(JObject o, string key)
        {
            bool b;
            return o != null && o[key] != null && bool.TryParse(Convert.ToString(o[key]), out b) && b;
        }

        private int CountArray(JObject root, string key)
        {
            JArray arr = root == null ? null : root[key] as JArray;
            return arr == null ? 0 : arr.Count;
        }

        private int CountObject(JObject root, string key)
        {
            JObject obj = root == null ? null : root[key] as JObject;
            return obj == null ? 0 : obj.Count;
        }

        private string ProofValue(Dictionary<string, object> proof, string key, string fallback)
        {
            if (proof == null || !proof.ContainsKey(key) || proof[key] == null) return fallback;
            return Convert.ToString(proof[key]);
        }

        private string BuildCounterSummary(Dictionary<string, object> proof)
        {
            if (proof == null || proof.Count == 0) return "unavailable";
            string[] keys = { "RequestsAttempted", "RequestsSucceeded", "RequestsFailed", "ResponsesRejected", "EventsDetected" };
            List<string> parts = new List<string>();
            foreach (string key in keys)
            {
                if (proof.ContainsKey(key)) parts.Add(key + "=" + Convert.ToString(proof[key]));
            }
            return parts.Count == 0 ? "no counters returned" : string.Join(" · ", parts.ToArray());
        }

        private Theme GetTheme()
        {
            Theme t;
            if (!_config.ThemeProfiles.TryGetValue(_config.SelectedThemeProfile, out t))
                t = _config.ThemeProfiles["forest"];
            return t;
        }

        #endregion

        #region Classes

        private class UiSession
        {
            public string Page = "dashboard";
            public int Scroll = 0;
        }

        private class UiConfig
        {
            public string SelectedThemeProfile = "forest";
            public int ScrollStep = 3;
            public Dictionary<string, Theme> ThemeProfiles = new Dictionary<string, Theme>();

            public static UiConfig Default()
            {
                UiConfig config = new UiConfig();
                config.ThemeProfiles["forest"] = new Theme
                {
                    Backdrop = "0.07 0.06 0.045 0.94",
                    Panel = "0.18 0.16 0.11 0.92",
                    Row = "0.12 0.105 0.075 0.82",
                    Input = "0.03 0.03 0.025 0.90",
                    Accent = "0.30 0.42 0.23 0.95",
                    Danger = "0.45 0.13 0.09 0.95",
                    Title = "0.92 0.86 0.72 1",
                    Text = "0.82 0.79 0.70 1",
                    Muted = "0.58 0.55 0.47 1"
                };
                config.ThemeProfiles["desert"] = new Theme
                {
                    Backdrop = "0.10 0.075 0.045 0.94",
                    Panel = "0.28 0.20 0.11 0.92",
                    Row = "0.18 0.13 0.075 0.82",
                    Input = "0.04 0.03 0.02 0.90",
                    Accent = "0.62 0.39 0.16 0.95",
                    Danger = "0.48 0.12 0.07 0.95",
                    Title = "0.96 0.82 0.58 1",
                    Text = "0.88 0.78 0.62 1",
                    Muted = "0.60 0.52 0.41 1"
                };
                config.ThemeProfiles["snow"] = new Theme
                {
                    Backdrop = "0.055 0.065 0.075 0.94",
                    Panel = "0.12 0.16 0.18 0.92",
                    Row = "0.08 0.11 0.13 0.82",
                    Input = "0.02 0.025 0.03 0.90",
                    Accent = "0.22 0.40 0.48 0.95",
                    Danger = "0.42 0.10 0.10 0.95",
                    Title = "0.84 0.92 0.96 1",
                    Text = "0.76 0.84 0.88 1",
                    Muted = "0.50 0.58 0.62 1"
                };
                config.ThemeProfiles["wasteland"] = new Theme
                {
                    Backdrop = "0.055 0.052 0.045 0.94",
                    Panel = "0.15 0.14 0.115 0.92",
                    Row = "0.10 0.095 0.08 0.82",
                    Input = "0.025 0.025 0.02 0.90",
                    Accent = "0.42 0.36 0.22 0.95",
                    Danger = "0.38 0.10 0.075 0.95",
                    Title = "0.84 0.80 0.66 1",
                    Text = "0.74 0.71 0.62 1",
                    Muted = "0.48 0.46 0.39 1"
                };
                return config;
            }

            public void Normalize()
            {
                if (string.IsNullOrEmpty(SelectedThemeProfile)) SelectedThemeProfile = "forest";
                ScrollStep = Math.Max(1, ScrollStep);
                if (ThemeProfiles == null || ThemeProfiles.Count == 0) ThemeProfiles = Default().ThemeProfiles;
                foreach (KeyValuePair<string, Theme> kv in Default().ThemeProfiles)
                {
                    if (!ThemeProfiles.ContainsKey(kv.Key)) ThemeProfiles[kv.Key] = kv.Value;
                }
            }
        }

        private class Theme
        {
            public string Backdrop = "0 0 0 0.94";
            public string Panel = "0.15 0.15 0.15 0.9";
            public string Row = "0.1 0.1 0.1 0.8";
            public string Input = "0.02 0.02 0.02 0.9";
            public string Accent = "0.3 0.4 0.3 0.95";
            public string Danger = "0.45 0.1 0.1 0.95";
            public string Title = "1 1 1 1";
            public string Text = "0.85 0.85 0.85 1";
            public string Muted = "0.55 0.55 0.55 1";
        }

        #endregion
    }
}
