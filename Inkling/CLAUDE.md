# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

**Inkling** is a Unity 6.0 (6000.2.0f1) project featuring real-time 2D fluid simulation with GPU compute shaders and ML-based stylization for mobile platforms. The project implements hierarchical simulation layers with a focus on performance-constrained environments.

**Technology Stack:**
- Unity 6000.2.0f1
- High Definition Render Pipeline (HDRP) 17.2.0
- Unity Burst + Mathematics for high-performance compute
- New Input System 1.14.2
- Custom packages via local file references

## Project Structure

The project uses a **nested Unity project structure**:
- Root: `E:\GitLab\the-smithy1\magi\Magi-AGI\Inkling\`
- Unity project: `Inkling/` (nested subdirectory)
- Working directory: `Inkling/` (always work from the nested Unity project)

### Assembly Structure

The codebase uses **per-system assembly definitions** for clean dependency management:

- **Magi.Inkling.Runtime**: Main runtime assembly
  - References: Unity.Mathematics, Unity.Burst, Unity.InputSystem, UnityEngine.UI
  - References custom packages: Magi.InkTools.Runtime, Magi.InkTools.Simulation, Magi.UnityTools.Runtime
  - Location: `Assets/_Project/Runtime/Magi.Inkling.Runtime.asmdef`

### Custom Package Dependencies

Configured via `depfile.yaml` and `Packages/manifest.json`:

```yaml
packages:
  com.unity.inputsystem: 1.14.2
  com.magi.unitytools: file:../../../MagiUnityTools/MagiUnityTools/Assets/_Project/MagiUnityTools
  com.inktools.sim: file:../../../InkTools/InkTools/Assets/_Project/InkTools.Simulation
```

**Important:** Package paths are relative to the parent directory structure. These packages must exist in sibling repositories.

### Banned APIs

Per `depfile.yaml` policy:
- ❌ `Resources.Load` - Use direct references or addressables
- ❌ `FindObjectOfType` - Use explicit references or dependency injection

## Key Systems

### 1. Fluid Simulation (`Systems/SimulationLOD0/`)

**SimDriver.cs** - Core GPU-accelerated fluid dynamics:
- Implements Navier-Stokes solver using compute shaders
- Uses ping-pong buffers via `PingPongRenderTexture` (from MagiUnityTools)
- Resolution-independent simulation (default 256x256)
- Configurable solver: Jacobi vs Red-Black Gauss-Seidel

**Key Parameters:**
- Viscosity, vorticity, dissipation rates
- Pressure iterations (20-80 for quality tiers)
- Diffusion iterations
- Timestep (fixed 0.016s default)

**MultiResolutionDriver.cs** - Upsampling pipeline:
- Separate simulation and display resolutions
- Temporal upsampling with motion compensation
- Adaptive resolution based on velocity magnitude

**SimulationRecorder.cs** - Dataset capture:
- Records hi-res (512x512) and lo-res (256x256) frame pairs
- Exports metadata JSON with simulation parameters
- Supports manual capture via `StartCapture()`/`StopCapture()`
- Batch capture mode for training datasets

### 2. Bootstrap System (`Dev/Bootstrap.cs`)

**Scene initialization pattern:**
- Creates render textures (hi-res/lo-res pair)
- Instantiates SimDriver if not in scene
- Sets up Canvas + RawImage for display
- Auto-wires recorder with capture driver
- Avoids Resources.Load by using direct Inspector references

**Usage:** Attach to empty GameObject in scene with compute shader reference assigned.

### 3. Compute Shader Architecture

**Location:** Check both:
- `Packages/com.inktools.sim/Runtime/Compute/Fluids.compute`
- `Assets/_Project/InkTools.Simulation/Runtime/Compute/Fluids.compute` (fallback)

**Required Kernels:**
- `Advection`, `Diffusion`, `Divergence`
- `Pressure`, `SubtractGradient`
- `Vorticity`, `VorticityConfinement`
- `AddForce`, `AddDensity`, `Clear`
- Optional: `PressureRedBlack`, `UpdateObstacles`, `ApplyObstacleBoundary`

### 4. Input System Integration

**Using New Input System (not legacy):**
```csharp
using UnityEngine.InputSystem;

// Check for null before use
if (Mouse.current != null && Mouse.current.leftButton.isPressed)
{
    Vector2 mousePos = Mouse.current.position.ReadValue();
    Vector2 mouseDelta = Mouse.current.delta.ReadValue();
}
```

All mouse input must use `Mouse.current` API with null checks.

### 5. Foveation System (`Systems/Foveation/`)

- Baselined stylization via compute shaders
- Hybrid ML + shader rendering for quality tiers
- Center tile high-quality, periphery optimized

## Development Workflow

### Running the Project

1. **Open Unity Project:**
   - Open `Inkling/Inkling/` in Unity Hub (nested directory)
   - Unity 6000.2.0f1 required

2. **Scene Setup:**
   - Main scene: `Assets/_Project/Scenes/Main.unity`
   - Bootstrap component auto-configures simulation
   - Assign `fluidComputeShader` reference in Bootstrap inspector

3. **Play Mode:**
   - Press Play - should see fluid simulation output
   - Left-click to inject forces
   - Performance stats visible in editor (OnGUI overlay)

### Testing the Fluid Simulation

**Visual verification:**
- Simulation output displayed via RawImage on Canvas
- Move mouse with left button pressed to see fluid response
- Check console for "[SimDriver] Initialized" message

**Performance monitoring:**
- SimDriver shows frame timings in editor GUI
- Target: <2-3ms on dev PC, <5ms on mobile
- Adjust `pressureIterations` if too slow

### Capturing Training Data

```csharp
// Via SimulationRecorder component
recorder.BeginScenario("test01");
recorder.StartCapture();  // Manual start
// ... let simulation run ...
recorder.StopCapture();   // Manual stop
```

**Output structure:**
```
Captures/
  scenario_frame_0000_hires.png          (512x512)
  scenario_frame_0000_lores_physics.png  (256x256)
  scenario_frame_0000.meta.json          (parameters)
