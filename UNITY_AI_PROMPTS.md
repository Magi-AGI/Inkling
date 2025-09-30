# Unity 6.2 AI Assistant Prompts for Inkling

## Scene Generation Prompts

### 1. Main Game Scene
```
Create a mobile-optimized 2D game scene with:
- Orthographic camera setup for 16:9 and 19.5:9 aspect ratios
- Safe area UI layout for notched devices
- Touch input manager with multitouch support
- Particle system for ambient ink effects
- Background layers with parallax scrolling
- Performance stats overlay (FPS, memory, draw calls)
- Audio manager with 3D spatial sound zones
```

### 2. Fluid Simulation Test Lab
```
Create a fluid simulation testing environment with:
- 512x512 simulation grid visualizer
- Debug quad displaying render textures in real-time
- UI panel with sliders for:
  - Viscosity (0.0001 to 0.01)
  - Diffusion (0.00001 to 0.001)
  - Vorticity (0 to 10)
  - Time scale (0.1 to 2.0)
- Spawn tools for different ink types (fire, water, metal, plant)
- Recording controls for capturing simulation sequences
- Performance profiler graph showing ms per simulation step
```

### 3. ML Training Data Capture Studio
```
Create a data capture scene for machine learning with:
- Fixed orthographic camera at (0, 5, 0) rotation (90, 0, 0)
- Pure black background, no skybox
- Dual render texture setup:
  - High-res: 512x512 RGBAFloat
  - Low-res: 256x256 RGBAFloat
- Scenario dropdown: [Fireball, Waterfall, Lightning, Ice Formation, Steam Cloud]
- Batch capture system with frame counter
- Side-by-side preview of high-res and low-res outputs
- JSON metadata export with simulation parameters
```

### 4. Foveated Rendering Demo
```
Create a foveated rendering demonstration scene with:
- Central high-detail zone (256x256)
- Peripheral low-detail zones (128x128 tiles)
- Seamless blending between zones
- Debug overlay showing tile boundaries
- Mouse/touch controlled focus point
- Performance comparison toggle (foveated vs full-res)
- Shader-based edge feathering system
```

### 5. Artistic Style Gallery
```
Create a style comparison gallery scene with:
- Grid layout showing same simulation with different styles:
  - Photorealistic
  - Watercolor
  - Cel-shaded
  - Impressionist
  - Japanese ink (sumi-e)
  - Abstract geometric
- Split-screen comparison mode
- Style blending slider
- Export functionality for style samples
```

## Component Generation Prompts

### Fluid Simulation Manager
```
Create a FluidSimulationManager component that:
- Manages GPU compute shaders for fluid dynamics
- Implements stable fluid solver (Navier-Stokes)
- Handles multiple fluid types with different properties:
  - Fire: Low viscosity, high turbulence, upward buoyancy
  - Water: Medium viscosity, surface tension, gravity
  - Lava: High viscosity, slow movement, heat emission
  - Electricity: No viscosity, branching patterns, instant propagation
- Double-buffers simulation textures for stability
- Provides API for adding forces and ink:
  - AddVelocity(Vector2 position, Vector2 force, float radius)
  - AddDensity(Vector2 position, float amount, InkType type)
  - AddVorticity(Vector2 position, float strength)
- Uses Unity Job System for parallel processing
- Includes LOD system for distance-based quality
```

### ML Inference Controller
```
Create an MLInferenceController that:
- Loads ONNX models using Unity Sentis
- Manages model lifecycle (load, warmup, execute, unload)
- Implements inference queue with priority system
- Provides multiple backends:
  - GPU Compute (fastest)
  - GPU Pixel (compatibility)
  - CPU (fallback)
- Caches tensor allocations to reduce GC
- Monitors inference time and auto-adjusts quality
- Supports model hot-swapping without stutters
- Includes async inference with callbacks
- Implements temporal stability (frame-to-frame coherence)
```

### Performance Tier Manager
```
Create a PerformanceTierManager that:
- Detects device capabilities at startup
- Monitors frame time and thermal state
- Automatically adjusts quality settings:
  - Tier 0: 256x256 sim, shader stylization, 30 FPS target
  - Tier 1: 384x384 sim, ML center only, 30 FPS target
  - Tier 2: 512x512 sim, full ML, 60 FPS target
- Provides manual override options
- Saves tier preferences per device
- Implements gradual tier transitions (no sudden jumps)
- Includes thermal throttling detection
```

