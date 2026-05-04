# WorldMind V2

**WorldMind V2** is a Rust Oxide/uMod AI plugin suite designed to make a Rust island feel more aware, more reactive, and more alive.

It connects your server to a local or hosted language model, listens to server events, builds memory, and gives your server owner/admin tools, player-facing intelligence, map intelligence, NPC flavor, death recaps, reputation, quests, Discord reports, and other WorldMind-powered systems.

> Made with love by Deviated Systems  
> Author: Devi8d0ne

---

## What WorldMind Does

WorldMind is not one small chatbot plugin.

It is a growing Rust AI suite that can power:

- Server-wide AI personality
- Player-facing `/ask` interactions
- Admin summaries and reports
- Map intelligence and tactical map layers
- Death recaps
- Player memory
- Reputation and titles
- Quest/objective logic
- NPC flavor and reactions
- Discord event reporting
- Raid pressure tracking
- Threat warnings
- Loot, item, economy, and shop intelligence
- Monument and location awareness
- World event memory

The long-term goal is simple:

**Your island should feel alive.**

---

## Important: You Need a Language Model

WorldMind needs a model endpoint to think.

Most server owners have never used one before. That is normal.

You have two common options:

1. **Run a local model using LM Studio**
2. **Use a hosted model API**

For most owners, start with **LM Studio**.

---

# Quick Start for Server Owners

## 1. Install Oxide/uMod

WorldMind is built for Rust servers running Oxide/uMod.

You should already know how to install normal `.cs` plugins into:

```text
oxide/plugins/
```

If your server does not already run Oxide/uMod, install that first before trying WorldMind.

---

## 2. Install LM Studio

LM Studio is a desktop app that lets you run AI models locally.

Open LM Studio and download a chat/instruct model.

Good starter options:

```text
Qwen instruct models
Llama instruct models
Mistral instruct models
```

Use a model your machine can actually run. Start small before going huge.

---

## 3. Start the LM Studio Server

In LM Studio, open:

```text
Developer / Local Server
```

Start the local server.

You should see a local URL similar to:

```text
http://localhost:1234
```

or:

```text
http://127.0.0.1:1234
```

Common endpoint examples:

```text
http://127.0.0.1:1234/v1/chat/completions
```

or:

```text
http://127.0.0.1:1234/v1/responses
```

Use the endpoint type your WorldMind config expects.

---

## 4. If Your Rust Server Is on Another Machine

If LM Studio is running on your personal PC and your Rust server is hosted somewhere else, `localhost` will not work.

`localhost` means “this same machine.”

So if your Rust server is hosted by a game host, this will fail:

```json
"Endpoint": "http://localhost:1234/v1/chat/completions"
```

because the host machine will look for LM Studio on itself, not on your PC.

You need one of these:

- Run LM Studio on the same machine as the Rust server
- Use a public tunnel such as ngrok or Cloudflare Tunnel
- Use a hosted AI provider
- Host your own model endpoint on a VPS/server

---

## 5. Example LM Studio Config

Your actual config may have more sections, but the important owner-supplied values usually look like this:

```json
{
  "Enabled": true,
  "Model": {
    "Provider": "LMStudio",
    "Endpoint": "http://127.0.0.1:1234/v1/chat/completions",
    "Model": "your-downloaded-model-name",
    "ApiKey": "",
    "TimeoutSeconds": 30
  }
}
```

If you are using an ngrok-style public URL, it may look like:

```json
{
  "Endpoint": "https://your-ngrok-url.ngrok-free.app/v1/chat/completions",
  "Model": "your-downloaded-model-name"
}
```

Do not include fake extra path pieces.

If LM Studio says your tunnel is:

```text
https://example.ngrok-free.app
```

then the endpoint is usually:

```text
https://example.ngrok-free.app/v1/chat/completions
```

or:

```text
https://example.ngrok-free.app/v1/responses
```

depending on what WorldMind is configured to call.

---

# Installation

## Basic Install

1. Stop your Rust server or prepare to reload plugins.
2. Upload the WorldMind `.cs` files to:

```text
oxide/plugins/
```

3. Start/reload the server.
4. Let configs generate.
5. Open the config files in:

```text
oxide/config/
```

6. Add your required setup values.
7. Reload the plugins.

Example reload:

```text
oxide.reload WorldMindV2
```

or in console:

```text
o.reload WorldMindV2
```

---

# Recommended Plugin Order

Start small. Do not install the entire suite at once if this is your first time.

## Stage 1: Core

Install and verify:

```text
WorldMindV2
WorldMindAsk
WorldMindAdminMind
WorldMindDiscordMind
WorldMindUIV2
```

Confirm the model works before adding more plugins.

## Stage 2: Memory and Server Intelligence

Add:

```text
WorldMindPlayerBrain
WorldMindMemoryAudit
WorldMindLocationBrainV2
WorldMindSignalBrainV2
WorldMindMapBrainV2
```

## Stage 3: Player-Facing Systems

Add:

```text
WorldMindDeathRecap
WorldMindThreatSense
WorldMindQuestBrain
WorldMindReputationBrain
WorldMindLootMind
```

## Stage 4: World Systems

Add:

```text
WorldMindNPCBrain
WorldMindMonumentBrain
WorldMindRaidBrain
WorldMindRoadPatrolsV2
WorldMindEventBrain
WorldMindBroadcastBrain
```

## Stage 5: Economy / Utility

Add:

```text
WorldMindItemBrain
WorldMindShopBrain
WorldMindProviderBrain
```

---

# First Commands to Try

After installing the core plugin, try:

```text
/worldmind status
/worldmind ask Is the model connected?
/ask what can you tell me about this server?
/wmadmin status
/wmdiscord status
```

