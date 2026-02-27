# Inkling — AI Agent Context

## What Is This Project?

Inkling is a 2D life-simulation game built in **Unity 6** (6000.2.5f1). Players create and control ink-based creatures in a GPU-accelerated fluid world, solving puzzles and befriending other Inklings. The core technical challenge is real-time simulation and stylization of multi-layered interactive ink.

- **Platforms:** iOS / Android (mobile-first, validated on desktop)
- **Target audience:** Women 25+, casual-to-mid-core
- **Design pillars:** Mystery & wonder, emergent living world, comfort & befriending
- **Branch:** `FirstPass` (primary development branch)

## Repository Layout

```
Inkling/                              # Repo root
├── Inkling/                          # Unity project folder
│   ├── Assets/_Project/
│   │   ├── Scenes/Main.unity         # Single main scene
│   │   ├── Scripts/
│   │   │   ├── Dev/Bootstrap.cs      # Entry point (execution order +100)
│   │   │   ├── Systems/              # 12 gameplay systems (see below)
│   │   │   ├── Services/             # ServiceBootstrap, ITUMS, Diagnostics
│   │   │   └── UI/
│   │   ├── Inks/                     # 10 InkDefinition ScriptableObjects
│   │   ├── Materials/
│   │   ├── Config/                   # Gesture templates, capture configs
│   │   └── Tests/                    # EditMode (25) + PlayMode (13)
│   ├── Packages/                     # Local UPM packages
│   └── ProjectSettings/
├── InkTools/                         # Engine-level sim/debug package (com.inktools.sim, com.inktools.core)
├── MagiUnityTools/                   # Shared infra package (com.magi.unitytools)
├── AGENTS.md                         # Legacy state snapshot (partially outdated)
├── GEMINI.md                         # Project overview for Gemini
└── CLAUDE.md                         # This file
```

## Systems Architecture

All gameplay systems live under `Assets/_Project/Scripts/Systems/<SystemName>/` with subfolders: `Core/`, `Data/`, `Components/`, `Services/`, `Compute/`, `Rendering/`.

Namespaces follow: `Magi.Inkling.Systems.<SystemName>`

### Core Simulation (SimulationLOD0)

The heart of the project. GPU Navier-Stokes fluid solver split into modular components (Phase 8):

| Module | File | Role |
|--------|------|------|
| **SimDriver** | `SimulationLOD0/SimDriver.cs` | Facade, owns all modules, exec order +50 |
| SimulationContext | `Core/SimulationContext.cs` | Shared GPU state (RTs, buffers, params) |
| SimulationResources | `Core/SimulationResources.cs` | RT/buffer allocation & cleanup |
| OperationQueue | `Core/OperationQueue.cs` | Queues density/force injections |
| FluidSolver | `Core/FluidSolver.cs` | Physics dispatch (advect, diffuse, pressure, vorticity) |
| SimulationDisplay | `Core/SimulationDisplay.cs` | Output compositing & perf metrics |

Key compute shaders: `BatchedInjection.compute`, `BatchedMask.compute`, `BatchedStamp.compute`, `InkInteractions.compute`

### Gameplay Systems

| System | Key Files | Status |
|--------|-----------|--------|
| **Brush** | `BrushInputController.cs`, `BrushConfig.cs` | Working, exec order -40 |
| **Player** | `PlayerCharacterController.cs`, `IPlayerCharacter.cs`, `PlayerConfig.cs` | Working, exec order -55 |
| **Creatures** | `CreatureBehavior.cs`, `AnimatedCreature.cs`, `CreatureDefinition.cs` | Code complete, needs content |
| **Growth** | `GrowthSystem.cs`, `Growth.compute`, `CA.compute` | Working, exec order +55 |
| **Agents** | `AgentSystem.cs`, `Agents.compute`, `AgentRenderer.cs` | Code complete, not in Main scene |
| **Obstacles** | `ObstacleSystem.cs`, circle/edge/polygon components | Code complete, not in Main scene |
| **Gestures** | `GestureInputController.cs`, `GestureRecognizer.cs` | In Main scene |
| **Capture** | `CaptureService.cs`, `ReadbackUtility.cs` | In Main scene |
| **Rendering** | `InkGradientPreset.cs`, `InkGradientRenderer.shader` | Shader baseline working |
| **OpticalFlow** | `OpticalFlowInput.cs` | Minimal |
| **Foveation** | Multi-resolution blending | Code-complete, not wired |

### Execution Order

```
-200  ServiceBootstrap
-100  ServiceLocator
 -55  PlayerCharacterController
 -50  TexturedInjector
 -45  OpticalFlowInput (if present)
 -41  GestureInputController
 -40  BrushInputController
   0  CreatureBehavior
  50  SimDriver
  55  GrowthSystem
 100  AgentSystem, Bootstrap
```

## Key Patterns & Conventions

### Service Discovery
- `ServiceLocator` lives in `Magi.UnityTools.Patterns` (moved from Inkling in Phase 8)
- `ServiceBootstrap` auto-discovers and registers all `IService` implementations
- Simulation interfaces (`ISimulationReader`, `ISimulationWriter`, `ISimulationService`) live in `Magi.InkTools.Simulation`

### Ink System
- 10 ink types defined as `InkDefinition` ScriptableObjects in `Assets/_Project/Inks/`
- Types: BlackBody, Water, Fire, Ice, Steam, Glitter, PlantSeeded, PlantGrown, ElectricitySeeded, ElectricityGrown
- Ink interactions use `AffinityGroup` assets with conjunctive product matrices (ThermalGroup, OrganicGroup)
- Per-ink interaction thresholds gate reactions in `InkInteractions.compute`

