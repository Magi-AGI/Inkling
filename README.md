Inkling (Game)
===============

Main Unity project for Inkling. This repo is managed via the MagiUnityDependencyManager using a `depfile.yaml` to author `Packages/manifest.json`.

Getting started
- Run `../MagiUnityDependencyManager/ink-deps.ps1 init -ProjectPath .` to scaffold `depfile.yaml`.
- Create a Unity project in this folder (Unity 6 LTS recommended) or open an existing one and run `ink-deps.ps1 apply` to materialize `Packages/manifest.json`.

Planned folders (once project is created by Unity)
- `Assets/Inkling/Runtime/Systems/{SimulationLOD0,Inference,Foveation}`
- `Assets/Inkling/Runtime/Dev/{DevOverlay,RecordReplay}`
- `Assets/Inkling/Shaders` and `Assets/Inkling/Editor`

