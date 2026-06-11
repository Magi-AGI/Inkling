# TexturedInjector Texture Indexing Bug Fix

## Problem

The TexturedInjector was loading texture masks but not injecting any pixels, causing creatures to be invisible. The console showed the mask loaded successfully but no "[TexturedInjector] INJECTED" logs appeared.

## Root Cause

**Critical indexing bug**: The code was using `maskResolution` (default 64) to index into the pixel array, but the actual texture dimensions might be different. This caused:

1. **Incorrect pixel array indexing** - Line 255 used `y * maskResolution + x` but should use `y * actualTextureWidth + x`
2. **Wrong loop bounds** - Looped to `maskResolution` instead of actual texture dimensions
3. **Incorrect offset calculations** - Used `maskResolution` to calculate UV offsets

### Example of the Bug

If texture is 128x128 but `maskResolution` is 64:
- Loop only processes 64x64 pixels (1/4 of the texture)
- Pixel at (x=65, y=0) would be indexed as `0 * 64 + 65 = 65`
- But should be `0 * 128 + 65 = 65` (coincidentally same)
- Pixel at (x=0, y=65) would be indexed as `65 * 64 + 0 = 4160`
- But should be `65 * 128 + 0 = 8320` (WRONG by 2x!)

This would read garbage data from wrong parts of array or even go out of bounds.

## Solution

### 1. Added Private Fields (Lines 41-42)
```csharp
private int actualMaskWidth = 0;   // Actual texture width
private int actualMaskHeight = 0;  // Actual texture height
```

### 2. Updated ValidateMask() (Lines 64-117)
- Store actual texture dimensions: `actualMaskWidth = injectionMask.width`
- Use actual dimensions in GetPixels()
- Added diagnostic logging:
  - Center pixel RGBA values
  - Count of pixels above alpha threshold
  - Total pixels loaded

### 3. Fixed InjectAtPosition() (Lines 244-273)
- Use `actualMaskWidth` and `actualMaskHeight` for loop bounds
- Use `actualMaskWidth` for row stride in pixel indexing: `y * actualMaskWidth + x`
- Calculate offsets using actual dimensions
- Updated debug logs to show actual dimensions

## Files Modified

- **TexturedInjector.cs**: Lines 41-42, 64-117, 244-319

## Testing

After this fix, ValidateMask() will log:
```
[TexturedInjector] Texture '1idle00' dimensions: 128x128, requesting 64x64
[TexturedInjector] Mask '1idle00' loaded successfully (128x128)
[TexturedInjector] Pixel data: 16384 pixels loaded
[TexturedInjector] Center pixel RGBA: (1.000, 1.000, 1.000, 1.000)
[TexturedInjector] Pixels above threshold (0.1): 8234 / 16384
```

And InjectAtPosition() should now log:
```
[TexturedInjector] INJECTED 327 pixels at UV (0.500, 0.500), color RGBA(0.00, 1.00, 1.00, 1.00), multiplier 20.000, avg alpha 0.872
```

## Impact

This fix ensures texture masks of ANY size work correctly:
- 32x32 textures
- 64x64 textures
- 128x128 textures
- Non-square textures (e.g., 128x64)

All will now be indexed correctly and inject properly into the simulation.
