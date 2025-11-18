# Unity 6.2 Scene Setup with AI Assistant

## Phase 1 Implementation Status

### �o. Implemented Components (Code)

1. **SimDriver.cs** - Fluid simulation driver with RT allocation and kernel dispatch (Assets/_Project/Scripts/Systems/SimulationLOD0/)
2. **SimulationRecorder.cs** - Runtime capture with JSON metadata export (Assets/_Project/Scripts/Systems/SimulationLOD0/)
3. **BaselineStylizer.shader** / **BaselineStylizerCompute.cs** - Self-contained non-ML fallback stylization (Assets/_Project/Scripts/Systems/Foveation/)
4. **BlendSeam.shader** - Foveation seam blending with feather mask (Assets/_Project/Scripts/Systems/Foveation/Shaders/)
5. **FoveatedComposer.cs** - Composite with proper seam blending (Assets/_Project/Scripts/Systems/Foveation/)  
   _Note: The component in `Main.unity` still references an old assembly name and must be re-attached to use the current class._
6. **Fluids.compute** - GPU fluid solver (InkTools/InkTools/Assets/_Project/Scripts/Simulation/Compute/)

### Planned / Not Yet Implemented

- **SentisRunner.cs** - TextAsset `.onnx.bytes` support with Load/Run implementation (Assets/_Project/Scripts/Systems/Inference/)  
  _The ONNX model asset exists (`Assets/_Project/Models/unet_fp32.onnx.bytes`), but there is no runtime inference script yet._

### dYZr Unity AI Assistant Prompts for Scene Setup

Use these prompts with Unity 6.2's built-in AI assistant to generate the necessary GameObjects and UI:

## 1. Main Simulation Scene

### Quick Setup (Recommended)
```
1. Create an empty GameObject named "SimulationBootstrap"
2. Add the Bootstrap component to it
3. Press Play - Bootstrap automatically sets up:
   - Render textures (hi-res and lo-res)
   - Simulation driver (SimDriver) if not already present
   - Simulation recorder
   - UI display with RawImage
   - Capture controls (press Space to capture)
```

### Manual Setup (Advanced)
```
Create a mobile-optimized scene for fluid simulation with these GameObjects:
- Main Camera: Orthographic, position (0, 10, 0), rotation (90, 0, 0), size 5
- SimulationDriver: Empty GameObject with SimDriver component
  * Connecting Fluids.compute:
    1. Select the GameObject with SimDriver component
    2. In the Inspector, find the "Fluid Compute" field under "Compute Shader" header
    3. Click the circle selector button next to the field
    4. In the asset picker, navigate to: Packages > InkTools Simulation > Runtime > Compute
    5. Select "Fluids.compute"
    - Note: The package should appear automatically after Unity imports packages
    - If Fluids.compute is missing: Reimport All or restart Unity to refresh packages
    - Alternative: Use TestPatternGenerator component for testing without compute shaders
  * SimDriver will automatically create render textures at the specified resolution
- TestPattern: GameObject with TestPatternGenerator component (for testing)
- DataCapture: GameObject with SimulationRecorder component
  * Assign hi-res and lo-res render textures
  * Set output folder path
- Display Quad: 10x10 quad at origin to display simulation render texture
- UI Canvas: Screen space overlay with RawImage component to display RT
```

### Troubleshooting

**Fluids.compute not appearing in asset picker:**
1. Check Package Manager (Window > Package Manager)
   - Look for "InkTools Simulation" in "In Project" view
   - If missing, check Packages/manifest.json has: `"com.inktools.sim": "file:../../InkTools/InkTools/Assets/_Project/Scripts/Simulation"`
2. Right-click in Project window > Reimport All
3. Restart Unity Editor to force package refresh

**SimDriver errors on Play:**
- "Compute shader not assigned": Assign Fluids.compute in Inspector
- "Kernel not found": Verify Fluids.compute has all required kernels (Advection, Divergence, Pressure, etc.)
- "Invalid dispatch": Check resolution is power of 2 and �% 2048