```

### Common Tasks

**Modify fluid parameters:**
- Select SimDriver GameObject in scene
- Adjust Inspector values: viscosity, vorticity, dissipation
- Values apply immediately in Play Mode

**Add new compute kernels:**
1. Add kernel to `Fluids.compute`
2. Add kernel index in SimDriver: `private int kernelMyNewKernel;`
3. Find kernel in `InitializeSimulation()`: `kernelMyNewKernel = fluidCompute.FindKernel("MyNewKernel");`
4. Dispatch in `SimulateFrame()` or `Update()`

**Create new stylization shader:**
- Add compute shader to `Assets/_Project/Runtime/Systems/Foveation/`
- Reference via Inspector (no Resources.Load)
- Dispatch after simulation, before composition

## Architecture Patterns

### Service Pattern (Planned)

Future architecture will use service-based design with:
- `IFluidSimulationService` interface
- ReactiveProperty for observable state
- Result<T> pattern for error handling
- ServiceLocator for dependency resolution

**Current:** MonoBehaviour-based with direct references (transitional state).

### Ping-Pong Buffer Pattern

From MagiUnityTools package:
```csharp
var velocity = new PingPongRenderTexture(resolution, resolution,
    RenderTextureFormat.RGHalf, "Velocity");

// After compute dispatch:
velocity.Swap();  // Swap read/write buffers

// Access current state:
RenderTexture current = velocity.Read;
```

### Performance Budgets

**Mobile targets (iPhone 12, Pixel 6):**
- Simulation: ≤ 5ms
- ML Inference: ≤ 4ms
- Compositing: ≤ 2ms
- Total: < 16.67ms (60 FPS)

**Quality tiers:**
- Tier 0: Shader-only, 256x256, no ML
- Tier 1: ML center tile, shader periphery, 384x384
- Tier 2: Full ML chain, 512x512

## Build Configuration

**Build targets:**
- iOS: Minimum version determined by Unity 6.0 requirements
- Android: API Level 24+ (Android 7.0)

**Render pipeline:**
- HDRP 17.2.0 configured
- Mobile-optimized settings required for deployment

## Debugging

**Common issues:**

1. **"No compute shader assigned"**
   - Assign Fluids.compute to Bootstrap.fluidComputeShader in Inspector
   - Check package path in Packages/com.inktools.sim

2. **"Kernel not found"**
   - Verify all kernels present in Fluids.compute
   - Check kernel names match exactly (case-sensitive)

3. **Input not working**
   - Verify `com.unity.inputsystem` in packages
   - Check `Magi.Inkling.Runtime.asmdef` references Unity.InputSystem
   - Ensure `Mouse.current` null check before use

4. **Black output / no fluid visible**
   - Check if initial density injected in `InitializeSimulation()`
   - Verify `UpdateDisplay()` blits to displayRT
   - Check RawImage.texture reference

**Performance profiling:**
- Use built-in OnGUI overlay in SimDriver (editor only)
- Detailed timings via `GetDetailedTimings()` method
- Unity Profiler: "Inkling." markers for compute dispatches

## Related Documentation

- `FLUID_SIMULATION_IMPROVEMENTS.md` - Architecture analysis and roadmap
- `IMPLEMENTATION_PLAN.md` - Detailed phase-by-phase plan
- `README.md` - High-level project overview
- `depfile.yaml` - Package dependencies and policies

## Quick Reference: File Paths

```
Inkling/                                    # Unity project root
├── Assets/
│   └── _Project/
│       ├── Runtime/
│       │   ├── Dev/
│       │   │   ├── Bootstrap.cs           # Scene bootstrap
│       │   │   └── TestPatternGenerator.cs
│       │   ├── Systems/
│       │   │   ├── SimulationLOD0/
│       │   │   │   ├── SimDriver.cs       # Main fluid sim
│       │   │   │   ├── SimulationRecorder.cs
│       │   │   │   ├── CaptureDriver.cs
│       │   │   │   └── MultiResolutionDriver.cs
│       │   │   ├── Foveation/
│       │   │   │   ├── BaselineStylizer.compute
│       │   │   │   └── FoveatedComposer.cs
│       │   │   └── Rendering/
│       │   │       └── InkRenderPipeline.cs
│       │   └── Magi.Inkling.Runtime.asmdef
│       ├── Scenes/
│       │   └── Main.unity
│       └── README_Assemblies.md
├── Packages/
│   └── manifest.json
├── ProjectSettings/
└── depfile.yaml                           # Dependency config
```

## Notes for AI Assistants

- Always work from the **nested** Unity project directory: `Inkling/Inkling/`
- Never use `Resources.Load` or `FindObjectOfType` - these are banned APIs
- Use direct Inspector references for assets (compute shaders, textures)
- New Input System only - no legacy Input class
- PingPongRenderTexture from MagiUnityTools for double-buffering
- Check for null when using Mouse.current (Input System)
- Performance-critical: measure all operations, target mobile budgets
- Git branch: `FirstPass` (working branch), merge target: `main`