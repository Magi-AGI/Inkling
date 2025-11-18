# Inkling

A Unity 6-based experimental ink-life sandbox built on a GPU fluid solver, with non-ML stylization in place and ML-driven stylization planned.

## Overview

Inkling lets you interact with a living world of ink-based organisms. The current implementation focuses on:

- Real-time 2D fluid simulation (Navier–Stokes) powered by the shared `InkTools` package
- Gradient-based and baseline stylization for different “ink types” (fire, water, ice, etc.)
- Runtime dataset capture for training future ML models

ML inference (Sentis U-Net) and full game-layer systems are design goals but not yet implemented in this project.

## Technical Architecture

- **LOD0 Fluid Simulation**: GPU Navier–Stokes solver from `InkTools` (`Fluids.compute`) driven by `SimDriver` and `PingPongRenderTexture`
- **Baseline Stylization Pipeline**: Non-ML stylization via `BaselineStylizer` (shader/compute) plus `InkGradientRenderer` for per-ink gradients
- **Foveation & Multi-Resolution (Code-Complete)**: `MultiResolutionDriver` and `FoveatedComposer` provide upsampling and seam blending; wiring into the main scene is still in progress
- **ML Stylization (Planned)**: U-Net ONNX model asset (`Assets/_Project/Models/unet_fp32.onnx.bytes`) is present; Sentis runtime and ML path are not yet wired

## Project Structure

High-level layout (Unity 6000.2.5f1 project nested under `Inkling/Inkling`):

```
Inkling/
  Inkling/
    Assets/
      _Project/
        Scenes/                 # Main.unity, presets
        Scripts/
          Dev/                  # Bootstrap, DevOverlay, RecordReplay, TestPatternGenerator
          Systems/
            SimulationLOD0/     # SimDriver, SimulationRecorder, TexturedInjector, MultiResolutionDriver
            Foveation/          # BaselineStylizer(Compute), FoveatedComposer, BlendSeam.shader
            Rendering/          # InkGradientPreset, InkGradientRenderer, InkRenderPipeline
          UI/                   # ScenarioDropdownHelper, ElementSpriteGenerator
        Models/                 # ONNX model bytes for future ML
    Packages/                   # Includes com.inktools.sim, com.magi.unitytools
    ProjectSettings/
```

## Setup Instructions

### Prerequisites
- Unity 6000.2.5f1 (Unity 6 LTS line)
- Desktop GPU with compute shader support (for development)
- Git LFS for binary assets (models, large textures)

### Getting Started
1. Open the Unity project at `./Inkling/Inkling` in Unity Hub/Editor.
2. Run the dependency manager from the repo root if needed:
   ```powershell
   ../MagiUnityDependencyManager/magi-deps.ps1 apply -ProjectPath ./Inkling -Strict
   ```
3. Let Unity import the local packages:
   - `com.inktools.core` / `com.inktools.sim`
   - `com.magi.unitytools`
4. In the Editor, open `Assets/_Project/Scenes/Main.unity` and press Play.

The `Bootstrap` component in `Main.unity` wires up:
- `SimDriver` (fluid simulation driven by `Fluids.compute`)
- RenderTextures for hi/lo-res output
- `SimulationRecorder` + `CaptureDriver` for runtime capture
- A simple `RawImage` UI showing the simulation.

## Development Workflow

### Simulation Development
- High-level driver: `Assets/_Project/Scripts/Systems/SimulationLOD0/SimDriver.cs`
- GPU solver: `InkTools/InkTools/Assets/_Project/Scripts/Simulation/Compute/Fluids.compute` and its `Include/*.hlsl` files
- Use `Main.unity` and `SimDriver`’s on-screen metrics (`OnGUI`) to tune resolution, vorticity, and solver iterations.

### Stylization & Rendering
- Baseline stylization:
  - Shader path: `Assets/_Project/Scripts/Systems/Foveation/BaselineStylizer.shader`
  - Compute path: `Assets/_Project/Scripts/Systems/Foveation/BaselineStylizerCompute.cs` + `BaselineStylizer.compute`
- Gradient compositing:
  - `Assets/_Project/Scripts/Systems/Rendering/InkGradientPreset.cs`
  - `Assets/_Project/Scripts/Systems/Rendering/InkGradientRenderer.shader`
- `SimDriver` can output either raw density/velocity or gradient-mapped color, depending on configuration.

### ML Integration (Planned / Partial)
- Model asset:
  - `Assets/_Project/Models/unet_fp32.onnx.bytes`
- Assembly is prepared for Sentis via `Magi.Inkling.asmdef`, but:
  - `Assets/_Project/Scripts/Systems/Inference/` is currently empty.
  - There is **no Sentis runtime (`SentisRunner`) or inference scene yet**.
- See `IMPLEMENTATION_PLAN.md` for the intended Sentis integration steps.

### Dataset Capture
- Runtime capture is implemented in:
  - `SimulationRecorder` (`Assets/_Project/Scripts/Systems/SimulationLOD0/SimulationRecorder.cs`)
  - `CaptureDriver` (`Assets/_Project/Scripts/Systems/SimulationLOD0/CaptureDriver.cs`)
- Captures:
  - Hi-res stylized output
  - Lo-res physics textures
  - Per-frame JSON metadata for training (resolution, formats, sim parameters).

## Performance Targets (Design)

- **Simulation (GPU)**: ~4–6 ms per frame on a mid-range desktop GPU
- **Baseline Stylizer**: ~2 ms per frame
- **ML Inference (Sentis)**: ~3–5 ms per frame (goal, once implemented)
- **Target Devices**: iPhone 12 / Pixel 6 as baseline mobile targets

These are design budgets; only the simulation and baseline stylizer are currently measurable in this project.

## Related Repositories

- **[InkTools](../InkTools)**: Core fluid simulation and cellular automata systems (includes `Fluids.compute`)
- **[InkModel](../InkModel)**: ML pipeline for training stylization models
- **[MagiUnityTools](../MagiUnityTools)**: Common Unity utilities (e.g., `PingPongRenderTexture`)
- **[MagiUnityDependencyManager](../MagiUnityDependencyManager)**: Package dependency management

## Build & Deployment

### Mobile Targets
- iOS: Xcode 14+ (URP/HDRP configuration out of scope of this doc)
- Android: Minimum API Level 24 (Android 7.0) recommended

The current focus is desktop iteration; mobile profiling and quality tiers are part of later phases.

### Planned Quality Tiers
- **Tier 0**: Shader-only stylization (no ML, current default)
- **Tier 1**: Hybrid ML + shader rendering (center ML, periphery shader)
- **Tier 2**: Full ML chain with foveation

Only Tier 0 is implemented today.

## Documentation

For more architecture and design notes, see:
- `FLUID_SIMULATION_IMPROVEMENTS.md`
- `IMPLEMENTATION_PLAN.md`
- `Inkling/Inkling/Assets/UNITY_SCENE_SETUP.md`

Additional game/ML design docs live in the separate knowledge repo (`magi-knowledge-repo-2`).
