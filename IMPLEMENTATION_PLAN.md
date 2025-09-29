# Inkling Implementation Plan - Phase 1

## Overview
Implementation of the Generative Art Pipeline using Unity 6.2 with AI-assisted scene generation.

## Phase 1: The Generative Art Pipeline

### Step 1: Unity Project Setup & Scene Creation

#### 1.1 Project Initialization
```bash
# In Inkling/Inkling/ directory (nested Unity project)
# Open with Unity Hub - Unity 6.2
```

#### 1.2 Unity AI Assistant Prompts for Scene Setup

##### Main Simulation Scene
```
Create a 2D scene optimized for mobile with these components:
- Main camera with orthographic projection
- Canvas for UI overlay with performance stats display
- Empty GameObject named "SimulationManager" at origin
- RenderTexture assets for simulation buffers (512x512, RGBAFloat)
- Post-processing volume for visual effects
```

##### Test Environment Scene
```
Create a test environment scene for fluid simulation with:
- Grid of spawn points for ink particles (16x16 grid)
- Debug visualization plane for render texture display
- UI panel with simulation parameter sliders (viscosity, diffusion, vorticity)
- Multiple camera angles including top-down orthographic view
- Lighting setup for 2D sprites with ambient and directional lights
```

##### Data Capture Scene
```
Create a data capture scene for ML training with:
- Fixed orthographic camera at position (0, 10, 0) looking down
- Black background with no post-processing
- Render texture capture setup at 512x512 and 256x256 resolutions
- UI controls for starting/stopping capture sequences
- File browser for export path selection
```

### Step 2: Implement LOD 0 Fluid Simulation

#### 2.1 Create Compute Shaders

Create `Inkling/Assets/Inkling/Shaders/Simulation/FluidSimulation.compute`:

```hlsl
#pragma kernel Advection
#pragma kernel Diffusion
#pragma kernel PressureIteration
#pragma kernel DivergenceFree
#pragma kernel AddForces

// Shared includes
#include "SimulationCommon.hlsl"

// Buffers
RWTexture2D<float4> VelocityField;
RWTexture2D<float4> DensityField;
RWTexture2D<float> PressureField;
RWTexture2D<float> DivergenceField;
Texture2D<float4> VelocityFieldPrev;
Texture2D<float4> DensityFieldPrev;

// Parameters
float DeltaTime;
float Viscosity;
float Diffusion;
float VorticityStrength;
uint2 SimulationSize;

[numthreads(8, 8, 1)]
void Advection(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= SimulationSize)) return;

    float2 uv = (id.xy + 0.5) / SimulationSize;
    float2 velocity = VelocityFieldPrev[id.xy].xy;
    float2 pos = uv - velocity * DeltaTime;

    // Bilinear sampling for advection
    float4 advected = SampleBilinear(DensityFieldPrev, pos * SimulationSize);
    DensityField[id.xy] = advected;
}
```

#### 2.2 Unity AI Assistant Prompt for Simulation Manager

```
Create a C# MonoBehaviour script called "FluidSimulationManager" that:
- Manages compute shader dispatch for fluid simulation
- Creates and manages RenderTextures for velocity, pressure, and density fields
- Implements double buffering for simulation stability
- Provides public methods for adding forces and ink to the simulation
- Uses Unity's Job System for CPU-side calculations
- Includes performance timing with Profiler markers
- Supports multiple ink types with different material properties
```

### Step 3: Data Capture System

#### 3.1 Create Capture Infrastructure

Create `Inkling/Assets/Inkling/Editor/DatasetCapture/SimulationRecorder.cs`:

```csharp
using UnityEngine;
using UnityEditor;
using System.IO;
using Unity.Collections;
using Unity.Jobs;

namespace Inkling.Editor.DataCapture
{
    public class SimulationRecorder : EditorWindow
    {
        private RenderTexture highResCapture; // 512x512
        private RenderTexture lowResPhysics;  // 256x256
        private string outputPath;
        private int frameCounter;
        private bool isRecording;

        [MenuItem("Inkling/Data Capture/Simulation Recorder")]
        public static void ShowWindow()
        {
            GetWindow<SimulationRecorder>("Simulation Recorder");
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Dataset Capture Settings", EditorStyles.boldLabel);

            outputPath = EditorGUILayout.TextField("Output Path:", outputPath);

            if (GUILayout.Button(isRecording ? "Stop Recording" : "Start Recording"))
            {
                if (isRecording) StopRecording();
                else StartRecording();
            }

            if (isRecording)
            {
                EditorGUILayout.LabelField($"Recording Frame: {frameCounter}");
            }
        }

        private void CaptureFrame(string scenarioName)
        {
            // High-res capture
            string hiresPath = Path.Combine(outputPath,
                $"{scenarioName}_frame_{frameCounter:D4}_hires.png");
            SaveRenderTexture(highResCapture, hiresPath);

            // Low-res physics target
            string loresPath = Path.Combine(outputPath,
                $"{scenarioName}_frame_{frameCounter:D4}_lores_physics.png");
            SaveRenderTexture(lowResPhysics, loresPath);

            frameCounter++;
        }
    }
}
```

