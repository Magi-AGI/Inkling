# GEMINI.md - Inkling Context

## Project Overview
**Inkling** is an experimental Unity 6-based ink-life sandbox. It utilizes a GPU-accelerated fluid solver (Navier-Stokes) to simulate living ink organisms. The project combines traditional shader-based stylization with ML-driven (U-Net) stylization using Unity Sentis.

### Key Technologies
- **Unity 6 (6000.2.5f1)**: High Definition Render Pipeline (HDRP 17.3.0).
- **GPU Fluid Simulation**: Custom Navier-Stokes solver implemented in HLSL/Compute Shaders (`Fluids.compute`).
- **Unity Sentis**: ML inference for real-time stylization.
- **Foveated Rendering**: Multi-resolution blending (center ML, peripheral native stylization).
- **GPU Agent System**: Flocking and fluid advection simulation for large agent counts.

### Core Architecture (Phase 8/9 Refined)
- **SimDriver**: Facade and lifecycle manager. It delegates to modular components:
  - **SimulationContext**: Shared state and parameters.
  - **SimulationResources**: Allocation and management of RenderTextures/Buffers.
  - **OperationQueue**: Command buffering for asynchronous sim updates (injections, stamps).
  - **FluidSolver**: The core Navier-Stokes implementation (Advection, Diffusion, Pressure, etc.). Supports multi-resolution (up to 1024x1024) with selective diffusion gating.
  - **SimulationDisplay**: Handles final compositing and UI metrics.
- **Player System**: 
  - **PlayerCharacterController**: Maps input to a visible ink avatar (`IPlayerCharacter`) using robust multi-plane UV mapping and hardened input switching.
  - **TexturedInjector**: Injects density masks (creatures/player) into the simulation with near-black luminance filtering to prevent background bleed.
- **Agent System**: GPU-based flocking (`AgentSystem`) integrated with fluid velocity.
- **Capture & Gesture**: 
  - **CaptureService**: Synchronized frame and metadata recording for ML training.
  - **GestureInputController**: Recognizes patterns (Circle, Line, etc.) for gameplay interaction with the same robust UV mapping as the player system.

---

## Building and Running
### Prerequisites
- **Unity 6000.2.5f1 (LTS)**
- **Desktop GPU** with Compute Shader support.
- **Git LFS** enabled for binary assets (models, textures).

### Setup
1. Open the project at `./Inkling/Inkling` in Unity Hub.
2. If local packages (`InkTools`, `MagiUnityTools`) are missing or broken, run the dependency manager from the repo root:
   ```powershell
   ../MagiUnityDependencyManager/magi-deps.ps1 apply -ProjectPath ./Inkling -Strict
   ```
3. Open `Assets/_Project/Scenes/Main.unity` and press Play.
   - **Note**: The main scene has been updated (2026-02-26) to wire `ServiceLocator`, `CaptureService`, `GestureInput`, and the `Player` system.
   - **Tuning**: High-resolution (1024) simulations use reduced diffusion iterations (e.g., 8) and rebalanced ink properties (Water/BlackBody) to maintain motion stability.

### Development Workflow
- **Simulation Tuning**: Adjust parameters (viscosity, vorticity, iterations) on the `SimDriver` component.
- **Diagnostic Tools**: `SimDriver` includes two key debug flags:
  - `debugInjectTestForce`: Injects a constant rightward force and red ink at the center. Use this to verify if the velocity-advection pipeline is functional independently of player input.
  - `debugLogForces`: Logs force injection details to the console every 60 frames to verify that commands are reaching the GPU.
- **Fluid Troubleshooting**:
  - If ink "sits and dissipates" without moving, verify that `Fluids.compute` has been recompiled (touch the file with a comment). A common issue is $dt^2$ attenuation in the `AddForce` kernel.
  - Enable `Display Velocity` on `SimDriver` to visualize the raw velocity field as color.
- **Input Hardening**: Both `PlayerCharacterController` and `BrushInputController` support New Input System and legacy hotkeys (1-0/Numpad) for ink selection.
- **Debug Assembly**: Many debug renderers are moved to a conditional `Magi.InkTools.Debug` assembly (use `INKTOOLS_DEBUG` define).
- **Reference Deep Dives**: Phase 7F focused on **The-Powder-Toy** for GPU-friendly pressure/heat/air field adaptations and element registry patterns.

---

## Development Conventions
- **Namespaces**: `Magi.Inkling.*` hierarchy.
- **Service Discovery**: Uses `Magi.UnityTools.Patterns.ServiceLocator` with `autoDiscover` enabled.
- **Input System**: Use the **New Input System** (`UnityEngine.InputSystem`).
- **Simulation Injection**: Use `ISimulationWriter` (via `SimDriver`) to queue density or force commands.
- **Performance**: Targets (mid-tier mobile): Simulation ≤ 5ms, ML Inference ≤ 4ms, Compositing ≤ 2ms.

---

## Key Files
- `README.md`: High-level project summary and setup.
- `IMPLEMENTATION_PLAN.md`: Detailed roadmap for Phase 1 (Generative Art Pipeline).
- `AGENTS.md`: Context for AI development agents.
- `Inkling/Assets/_Project/Scripts/Systems/SimulationLOD0/SimDriver.cs`: Central simulation driver.
- `InkTools/Assets/_Project/Scripts/Simulation/Compute/Fluids.compute`: Core GPU solver logic.
