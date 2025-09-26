# Inkling

A Unity-based mobile game featuring ink-based life simulation with hierarchical rendering and machine learning stylization.

## Overview

Inkling is a revolutionary mobile game where players interact with a living world of ink-based organisms. The game features advanced fluid simulation, cellular automata, and machine learning-driven stylization to create emergent gameplay experiences.

## Technical Architecture

- **Hierarchical Simulation Layers (HSL)**: Multi-scale simulation from molecular to organism level
- **ML Stylization Pipeline**: Real-time inference using trained U-Net models for artistic rendering
- **Foveated Rendering**: High-detail central zone with optimized peripheral rendering
- **Physics Integration**: Hybrid approach using Unity DOTS and classical physics

## Project Structure

```
Inkling/
├── Assets/
│   └── Inkling/
│       ├── Runtime/
│       │   ├── Systems/
│       │   │   ├── SimulationLOD0/    # Core molecular simulation
│       │   │   ├── Inference/         # ML inference pipeline
│       │   │   └── Foveation/         # Foveated rendering system
│       │   └── Dev/
│       │       ├── DevOverlay/        # Debug visualization
│       │       └── RecordReplay/      # Deterministic replay system
│       ├── Shaders/                   # Compute and rendering shaders
│       └── Editor/                    # Unity Editor extensions
├── ProjectSettings/                   # Unity configuration
└── Packages/                          # Dependencies via MagiUnityDependencyManager
```

## Setup Instructions

### Prerequisites
- Unity 2022.3 LTS or newer
- NVIDIA GPU with compute shader support (for development)
- Git LFS for binary assets

### Getting Started
1. Ensure your Unity project root is `./Inkling` (nested)
2. Run dependency manager to set up packages:
   ```powershell
   ../MagiUnityDependencyManager/magi-deps.ps1 apply -ProjectPath ./Inkling -Strict
   ```
3. Open the Unity project at `./Inkling` in Unity Editor
4. Import required packages via Unity Package Manager:
   - Unity Sentis (ML inference)
   - Universal Render Pipeline (URP)
   - Unity DOTS packages

## Development Workflow

### Simulation Development
- Core simulation logic in `Assets/Inkling/Runtime/Systems/SimulationLOD0/`
- Compute shaders in `Assets/Inkling/Shaders/Simulation/`
- Use deterministic fixed timestep for reproducibility

### ML Integration
- ONNX models stored in `Assets/Inkling/ML/Models/`
- Inference pipeline in `Assets/Inkling/Runtime/Systems/Inference/`
- Dataset capture tools in `Assets/Inkling/Editor/DatasetCapture/`

### Performance Targets
- **Simulation**: ≤ 4-6ms per frame
- **ML Inference**: ≤ 3-5ms per frame
- **Compositing**: ≤ 2ms per frame
- **Target Devices**: iPhone 12, Pixel 6 (baseline)

## Related Repositories

- **[InkTools](../InkTools)**: Core fluid simulation and cellular automata systems
- **[InkModel](../InkModel)**: ML pipeline for training stylization models
- **[MagiUnityTools](../MagiUnityTools)**: Common Unity patterns and utilities
- **[MagiUnityDependencyManager](../MagiUnityDependencyManager)**: Package dependency management

## Build & Deployment

### Mobile Platforms
- iOS: Xcode 14+ required
- Android: Minimum API Level 24 (Android 7.0)

### Quality Settings
- **Tier 0**: Shader-only stylization (no ML)
- **Tier 1**: Hybrid ML + shader rendering
- **Tier 2**: Full ML chain with foveation

## Documentation

See the [magi-knowledge-repo-2](../../magi-knowledge-repo-2/docs/games/inkling/) for comprehensive documentation including:
- Technical specification
- Implementation overview
- ML pipeline setup
- Art and design guidelines
