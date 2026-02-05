# Games+Inkling+Phase 7F Plan

**Status:** Planning  
**Theme:** Remaining reference repo deep-dives (no repeats from Phase 7A–7E)

---
## Repos already covered (do not repeat)
- Inkling family: Inkling, Inkling 0.1, Inkling-Origins, Inkling2, InklingGame, InklingUnity3D, InklingUnity3D-2, InklingUnityProject, InklingUnityProject-2, Inkling_Old, Inkling_old-2.
- InkTools (primary) and InkTools-2/InkTools-3 copies.
- ofxFlowTools + openFrameworks variants (openFrameworks, openFrameworks2/3/4).

## Remaining candidates in References
- ArtistProject, ArtistProject-2
- boardgame.io
- duelyst
- forge
- Ledge-MSE2, Ledge-MSE2-sets
- LGTools
- rlcard
- sail_redux
- The-Powder-Toy (selected for 7F deep dive)

## Selected repo for this phase
**The-Powder-Toy** (2D cellular/particle sandbox)  
- Focus: GPU-friendly adaptations of pressure/heat/air fields, element registry, and sandbox debugging tools that can inform Inkling’s simulation/UX.  
- Key systems to examine: Air/pressure solver, ambient heat, element definitions and rules, save/load format, in-game debug visualization.

## Next actions
1) Triage The-Powder-Toy source (simulation/, especially Air.cpp, Simulation.cpp, element registry) and list portable patterns.  
2) Identify concrete features to port or prototype in InkTools/Inkling (e.g., pressure field debug, ambient heat control, element registry metadata UI).  
3) Summarize findings + recommendations on this card; create sub-tasks if any ports are chosen.

## Exit criteria for 7F
- At least one actionable porting task from The-Powder-Toy captured for InkTools/Inkling.  
- No duplicated review of previously covered Phase 7 repositories.
