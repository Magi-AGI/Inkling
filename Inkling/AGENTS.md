# Repository Guidelines

## Project Structure & Module Organization
- Unity 6 project (Editor `6000.2.5f1`). Primary code lives under `Assets/_Project/Runtime` with modules:
  - `Dev/` (bootstrapping, debug helpers), `Systems/` (Foveation, Rendering, SimulationLOD0), `UI/` (runtime UI).
  - Scenes in `Assets/_Project/Scenes/` (e.g., `Main.unity`).
  - Assets and models in `Assets/_Project/Models/` and `Assets/_Project/Materials/`.
- Settings and pipelines in `Assets/Settings/` (HDRP) and `ProjectSettings/`.
- Package metadata under `Packages/`.

## Build, Test, and Development Commands
- Open in Unity Hub with Editor `6000.2.5f1` and target Windows/Mac as needed.
- Build: use Unity Editor `File > Build Settings` (HDRP configured). For CI/CLI builds, invoke your Unity editor:
  - `Unity -batchmode -quit -projectPath . -buildTarget StandaloneWindows64 -executeMethod BuildUtilities.Build` (provide/build script as needed).
- Play/iterate: open `Main.unity` and press Play. Dev bootstrap is in `Dev/Bootstrap.cs`.
- Tests (Unity Test Runner):
  - GUI: `Window > General > Test Runner` (EditMode/PlayMode).
  - CLI: `Unity -batchmode -quit -projectPath . -runTests -testPlatform PlayMode -testResults results.xml`.

## Coding Style & Naming Conventions
- C# with 4‑space indentation, Allman braces, nullable where appropriate.
- Naming: PascalCase for types/methods/properties; camelCase for fields; `[SerializeField] private` for inspector fields; avoid `m_` unless matching existing files.
- Assembly definitions: keep code within `Magi.Inkling.Runtime` asmdef boundaries; avoid cross‑module leaks.

## Testing Guidelines
- Use Unity Test Framework. Place tests under `Assets/Tests/EditMode` and `Assets/Tests/PlayMode`.
- Prefer EditMode for pure logic, PlayMode for scene/graphics. Include minimal scenes/prefabs for PlayMode.
- Code coverage package is enabled; when possible, add coverage reports in CI (`ProjectSettings/Packages/com.unity.testtools.codecoverage`).

## Commit & Pull Request Guidelines
- Use concise, conventional commits (e.g., `feat(foveation): add seam blending`).
- PRs must include: clear description, scope of changes, test plan (runner output), and screenshots/video for visible changes.
- Do not commit `Library/`. Preserve `.meta` files and GUIDs; avoid asset renames unless necessary and update references.

## Agent‑Specific Tips
- Prefer direct references over `Resources.Load` (see `Bootstrap.cs`).
- Don’t move shaders/materials without updating shader references and render pipeline assets.
- Keep public fields for inspector tuning; avoid breaking serialized field names.

