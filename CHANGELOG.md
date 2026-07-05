# Changelog

All notable changes to Pathstone are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- WorldDevKit epic (#90) — standalone world editor application (14 sub-issues tracked)
- Prompt structure optimization for provider-side caching (#89)

## [0.3.0] — 2025-01-31

### Added
- Full combat state machine: initiative, rounds, death saves (#88, #63)
- Tool suite expanded to 33 tools: move_player, give_item, spawn_item_on_ground,
  equip_player, update_quest, set_flag, get_world_state, get_npc_state, award_xp,
  roll_attack, deal_damage, apply_status, create_item_template, create_npc_template,
  create_building_template, start_combat, end_combat, next_turn, death_save,
  set_weather, get_weather, adjust_reputation, get_factions, get_lore,
  craft_item, search_location, set_market_price, get_market_price (#8, #88)
- Travel UI with clickable exits + random encounters (#1, #14)
- Character creation flow with race/class/background selection (#2)
- GM streaming narration via SSE (#5)
- Token billing UI: per-turn + per-session usage widget (#6)
- Crash logs + global exception handler (#16)
- Anti-loop detection in GM tool calls (#24)
- Unit tests: 269+ tests across 8 test files (#15)
- Tooltips on all interactive controls (#50)
- Save migration: v1→v2 backfills Item.Weight from template (#17)
- Default world expanded: 6→14 locations, 2 new quests (#42)
- Client reconnect on disconnect with 3-attempt backoff (#7)
- WorldBuilder pause/resume with state persistence (#19)
- Local model support: Ollama/llama.cpp presets + documentation (#27)
- Quest rewards claiming UI (#70)
- Onboarding / first-run tutorial (#73)
- Save browser improvements: sort, search, multi-select delete, metadata (#74)
- Multiplayer lobby polish: ready toggles, start game, share address (#77)
- Chunked world generation: regions generated on demand via travel (#20)
- Custom ruleset per world: AI designs attribute names for non-standard themes (#21)
- Pet-agent custom task UI: editable delegation list (#22)
- Re-build existing world: partial/full rebuild from save screen (#23)
- Weather system: set_weather/get_weather tools, GM context, UI display (#34)
- Day/night cycle: time-of-day effects on encounters + perception (#35)
- Faction system: reputation, alignment, GM tools, UI badges (#36)
- Lore database: get_lore tool for canonical world lore (#43)
- Integration tests for multiplayer: 6 loopback tests with StubAiClient (#57)
- CI/CD pipeline: build+test on 3 platforms, release on tag (#58)
- Content expansion: 116 items, 47 NPCs, 33 buildings (#39, #40, #41)
- Themes: light/dark toggle + 6 accent colors (#47)
- Settings dialog: 4-tab layout (AI/Display/Multiplayer/Advanced) (#78)
- Stack management: split/merge stackable items (#64)
- Animations: screen transitions, panel fades, button hover (#46)
- Save compression: gzip world.json/log.json/state.json (#80)
- Backup saves: auto-rotate, max 5, 30-day retention (#81)
- Save sharing: export/import .pathstone-world files (#33)
- Economy simulation: market price modifiers per location (#37)
- Crafting system: CraftingRecipe + craft_item tool (#65)
- Container/looting: search_location tool (#67)
- AI worlds created from scratch: no standard template references
- Player kick/ban UI for host (#30)
- Spectator mode: spectators can't submit actions (#31)
- Late joiner sync: host sends last 20 log entries (#32)
- Save schema migration tests (#72)
- Achievement system: 8 milestones with toast notifications (#86)
- Keyboard shortcuts: Enter/Esc/Ctrl+S/Ctrl+L/1-4/F1 (#51)
- Item rarity colors in inventory UI (#66)
- Windows installer: NSIS, self-contained single-file (#4)
- Context window management: history summarization (#25)
- Multi-model support: per-role model overrides (#26)
- Hot-reload prompts for development (#82)

### Changed
- Profile/Settings split: nickname edited inline on main menu, settings screen
  is AI-only (#78 update)
- GM system prompt restructured for provider-side prompt caching: static prefix
  (system + narrator + tools-guide + world lore) + dynamic tail (world state)
- ContentRegistry expanded from 26→116 items, 12→47 NPCs, 8→33 buildings
- DefaultWorld expanded from 6→14 locations

### Removed
- Voice chat (#84) — not applicable to a text RPG
- Sounds (#44) and music (#45) — silence is the intended audio design
- Demo mode (#61) — scripted GM can't convey the AI-driven experience
- Daily challenge (#87) — requires central server, contradicts P2P architecture
- Code signing (#56) — certificates cost money, project is free/non-commercial
- AI response caching (#83) — obsolete; modern providers cache automatically

## [0.2.0] — 2025-01-15

### Added
- Desktop rewrite: C# / .NET 8 / Avalonia UI
- Core layers: Common, Rules, World, AI, Saves, Profile, Multiplayer
- 5 GM tools: roll_dice, get_player_state, get_location, spawn_npc, advance_time
- WorldBuilder: 3-stage pipeline (planner → committer → narrator)
- Multiplayer: in-process WebSocket host + ClientWebSocket client
- Save system: file-based with atomic writes
- Profile system: single local profile, no auth
- 4 side panels: Character, Inventory, Quests, World
- Inventory actions: use/equip/unequip/drop
