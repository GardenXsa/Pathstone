# Pathstone

**A desktop multiplayer narrative role-playing game with an AI Game Master.**

Pathstone is a single-binary desktop application (Windows / macOS / Linux) that
runs a procedural narrative TTRPG session. One player hosts the world in-process;
others connect over a direct WebSocket. An OpenAI-compatible LLM acts as the Game
Master: it resolves player actions, narrates outcomes, and mutates the world state
through a tool-call loop. The world is generated from a free-form brief via a
three-stage AI pipeline (planner, deterministic committer, narrator), with
optional pet-agent delegations for richer detail.

The project is a complete rewrite of an earlier Next.js / TypeScript prototype.
The web stack (HTTP server, JWT auth, socket.io, browser client) has been
replaced with a native desktop architecture: C# / .NET 8 / Avalonia UI, raw
`System.Net.WebSockets`, file-based profile identity, and an in-process host.

---

## Table of contents

1. [Status](#status)
2. [Technology stack](#technology-stack)
3. [Solution layout](#solution-layout)
4. [Architecture overview](#architecture-overview)
5. [Core layers](#core-layers)
   - [Common](#common)
   - [Rules](#rules)
   - [World](#world)
   - [AI](#ai)
   - [Saves](#saves)
   - [Profile](#profile)
   - [Multiplayer](#multiplayer)
6. [Desktop application](#desktop-application)
7. [Build and run](#build-and-run)
8. [Configuration](#configuration)
9. [AI pipeline](#ai-pipeline)
10. [Multiplayer protocol](#multiplayer-protocol)
11. [Save file format](#save-file-format)
12. [Project conventions](#project-conventions)
13. [Roadmap](#roadmap)
14. [Contributing](#contributing)
15. [License](#license)

---

## Status

Alpha. The foundation is in place and the application boots, loads a world,
runs a Game Master turn, and persists state. The feature surface is incomplete;
see the [Roadmap](#roadmap) and the open issues tracker for what remains.

What works today:

- Local profile (single identity per install, no auth).
- Main menu with new-game, load, host, join, profile screens.
- AI world-build pipeline: planner produces a `WorldPlan`, deterministic
  committer materializes it into a live `World`, narrator writes the opening
  narration. Optional pet-agent delegations enrich the world.
- Single-player game loop: submit action, Game Master resolves via tool calls,
  narrative appended to log, world state updated, save persisted.
- Multiplayer: host runs an in-process WebSocket server; clients connect by
  IP:port. Action queue, chat, narrative streaming, state synchronization.
- Four tabbed side panels: Character, Inventory, Quests, World. Inventory
  actions (use / equip / unequip / drop) mutate the world and persist.

What is not yet implemented: travel UI, character creation flow, combat
system, sounds, packaging, localization, and a long tail of polish items.

---

## Technology stack

| Concern              | Choice                                            |
|----------------------|---------------------------------------------------|
| Framework            | .NET 8 (LTS)                                      |
| Language             | C# 12                                             |
| UI toolkit           | Avalonia UI 12.0.5 (XAML, MVVM, cross-platform)   |
| MVVM                 | CommunityToolkit.Mvvm 8.3 (source generators)    |
| DI                   | Microsoft.Extensions.DependencyInjection 8.0      |
| Persistence          | System.Text.Json + Microsoft.Data.Sqlite 8.0.10   |
| Markdown             | Markdig 0.37 (narrative rendering)                |
| Real-time transport  | `System.Net.WebSockets` (HttpListener + ClientWebSocket) |
| HTTP client          | `HttpClient` (static shared instance)             |
| AI provider          | OpenAI-compatible HTTP `/v1/chat/completions`     |

No web server. No browser. No JWT, cookies, or socket.io. No external WebSocket
libraries. The host is a background task inside the desktop process.

---

## Solution layout

```
Pathstone/
├── MyGame.sln
├── .gitignore
├── README.md
└── src/
    ├── MyGame.Core/                  # Engine, AI, persistence, multiplayer (no UI)
    │   ├── Common/                   # EntityId, Rng, EventBus, Result, Version
    │   ├── Rules/                    # D20, Check
    │   ├── World/                    # World, Calendar, Ruleset, entities, content
    │   │   ├── Entities/             # Player, Npc, Location, Building, Item, Quest
    │   │   └── Content/              # ContentRegistry + embedded data.json
    │   ├── AI/                       # AiClient, Messages, PromptLoader, Tools
    │   │   ├── Agents/               # GameMaster, StartSceneAgent, PetAgent, WorldBuilderOrchestrator
    │   │   ├── Prompts/              # 12 embedded .md prompt templates
    │   │   └── Tools/                # ToolRegistry + 5 built-in tools
    │   ├── Saves/                    # SaveManager, SaveMeta, LogEntry, CharacterSheet
    │   ├── Profile/                  # Profile, ProfileStore, Settings, SettingsStore
    │   └── Multiplayer/              # HostServer, GameClient, HostSession, ClientSession
    │       └── Protocol/             # NetMessage + 20 message records
    └── MyGame.Desktop/               # Avalonia UI
        ├── Services/                 # ServiceHost (DI)
        ├── ViewModels/               # Main, MainMenu, Profile, HostGame, JoinGame, Game,
        │   ├── Panels/               #   CharacterPanel, InventoryPanel, QuestPanel, WorldPanel
        │   └── ...
        └── Views/                    # XAML + code-behind for each ViewModel
            └── Panels/               #   4 panel views
```

The Core project has zero UI dependencies and is unit-testable in isolation.
The Desktop project depends on Core and never contains game logic.

---

## Architecture overview

Pathstone is built around three principles.

**1. The engine is pure and deterministic.** `MyGame.Core.World.World` is the
single authoritative state container. All mutations go through its methods
(`SpawnPlayer`, `SpawnNpc`, `AddLocation`, `SpawnItemOnGround`, ...). The RNG
is seedable and serializable (`Rng.State` / `Rng.FromState`), so a saved game
resumes exactly where it left off, bit-for-bit. The AI never mutates the world
directly; it calls tools, and the tools mutate the world.

**2. The host is the application.** There is no separate server process. When
a player clicks "Host game", the desktop process starts an
`HttpListener`-backed WebSocket server on an OS-assigned port. The host's
GameMaster engine runs in-process. Clients connect to the host's IP:port over a
raw WebSocket. When the host closes the application, the world is saved and
the session pauses. This is the desktop idiom (Don't Starve Together, Baldur's
Gate 3), not the web-server idiom.

**3. Identity is local.** A single `Profile` record lives in
`%APPDATA%/Pathstone/profile.json`. No JWT, no cookies, no auto-created guest
users. The phantom-user bug class that plagued the earlier web prototype is
structurally impossible: there is no server-side session store to get out of
sync with the client.

### Data flow

```
                       ┌─────────────────────────────────────────────┐
                       │              MyGame.Desktop                  │
                       │  (Avalonia UI + ViewModels + Services)       │
                       └────────────┬────────────────────────┬────────┘
                                    │ commands / events      │ WebSocket
                                    ▼                        ▼
                       ┌─────────────────────┐  ┌──────────────────────┐
                       │   MyGame.Core       │  │   MyGame.Core         │
                       │   (engine + AI)     │  │   Multiplayer         │
                       │                     │  │                       │
                       │  World ◄──tools── AI│  │  HostServer           │
                       │   ▲                 │  │  GameClient           │
                       │   │ SaveManager      │  │  HostSession          │
                       │   │ ProfileStore     │  │  ClientSession        │
                       │   ▼                 │  │  ActionQueue          │
                       │  AppData/Pathstone/ │  │  Protocol (20 msgs)   │
                       └─────────────────────┘  └──────────────────────┘
                                                          │
                                                          ▼
                                                 remote clients
```

In single-player mode the multiplayer layer is dormant. In host mode the
HostServer runs inside the same process as the GameMaster and broadcasts state
changes to connected clients. In client mode the local GameMaster is absent;
all action resolution happens on the host and arrives as `NarrativeFinal` /
`StateUpdate` messages.

---

## Core layers

### Common

Foundation types used everywhere. No dependencies on other layers.

- **`EntityId`** — a `readonly record struct` wrapping a cuid-style string.
  Generated via `EntityId.NewId()` (timestamp base36 + random base36). Sortable,
  lexicographically comparable, JSON-convertible as a bare string.
- **`Rng`** — deterministic PRNG (PCG32, 64-bit state). `Next()`,
  `NextInt(min, max)`, `Pick<T>`, `Shuffle<T>`, `Fork()`. State round-trips
  through `State` / `FromState(long)` so saves reproduce exactly.
- **`EventBus`** — generic in-process pub/sub. `Subscribe<T>` returns an
  `IDisposable`; `Publish<T>` dispatches with exceptions caught per handler.
- **`Result<T>`** — `readonly record struct` for error handling without
  exceptions for flow control.
- **`Version`** — semantic version constant + parse / compare helpers.

### Rules

- **`D20`** — `Roll`, `RollWithModifier`, `RollStat` (4d6 drop lowest),
  `Advantage`, `Disadvantage`.
- **`Check`** — `RollCheck(rng, modifier, dc)` returns a `CheckResult` with
  `CriticalSuccess` (natural 20) / `CriticalFailure` (natural 1) flags.
  `DifficultyClass` enum: Easy(5) / Medium(10) / Hard(15) / VeryHard(20) /
  NearlyImpossible(25).

### World

The aggregate root and all entity types.

- **`World`** — holds entity collections (Players, Npcs, Items, Buildings,
  Locations, Quests), the `Rng`, `Clock` (`GameTime`), `Ruleset`, `Registries`
  (content), and a free-form `Flags` metadata bag. Serializes to JSON via
  `ToJson()` / `FromJson()`. RNG state round-trips for deterministic resumes.
- **`Calendar` / `GameTime`** — in-game clock (Day / Hour / Minute) with
  `Advance(int minutes)` and localization-aware formatting.
- **`Ruleset`** — game configuration record (turn model, token bill mode,
  visibility, transport mode) + `Rulesets.DefaultDnd` factory.
- **`EntityFactory`** — factory methods for `CreatePlayer`, `CreateNpcFromTemplate`,
  `InstantiateItem`, `CreateBuilding`, with attribute derivation and AC recompute.
- **`ContentRegistry`** — registers `ItemTemplate` / `NpcTemplate` /
  `BuildingTemplate`. Loads from an embedded `data.json` (26 items, 12 NPCs,
  8 buildings by default).
- **`DefaultWorld`** — factory for a hand-authored starting scene ("Долина
  Туманов") used by the "New Game" button when AI generation is off.
- **`WorldBuilderCommitter`** — deterministic plan-to-world mutator. Six
  internal sub-stages: custom templates, locations (with bidirectional exit
  wiring), population, buildings, content (starter gear + player), title.
  Fault-tolerant (broken entries are skipped and counted) and idempotent
  (re-commit matches by location name, no duplicates).

### AI

- **`AiSettings`** — record: `BaseUrl`, `ApiKey`, `Model`, `Temperature`,
  `MaxTokens`.
- **`AiClient`** — OpenAI-compatible HTTP client. `ChatAsync`,
  `ChatWithToolsAsync`, `StreamChatAsync` (SSE `data:` line parsing). Static
  shared `HttpClient`. Retry once on 429 with 2s backoff; typed `AiException`
  on 401/403/5xx. Parses token usage into the response.
- **`Messages`** — `ChatMessage` (Role / Content / ToolCalls / ToolCallId /
  Name), `ToolCall`, `ToolDefinition`, `ChatResponse`. Factory helpers
  (`System`, `User`, `Assistant`, `AssistantWithTools`, `ToolResult`).
- **`PromptLoader`** — reads embedded `.md` prompt resources. `Get(name)`,
  `Render(name, vars)` with `{{var}}` substitution, `Exists(name)`.
- **`ToolRegistry`** — registers tool definitions + handlers. Lenient JSON arg
  parsing (handles double-escaped, malformed). Five built-in tools:
  `roll_dice`, `get_player_state`, `get_location`, `spawn_npc`, `advance_time`.
- **`GameMaster`** — the main agent. Builds messages, calls
  `ChatWithToolsAsync`, executes tool calls in a loop until no more, returns
  `NarrativeResult` (narration text, token usage, applied tool calls).
- **`StartSceneAgent`** — single AI call producing an atmospheric opening
  scene description (no tool loop).
- **`PetAgent`** — delegated sub-agent. Own message history, own iteration cap,
  `pet_done` signal tool. Used by the world-builder orchestrator for mass
  tasks ("spawn 10 NPCs", "create 5 items").
- **`WorldBuilderOrchestrator`** — three-stage pipeline: planner (1 AI call
  produces `WorldPlan` JSON), deterministic committer (no AI, mutates World
  from plan), narrator (1 AI call writes opening narration). Optional pet
  delegations run between committer and narrator. Progress reported via
  `IProgress<WorldBuildProgress>`.

### Saves

- **`SaveManager`** — file-based. Each save is a directory under
  `%APPDATA%/Pathstone/saves/{saveId}/` containing `meta.json`, `world.json`,
  `log.json`, `state.json`. Atomic writes (`.tmp` then `File.Move`).
  `CreateSave`, `LoadAll`, `SaveAll`, `ListSaves`, `DeleteSave`.
- **`SaveMeta`** — id, name, ownerId, characterName, worldTitle, buildStatus,
  engineVersion, timestamps, turn count.
- **`LogEntry`** — narrative / action / system / tool log entries with
  metadata bag.
- **`CharacterSheet`** — standalone exportable character record (not tied to a
  World). For the BG3-style "your character travels with you between hosts"
  feature. `CharacterSheetStore` manages files under
  `%APPDATA%/Pathstone/characters/`.

### Profile

Replaces the earlier JWT / cookie / auto-create-user auth entirely.

- **`Profile`** — record: `Guid Id`, `string Nickname`, timestamps.
  Nickname validation (2-20 chars, Latin / Cyrillic / digits / spaces /
  hyphens / underscores).
- **`ProfileStore`** — single local profile. `GetOrCreate()` loads from disk
  or creates with a random nickname (Russian prefixes + 4-char suffix).
  `Rename(string)`. Lives at `%APPDATA%/Pathstone/profile.json`.
- **`Settings`** — combines `AiSettings` + UI prefs (last server, autosave
  interval, stream narrative flag, language, volume, animations).
- **`SettingsStore`** — load / save / update with `Changed` event.

### Multiplayer

Raw `System.Net.WebSockets`. No socket.io. No external libraries.

- **`HostServer`** — `HttpListener`-backed WebSocket server on an OS-assigned
  port (port 0 → OS picks free port). Per-connection `SemaphoreSlim` for
  concurrent-send safety. Auto-adds new connections as members.
- **`GameClient`** — `ClientWebSocket` outgoing connection. Handshake
  (`HelloMsg` → `WelcomeMsg`), background read loop, event-based dispatch.
- **`HostSession`** — glues `HostServer` + `GameMaster` + `SaveManager` +
  `ActionQueue`. Player action arrives → enqueue → GM processes (streaming
  narrative) → broadcast deltas → save state → broadcast state update.
- **`ClientSession`** — glues `GameClient` + local state cache. Incoming
  narrative deltas append to local log; state updates patch the local World
  view; outgoing actions send to host.
- **`ActionQueue`** — thread-safe pending-action queue. `Enqueue`,
  `Cancel(actionId)`, `DrainAll()`.
- **`MemberInfo`** — connection id, profile id, nickname, role
  (Host/Player/Spectator), status (Pending/Ready/Playing/Disconnected).
- **`Protocol`** — 20 message records, polymorphic JSON via
  `[JsonPolymorphic]` + `[JsonDerivedType]` with a `"kind"` discriminator.
  Categories: handshake (Hello/Welcome/Reject), lobby (MemberJoined/Left,
  Ready, Chat, StatusChanged), game (ActionQueued/Resolving, NarrativeDelta,
  NarrativeFinal, StateUpdate, TurnEnd, ActionCancel/Cancelled), system
  (Error, Kicked, Ping/Pong).

---

## Desktop application

Avalonia 12 with the FluentTheme (dark variant). MVVM via
CommunityToolkit.Mvvm source generators (`[ObservableProperty]`,
`[RelayCommand]`).

### Screens

1. **Main menu** — six buttons: New game (DefaultWorld), Create world (AI
   pipeline), Host game, Join game, Load save, Profile. Shows current
   nickname.
2. **Profile** — edit nickname + AI settings (BaseUrl, ApiKey, Model,
   Temperature, MaxTokens).
3. **Host game** — game name + max players + optional AI world-build
   checkbox + brief.
4. **Join game** — host IP + port.
5. **World brief** — free-form brief TextBox + 3 presets (dark fantasy /
   cyberpunk / post-apoc) + advanced-generation checkbox (pet-agent
   delegations).
6. **World build** — progress dialog with live percent, stage label, detail,
   cancel button, "Continue" on success.
7. **Game** — three-column layout: left tabbed panels (Character / Inventory /
   Quests / World) for single-player, members + chat for multiplayer; center
   narrative log + action input; right pending-actions queue (multiplayer
   only).

### Side panels

- **CharacterPanel** — identity, attributes, resources, equipped gear,
  proficient skills, currency.
- **InventoryPanel** — carried items with use/equip/drop buttons, equipped
  items with unequip, carrying-capacity bar. Actions raise
  `ItemActionRequested` events handled by `GameViewModel` (use → apply
  consumable healing + effects; equip → swap slot; unequip → return to bag;
  drop → spawn on ground at current location).
- **QuestPanel** — active / completed / failed quests with objectives and
  rewards.
- **WorldPanel** — world title + clock + turn, current location (description,
  exits, inhabitants, buildings, ground items), all-locations map with
  visited / discovered markers.

All four panels refresh from the live `World` after every GM turn or state
update via `RefreshFromWorld()`.

---

## Build and run

### Prerequisites

- .NET 8 SDK (8.0.4xx or newer)
- Avalonia workloads are not required (the desktop project uses the default
  Microsoft.NET.Sdk)

### Build

```bash
dotnet build MyGame.sln
```

Expected output: `0 Warning(s) 0 Error(s)`.

### Run

```bash
dotnet run --project src/MyGame.Desktop
```

The application starts at the main menu. First visit: open Profile, set a
nickname and AI settings (an OpenAI-compatible API key is required for any AI
feature: world-build, GM turns, narration).

### Headless / CI smoke test

```bash
xvfb-run -a dotnet run --project src/MyGame.Desktop
```

The application initializes the window, renders the main menu, and idles. There
is no automated UI test harness yet (see issue tracker).

---

## Configuration

All configuration lives under `%APPDATA%/Pathstone/` (Windows),
`~/.config/Pathstone/` (Linux), or `~/Library/Application Support/Pathstone/`
(macOS). The folder contains:

| File             | Purpose                                  |
|------------------|------------------------------------------|
| `profile.json`   | Local profile (id + nickname)            |
| `settings.json`  | UI prefs + AI settings                   |
| `saves/`         | One directory per save (4 JSON files)    |
| `characters/`    | Exported portable character sheets       |

The AI settings support any OpenAI-compatible endpoint. Set `BaseUrl` to the
provider's API root (e.g. `https://api.openai.com/v1`,
`http://localhost:11434/v1` for Ollama). The `ApiKey` is sent as a Bearer
token; leave null for providers that don't require auth.

---

## AI pipeline

The world-build pipeline is the most complex AI flow. It runs three stages,
optionally four.

### Stage 1: Planner (1 AI call)

Loads the `world-planner.md` prompt, fills `{{WORLD_BRIEF}}`,
`{{WORLD_STATE}}`, `{{ITEM_TEMPLATES}}`, `{{NPC_TEMPLATES}}`,
`{{BUILDING_TEMPLATES}}`, and asks the model to produce a `WorldPlan` as a
fenced JSON block. The plan is a richly-typed record: title, theme, setting,
atmosphere, locations (with terrain/danger/role/connections), NPCs, buildings,
custom templates, starter gear, opening hook, plus optional lore (cosmology,
history, regions, cultures, economy, magic system, factions).

### Stage 2: Committer (no AI)

`WorldBuilderCommitter` mutates the live `World` from the plan in six
sub-stages: custom templates → locations (with bidirectional exit wiring) →
population → buildings → content (starter gear + player) → title. Each
sub-stage is fault-tolerant (broken entries skipped + counted) and idempotent
(re-commit matches by name, no duplicates).

### Stage 2b: Pet-agent delegations (optional, N AI calls)

If the caller provided `PetDelegation` records, the orchestrator runs each as
a separate `PetAgent` with its own LLM conversation + tool loop. Each
delegation has a label, a task description, optional per-pet AI settings, and
an iteration cap. The pet agent has full tool access (spawn_npc, give_item,
etc.) and signals completion via the `pet_done` tool. Failures are non-fatal;
one delegation crashing does not block others.

### Stage 3: Narrator (1 AI call)

Loads the `world-narrator.md` prompt, fills `{{WORLD_PLAN}}` (readable
summary) and `{{WORLD_STATE}}` (live world snapshot after the committer ran —
real location descriptions, real NPC names). Asks the model for 2 short
paragraphs of atmospheric opening narration. The narration is saved as the
first `LogEntry` on the save, so it appears immediately when the player enters
the game.

### Cost

Without pet delegations: 2 AI calls per world build, ~30-90 seconds.
With the default 2 pet delegations: 4 AI calls, ~60-150 seconds.

---

## Multiplayer protocol

All messages inherit from `NetMessage` and use polymorphic JSON serialization
via `[JsonPolymorphic]` + `[JsonDerivedType]` attributes with a `"kind"`
discriminator field. The wire format is camelCase UTF-8 JSON.

### Handshake

1. Client opens WebSocket to `ws://{host}:{port}/`.
2. Client sends `HelloMsg` with protocol version + profile nickname + profile id.
3. Host validates nickname uniqueness, assigns a connection id, sends
   `WelcomeMsg` with the party state snapshot + the client's role.
4. Host broadcasts `MemberJoinedMsg` to all other connections.

### Lobby events

`MemberJoinedMsg`, `MemberLeftMsg`, `MemberReadyMsg`, `ChatMsg`,
`StatusChangedMsg` (party status: Lobby / Worldbuilding / CharacterCreation /
Playing).

### Game events

- Client → Host: `ActionQueuedMsg` (player submits an action), `ActionCancelMsg`.
- Host → All: `ActionResolvingMsg` (host starts resolving a batch),
  `NarrativeDeltaMsg` (streamed text chunk), `NarrativeFinalMsg` (full text +
  tool events), `StateUpdateMsg` (world snapshot), `TurnEndMsg`.
- Host → One: `ErrorMsg`, `KickedMsg`.

### System

`PingMsg` / `PongMsg` for keepalive. The client fires `Disconnected` on socket
close; the UI shows a reconnect prompt. Reconnect logic is not yet
implemented (see issue tracker).

---

## Save file format

Each save is a directory `%APPDATA%/Pathstone/saves/save_{Guid:N}/` containing
four JSON files.

### `meta.json`

```json
{
  "id": "save_abc123...",
  "name": "Тёмные Шпили Велариса",
  "ownerId": "profile-guid",
  "partyId": null,
  "characterName": "Странник",
  "characterLevel": 1,
  "worldTitle": "Тёмные Шпили Велариса",
  "turn": 7,
  "buildStatus": "done",
  "engineVersion": "0.2.0",
  "createdAt": "2025-01-01T12:00:00Z",
  "updatedAt": "2025-01-01T14:30:00Z"
}
```

### `world.json`

The full `World` state: all entity collections, RNG state, clock, ruleset,
content registry, flags. Serialized via `World.ToJson()` with camelCase
policy and `UnsafeRelaxedJsonEscaping` (Cyrillic writes as raw UTF-8, not
`\uXXXX` escapes). Round-trips through `World.FromJson()` bit-for-bit; RNG
state reproduces exactly.

### `log.json`

Array of `LogEntry` records (narrative / action / system / tool entries with
timestamps and metadata).

### `state.json`

Runtime state: turn number, current player entity id, token usage totals,
build state. A denormalized subset of `world.json` for fast UI boot without
loading the full world.

### Atomic writes

Each file is written to `{file}.tmp` then `File.Move`d to its final path.
`meta.json` is written last, so a crash mid-save leaves a loadable (stale-meta)
state rather than a corrupted one.

---

## Project conventions

### Naming

- PascalCase for types, methods, properties.
- camelCase for local variables and parameters.
- `_camelCase` for private fields.

### Namespaces

- `MyGame.Core.Common`, `MyGame.Core.Rules`, `MyGame.Core.World`,
  `MyGame.Core.World.Entities`, `MyGame.Core.World.Content`,
  `MyGame.Core.AI`, `MyGame.Core.AI.Agents`, `MyGame.Core.AI.Prompts`,
  `MyGame.Core.AI.Tools`, `MyGame.Core.Saves`, `MyGame.Core.Profile`,
  `MyGame.Core.Multiplayer`, `MyGame.Core.Multiplayer.Protocol`.
- `MyGame.Desktop.Services`, `MyGame.Desktop.ViewModels`,
  `MyGame.Desktop.ViewModels.Panels`, `MyGame.Desktop.Views`,
  `MyGame.Desktop.Views.Panels`.

### Async

- `Task<T>` and `CancellationToken` on all public async methods.
- `ConfigureAwait(false)` in Core (no synchronization context).
- UI thread marshaling via `Avalonia.Threading.Dispatcher.UIThread.Post` in
  Desktop event handlers.

### JSON

- `System.Text.Json` throughout.
- camelCase wire format for AI responses and save files.
- `PropertyNameCaseInsensitive = true` on deserialization.
- `UnsafeRelaxedJsonEscaping` for human-readable Cyrillic in saves.

### Mutability

- Records for immutable data (entity snapshots, message types, plan records).
- Classes for stateful services (`World`, `GameMaster`, `SaveManager`,
  `HostServer`).
- Entities are mutable classes (HP changes, location shifts, inventory
  mutations happen in place).

### Error handling

- `Result<T>` for flow-control errors in Core (file not found, parse failure).
- Exceptions for programmer errors (null args, invalid state).
- `AiException` for HTTP / parse failures from the LLM provider.
- All tool handlers wrapped in try/catch; failures become `ToolResult` with
  `IsError = true` and feed back to the model.

### Thread safety

- `ConcurrentDictionary` for connection registries.
- `SemaphoreSlim` per WebSocket (concurrent sends corrupt the wire).
- Plain `lock` for the action queue (`DrainAll` wants bulk removal).
- `EventBus` dispatches outside the lock to allow re-entry.

---

## Roadmap

The roadmap is tracked in the [GitHub issues](https://github.com/GardenXsa/Pathstone/issues).
Issues are labeled by layer (`core`, `ui`, `ai`, `mp`, `saves`, `engine`,
`content`, `audio`, `packaging`, `tooling`, `polish`) and by priority
(`mvp`, `mvp-blocker`, `stretch`).

High-level themes, in rough priority order:

1. **MVP completion** — travel UI, character creation, combat, basic packaging.
   These are the `mvp-blocker` issues.
2. **AI robustness** — streaming narration, context window management,
   anti-loop detection, multi-model support, local models.
3. **Multiplayer polish** — reconnect, host migration, UPnP, late-joiner sync.
4. **Content depth** — more templates, faction system, economy, procedural
   dungeons, weather.
5. **Polish** — sounds, music, animations, themes, localization,
   accessibility.
6. **Packaging** — Windows installer, macOS bundle, Linux AppImage, auto-update,
   code signing.
7. **Stretch** — voice chat, mod support, demo mode, documentation site.

---

## Contributing

The project is in active alpha development. The architecture is settled; the
work is feature implementation. If you want to contribute:

1. Pick an issue labeled `good first issue` or `mvp` from the tracker.
2. Fork the repository and create a feature branch.
3. Follow the conventions above. Build must pass with 0 warnings, 0 errors.
4. Open a pull request referencing the issue.

For architectural questions, open a discussion or an issue labeled `question`.

### Development environment

- .NET 8 SDK
- Any C#-aware editor (Visual Studio, Rider, VS Code with C# Dev Kit)
- Linux/macOS developers: `xvfb-run` for headless smoke testing

### Running tests

There is no test project yet. Unit tests for Core (engine rules, save
round-trip, AI client with a mock HTTP handler) are tracked in the issue
tracker.

---

## License

To be determined. The current alpha is source-available on GitHub. A
permissive license (MIT or Apache 2.0) is likely once the MVP ships.

---

## Acknowledgements

Pathstone is a rewrite of an earlier prototype. The architecture was
reconsidered from scratch after the prototype's web-first assumptions (Next.js
API server, JWT auth, socket.io, browser client) proved to be a poor fit for
a desktop TTRPG application. The rewrite keeps the game design and the AI
prompt corpus; it replaces everything else.
