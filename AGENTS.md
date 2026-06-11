# Inkling Project State Snapshot (2026-02-26)

This file captures the current project state from:
- Wiki technical cards under `Games+Inkling+Technical+*`
- Wiki phase cards under `Games+Inkling+Phase *`
- Direct code/scene inspection in this repo

## Sources Reviewed

### Technical cards
- `Games+Inkling+Technical+intro`
- `Games+Inkling+Technical+Inkling Unity6 Best Practices`
- `Games+Inkling+Technical+Technical Specification`
- `Games+Inkling+Technical+Agent System`
- `Games+Inkling+Technical+Cleanup Plan 2026-01-27`
- `Games+Inkling+Technical+Unity Setup Guide`
- `Games+Inkling+Technical+Implementation Overview` (very large; mixed current architecture + broad research notes)
- `Games+Inkling+Technical+DX12 Density Display Flickering Fix`

### Phase cards
- `Games+Inkling+Phase 6 Display Stabilization`
- `Games+Inkling+Phase 7A Feature Parity Plan`
- `Games+Inkling+Phase 7B Plan`
- `Games+Inkling+Phase 7C Plan`
- `Games+Inkling+Phase 7D Plan`
- `Games+Inkling+Phase 7E Plan`
- `Games+Inkling+Phase 7F Plan`
- `Games+Inkling+Phase 8 Preparation`
- `Games+Inkling+Phase 8 Plan`
- `Games+Inkling+Phase 8 Workstream A Implementation Plan`
- `Games+Inkling+Phase 8 Workstream B Implementation Plan`
- `Games+Inkling+Phase 8 Workstream C Implementation Plan`
- `Games+Inkling+Phase 8 Workstream D Implementation Plan`
- `Games+Inkling+Phase 8 Workstream E Implementation Plan`
- `Games+Inkling+Phase 8 Migration Reference`
- `Games+Inkling+Phase 9 Preview`

## Wiki-Documented Status (as of latest card updates)

- Phase 6 marked complete (display stabilization, DX12 flicker fix lineage).
- Phase 7A-7F marked complete, with some manual Unity scene wiring explicitly deferred.
- Phase 8 marked complete across Workstreams A-E (+ migration/docs).
- Phase 9 marked in progress:
1. Player avatar control complete (dated 2026-02-09 in card).
2. Spawning loop not started.
3. Befriend logic not started.

## Code-Verified Status (this repository)

### Confirmed implemented in code
- SimDriver modular split is present:
1. `SimulationContext`
2. `SimulationResources`
3. `OperationQueue`
4. `FluidSolver`
5. `SimulationDisplay`
6. `SimDriver` facade + `ISimulationDebug`
- Controller renames are present:
1. `BrushInputController`
2. `GestureInputController`
- Player system exists:
1. `IPlayerCharacter`
2. `PlayerConfig`
3. `PlayerCharacterController`
4. `TexturedInjector.ExternallyControlled`
- Gameplay systems exist in code:
1. Growth (`GrowthSystem`, `Growth.compute`)
2. Obstacles (`ObstacleSystem`, obstacle components/compute)
3. Creatures (`AnimatedCreature`, `CreatureBehavior`, configs)
4. Agents (`AgentSystem`, `Agents.compute`, renderer/shader)
5. Capture (`CaptureService`, `ReadbackUtility`, configs)
6. ITUMS wiring and diagnostics hooks

### Test inventory in this repo
- Inkling tests currently contain:
1. 25 EditMode test attributes
2. 13 PlayMode test attributes
- Test asmdef split is present (`Magi.Inkling.Tests`, `.EditMode`, `.PlayMode`).

## Main Scene Reality Check (`Inkling/Assets/_Project/Scenes/Main.unity`)

### Present in scene
- `SimDriver`
- `GrowthSystem`
- `TexturedInjector`
- `DiagnosticsHUD` (component present; disabled)
- `ServiceBootstrap`

### Not found in scene (despite some wiki/setup guidance implying ready wiring)
- `CaptureService`
- `PlayerCharacterController`
- `GestureInputController`
- `CreatureBehavior` / `AnimatedCreature`
- `AgentSystem`
- `ObstacleSystem`

### Stale/missing script GUID references in `Main.unity`
- Brush object points to missing script GUID `6eaa3be8c72c4861a5b43a1a996a96b4` while labeling `BrushInputController`.
- ServiceLocator object points to missing script GUID `90af2cde74ef4c6d8c5e9c3a1d395db5` with old class identifier `Magi.Inkling.Services.Core.ServiceLocator`.
- Current ServiceLocator implementation exists in `MagiUnityTools` under `Magi.UnityTools.Patterns.ServiceLocator`.

## Card-to-Code Gaps Worth Tracking

- The Unity Setup Guide claims `CaptureService` and fully wired core loop in Main scene; current `Main.unity` does not include `CaptureService` and lacks player/creature gameplay setup.
- Phase 9 card says player avatar code is complete; code exists, but scene setup is still manual.
- Agent system card says implemented; core implementation exists, but `DespawnInRegion` remains TODO in code.
- Some docs discuss strict completed migrations; scene serialization still reflects pre-migration script references.

## Working Tree Notes

- Current branch: `FirstPass` (ahead of `origin/FirstPass`).
- Working tree is not clean: many modified assets and many untracked `.meta` files are present.
- Treat this as an in-progress Unity workspace, not a clean reproducible baseline.

