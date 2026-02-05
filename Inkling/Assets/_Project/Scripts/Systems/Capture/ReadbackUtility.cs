using System;
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Magi.UnityTools.Core;
using Magi.UnityTools.Patterns;
using Magi.UnityTools.Diagnostics;
using Magi.Inkling.Services.Diagnostics;

namespace Magi.Inkling.Systems.Capture
{
    /// <summary>
    /// Thin wrapper around AsyncGPUReadback that reports Result + metadata
    /// and falls back to CPU readback when the platform/API cannot service
    /// the request. Designed to be fire-and-forget for captures.
    /// </summary>
    public static class ReadbackUtility
    {
        public struct ReadbackMetadata
        {
            public int width;
            public int height;
            public string format;
            public bool asyncSupported;
            public bool usedAsync;
        }

        public static Result RequestTextureToPng(RenderTexture src, string outputPngPath, ILogSink logSink, out ReadbackMetadata meta)
        {
            meta = new ReadbackMetadata
            {
                width = src.width,
                height = src.height,
                format = src.graphicsFormat.ToString(),
                asyncSupported = SystemInfo.supportsAsyncGPUReadback,
                usedAsync = SystemInfo.supportsAsyncGPUReadback
            };

            Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath)!);

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                meta.usedAsync = false;
                return FallbackReadback(src, outputPngPath);
            }

            try
            {
                // Meta indicates we attempted async; if an error happens we still return Success() from this call
                // and rely on fallback result to signal errors.
                AsyncGPUReadback.Request(src, 0, TextureFormat.RGBA32, req =>
                {
                    if (req.hasError)
                    {
                        logSink?.Add("AsyncGPUReadback failed; using CPU fallback.");
                        var fb = FallbackReadback(src, outputPngPath);
                        if (!fb.IsSuccess)
                            logSink?.Add($"Capture fallback failed: {fb.Error}");
                        return;
                    }

                    NativeArray<byte> data = req.GetData<byte>();
                    var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, true);
                    tex.LoadRawTextureData(data);
                    tex.Apply();
                    File.WriteAllBytes(outputPngPath, tex.EncodeToPNG());
                    UnityEngine.Object.Destroy(tex);
                });

                return Result.Success();
            }
            catch (Exception ex)
            {
                meta.usedAsync = false;
                return Result.Fail(ex);
            }
        }

        private static Result FallbackReadback(RenderTexture src, string path)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = src;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, true);
            try
            {
                tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            catch (Exception ex)
            {
                return Result.Fail(ex);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
                RenderTexture.active = prev;
            }
            return Result.Success();
        }
    }
}
