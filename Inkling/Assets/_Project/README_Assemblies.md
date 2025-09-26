Assemblies & asmdef Layout (Inkling)
====================================

Goal: avoid common Unity asmdef pitfalls, keep compile times fast, and make dependencies explicit.

Folder Layout
- `Assets/_project/Runtime/Dev` → Development helpers (Bootstrap, overlays, test gen)
  - asmdef: `Inkling.Dev`
- `Assets/_project/Runtime/Systems/SimulationLOD0` → Simulation recorder + drivers
  - asmdef: `Inkling.SimulationLOD0`
- `Assets/_project/Runtime/Systems/Inference` → Model loading/inference
  - asmdef: `Inkling.Inference`
- `Assets/_project/Runtime/Systems/Foveation` → Center/periphery composition
  - asmdef: `Inkling.Foveation`

Hard Rules
- Exactly one asmdef per folder. Do not place multiple asmdefs in the same directory.
- Keep asmdefs out of parent folders that contain other asmdefs (e.g., do not put an asmdef directly in `Runtime/`).
- Avoid cyclic references: Dev may depend on Systems, but Systems must not depend on Dev.

Required References
- `Inkling.SimulationLOD0`
  - References: `Unity.InputSystem` (uses the new Input System)
- `Inkling.Dev`
  - References: `UnityEngine.UI` (Canvas/RawImage), `Inkling.SimulationLOD0`
- `Inkling.Inference`
  - Add Sentis reference when wiring runtime (e.g., define constraints or direct reference if package present)
- `Inkling.Foveation`
  - Typically no external references beyond UnityEngine/UnityEngine.Rendering

How to Add a Reference (Inspector)
- Select the `.asmdef` asset in Project window.
- In Inspector → “Assembly Definition References” → “+” → choose the target assembly (e.g., `Inkling.SimulationLOD0`).
- Apply/Save. Unity recompiles assemblies.

Editor‑Only Code
- Place editor scripts in an `Editor/` subfolder under the feature folder.
- Create a dedicated editor asmdef (e.g., `Inkling.Dev.Editor`) and set Include Platforms → Editor.
- Do not reference Editor assemblies from Runtime assemblies.

Scripting Defines / Package Guards
- For optional packages (e.g., Sentis), prefer a scripting define or Version Defines:
  - In asmdef, use `versionDefines` or `defineConstraints` to compile code paths only when the package exists.

Troubleshooting
- Error: “Folder contains multiple assembly definition files”
  - Cause: more than one `.asmdef` in the same directory.
  - Fix: move each asmdef into its own subfolder or remove duplicates.
- Error: “The type or namespace ‘X’ does not exist”
  - Cause: missing assembly reference.
  - Fix: add the target assembly to the caller asmdef’s “Assembly Definition References”.

Notes
- `_project` prefix keeps first‑party folders grouped and above third‑party content.
- Keep Systems modular; prefer Dev depending on Systems, not vice‑versa.