#### 3.2 Unity AI Assistant Prompt for Capture Tools

```
Create an editor window for dataset capture that includes:
- Scenario selection dropdown (fire, water, ice, electricity effects)
- Batch capture mode for multiple scenarios
- Real-time preview of captured frames
- Metadata export (JSON) with simulation parameters per frame
- Integration with Timeline for scripted capture sequences
- Automatic file naming with versioning
- Progress bar for long capture sessions
```

### Step 4: Baseline Shader Stylization (Non-ML)

#### 4.1 Create Stylization Shaders

Create `Inkling/Assets/Inkling/Shaders/Stylization/BaselineStylizer.shader`:

```hlsl
Shader "Inkling/BaselineStylizer"
{
    Properties
    {
        _MainTex ("Simulation Texture", 2D) = "white" {}
        _ColorRamp ("Color Ramp", 2D) = "white" {}
        _StyleIntensity ("Style Intensity", Range(0, 1)) = 0.5
        _EdgeThreshold ("Edge Threshold", Range(0, 1)) = 0.1
    }

    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_ColorRamp);
            SAMPLER(sampler_ColorRamp);

            float _StyleIntensity;
            float _EdgeThreshold;

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                float4 sim = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);

                // Sobel edge detection
                float edges = SobelEdgeDetection(input.uv, _MainTex, sampler_MainTex);

                // Artistic color mapping
                float intensity = length(sim.rgb);
                float4 stylized = SAMPLE_TEXTURE2D(_ColorRamp, sampler_ColorRamp, float2(intensity, 0.5));

                // Blend based on edges
                float4 result = lerp(stylized, float4(0, 0, 0, 1), edges * _EdgeThreshold);
                result = lerp(sim, result, _StyleIntensity);

                return result;
            }
            ENDHLSL
        }
    }
}
```

#### 4.2 Unity AI Assistant Prompt for Shader Variants

```
Create a shader with multiple stylization techniques:
- Watercolor effect with color bleeding simulation
- Ink wash with gradient accumulation
- Cel shading with configurable bands
- Impressionist brush strokes using noise
- Japanese ink painting (sumi-e) style
Include shader variants for mobile optimization and quality tiers
```

### Step 5: ML Inference Pipeline with Sentis

#### 5.1 Create Inference Manager

Create `Inkling/Assets/Inkling/Runtime/Systems/Inference/MLInferenceManager.cs`:

```csharp
using Unity.Sentis;
using UnityEngine;
using System.Collections.Generic;

namespace Inkling.Runtime.Inference
{
    public class MLInferenceManager : MonoBehaviour
    {
        [System.Serializable]
        public class LODModel
        {
            public int sourceLOD;
            public int targetLOD;
            public ModelAsset modelAsset;
            public float inferenceTimeMs;
        }

        [SerializeField] private List<LODModel> lodModels;
        private Dictionary<int, IWorker> workers;
        private RenderTexture inferenceInput;
        private RenderTexture inferenceOutput;

        void Start()
        {
            InitializeWorkers();
        }

        private void InitializeWorkers()
        {
            workers = new Dictionary<int, IWorker>();

            foreach (var lodModel in lodModels)
            {
                var runtimeModel = ModelLoader.Load(lodModel.modelAsset);
                var worker = WorkerFactory.CreateWorker(
                    WorkerFactory.Type.GPUCompute,
                    runtimeModel
                );
                workers[lodModel.sourceLOD] = worker;
            }
        }

        public RenderTexture ProcessLODTransition(RenderTexture input, int fromLOD, int toLOD)
        {
            using (var marker = new ProfilerMarker($"ML Inference LOD{fromLOD}→{toLOD}"))
            {
                marker.Begin();

                var worker = workers[fromLOD];
                var inputTensor = TextureConverter.ToTensor(input);

                worker.Execute(inputTensor);
                var outputTensor = worker.PeekOutput();

                inferenceOutput = TextureConverter.ToRenderTexture(outputTensor);

                marker.End();
                return inferenceOutput;
            }
        }
    }
}
```

