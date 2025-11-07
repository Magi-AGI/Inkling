# Ink Gradients & Creatures - Quick Start Guide

## Feature Overview

Two new features have been added to reach feature parity with reference Inkling projects:

1. **Ink Gradient Rendering** - Apply beautiful color gradients to fluid simulation
2. **Textured Injectors (Creatures)** - Create shaped ink patterns that move autonomously

---

## 1. Ink Gradient Rendering

### Quick Start - Switching Ink Types

**Press number keys 1-8** during Play mode to switch between ink types:
- **1** = Fire (red/orange flames)
- **2** = Water (blue fluid)
- **3** = Metal (silver/gray)
- **4** = Electricity (bright blue-white)
- **5** = Ice (cyan/light blue)
- **6** = Plant (green/organic)
- **7** = Steam (white/gray vapor)
- **8** = Dust (brown/tan particles)

The current ink type is displayed in the top-left corner of the editor window.

When you inject with the mouse, the ink type determines which gradient channel is used:
- Fire/Water/Metal map to R/G/B channels respectively
- The gradient shader blends these channels to create the final appearance

### Setup

1. **Create a Gradient Preset Asset**
   - Right-click in Project → Create → Inkling → Ink Gradient Preset
   - This creates a ScriptableObject with 8 pre-configured ink types:
     - Fire, Water, Metal, Electricity, Ice, Plant, Steam, Dust

2. **Create a Material**
   - Create new Material
   - Set Shader to: `Inkling/InkGradientRenderer`

3. **Assign to SimDriver**
   - Select your SimDriver GameObject
   - Enable `Use Gradient Rendering` checkbox
   - Assign the Gradient Preset to `Gradient Preset` field
   - Assign the Material to `Gradient Material` field

### How It Works

- The gradient system maps density values to colors using Unity Gradients
- Each ink type has its own gradient, emission strength, and animation curve
- The shader automatically generates gradient textures and applies them
- Supports edge glow, saturation boost, and emission effects

### Customizing Gradients

Open your InkGradientPreset asset and adjust:
- **Gradient Colors**: Click gradient to edit color keys
- **Emission**: Controls how bright/glowy each ink type appears
- **Intensity Curve**: AnimationCurve to control density→color mapping
- **Global Settings**: Saturation, brightness, edge glow

---

## 2. Textured Injectors (Ink Creatures)

### Setup

