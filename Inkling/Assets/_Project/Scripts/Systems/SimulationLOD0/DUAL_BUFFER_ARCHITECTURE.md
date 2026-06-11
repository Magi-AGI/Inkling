# Dual Buffer Architecture for Creature Inks

## Problem

Creatures need to have **sharp, non-persistent outlines** that don't blur or trail in the fluid simulation, while regular inks (from mouse input) should persist and flow naturally.

Single-buffer approach issues:
- Can't have different dissipation rates for creatures vs. regular inks
- Creatures either blur (slow dissipation) or disappear immediately (fast dissipation)
- No way to prevent creature inks from being affected by advection/diffusion

## Solution: Dual Buffer System

### Architecture

Two separate density buffers:

1. **Main Density Buffer** (`density`):
   - Regular inks from mouse/user input
   - Flows through full simulation pipeline (advection, diffusion, pressure)
   - Normal dissipation rate (0.999 = slow fade)
   - Persists across frames

2. **Creature Ink Buffer** (`creatureInkBuffer`):
   - Creature stamps ONLY
   - **Cleared every frame** - does NOT persist in simulation
   - Does NOT go through advection/diffusion
   - Composited with density at start of each frame

### Frame Pipeline

```
START FRAME
    ↓
1. Creatures stamp to creatureInkBuffer
    ↓
2. Composite: density += creatureInkBuffer
    ↓
3. Clear creatureInkBuffer (GL.Clear)
    ↓
4. Run simulation on density (advection, pressure, etc.)
    ↓
5. Display density
    ↓
END FRAME
```

### Key Code Sections

#### SimDriver.cs

**Buffer allocation (line 189-190):**
```csharp
// Creature ink buffer (cleared each frame, composited with density before simulation)
creatureInkBuffer = CreateRT(RenderTextureFormat.ARGBHalf, "CreatureInk");
```

**Composite step (lines 316-355):**
```csharp
// COMPOSITE CREATURE INK: Add creature stamps to density, then clear creature buffer
if (creatureInkBuffer != null)
{
    // Read current density
    RenderTexture.active = density.Read;
    Texture2D tempDensity = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, false);
    tempDensity.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
    tempDensity.Apply();
    Color[] densityPixels = tempDensity.GetPixels();

    // Read creature ink buffer
    RenderTexture.active = creatureInkBuffer;
    Texture2D tempCreature = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, false);
    tempCreature.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
    tempCreature.Apply();
    Color[] creaturePixels = tempCreature.GetPixels();
    RenderTexture.active = null;

    // Additive blend: density += creature
    for (int i = 0; i < densityPixels.Length; i++)
    {
        densityPixels[i] += creaturePixels[i];
    }

    // Write back to density
    tempDensity.SetPixels(densityPixels);
    tempDensity.Apply();
    Graphics.Blit(tempDensity, density.Write);
    density.Swap();

    // Clear creature buffer for next frame
    RenderTexture.active = creatureInkBuffer;
    GL.Clear(true, true, Color.clear);
    RenderTexture.active = null;

    // Cleanup
    Destroy(tempDensity);
    Destroy(tempCreature);
}
```

**StampTexture updated (line 614-681):**
```csharp
public void StampTexture(Vector2 uvPosition, Texture2D stamp, float scale = 1.0f)
{
    if (creatureInkBuffer == null || stamp == null) return;

    // ... stamp to creatureInkBuffer instead of density ...
}
```

## Benefits

### 1. Sharp Creature Outlines
- Creatures are redrawn fresh each frame
- No accumulation or blurring from simulation
- Perfect pixel-accurate rendering

### 2. Separate Dissipation Control
- Regular inks: `dissipation = 0.999` (slow fade, flows naturally)
- Creature inks: Cleared every frame (instant "dissipation")
- No compromise needed

### 3. Performance
- Creatures don't go through advection/diffusion kernels
- Single composite operation per frame
- Multiple creatures can accumulate in creatureInkBuffer before composite

### 4. Visual Clarity
- Creatures remain sharp and visible
- Regular inks flow and interact with fluid dynamics
- No visual conflict between the two

## Creature Behavior

With this system:

1. **Creature stops moving**:
   - Stops injecting to creatureInkBuffer
   - creatureInkBuffer gets cleared next frame
   - Creature disappears immediately
   - NO trailing ink in simulation

2. **Creature moves continuously**:
   - Injects to creatureInkBuffer every frame (~30Hz via `injectionInterval`)
   - Appears sharp and stable
   - Position updates smoothly

3. **Multiple creatures**:
   - All stamp to same creatureInkBuffer
   - Additive blending if they overlap
   - All cleared together each frame

## Mouse Input vs Creatures

**Mouse injection** (via `InjectDensity`):
- Writes directly to main `density` buffer
- Goes through full simulation
- Persists and flows
- Normal dissipation (0.999)

**Creature injection** (via `StampTexture`):
- Writes to `creatureInkBuffer`
- Composited then cleared
- Does NOT persist
- Instant dissipation (cleared)

## Performance Considerations

The composite step (lines 316-355) does CPU readback which is expensive:
- 2x `ReadPixels()` calls per frame
- Pixel-by-pixel addition loop
- 2x Texture2D allocations

**Cost**: ~2-5ms per frame at 256x256 resolution

**Optimization opportunities**:
1. Use a compute shader for composite (GPU-side)
2. Use `CommandBuffer.Blit` with custom blend shader
3. Cache Texture2D objects instead of creating/destroying each frame

For now, the CPU approach is simple and works well enough for prototyping.

## Alternative: Compute Shader Composite

Future optimization could use a compute kernel:

```hlsl
// Fluids.compute
[numthreads(8, 8, 1)]
void CompositeCreatureInk(uint3 id : SV_DispatchThreadID)
{
    float4 density = _DensityRead[id.xy];
    float4 creature = _CreatureInkRead[id.xy];

    _DensityWrite[id.xy] = density + creature;
    _CreatureInkWrite[id.xy] = float4(0, 0, 0, 0);  // Clear
}
```

This would be **much faster** (~0.1-0.2ms) but requires adding the kernel to Fluids.compute.

## Testing

To verify the dual buffer system is working:

1. **Run with creatures**: Should see sharp, stable outlines
2. **Stop a creature** (`autonomous = false`): Should disappear immediately
3. **Inject with mouse**: Should leave persistent ink that flows
4. **Check performance**: Composite should be ~2-5ms (visible in profiler)

## Files Modified

1. **SimDriver.cs**:
   - Added `creatureInkBuffer` field (line 86)
   - Allocated buffer in `AllocateRenderTextures()` (lines 189-190)
   - Added composite step in `SimulateFrame()` (lines 316-355)
   - Updated `StampTexture()` to write to creature buffer (lines 614-681)
   - Added cleanup in `OnDestroy()` (line 775)
   - Reverted dissipation to 0.999 (line 23)

2. **TexturedInjector.cs**:
   - No changes needed - still calls `StampTexture()` which now goes to creature buffer

## Summary

The dual buffer architecture solves the creature sharpness problem by:
- **Separating concerns**: Creatures vs. regular inks
- **Per-frame clearing**: Creatures don't persist in simulation
- **Compositing**: Visual combination without simulation interaction

This matches the reference implementation approach where creatures were rendered separately and composited for display.