#### 5.2 Unity AI Assistant Prompt for ML Integration

```
Create a complete ML inference system that:
- Loads multiple ONNX models for different LOD transitions
- Implements tensor caching to reduce allocations
- Provides fallback to baseline stylization if inference fails
- Monitors inference time and automatically downgrades quality if too slow
- Supports async inference with callback system
- Includes warmup routine to prevent first-frame stutters
- Implements model hot-swapping for A/B testing
```

### Step 6: Performance Monitoring

#### 6.1 Create Performance Monitor

```csharp
namespace Inkling.Runtime.Diagnostics
{
    public class PerformanceMonitor : MonoBehaviour
    {
        public struct FrameMetrics
        {
            public float simulationMs;
            public float inferenceMs;
            public float compositingMs;
            public float totalFrameMs;
            public int qualityTier;
        }

        // Target budgets (milliseconds)
        private const float SIMULATION_BUDGET = 5.0f;  // 4-6ms target
        private const float INFERENCE_BUDGET = 4.0f;   // 3-5ms target
        private const float COMPOSITING_BUDGET = 2.0f; // 2ms target
    }
}
```

#### 6.2 Unity AI Assistant Prompt for Debug Overlay

```
Create a debug overlay UI that displays:
- Real-time frame timing breakdown (simulation, inference, compositing)
- Memory usage (GPU and CPU)
- Current quality tier and automatic adjustments
- Heatmap overlay showing computation density
- Seam visualization for foveated rendering
- Graph showing performance over last 60 frames
- Export button for performance reports
Make it mobile-friendly with minimal performance impact
```

### Step 7: Test Scenes and Validation

#### 7.1 Unity AI Assistant Prompts for Test Scenarios

##### Fire Effect Test
```
Create a test scene for fire simulation with:
- Particle emitter at bottom center emitting upward
- Temperature gradient visualization
- Vorticity controls for flame turbulence
- Color temperature mapping from blue to white to orange
- Smoke density accumulation at top
```

##### Water Effect Test
```
Create a test scene for water simulation with:
- Container boundaries with collision
- Surface tension visualization
- Wave propagation from touch/click points
- Viscosity adjustment for different liquids
- Color depth based on density
```

##### Multi-Material Interaction Test
```
Create a test scene demonstrating material interactions:
- Fire meeting water creates steam
- Ice melting into water near fire
- Electricity conducting through metal
- Plant growth in water
- Dust settling and accumulating
```

## Implementation Checklist

### Week 1: Foundation
- [ ] Set up Unity 6.2 project structure
- [ ] Create base scenes with AI assistant
- [ ] Implement basic fluid simulation compute shaders
- [ ] Test simulation at various resolutions

### Week 2: Data Pipeline
- [ ] Complete simulation recorder tool
- [ ] Capture initial dataset (100+ frames per effect)
- [ ] Create baseline stylization shaders
- [ ] Implement A/B comparison viewer

### Week 3: ML Integration
- [ ] Train first U-Net model (Python side)
- [ ] Export to ONNX and test in Unity
- [ ] Implement Sentis inference pipeline
- [ ] Compare ML vs baseline performance

### Week 4: Optimization & Polish
- [ ] Profile and optimize performance
- [ ] Implement quality tier system
- [ ] Add foveated rendering (if needed)
- [ ] Create demo scenes showcasing the pipeline

## Performance Validation Criteria

### Mobile Targets (iPhone 12, Pixel 6)
- Total frame budget: < 16.67ms (60 FPS)
- Simulation: ≤ 5ms
- ML Inference: ≤ 4ms
- Compositing: ≤ 2ms
- Remaining: 5.67ms for game logic

### Quality Tiers
1. **Tier 0 (Fallback)**: Shader-only, 256x256, no ML
2. **Tier 1 (Standard)**: ML center tile, shader periphery, 384x384
3. **Tier 2 (Premium)**: Full ML chain, 512x512

## Next Actions

1. Open Unity 6.2 and create the project structure
2. Use AI assistant with provided prompts to generate initial scenes
3. Copy compute shader templates from this plan
4. Begin implementing FluidSimulationManager
5. Set up performance monitoring from day one

## Integration Points

- **InkTools**: Reference implementation for fluid dynamics
- **InkModel**: Training pipeline for stylization models
- **MagiUnityTools**: Use singleton patterns and performance utilities
- **MagiUnityDependencyManager**: Configure dependencies via depfile.yaml