### Simulation Recorder
```
Create a SimulationRecorder editor tool that:
- Records simulation frames to PNG sequences
- Captures at multiple resolutions simultaneously
- Exports with naming convention:
  - {scenario}_{frame:0000}_{resolution}.png
- Includes metadata export:
  - Frame number
  - Simulation parameters
  - Timestamp
  - Random seed
- Supports batch recording of multiple scenarios
- Implements frame skipping for consistent capture rate
- Provides real-time preview during recording
- Includes progress bar and time estimation
```

### Dataset Preparation Tool
```
Create a DatasetPreparationTool that:
- Pairs simulation captures with artist targets
- Validates data integrity (matching frame counts)
- Generates train/validation/test splits
- Creates augmented versions:
  - Random rotation (±10°)
  - Color jitter (±10%)
  - Horizontal/vertical flips
- Exports to PyTorch-compatible format
- Generates statistics report:
  - Total frames
  - Distribution per scenario
  - Missing artist targets
- Includes data visualization previews
```

## Shader Generation Prompts

### Compute Shader for Fluid Simulation
```
Create a compute shader for 2D fluid simulation with:
- Kernels for:
  - Advection (semi-Lagrangian)
  - Diffusion (Jacobi iteration)
  - Pressure projection (Poisson solver)
  - Vorticity confinement
  - External forces
- Support for multiple ink types in single simulation
- Boundary conditions (walls, wrap, open)
- Double-buffered textures for stability
- Optimized thread group sizes (8x8)
- Shared memory usage for performance
```

### Stylization Shader Library
```
Create a shader library for artistic stylization with:
- Watercolor effect:
  - Edge darkening
  - Color bleeding
  - Paper texture overlay
- Cel shading:
  - Configurable color bands
  - Outline detection
  - Flat color regions
- Oil painting:
  - Brush stroke simulation
  - Impasto effect
  - Color mixing
- All shaders should:
  - Support mobile GPUs
  - Include quality variants
  - Use single-pass rendering
```

### Foveation Compositing Shader
```
Create a foveation compositing shader that:
- Blends central high-detail with peripheral low-detail
- Uses smooth falloff functions (Gaussian, sigmoid)
- Maintains color consistency across boundaries
- Supports dynamic focus point
- Includes debug visualization mode
- Optimized for mobile tile-based rendering
```

## UI Generation Prompts

### Performance Overlay HUD
```
Create a mobile-friendly performance HUD that shows:
- FPS counter with rolling average
- Frame time breakdown:
  - Simulation: [■■■□□] 3.2ms
  - ML Inference: [■■□□□] 2.1ms
  - Rendering: [■□□□□] 1.5ms
- Memory usage bar
- Current quality tier indicator
- Thermal state warning icon
- Minimizable to corner widget
- Semi-transparent with adjustable opacity
```

### Simulation Control Panel
```
Create a debug control panel with:
- Tabbed interface:
  - Physics (viscosity, diffusion, vorticity)
  - Rendering (quality tier, stylization)
  - ML (model selection, inference backend)
  - Recording (capture settings, export path)
- Preset dropdown for common scenarios
- Save/load configuration buttons
- Reset to defaults option
- Collapsible sections for organization
```

## Testing Scene Prompts

### Automated Performance Test
```
Create an automated performance test scene that:
- Runs through predetermined scenarios
- Measures frame times for each scenario
- Tests all quality tiers
- Logs results to CSV file
- Generates performance report with graphs
- Identifies performance bottlenecks
- Compares against target budgets
- Runs without user interaction
```

### Visual Regression Test
```
Create a visual regression test scene that:
- Loads reference images
- Runs simulation with fixed seed
- Captures output frames
- Compares using SSIM metric
- Highlights differences visually
- Generates pass/fail report
- Supports baseline updates
- Integrates with CI/CD pipeline
```

## Best Practices for Unity 6.2 AI Assistant

1. **Always specify**:
   - Target platform (mobile iOS/Android)
   - Performance constraints
   - Required Unity packages (URP, Sentis, etc.)

2. **Include in prompts**:
   - Specific component names
   - Public API methods needed
   - Performance requirements
   - Mobile optimization needs

3. **Request**:
   - Profiler marker integration
   - Memory pool usage
   - Async/await patterns where appropriate
   - DOTS/Job System for heavy computation

4. **Avoid requesting**:
   - Resources.Load (use Addressables)
   - GameObject.Find (use references)
   - SendMessage (use events/interfaces)
   - Synchronous I/O operations