# Gemini Code Assistant Guidance

This document provides guidance to the Gemini code assistant when working with the **Inkling** repository.

## Project Overview

**Inkling** is a Unity 6.0 project that implements a real-time 2D fluid simulation using GPU compute shaders. It also features ML-based stylization and is designed for performance-constrained environments like mobile platforms.

**Key Technologies:**
- **Engine:** Unity 6000.2.5f1
- **Rendering:** High Definition Render Pipeline (HDRP) 17.2.0
- **Compute:** Unity Burst and Unity.Mathematics for high-performance C#
- **Input:** Unity's new Input System (1.14.2)
- **Custom Packages:** The project relies on local packages (`com.magi.unitytools`, `com.inktools.sim`) for shared functionality.

**Architecture:**
- The core logic is organized into assembly definitions (`.asmdef`) to enforce clear dependencies. The main runtime code lives in `Magi.Inkling.Runtime`.
- The simulation is driven by compute shaders, with `SimDriver.cs` orchestrating the process.
- The project follows a pattern of dependency injection and direct references, avoiding expensive runtime lookups like `Resources.Load` and `FindObjectOfType`.

## Building and Running

### Editor Workflow

1.  **Open Project:** Open the `Inkling/` directory in Unity Hub using **Unity version 6000.2.5f1**.
2.  **Open Scene:** The main scene for development and testing is `Assets/_Project/Scenes/Main.unity`.
3.  **Run:** Press the **Play** button in the editor. The simulation should start automatically, driven by the `Bootstrap.cs` component in the scene.

### Build Commands

- **From Editor:** Use `File > Build Settings` to create a build for a specific platform (e.g., Windows, Mac, iOS, Android).
- **From CLI:**
  ```bash
  # Example for a Windows 64-bit build
  Unity -batchmode -quit -projectPath . -buildTarget StandaloneWindows64 -executeMethod BuildUtilities.Build
  ```
  *(Note: A `BuildUtilities.cs` script with a `Build` method is required for this command to work.)*

### Testing

- **From Editor:** Use the **Test Runner** window (`Window > General > Test Runner`) to run EditMode and PlayMode tests.
- **From CLI:**
  ```bash
  Unity -batchmode -quit -projectPath . -runTests -testPlatform PlayMode -testResults results.xml
  ```

## Development Conventions

### Coding Style

- **Formatting:** C# with 4-space indentation and Allman-style braces.
- **Naming:**
    - `PascalCase` for types, methods, and properties.
    - `camelCase` for local variables and private fields.
    - Use `[SerializeField] private` for fields that need to be exposed in the Inspector.
- **Banned APIs:** Do not use `Resources.Load` or `FindObjectOfType`. Instead, use direct references assigned in the Inspector or a dependency injection pattern.

### Project Structure

- **Runtime Code:** All core runtime logic should be placed within `Assets/_Project/Runtime/`.
- **Assembly Definitions:** Keep code within the boundaries of the existing `.asmdef` files. Create new assemblies for new, distinct systems.
- **Scenes:** All scenes should be located in `Assets/_Project/Scenes/`.
- **Assets:** Models, materials, and textures should be organized under `Assets/_Project/Models/` and `Assets/_-Project/Materials/`.

### Version Control

- **Commits:** Follow conventional commit message standards (e.g., `feat(simulation): ...`, `fix(rendering): ...`).
- **Pull Requests:** Ensure PRs have a clear description, document the changes, and include evidence of testing (e.g., screenshots, Test Runner results).
- **Meta Files:** Always preserve `.meta` files. Do not commit the `Library/` directory.