1. **Create Creature Textures**
   - Create or import small textures (64x64 to 128x128 recommended)
   - Use alpha channel to define shape
   - Examples: fish silhouette, bird, abstract blob, etc.
   - Save as PNG with transparency

   **IMPORTANT: Make Texture Readable**
   - Select texture in Project window
   - In Inspector, check **"Read/Write Enabled"**
   - Click **Apply**
   - (Without this, you'll get "texture data is not readable" error)

2. **Add TexturedInjector Component**
   ```
   GameObject → Create Empty → "Ink Creature"
   Add Component → TexturedInjector
   ```

3. **Configure Settings**
   - **Sim Driver**: Auto-found or assign manually
   - **Injection Mask**: Your creature texture
   - **Mask Resolution**: 64 (matches texture size)
   - **Ink Color**: Color of the creature
   - **Density Multiplier**: How opaque it appears (try 5-10)
   - **Autonomous**: true for wandering behavior

### Behavior Modes

**Autonomous Mode** (autonomous = true)
- Creature wanders randomly within bounds
- Bounces off edges
- Leaves ink trail as it moves
- Randomized direction changes

**Player-Controlled Mode** (autonomous = false)
- Follows mouse cursor
- Smooth interpolation to mouse position
- Still leaves ink trail

### Parameters

**Movement:**
- `Move Speed`: How fast creature moves (0.1 = slow, 0.5 = fast)
- `Movement Bounds`: UV limits (0.9, 0.9 = stays away from edges)
- `Rotation Speed`: Currently unused, for future rotation feature

**Injection:**
- `Inject While Moving`: Only inject when moving (vs always)
- `Injection Interval`: Time between injections (0.033 = 30fps)
- `Add Velocity Trail`: Creature pushes fluid as it moves
- `Velocity Scale`: How much fluid motion creature creates

### Creating Creature Templates

Recommended textures for different effects:

**Fish/Aquatic:**
- Simple fish silhouette
- Gradually fading alpha from head to tail
- Size: 64x32 (elongated)

**Bird/Flying:**
- Bird with spread wings
- Strong alpha in body, softer in wings
- Size: 64x64

**Abstract Blob:**
- Circular gradient (solid center → transparent edge)
- Size: 64x64
- Creates organic, amoeba-like movement

**Particle Swarm:**
- Multiple small dots in one texture
- Each with own alpha gradient
- Creates cluster effect

---

## 3. Combining Features

### Colored Creatures

1. Set TexturedInjector `Ink Color` to match gradient
2. Use gradient preset to make creature color evolve over time
3. Example: Blue creature that fades to cyan as density dissipates

### Multiple Creatures

- Add multiple TexturedInjector components (separate GameObjects)
- Each with different textures and colors
- They all share the same fluid simulation
- Creates complex interaction patterns

### Interaction Examples

**Fire & Water Creatures:**
- One creature injects red/orange ink (fire)
- Another injects blue ink (water)
- When they meet, colors blend in simulation
- Future: Add interaction shader for steam generation

---

## 4. Performance Considerations

### Texture Resolution

- Keep injection masks small (64x64 or 128x128)
- Larger masks = more compute per injection
- For 64x64 mask, ~4096 density injections per frame

### Injection Rate

- Default `Injection Interval = 0.033` (30Hz) is good balance
- Increase for better performance (0.066 = 15Hz)
- Decrease for smoother trails (0.016 = 60Hz)

### Number of Creatures

- Up to 5-10 creatures should run smoothly on desktop
- Mobile: Limit to 2-3 creatures
- Each creature adds ~1-2ms per frame

---

## 5. API Reference

### SimDriver Public Methods

```csharp
// Inject force at UV position (0-1 range)
public void InjectForce(Vector2 uvPosition, Vector2 force)

// Inject density at UV position (0-1 range)
public void InjectDensity(Vector2 uvPosition, Color color)
```

### TexturedInjector Public Methods

```csharp
// Set creature position in UV space
public void SetPosition(Vector2 uvPos)

// Set creature velocity
public void SetVelocity(Vector2 vel)

// Get current UV position
public Vector2 GetPosition()

// Manually trigger injection
public void TriggerInjection()
```

---

## 6. Troubleshooting

**Gradient not showing:**
- Check `Use Gradient Rendering` is enabled
- Verify Material uses `Inkling/InkGradientRenderer` shader
- Ensure Gradient Preset is assigned
- Make sure `Display Velocity` is OFF

**Creature not appearing:**
- Verify injection mask texture is assigned
- **MOST COMMON**: Enable "Read/Write Enabled" on texture (see detailed fix below)
- Check `Ink Color` alpha is > 0
- **Increase `Density Multiplier` to 20-50** for good visibility (default 20)
- Ensure SimDriver reference is set
- Check Console for "[TexturedInjector] Mask loaded successfully" message
- If using gradients: Make sure creature `Ink Color` matches one of the gradient channels
  - Red tint for Fire, Green for Water, Blue for Metal
  - Or use pure channel colors: (1,0,0,1) for Fire, (0,1,0,1) for Water, etc.

**"Texture data is not readable" error:**
This is the most common error when setting up TexturedInjector. To fix:

1. **Select your creature texture** in Project window
2. **In Inspector panel**, look for "Advanced" dropdown
3. **Check "Read/Write Enabled"** checkbox
4. **Click "Apply"** button at bottom of Inspector
5. **Restart Play mode** if already running

Why this is needed: TexturedInjector reads texture pixels on the CPU to determine injection shape. Unity textures are GPU-only by default for performance. The "Read/Write Enabled" setting makes a CPU-readable copy.

If you see this error in Console:
```
ArgumentException: Texture2D.GetPixels: texture data is either not readable
```

You'll also see a helpful error message from TexturedInjector:
```
[TexturedInjector] Texture 'YourTextureName' is not readable!
To fix: Select texture in Project window → Inspector → Enable 'Read/Write Enabled' → Apply.
```

The component validates the texture on Start() and will refuse to inject if the texture isn't readable, preventing crashes.

**Creature moves too fast/slow:**
- Adjust `Move Speed` (try 0.05 to 0.5 range)
- Check `Injection Interval` isn't too high
- Verify `Movement Bounds` aren't too restrictive

**Performance issues:**
- Reduce `Mask Resolution` to 32 or 64
- Increase `Injection Interval` to 0.05+
- Disable `Add Velocity Trail` on some creatures
- Reduce number of active creatures

---

## Next Steps

- **Fire/Water Interactions**: Coming soon - interaction shader for material reactions
- **Creature AI**: Enhanced behavior patterns (schooling, fleeing, following)
- **Texture Animation**: Support for animated sprite sheets
- **Trail Rendering**: Dedicated trail visualization separate from simulation

For questions or issues, check the implementation files:
- `SimDriver.cs` - Main simulation driver
- `TexturedInjector.cs` - Creature injection logic
- `InkGradientPreset.cs` - Gradient configuration
- `InkGradientRenderer.shader` - Rendering shader