**Alternative without compute shaders:**
- Use TestPatternGenerator for visual testing
- Bootstrap component includes TestPatternGenerator automatically
```

## 2. ML Inference Test Scene (Planned)

```
Create a test scene for ML inference with:
- Camera: Orthographic at (0, 5, 0) looking down
- InferenceManager: GameObject with `SentisRunner` component (to be implemented)
- Input Display: Quad at (-3, 0, 0) showing input texture
- Output Display: Quad at (3, 0, 0) showing inference result
- UI Panel: Debug info showing inference time in milliseconds
```

## 3. Baseline Stylizer Demo

```
Create a stylization comparison scene with:
- Split-screen camera setup (two cameras, each rendering to half screen)
- Left Display: Raw simulation texture on quad
- Right Display: Stylized result using BaselineStylizer shader
- UI Controls: Sliders for StyleIntensity, EdgeThreshold, WatercolorBleed
- Material: Create material using Inkling/BaselineStylizer shader
```

## 4. Performance Monitoring UI (Planned)

```
Create a performance overlay UI prefab with:
- Canvas: Screen space overlay, sort order 999
- Background Panel: Semi-transparent black (alpha 0.7), anchored to top-left
- Text Elements:
  - FPS: "FPS: {value}"
  - Simulation: "Sim: {value}ms"
  - Inference: "ML: {value}ms"
  - Memory: "Mem: {value}MB"
- Graph: Line graph showing last 60 frames performance
- Toggle Button: Show/hide overlay
```

## 5. Data Capture Controls (Planned)

```
Create a capture control panel with:
- Dropdown: Scenario selection (Fire, Water, Ice, Lightning)
  * Add ScenarioDropdownHelper component to auto-generate colored sprites
  * Or use ElementSpriteGenerator for more detailed sprites
- Input Field: Frame count (default 100)
- Start/Stop Button: Toggle capture state
- Progress Bar: Show capture progress
- Status Text: Current frame number
- Export Path: Display output directory

Setting up Dropdown Sprites:
1. Add ScenarioDropdownHelper component to Dropdown GameObject
2. It will auto-generate colored square sprites for each scenario
3. Or for custom sprites:
   - Add ElementSpriteGenerator to any GameObject
   - Access sprites via ElementSpriteGenerator.ElementSprites["Fire"] etc.
   - Assign to dropdown options manually if needed
```

## 6. Manual Editor Checklist (Summary)

For human collaborators wiring the scene by hand:

- **Open Project**
  - Open `Inkling/Inkling` in Unity 6.2 (`6000.2.5f1`).
  - Run `magi-deps.ps1 apply -ProjectPath ./Inkling -Strict` from the repo root once, then let Unity import packages.

- **Main Scene (`Assets/_Project/Scenes/Main.unity`)**
  - Ensure a `Bootstrap` GameObject with `Magi.Inkling.Dev.Bootstrap` exists.
    - (Optional) Assign `fluidComputeShader` if you don’t want Bootstrap to auto-load `Fluids.compute`.
  - Ensure a GameObject with `Magi.Inkling.Systems.SimulationLOD0.SimDriver`:
    - Assign `fluidCompute` to the `Fluids.compute` asset from `com.inktools.sim`.
    - Assign a `Renderer` (quad) to `displayRenderer`.
    - Optionally assign an `InkGradientPreset` asset and a material using `Inkling/InkGradientRenderer`.
  - Confirm that when you hit Play, SimDriver shows timing stats in the Game view (`OnGUI`) and animates ink.

- **Recorder & Capture**
  - If using `Bootstrap`, it auto-creates a `Recorder` GameObject with `SimulationRecorder` + `CaptureDriver`.
  - If placing `SimulationRecorder` manually:
    - Assign `hiRes` and `loResPhysics` RenderTextures.
    - Set `outputFolder` to a writable path (relative to project).
  - Press Space in Play mode to capture frames via `CaptureDriver`.

- **Foveation / Multi-Resolution**
  - Fix any missing-script references that were previously `FoveatedComposer` to `Magi.Inkling.Systems.Foveation.FoveatedComposer`.
  - If you add `MultiResolutionDriver`:
    - Set its `simDriver` reference to the existing `SimDriver`.
    - Assign a `Renderer` to `displayRenderer` if you want a separate high-res output.

- **Dev Overlay (when you hook it up)**
  - Add a GameObject with `Magi.Inkling.Dev.DevOverlay`.
  - Add a small script (or extend `SimDriver`) to push `simMs`, `inferMs`, `composeMs` into the overlay each frame.

- **ML Inference (once Sentis is added)**
  - Install Unity Sentis via Package Manager.
  - Create a new scene with:
    - Camera + input/output quads.
    - A `SentisRunner` component (to be implemented) that:
      - References `Assets/_Project/Models/unet_fp32.onnx.bytes`.
      - Has input RenderTexture (from SimDriver) and output RenderTexture fields.
  - Wire `SentisRunner`’s output into the same gradient/ink rendering path used by the baseline stylizer.

## Next Steps in Unity

### 1. Package Installation
Run dependency management script:
```bash
magi-deps.ps1 apply -ProjectPath ./Inkling -Strict
```

This will configure the correct versions from depfile.yaml for Unity 6.x:
- Unity.Sentis 2.x
- Unity.RenderPipelines.Universal 17.x
- Unity.Burst 1.8.x
```