For MapBrain:

```text
/wmmap
/wmmap status
/wmmap perms
/wmmap ui on
```

For UI:

```text
/wmui
/wmui status
```

---

# Permissions

Permissions vary by plugin, but the pattern is usually:

```text
pluginname.admin
pluginname.use
```

Example:

```text
oxide.grant user 76561198000000000 worldmindv2.admin
oxide.grant user 76561198000000000 worldmindask.use
oxide.grant group admin worldmindmapbrainv2.admin
oxide.grant group admin worldmindmapbrainv2.admin.ui
```

For player-facing features, grant the relevant `.use` permission to your player group:

```text
oxide.grant group default worldmindask.use
```

For admin features, grant to your admin group:

```text
oxide.grant group admin worldmindadminmind.admin
```

---

# WorldMindMapBrainV2 Permissions Example

MapBrain has many permission layers because map intelligence can expose sensitive information.

Common admin permissions:

```text
worldmindmapbrainv2.admin
worldmindmapbrainv2.admin.ui
worldmindmapbrainv2.admin.ui.move
worldmindmapbrainv2.admin.intel
worldmindmapbrainv2.admin.players
worldmindmapbrainv2.admin.sleepers
worldmindmapbrainv2.admin.tc
worldmindmapbrainv2.admin.stashes
worldmindmapbrainv2.admin.bags
worldmindmapbrainv2.admin.npcs
worldmindmapbrainv2.admin.heat
worldmindmapbrainv2.admin.raid
worldmindmapbrainv2.admin.zones
```

Common player permissions:

```text
worldmindmapbrainv2.player
worldmindmapbrainv2.player.basic
worldmindmapbrainv2.player.heat
worldmindmapbrainv2.player.zones
worldmindmapbrainv2.player.intel
worldmindmapbrainv2.player.pulse
```

Do not give admin map permissions to normal players unless you want them to see admin-only intelligence.

---

# Config Notes

WorldMind configs should keep owner-supplied setup values near the top.

Look for fields such as:

```text
Endpoint
Model
ApiKey
SteamApiKey
DiscordWebhookUrl
ServerName
ServerStyle
```

You should not need to edit internal system fields unless you know what they do.

---

# Keeping Configs Safe

WorldMind plugins are designed to preserve owner-edited configs and data.

General rule:

- First load creates defaults
- Later reloads should merge missing fields
- Existing owner values should not be overwritten

If a config looks wrong after an update, back it up before deleting anything.

---

# Troubleshooting

## “LM Studio HTTP 0”

Usually means WorldMind cannot reach your endpoint.

Check:

```text
Is LM Studio running?
Is the LM Studio local server started?
Is the endpoint URL correct?
Is the Rust server on the same machine?
If using ngrok, is the tunnel still active?
Did the tunnel URL change?
```

## “NameResolutionFailure”

Usually means the URL/domain is wrong or unreachable.

Common causes:

```text
Typo in ngrok URL
Using a dead tunnel
Using localhost from a hosted Rust server
Wrong domain suffix
```

## “messages field is required”

Your endpoint and request format do not match.

Example:

- Plugin is sending Responses API format
- Endpoint expects Chat Completions format

or the reverse.

Fix by matching the WorldMind provider/config to the endpoint type:

```text
/v1/chat/completions
```

or:

```text
/v1/responses
```

## Model answers with nonsense

Try:

```text
Use a better instruct model
Lower temperature
Improve the system prompt
Reduce context spam
Check that event payloads are clean
Enable anti-garbage guards if available
```

## Plugin compiled but nothing happens

Check:

```text
Permissions
Config Enabled = true
Correct command
Console errors
WorldMindV2 status
Model endpoint status
Required plugin references
```

## Discord spam

Find the plugin sending it and reduce/disable that event type in config.

For example, if reputation shifts are posting too much, look for:

```text
WorldMindReputationBrain
Discord
Webhook
Notify
Scientist
NPC
```

Then disable the noisy category rather than removing the whole plugin.

---

# Recommended Owner Setup Flow

Use this order:

1. Install `WorldMindV2`
2. Connect LM Studio
3. Run `/worldmind status`
4. Test `/worldmind ask hello`
5. Install `WorldMindAsk`
6. Test `/ask`
7. Install admin/Discord tools
8. Install memory/location/signal tools
9. Install player-facing systems
10. Install advanced world systems

Do not install 20 plugins and then troubleshoot everything at once.

---

# Security Notes

Do not publish private keys or webhooks.

Keep these private:

```text
Discord webhook URLs
API keys
Steam API keys
Bearer tokens
Tunnel URLs if you do not want them public
```

Do not give normal players admin permissions.

Do not expose admin map layers unless you intend to.

---

# Performance Notes

AI calls are heavier than normal plugin logic.

Recommended:

```text
Use cooldowns
Use throttles
Avoid calling the model for every tiny event
Batch summaries where possible
Use local facts first
Only call the model when it adds value
```

WorldMind should enhance the island, not flood it.

---

# Good First Test

After setup, stand in-game and run:

```text
/ask what is WorldMind?
```

Then test admin:

```text
/wmadmin status
```

Then test map:

```text
/wmmap
```

Open your Rust map and verify the WorldMind rail appears.

---

# Support Checklist

When asking for help, include:

```text
Plugin name
Plugin version
Exact compile error
Exact console error
Config endpoint value, with secrets removed
Whether LM Studio is local or remote
Whether Rust server is hosted or local
What command you ran
What you expected
What happened
```

Do not send private API keys or Discord webhook URLs.

---

# Final Note

WorldMind V2 is meant to become the intelligence layer of your Rust server.

Start with the core. Prove the model connection. Then add the rest of the island brain one plugin at a time.