### TexturedInjector
- Shared component used by both Player and Creatures to inject density into the simulation
- `ExternallyControlled` property suppresses autonomous wandering (used by PlayerCharacterController)
- `simulationServiceSource` field wires to SimDriver
- Near-black pixels treated as transparent to prevent accidental BlackBody flooding

### Assembly Definitions
- `Magi.Inkling` — main game assembly
- `Magi.Inkling.Brush` — brush input (decoupled)
- `Magi.Inkling.Gestures` — gesture recognition (decoupled)
- `Magi.Inkling.Capture` — dataset capture
- `Magi.Inkling.Tests.EditMode` / `.PlayMode` — test assemblies
- `Magi.InkTools.Debug` — guarded by `INKTOOLS_DEBUG` define

### Coding Conventions
- Use Unity 6 APIs: `FindFirstObjectByType` / `FindAnyObjectByType` (not deprecated `FindObjectOfType`)
- Use New Input System (`UnityEngine.InputSystem`)
- Inspector references over `Resources.Load`
- Prefer composition over monoliths
- `[SerializeField]` stays on MonoBehaviour facades; internal modules are plain C# classes
- Compute shader thread groups: 8x8 default (good for mobile GPUs)
- Use `enableRandomWrite = true` only on compute targets

### Using Statements (Post-Phase 8)
```csharp
using Magi.UnityTools.Patterns;     // ServiceLocator, IService, Result, ILogSink
using Magi.InkTools.Simulation;     // ISimulationReader, ISimulationWriter, ISimulationService
```

Debug renderers require:
```csharp
#if INKTOOLS_DEBUG
using Magi.InkTools.Simulation;     // VelocityArrowsRenderer, etc.
#endif
```

## Main Scene State (as of 2026-02-26)

The Main scene has been updated to include baseline runtime wiring:

**Present and wired:**
- SimDriver + all modular core modules
- GrowthSystem
- BrushInputController
- ServiceLocator (auto-discover enabled) + ServiceBootstrap
- Player GameObject (tag: `Player`, TexturedInjector + PlayerCharacterController)
- GestureInput GameObject (GestureInputController + templates)
- CaptureService GameObject (wired to SimDriver)
- Inkling1 creature (TexturedInjector + CreatureBehavior)
- DiagnosticsHUD (disabled, toggle with F9 when enabled)

**Not in Main scene (optional, add manually):**
- AgentSystem + AgentRenderer
- ObstacleSystem + obstacle components
- Additional creatures (duplicate Inkling1, assign different configs)
- MultiResolutionDriver

## Development Phase Status

| Phase | Status | Summary |
|-------|--------|---------|
| Phase 6 | Complete | Display stabilization, DX12 flicker fix |
| Phase 7A-7F | Complete | Feature parity, architecture migration |
| Phase 8 | Complete | Modular SimDriver split, test hardening (62 tests), debug cleanup |
| **Phase 9** | **In Progress** | Gameplay loop |

### Phase 9 Progress
1. **Player Avatar Control** — Complete (2026-02-09)
2. **Spawning Loop** — Not started
3. **Befriend Meter/Logic** — Not started

### Phase 9 Open Questions
- Spawning: event-driven (gesture triggers) vs continuous (world-state threshold)?
- Befriend: what metrics drive the meter? (proximity, ink type, duration, gesture?)
- How does ITUMS persona interact with gameplay difficulty/progression?

## Testing

- **62 total tests:** 15 MagiUnityTools + 9 InkTools + 25 Inkling EditMode + 13 Inkling PlayMode
- Test assemblies split for batchmode discovery
- Shared `TestHelpers` in MagiUnityTools
- Run via Unity Test Runner or CLI

## Performance Budgets (Mobile Targets)

- Simulation: ≤4-6 ms
- ML inference: ≤3-5 ms (when wired)
- Compositing/foveation: ≤2 ms
- GPU memory (sim + models + RTs): ≤350 MB
- Target devices: iPhone 12 (A14), Pixel 6 (Tensor G1)

## ML Pipeline (Future)

- Unity Sentis for runtime inference (ONNX models)
- U-Net for LOD0->LOD1 stylization transition
- Shader/mip-chain baseline is Tier 0 fallback (always ships)
- Model exists (`unet_fp32.onnx.bytes`) but inference path not wired
- Automated dataset capture via CaptureService

## Wiki Reference

Detailed design documentation lives on the Magi Archive wiki under `Games+Inkling+*`:
- `Games+Inkling+Technical+*` — architecture, setup guide, agent system, best practices
- `Games+Inkling+Phase *` — phase plans and implementation history
- `Games+Inkling+GDD V3` — full game design document
- `Games+Inkling+Ink Interactions Design` — thermal/organic reaction system
- `Games+Inkling+Core Design+*` — design philosophy, pillars, comparables

## Important Notes for AI Agents

1. **Do not modify `.meta` files** — let Unity generate them
2. **Scene changes require care** — Main.unity is YAML; prefer programmatic wiring over manual edits when possible
3. **Respect execution order** — components depend on ordered Update() calls
4. **Test before committing** — 62 tests should stay green
5. **`INKTOOLS_DEBUG`** — must be in scripting defines to use debug renderers
6. **AGENTS.md is partially outdated** — Main scene wiring was updated 2026-02-26; this CLAUDE.md reflects current state
7. **Three repos in play** — Inkling (game), InkTools (engine), MagiUnityTools (shared). Changes to interfaces may span repos