### 2. Create Assembly Definitions

Create asmdefs for proper code organization:

**Assets/_Project/Scripts/Magi.Inkling.asmdef**
```json
{
  "name": "Magi.Inkling",
  "rootNamespace": "Magi.Inkling",
  "references": [
    "Unity.Mathematics",
    "Unity.Burst",
    "Unity.InputSystem",
    "Unity.Sentis",
    "Magi.UnityTools",
    "Magi.InkTools"
  ],
  "includePlatforms": [],
  "excludePlatforms": []
}
```


### 3. Render Texture Setup

Create render textures for simulation:
- **HiRes**: 512x512, RGBAHalf, Enable Random Write
- **LoRes**: 256x256, RGBAHalf, Enable Random Write
- **Display**: 512x512, ARGB32, for visualization

### 4. Material Setup

1. Create material: `BaselineStyleMaterial`
2. Set shader: `Inkling/BaselineStylizer`
3. Assign color ramp texture (gradient from blue �+' orange �+' white for fire)
4. Assign paper texture for watercolor effect

## Performance Validation

### Target Metrics (Mobile)
- **Simulation**: �% 5ms
- **ML Inference**: �% 4ms (when implemented)
- **Baseline Stylizer**: �% 2ms
- **Total Frame**: < 16.67ms (60 FPS)

### Test on Device
1. Build to mobile device (iOS/Android)
2. Enable performance overlay
3. Run different scenarios
4. Verify performance targets are met
5. Use baseline stylizer if ML inference exceeds budget

## Integration with InkTools

Once the minimal LOD0 simulation is ready in `com.inktools.sim`:

1. Replace test pattern with actual fluid simulation
2. Link SimulationRecorder to capture real sim data
3. Export datasets for ML training
4. Train U-Net models using InkModel pipeline
5. Import .onnx.bytes models back to Unity
6. Enable ML inference path alongside baseline

## Quality Gates

- �o. Color space metadata in captures
- �o. ONNX opset 13 compatibility
- �o. RGBAHalf for mobile memory optimization
- �o. Self-contained shaders (no missing helpers)
- �o. Runtime capture (not editor-only)
- [ ] Performance metrics in DevOverlay (currently only `SimDriver.OnGUI` shows timings)

## Current Status

The core infrastructure for fluid simulation, baseline stylization, and runtime capture is in place and wired into `Main.unity`.  
Use Unity's AI assistant with the prompts above to quickly generate any missing GameObjects and UI (performance overlay, capture controls, ML inference scene).  
Short term, focus on:

1. Using the baseline stylizer as the non-ML fallback.
2. Adding a simple inference scene and `SentisRunner` once models are ready.
3. Fixing the `FoveatedComposer` component reference in `Main.unity` and validating foveated blending on top of the existing pipeline.
