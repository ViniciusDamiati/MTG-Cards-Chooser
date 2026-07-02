# CUDA in C# — Comprehensive Developer Guide

> Based on the implementation in `Services/CudaNppImagePipeline.cs` from this project.
> Covers everything needed to add GPU-accelerated image processing (or any CUDA workload)
> to a future .NET 6/7/8/9 project using **ManagedCuda** and **CUDA NPP**.

---

## Table of Contents

1. [Prerequisites & Installation](#1-prerequisites--installation)
2. [NuGet Packages](#2-nuget-packages)
3. [CUDA Concepts Primer](#3-cuda-concepts-primer)
4. [NPP (NVIDIA Performance Primitives) Primer](#4-npp-nvidia-performance-primitives-primer)
5. [Project Setup](#5-project-setup)
6. [DLL Discovery & Graceful Fallback](#6-dll-discovery--graceful-fallback)
7. [Creating & Managing the CUDA Context](#7-creating--managing-the-cuda-context)
8. [NPP Stream Context](#8-npp-stream-context)
9. [Image Processing Pipeline (Reference Implementation)](#9-image-processing-pipeline-reference-implementation)
10. [Pixel Format Conversions](#10-pixel-format-conversions)
11. [Thread Safety](#11-thread-safety)
12. [Memory Management & IDisposable Pattern](#12-memory-management--idisposable-pattern)
13. [Error Handling Strategy](#13-error-handling-strategy)
14. [Performance Tips for RTX / Ampere GPUs](#14-performance-tips-for-rtx--ampere-gpus)
15. [Debugging & Diagnostic Output](#15-debugging--diagnostic-output)
16. [Complete Boilerplate Template](#16-complete-boilerplate-template)
17. [Common Pitfalls & Solutions](#17-common-pitfalls--solutions)
18. [NPP API Quick Reference](#18-npp-api-quick-reference)
19. [Frequently Asked Questions](#19-frequently-asked-questions)

---

## 1. Prerequisites & Installation

### Hardware
- Any NVIDIA GPU with **Compute Capability 5.0+** (Maxwell or newer)
- RTX series (Ampere/Ada): fully supported, 10–24 GB VRAM typical

### Software

| Component | Minimum Version | Where to get |
|---|---|---|
| NVIDIA Display Driver | 520+ | https://www.nvidia.com/drivers |
| CUDA Toolkit | **12.2+** (for NPP API 13 DLLs `_13`) | https://developer.nvidia.com/cuda-downloads |
| .NET SDK | 6.0+ | https://dotnet.microsoft.com |
| Windows | 10 / 11 | — |

> **Why 12.2?** Starting with CUDA Toolkit 12.2, NVIDIA introduced NPP API version 13.
> The ManagedCuda.NPP 2.x packages import DLLs with the `_13` suffix
> (`nppisu64_13.dll`, `nppig64_13.dll`, etc.). Older Toolkits ship `_12` DLLs and will
> not work with ManagedCuda 2.12.

### Verifying Your Installation

```powershell
# Check driver version
nvidia-smi

# Check Toolkit version
nvcc --version

# Confirm NPP DLLs exist
Get-ChildItem "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA" -Recurse -Filter "nppisu64_13.dll"
```

### CUDA Toolkit Installer Gotcha — PATH

The CUDA Toolkit installer **does NOT always add its `bin\` directory to PATH**.
If `nvcc --version` fails after installation, add it manually:

```
System Properties → Advanced → Environment Variables → Path → New:
C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.x\bin
```

Or use the code in §6 to load DLLs by absolute path at runtime (no PATH needed).

---

## 2. NuGet Packages

```xml
<!-- In your .csproj -->
<PackageReference Include="ManagedCuda"     Version="2.12.0" />
<PackageReference Include="ManagedCuda.NPP" Version="2.12.0" />
```

| Package | Purpose |
|---|---|
| `ManagedCuda` | Core CUDA runtime: contexts, streams, device memory, kernel launches |
| `ManagedCuda.NPP` | NVIDIA Performance Primitives for image processing (resize, filter, etc.) |
| `ManagedCuda.NVRTC` | Runtime compilation of CUDA kernels from C++ source at runtime (optional) |
| `ManagedCuda.CudaBlas` | cuBLAS: GPU-accelerated linear algebra (optional) |
| `ManagedCuda.CuDNN` | cuDNN: deep learning primitives (optional) |

### Key Namespaces

```csharp
using ManagedCuda;                  // CudaContext, PrimaryContext, CudaDeviceVariable<T>
using ManagedCuda.BasicTypes;       // CUstream, CUdevice, CUcontext
using ManagedCuda.NPP;              // NPPImage_8uC3, NppStreamContext, NppiRect, etc.
using System.Runtime.InteropServices; // NativeLibrary.TryLoad
```

---

## 3. CUDA Concepts Primer

### GPU vs CPU Execution

```
CPU (Host)                         GPU (Device)
─────────────────────              ────────────────────────────────
Serial / few cores                 Thousands of CUDA cores (parallel)
Large cache, branch predict        Less cache, no branch prediction
System RAM                         Dedicated VRAM (10 GB on RTX 3080)
.NET code runs here                CUDA kernels run here
```

### CUDA Context

A **context** is the GPU equivalent of a CPU process — it owns all GPU resources
(memory allocations, streams, modules) for a single device.

```
One GPU → One PrimaryContext per application (recommended)
         Multiple CudaContext (legacy, one per thread — avoid)
```

### Streams

A **stream** is a sequence of GPU operations that execute in order.
Operations on different streams can run concurrently.

```
NullStream (CUstream.NullStream) — synchronous, default, simplest
Named streams                    — async, for overlapping GPU work
```

For image processing pipelines where correctness > throughput: use `NullStream`.

### Device Memory vs Host Memory

```csharp
// Allocate on GPU (device)
using var gpuBuffer = new CudaDeviceVariable<float>(1024);

// Copy CPU → GPU
float[] cpuData = new float[1024];
gpuBuffer.CopyToDevice(cpuData);

// Copy GPU → CPU
gpuBuffer.CopyToHost(cpuData);
```

---

## 4. NPP (NVIDIA Performance Primitives) Primer

NPP is NVIDIA's library of highly optimised image and signal processing functions
that run directly on CUDA cores. Think of it as OpenCV but running entirely on GPU.

### DLL Structure (CUDA Toolkit 12.2+)

| DLL | Contents |
|---|---|
| `nppc64_13.dll` | NPP core (required by all NPP DLLs) |
| `nppisu64_13.dll` | **Utility / stream context** (`nppGetStreamContext`) |
| `nppig64_13.dll` | **Geometry transforms**: Resize, Rotate, Copy, Crop |
| `nppif64_13.dll` | **Filtering**: Gauss, Median, Laplacian, Sharpen, Convolution |
| `nppial64_13.dll` | Arithmetic/logic operations |
| `nppicc64_13.dll` | Color conversion (RGB↔YUV, RGB↔HSV, etc.) |
| `nppisc64_13.dll` | Statistical operations (mean, stddev, histogram) |

### Image Type Naming Convention

```
NPPImage_[bit-depth][type]C[channels]
         ─────────────────────────────
         8u  = 8-bit unsigned (0–255)  ← most common for images
         16u = 16-bit unsigned
         32f = 32-bit float
         
         C1 = 1 channel (grayscale)
         C3 = 3 channels (BGR or RGB)  ← used in this project
         C4 = 4 channels (BGRA)

Examples:
  NPPImage_8uC3  = 3-channel 8-bit (BGR24)   ← our pixel format
  NPPImage_8uC4  = 4-channel 8-bit (BGRA32)
  NPPImage_32fC1 = grayscale float32
```

### Key Operations Available

```csharp
// Resize (GPU)
src.Resize(dst, InterpolationMode.Cubic, streamCtx);

// Copy ROI / Crop (GPU)  
src.Copy(dst, offsetX, offsetY);          // copies dst.Size starting at (offsetX, offsetY) in src

// Gaussian blur (GPU)
src.FilterGaussBorder(dst, MaskSize.Size_3_X_3, NppiBorderType.Replicate, streamCtx, roi);

// Custom convolution / sharpen kernel (GPU)
src.FilterBorder(dst, kernelDevice, kernelSize, anchor, NppiBorderType.Replicate, streamCtx, roi);

// Color conversion (GPU)
src.RGBToGray(dst, streamCtx);
src.BGRToRGB(dst, streamCtx);

// Threshold (GPU)
src.Threshold(dst, threshold, NPP_CMP_GREATER, value, streamCtx);
```

---

## 5. Project Setup

### csproj Configuration

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Prevent duplicate AssemblyInfo conflicts with ManagedCuda -->
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="ManagedCuda"     Version="2.12.0" />
    <PackageReference Include="ManagedCuda.NPP" Version="2.12.0" />
    <!-- Add SkiaSharp only if you need CPU image processing fallback -->
    <PackageReference Include="SkiaSharp"       Version="3.116.1" />
  </ItemGroup>
</Project>
```

> **Important:** `GenerateAssemblyInfo=false` and `GenerateTargetFrameworkAttribute=false`
> prevent the compiler from emitting duplicate `[assembly: AssemblyVersion]` attributes
> that conflict with the ones already embedded in ManagedCuda's assemblies.

---

## 6. DLL Discovery & Graceful Fallback

This is the most important pattern for production code. The CUDA Toolkit DLLs may
not be in PATH, and the application must degrade gracefully on machines without a GPU.

### The Two-Pass Discovery Strategy

```csharp
private static readonly string[] RequiredNppDlls =
{
    "nppisu64_13",   // stream-context utilities
    "nppig64_13",    // geometry transforms (Resize, Copy)
    "nppif64_13",    // image filtering (Gauss, Convolution)
};

private const string CudaToolkitRoot =
    @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";

private static bool EnsureNppDllsLoaded(out string source)
{
    // Pass 1: PATH / Windows system directory search (fast)
    if (RequiredNppDlls.All(dll => NativeLibrary.TryLoad(dll, out _)))
    {
        source = "PATH";
        return true;
    }

    // Pass 2: scan all installed CUDA versions by absolute path
    // (bypasses PATH requirement — works even when PATH is not configured)
    if (Directory.Exists(CudaToolkitRoot))
    {
        var binDirs = Directory
            .GetDirectories(CudaToolkitRoot, "v*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(d => d)          // newest version first
            .Select(d => Path.Combine(d, "bin"))
            .Where(Directory.Exists);

        foreach (string dir in binDirs)
        {
            bool allLoaded = RequiredNppDlls.All(dll =>
            {
                string fullPath = Path.Combine(dir, dll + ".dll");
                return File.Exists(fullPath) && NativeLibrary.TryLoad(fullPath, out _);
            });

            if (allLoaded) { source = dir; return true; }
        }
    }

    // Build actionable diagnostic for the console
    bool rootExists = Directory.Exists(CudaToolkitRoot);
    if (!rootExists)
    {
        source = $"CUDA Toolkit not found at '{CudaToolkitRoot}'. " +
                 "Install from https://developer.nvidia.com/cuda-downloads (12.2+)";
    }
    else
    {
        string versions = string.Join(", ",
            Directory.GetDirectories(CudaToolkitRoot, "v*")
                     .Select(Path.GetFileName));
        source = $"Found CUDA [{versions}] but no _13 DLLs. Upgrade to Toolkit 12.2+.";
    }
    return false;
}
```

### Why Not Just Catch DllNotFoundException?

```csharp
// ❌ WRONG — looks correct but fails in practice
try
{
    var ctx = new NppStreamContext(CUstream.NullStream); // triggers DllNotFoundException
}
catch (DllNotFoundException)
{
    return null; // ← this catch is NEVER reached in some CLR versions
}
```

**Root cause:** When the CLR JIT-compiles a method or initialises a managed type that
has P/Invoke bindings to a missing DLL, it may throw `DllNotFoundException` *at
type-initialisation time*, **before** the `try` block's execution frame is established.
The exception then propagates to the caller's stack, bypassing the `catch`.

**Solution:** Use `NativeLibrary.TryLoad` (which returns `false` instead of throwing)
*before* any managed type from `ManagedCuda.NPP` is accessed.

---

## 7. Creating & Managing the CUDA Context

### PrimaryContext vs CudaContext

```csharp
// ✅ Recommended: PrimaryContext
// - One per GPU per process (matches the CUDA driver model)
// - Shared with the display driver's context on consumer GPUs
// - Required when using NPP
var ctx = new PrimaryContext(gpuIndex: 0);
ctx.SetCurrent(); // bind to calling thread

// ❌ Legacy: CudaContext
// - Creates a new exclusive context (may conflict with NPP on some drivers)
// - Avoid for NPP workloads
var ctx = new CudaContext(gpuIndex: 0);
```

### Multi-GPU Setup

```csharp
int deviceCount = CudaContext.GetDeviceCount();
Console.WriteLine($"Found {deviceCount} CUDA device(s)");

for (int i = 0; i < deviceCount; i++)
{
    string name = CudaContext.GetDeviceName(i);
    Console.WriteLine($"  GPU {i}: {name}");
}

// Use GPU 0 (first / primary)
var ctx = new PrimaryContext(0);
```

### Binding Context to Thread

A CUDA context must be **current** on the thread that uses it.
In async / multi-threaded code, always call `ctx.SetCurrent()` before GPU operations:

```csharp
await Task.Run(() =>
{
    ctx.SetCurrent(); // ← bind to this thread-pool thread
    // GPU operations here
});
```

---

## 8. NPP Stream Context

`NppStreamContext` tells NPP which CUDA stream to use.
It must be created after the CUDA context is current.

```csharp
// Synchronous execution (simplest, correct for sequential pipelines)
var streamCtx = new NppStreamContext(CUstream.NullStream);

// Asynchronous execution (advanced — operations on this stream don't block)
using var stream = new CudaStream();
var streamCtx = new NppStreamContext(stream.Stream);
// ... GPU ops ...
stream.Synchronize(); // wait for GPU to finish
```

For image processing where you process one image at a time, `NullStream` is correct
and simplest. Named streams are beneficial when running multiple independent GPU
operations in parallel (e.g., processing N images simultaneously on the same GPU).

---

## 9. Image Processing Pipeline (Reference Implementation)

### Complete Resize → Crop → Denoise → Sharpen Pipeline

```csharp
private SKBitmap RunGpuPipeline(
    SKBitmap sourceBitmap,
    int targetW, int targetH,
    int cropX, int cropY, int cropW, int cropH,
    float[] sharpenKernel3x3,      // 9-element float array
    NppStreamContext streamCtx)
{
    // ── Step 1: SKBitmap (BGRA 8888) → BGR byte[] ───────────────────────
    byte[] srcBgr = BgraToRgb(sourceBitmap);  // strips Alpha channel

    // ── Step 2: Upload to GPU ────────────────────────────────────────────
    using var gpuSrc = new NPPImage_8uC3(sourceBitmap.Width, sourceBitmap.Height);
    gpuSrc.CopyToDevice(srcBgr);

    // ── Step 3: Resize on GPU (Cubic/Catmull-Rom) ────────────────────────
    using var gpuResized = new NPPImage_8uC3(targetW, targetH);
    gpuSrc.Resize(gpuResized, InterpolationMode.Cubic, streamCtx);

    // ── Step 4: Crop ROI on GPU via SetRoi ───────────────────────────────
    // ⚠ Do NOT use Copy(dst, x, y) — that overload resolves to the
    //   channel-extraction Copy(NPPImage_8uC1 dst, int channelSrc) and
    //   throws ArgumentOutOfRangeException when x > 2.
    //
    // Correct idiom: set the ROI on the source to the desired sub-rectangle,
    // then call the plain Copy(dst).  DevPtrRoi is automatically adjusted so
    // only the ROI pixels are copied into the (correctly sized) destination.
    using var gpuCropped = new NPPImage_8uC3(cropW, cropH);
    gpuResized.SetRoi(cropX, cropY, cropW, cropH);
    gpuResized.Copy(gpuCropped);
    gpuResized.SetRoi(0, 0, targetW, targetH); // restore full-image ROI

    // ── Step 5: Gaussian denoise on GPU (3×3, border replication) ────────
    using var gpuBlurred = new NPPImage_8uC3(cropW, cropH);
    gpuCropped.FilterGaussBorder(
        gpuBlurred,
        MaskSize.Size_3_X_3,
        NppiBorderType.Replicate,
        streamCtx,
        new NppiRect(0, 0, cropW, cropH));

    // ── Step 6: Laplacian sharpen on GPU ────────────────────────────────
    // Upload kernel to device memory first
    using var gpuKernel = new CudaDeviceVariable<float>(sharpenKernel3x3.Length);
    gpuKernel.CopyToDevice(sharpenKernel3x3);

    using var gpuSharpened = new NPPImage_8uC3(cropW, cropH);
    gpuBlurred.FilterBorder(
        gpuSharpened,
        gpuKernel,
        new NppiSize(3, 3),          // kernel size
        new NppiPoint(1, 1),         // kernel anchor (center for 3x3)
        NppiBorderType.Replicate,
        streamCtx,
        new NppiRect(0, 0, cropW, cropH));

    // ── Step 7: Download from GPU ────────────────────────────────────────
    byte[] dstBgr = new byte[cropW * cropH * 3];
    gpuSharpened.CopyToHost(dstBgr);

    // ── Step 8: BGR byte[] → SKBitmap (BGRA 8888) ────────────────────────
    return RgbToSKBitmap(dstBgr, cropW, cropH);
}
```

### Interpolation Modes for Resize

```csharp
InterpolationMode.NearestNeighbor  // Fastest, pixelated
InterpolationMode.Linear           // Bilinear — good quality, fast
InterpolationMode.Cubic            // Bicubic / Catmull-Rom — best quality ← recommended
InterpolationMode.Super            // Supersampling — highest quality, slowest
InterpolationMode.Lanczos          // Lanczos — excellent for downscaling
```

### Border Types

```csharp
NppiBorderType.Replicate   // Edge pixels repeated — no dark border artifacts ← recommended
NppiBorderType.Reflect     // Mirror reflection at border
NppiBorderType.Wrap        // Tiling/wraparound
NppiBorderType.Const       // Fill border with constant (default: 0 = black)
```

---

## 10. Pixel Format Conversions

SkiaSharp uses **BGRA 8888** (4 bytes per pixel: Blue, Green, Red, Alpha).
NPP C3 images use **BGR 24-bit** (3 bytes per pixel, no Alpha).

```csharp
/// <summary>BGRA 8888 (SkiaSharp) → BGR 24-bit (NPP C3) — strips Alpha.</summary>
private static byte[] BgraToRgb(SKBitmap bmp)
{
    byte[] pixels = bmp.Bytes; // B G R A B G R A ...
    byte[] bgr    = new byte[bmp.Width * bmp.Height * 3];
    for (int s = 0, d = 0; s < pixels.Length; s += 4, d += 3)
    {
        bgr[d]     = pixels[s];       // B
        bgr[d + 1] = pixels[s + 1];   // G
        bgr[d + 2] = pixels[s + 2];   // R
        // pixels[s + 3] = Alpha — discarded
    }
    return bgr;
}

/// <summary>BGR 24-bit (NPP C3) → BGRA 8888 SKBitmap (Alpha = 255 fully opaque).</summary>
private static SKBitmap RgbToSKBitmap(byte[] bgr, int width, int height)
{
    var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
    var bmp  = new SKBitmap(info);

    byte[] bgra = new byte[width * height * 4];
    for (int s = 0, d = 0; s < bgr.Length; s += 3, d += 4)
    {
        bgra[d]     = bgr[s];         // B
        bgra[d + 1] = bgr[s + 1];     // G
        bgra[d + 2] = bgr[s + 2];     // R
        bgra[d + 3] = 255;            // A = fully opaque
    }

    // Pin the managed array and install it as the bitmap's pixel buffer
    var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
    try
    {
        bmp.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
        return bmp;
    }
    finally
    {
        handle.Free(); // unpin — SkiaSharp has copied the data internally
    }
}
```

### Common Color Conversions on GPU

```csharp
// BGR → Grayscale (on GPU via NPP)
using var gpuGray = new NPPImage_8uC1(width, height);
gpuBgr.BGRToGray(gpuGray, streamCtx);

// BGR → BGRA (add Alpha channel = 255 on GPU)
using var gpuBgra = new NPPImage_8uC4(width, height);
gpuBgr.BGRToBGRA(gpuBgra, 255, streamCtx);

// BGR ↔ RGB channel swap (in-place or out-of-place)
gpuBgr.SwapChannels(new int[] { 2, 1, 0 }, streamCtx);  // BGR → RGB
```

---

## 11. Thread Safety

CUDA contexts are per-thread. When using `async`/`await` or `Task.Run`, the OS can
resume execution on a different thread-pool thread, which does not have the CUDA
context bound to it.

### Correct Pattern

```csharp
public sealed class GpuPipeline : IDisposable
{
    private readonly PrimaryContext _ctx;
    private readonly NppStreamContext _streamCtx;
    
    // SemaphoreSlim(1,1) = binary mutex: one thread uses GPU at a time
    private readonly SemaphoreSlim _gpuLock = new SemaphoreSlim(1, 1);

    public SKBitmap Process(SKBitmap input)
    {
        _gpuLock.Wait();
        try
        {
            _ctx.SetCurrent(); // ← ALWAYS rebind before GPU work
            return RunPipeline(input);
        }
        finally
        {
            _gpuLock.Release();
        }
    }
}
```

### Producer-Consumer Pattern (GPU + CPU Pipeline)

For maximum throughput: overlap GPU work (SR upscaling) with CPU work
(resize/crop/encode) using `System.Threading.Channels`:

```csharp
// GPU producer: process images one-by-one, write results to channel
var channel = Channel.CreateBounded<(string path, bool ok)>(capacity: 2);

var producer = Task.Run(async () =>
{
    foreach (string img in images)
    {
        bool ok = await RunGpuSuperResolutionAsync(img, tempPath);
        await channel.Writer.WriteAsync((tempPath, ok));
    }
    channel.Writer.Complete();
});

// CPU consumer: encode and save images as they arrive
await foreach (var (path, ok) in channel.Reader.ReadAllAsync())
{
    await Task.Run(() => EncodeAndSave(path));
}

await producer; // propagate any GPU exception
```

This keeps the GPU and CPU both busy simultaneously:
```
GPU:  [img-1]────[img-2]────[img-3]────[img-4]
CPU:       [img-1]────[img-2]────[img-3]────[img-4]
```

---

## 12. Memory Management & IDisposable Pattern

**All** ManagedCuda objects (`NPPImage_8uC3`, `CudaDeviceVariable<T>`, `PrimaryContext`, etc.)
implement `IDisposable`. Not disposing them leaks GPU VRAM.

### Always use `using`

```csharp
// ✅ Correct — automatic disposal when scope exits
using var gpuImage = new NPPImage_8uC3(width, height);
gpuImage.CopyToDevice(data);

// ❌ Wrong — leaks VRAM if exception occurs before manual Dispose
var gpuImage = new NPPImage_8uC3(width, height);
gpuImage.CopyToDevice(data);
gpuImage.Dispose(); // ← never reached if CopyToDevice throws
```

### Long-lived Objects (Class Fields)

For objects that live for the lifetime of the class, implement `IDisposable`:

```csharp
public sealed class GpuPipeline : IDisposable
{
    private readonly PrimaryContext _ctx;
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
    private bool _disposed;

    public void Dispose()
    {
        if (!_disposed)
        {
            _lock.Dispose();
            _ctx.Dispose();    // frees CUDA context + all GPU allocations
            _disposed = true;
        }
    }
}

// Usage — always wrap in using
using GpuPipeline? gpu = GpuPipeline.TryCreate();
if (gpu != null)
{
    // use gpu
} // ← Dispose() called here automatically
```

### VRAM Budget Guidelines (RTX 3080 — 10 GB)

| Image size | BGR 24-bit footprint |
|---|---|
| 672 × 936 (Scryfall source) | 1.8 MB |
| 2688 × 3744 (4× upscaled) | 28.6 MB |
| 3000 × 4159 (target card) | 35.5 MB |
| 2527 × 1827 (art crop) | 13.1 MB |

A single pipeline with 5 intermediate buffers uses ~110 MB.
RTX 3080 can run ~90 images in parallel before VRAM pressure.

---

## 13. Error Handling Strategy

### The Three Layers

```
Layer 1 — NativeLibrary.TryLoad    : probe DLLs silently BEFORE accessing managed types
Layer 2 — TryCreate factory        : catch ALL exceptions, return null on failure
Layer 3 — Caller fallback          : if null, use CPU path (SkiaSharp, etc.)
```

```csharp
// Layer 2: factory catches everything
public static GpuPipeline? TryCreate()
{
    try
    {
        if (!EnsureNppDllsLoaded(out string diag))
        {
            Console.WriteLine($"GPU unavailable: {diag}");
            return null;
        }
        // ... initialise ...
        return new GpuPipeline(ctx, streamCtx);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"GPU init failed: {ex.Message}");
        return null;
    }
}

// Layer 3: caller falls back gracefully
using GpuPipeline? gpu = GpuPipeline.TryCreate();

SKBitmap result = gpu != null
    ? gpu.Process(input)          // GPU path
    : ProcessOnCpu(input);        // CPU fallback
```

### What Can Go Wrong and When

| Exception | When | Root cause |
|---|---|---|
| `DllNotFoundException` | Type-init / first P/Invoke | DLL not on PATH or not installed |
| `CudaException` | Any GPU operation | Driver bug, out of VRAM, invalid parameters |
| `NullReferenceException` | After context disposed | Using pipeline after `Dispose()` |
| `ObjectDisposedException` | After context disposed | Correctly throw if disposed |
| `OutOfMemoryException` | `new NPPImage_8uC3(...)` | GPU VRAM exhausted |

---

## 14. Performance Tips for RTX / Ampere GPUs

### Tile Size for realesrgan-ncnn-vulkan

```
-t 0   = auto (use as much VRAM as possible)
         RTX 3080 (10 GB) → very large tiles → fewer GPU passes → fastest
-t 512 = fixed 512px tiles (safe for 4 GB cards)
```

### Thread Configuration

```
-j load:proc:save
   1:4:1   = 4 GPU compute threads (good for RTX 3080 — 8704 CUDA cores)
   2:8:2   = 8 compute threads (for RTX 3090 / 4090 — more SM units)
```

### Kernel Upload Caching

If the same convolution kernel is used repeatedly, cache the GPU copy:

```csharp
// ❌ Bad — uploads kernel to GPU on every image
using var gpuKernel = new CudaDeviceVariable<float>(kernel.Length);
gpuKernel.CopyToDevice(kernel);

// ✅ Good — upload once, reuse
private CudaDeviceVariable<float>? _cachedKernel;

private CudaDeviceVariable<float> GetOrCreateKernel(float[] kernel)
{
    if (_cachedKernel == null)
    {
        _cachedKernel = new CudaDeviceVariable<float>(kernel.Length);
        _cachedKernel.CopyToDevice(kernel);
    }
    return _cachedKernel;
}
```

### Intermediate Buffer Reuse

For batch processing, pre-allocate intermediate buffers once:

```csharp
// Pre-allocate for batch processing
using var gpuResized  = new NPPImage_8uC3(targetW, targetH);
using var gpuCropped  = new NPPImage_8uC3(cropW, cropH);
using var gpuBlurred  = new NPPImage_8uC3(cropW, cropH);
using var gpuSharpened = new NPPImage_8uC3(cropW, cropH);

foreach (string imagePath in images)
{
    // Reuse pre-allocated buffers for each image
    using var gpuSrc = new NPPImage_8uC3(src.Width, src.Height);
    gpuSrc.CopyToDevice(pixels);
    gpuSrc.Resize(gpuResized, InterpolationMode.Cubic, streamCtx);
    // etc.
}
```

---

## 15. Debugging & Diagnostic Output

### Print Available GPU Info at Startup

```csharp
int count = CudaContext.GetDeviceCount();
for (int i = 0; i < count; i++)
{
    var info = CudaContext.GetDeviceInfo(i);
    Console.WriteLine($"GPU {i}: {CudaContext.GetDeviceName(i)}");
    Console.WriteLine($"  Compute capability : {info.ComputeCapability.Major}.{info.ComputeCapability.Minor}");
    Console.WriteLine($"  Total VRAM         : {info.TotalGlobalMemory / 1024 / 1024} MB");
    Console.WriteLine($"  CUDA cores         : {info.MultiProcessorCount * 128}"); // approx for Ampere
    Console.WriteLine($"  Max threads/block  : {info.MaxThreadsPerBlock}");
}
```

### Check VRAM Usage

```csharp
ctx.SetCurrent();
ctx.GetMemInfo(out ulong free, out ulong total);
Console.WriteLine($"VRAM: {free/1024/1024} MB free / {total/1024/1024} MB total");
```

### Enable CUDA Error Checking

ManagedCuda throws `CudaException` on CUDA errors. Enable in Debug builds:

```csharp
#if DEBUG
CudaContext.EnablePeerAccess = true; // for multi-GPU debugging
#endif
```

### Verify NPP DLL Loading

```csharp
bool nppisu = NativeLibrary.TryLoad("nppisu64_13", out IntPtr h1);
bool nppig  = NativeLibrary.TryLoad("nppig64_13",  out IntPtr h2);
bool nppif  = NativeLibrary.TryLoad("nppif64_13",  out IntPtr h3);
Console.WriteLine($"NPP DLLs: nppisu={nppisu}, nppig={nppig}, nppif={nppif}");
Console.WriteLine($"Handles:  0x{h1:X}, 0x{h2:X}, 0x{h3:X}");
```

---

## 16. Complete Boilerplate Template

Copy-paste this for any new CUDA image processing service:

```csharp
using System.Runtime.InteropServices;
using ManagedCuda;
using ManagedCuda.BasicTypes;
using ManagedCuda.NPP;

namespace YourNamespace
{
    internal sealed class GpuImagePipeline : IDisposable
    {
        // ── DLL requirements ──────────────────────────────────────────────
        private static readonly string[] RequiredNppDlls =
        {
            "nppisu64_13",
            "nppig64_13",
            "nppif64_13",
        };

        private const string CudaToolkitRoot =
            @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";

        // ── State ─────────────────────────────────────────────────────────
        private readonly PrimaryContext _ctx;
        private readonly NppStreamContext _streamCtx;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private bool _disposed;

        private GpuImagePipeline(PrimaryContext ctx, NppStreamContext streamCtx)
        {
            _ctx       = ctx;
            _streamCtx = streamCtx;
        }

        // ── Factory ───────────────────────────────────────────────────────
        public static GpuImagePipeline? TryCreate(int gpuIndex = 0)
        {
            try
            {
                if (!EnsureNppDllsLoaded(out string diag))
                {
                    Console.WriteLine($"  CUDA NPP : {diag}");
                    return null;
                }

                if (CudaContext.GetDeviceCount() == 0) return null;

                var ctx = new PrimaryContext(gpuIndex);
                ctx.SetCurrent();
                var streamCtx = new NppStreamContext(CUstream.NullStream);
                return new GpuImagePipeline(ctx, streamCtx);
            }
            catch { return null; }
        }

        public static string GetDeviceName(int gpuIndex = 0)
        {
            try { return CudaContext.GetDeviceName(gpuIndex); }
            catch { return "unknown GPU"; }
        }

        // ── Public API ────────────────────────────────────────────────────
        public byte[] ProcessImage(byte[] bgrPixels, int w, int h /*, ... */)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lock.Wait();
            try
            {
                _ctx.SetCurrent();
                return RunPipeline(bgrPixels, w, h);
            }
            finally { _lock.Release(); }
        }

        // ── GPU pipeline ──────────────────────────────────────────────────
        private byte[] RunPipeline(byte[] srcBgr, int w, int h)
        {
            using var gpuSrc = new NPPImage_8uC3(w, h);
            gpuSrc.CopyToDevice(srcBgr);

            // TODO: add your NPP operations here
            // using var gpuOut = new NPPImage_8uC3(outW, outH);
            // gpuSrc.Resize(gpuOut, InterpolationMode.Cubic, _streamCtx);
            // ...

            byte[] result = new byte[w * h * 3];
            gpuSrc.CopyToHost(result);
            return result;
        }

        // ── DLL discovery (copy from §6) ──────────────────────────────────
        private static bool EnsureNppDllsLoaded(out string source)
        {
            if (RequiredNppDlls.All(dll => NativeLibrary.TryLoad(dll, out _)))
            { source = "PATH"; return true; }

            if (Directory.Exists(CudaToolkitRoot))
            {
                var dirs = Directory
                    .GetDirectories(CudaToolkitRoot, "v*")
                    .OrderByDescending(d => d)
                    .Select(d => Path.Combine(d, "bin"))
                    .Where(Directory.Exists);

                foreach (string dir in dirs)
                {
                    if (RequiredNppDlls.All(dll =>
                    {
                        string p = Path.Combine(dir, dll + ".dll");
                        return File.Exists(p) && NativeLibrary.TryLoad(p, out _);
                    }))
                    { source = dir; return true; }
                }
            }

            source = Directory.Exists(CudaToolkitRoot)
                ? "Installed Toolkit lacks _13 DLLs. Upgrade to 12.2+."
                : $"CUDA Toolkit not found. Install from https://developer.nvidia.com/cuda-downloads";
            return false;
        }

        // ── IDisposable ───────────────────────────────────────────────────
        public void Dispose()
        {
            if (!_disposed)
            {
                _lock.Dispose();
                _ctx.Dispose();
                _disposed = true;
            }
        }
    }
}
```

---

## 17. Common Pitfalls & Solutions

### Pitfall 1: `DllNotFoundException` escapes `try/catch`

**Symptom:** App crashes with `DllNotFoundException` even though the call is inside a try block.  
**Cause:** CLR type-initialiser fires before try block frame.  
**Fix:** Use `NativeLibrary.TryLoad` to pre-check DLLs (see §6).

---

### Pitfall 2: `CudaException: CUDA_ERROR_INVALID_CONTEXT`

**Symptom:** First GPU op after `await` throws.  
**Cause:** Thread-pool resumed on a different thread; CUDA context not current.  
**Fix:** Always call `ctx.SetCurrent()` inside `Task.Run` before GPU ops (see §11).

---

### Pitfall 3: `NPPImage_8uC3` dimensions wrong for `FilterBorder`

**Symptom:** `CudaException: NPP_WRONG_INTERSECTION_ROI_ERROR`.  
**Cause:** `NppiRect` ROI extends outside the image bounds.  
**Fix:** Ensure `NppiRect(0, 0, width, height)` matches the image dimensions exactly.

---

### Pitfall 4: VRAM leak in long-running service

**Symptom:** VRAM usage grows until OOM crash.  
**Cause:** NPP images created without `using` inside a loop.  
**Fix:** Always `using var gpuImg = new NPPImage_8uC3(...)`.

---

### Pitfall 5: `ObjectDisposedException` on `PrimaryContext`

**Symptom:** Crash on second `TryCreate` call or after program restart in same session.  
**Cause:** `PrimaryContext.Dispose()` permanently destroys the primary context — it cannot be recreated.  
**Fix:** Keep `PrimaryContext` alive for the application lifetime; only dispose on exit.

---

### Pitfall 6: Wrong DLL suffix version

**Symptom:** `DllNotFoundException: nppisu64_13` but you have CUDA 12.1 installed.  
**Cause:** CUDA 12.0/12.1 ships `_12` DLLs; NPP API 13 requires 12.2+.  
**Fix:** Upgrade to CUDA Toolkit 12.2 or later.

---

### Pitfall 8: `EntryPointNotFoundException` for `nppiCopy_8u_C3R` in `nppidei64_13`

**Symptom:** `EntryPointNotFoundException: Unable to find an entry point named 'nppiCopy_8u_C3R' in DLL 'nppidei64_13'.`  
**Cause:** ManagedCuda 2.12.0 has `[DllImport("nppidei64_13")]` for `nppiCopy_8u_C3R`, but starting with **CUDA Toolkit 12.2** NVIDIA moved that function to `nppig64_13` (Geometry module). The DLL *loads* successfully (no `DllNotFoundException`) but the entry point is absent.

**NPP DLL routing for CUDA 12.2+ (NPP API 13):**

| DLL | Contains |
|---|---|
| `nppidei64_13` | Data Exchange & Initialization: `nppiSet_*`, `nppiConvert_*` |
| `nppig64_13` | **Geometry**: `nppiCopy_8u_C3R`, `nppiResize_*`, `nppiRotate_*` |
| `nppif64_13` | Filtering: `nppiFilterGauss_*`, `nppiFilterBorder_*` |

**Fix:** bypass ManagedCuda's `Copy()` entirely and P/Invoke directly into the correct DLL:

```csharp
// ✅ Direct P/Invoke into nppig64_13 — bypasses ManagedCuda's wrong import
[DllImport("nppig64_13", CallingConvention = CallingConvention.Cdecl,
           EntryPoint = "nppiCopy_8u_C3R")]
private static extern int NppiCopy8uC3R(
    ulong pSrc, int nSrcStep,    // GPU pointer (CUdeviceptr → ulong) + row pitch
    ulong pDst, int nDstStep,
    NppiSize oSizeROI);

private static void CropOnGpu(NPPImage_8uC3 src, int cropX, int cropY, NPPImage_8uC3 dst)
{
    // base + (row * pitch + col * 3 bytes per BGR pixel)
    ulong srcRoiPtr = (ulong)src.DevicePointer
                    + (ulong)((long)cropY * src.Pitch + cropX * 3);

    int status = NppiCopy8uC3R(
        srcRoiPtr,         src.Pitch,
        dst.DevicePointer, dst.Pitch,
        new NppiSize(dst.Width, dst.Height));

    if (status != 0)
        throw new InvalidOperationException($"nppiCopy_8u_C3R failed: {status}");
}
```

> **Note:** pre-loading `nppig64_13` via `NativeLibrary.TryLoad(fullPath, out _)` ensures
> the DLL is in the process's module table even when it was not in PATH, so the
> `[DllImport("nppig64_13")]` P/Invoke resolves correctly.

---

### Pitfall 7: `GenerateAssemblyInfo` conflict

**Symptom:** Build error: `error CS0579: Duplicate 'System.Reflection.AssemblyVersionAttribute'`.  
**Fix:** Add to `.csproj`:
```xml
<GenerateAssemblyInfo>false</GenerateAssemblyInfo>
<GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
```

---

## 18. NPP API Quick Reference

### Image Upload / Download

```csharp
using var gpu = new NPPImage_8uC3(width, height);

// CPU → GPU
gpu.CopyToDevice(byteArray);

// GPU → CPU
byte[] result = new byte[width * height * 3];
gpu.CopyToHost(result);
```

### Resize

```csharp
using var dst = new NPPImage_8uC3(newWidth, newHeight);
src.Resize(dst, InterpolationMode.Cubic, streamCtx);
```

### Crop (Copy ROI via SetRoi)

> ⚠ **Do NOT** use `src.Copy(dst, cropX, cropY)` — that overload resolves to the
> channel-extraction signature `Copy(NPPImage_8uC1 dst, int channelSrc)` and throws
> `ArgumentOutOfRangeException` when `cropX > 2`.

```csharp
// Correct idiom: narrow the source to the desired sub-rectangle with SetRoi,
// then call the plain Copy(dst).  DevPtrRoi is adjusted automatically so only
// the ROI pixels are transferred to the (correctly-sized) destination.
using var dst = new NPPImage_8uC3(cropWidth, cropHeight);
src.SetRoi(cropX, cropY, cropWidth, cropHeight);
src.Copy(dst);
src.SetRoi(0, 0, src.Width, src.Height); // restore full-image ROI
```

### Gaussian Blur

```csharp
using var dst = new NPPImage_8uC3(w, h);
src.FilterGaussBorder(dst, MaskSize.Size_3_X_3,
    NppiBorderType.Replicate, streamCtx, new NppiRect(0, 0, w, h));
// MaskSize options: Size_3_X_3, Size_5_X_5, Size_7_X_7, Size_9_X_9, Size_11_X_11, Size_13_X_13
```

### Custom Convolution Kernel

```csharp
// Sharpening kernel (3×3 Laplacian unsharp mask)
float[] kernel = {
    -0.25f, -0.5f, -0.25f,
    -0.5f,   4.0f, -0.5f,
    -0.25f, -0.5f, -0.25f
};

using var gpuKernel = new CudaDeviceVariable<float>(9);
gpuKernel.CopyToDevice(kernel);

using var dst = new NPPImage_8uC3(w, h);
src.FilterBorder(dst, gpuKernel,
    new NppiSize(3, 3),   // kernel dimensions
    new NppiPoint(1, 1),  // kernel anchor (center = 1,1 for 3×3)
    NppiBorderType.Replicate, streamCtx, new NppiRect(0, 0, w, h));
```

### Color Conversions

```csharp
// BGR → Grayscale
using var gray = new NPPImage_8uC1(w, h);
src.BGRToGray(gray, streamCtx);

// BGR → HSV
using var hsv = new NPPImage_8uC3(w, h);
src.BGRToHSV(hsv, streamCtx);

// Apply LUT (look-up table for colour grading)
int[] lut = Enumerable.Range(0, 256).Select(i => Math.Min(255, i + 20)).ToArray();
src.LUTLinear(dst, lut, streamCtx);
```

### Threshold / Mask

```csharp
// Zero out pixels below threshold (binary mask)
using var dst = new NPPImage_8uC3(w, h);
src.Threshold(dst, threshold: 128, NPP_CMP_LESS, value: 0, streamCtx);
```

---

## 19. Frequently Asked Questions

**Q: Do I need the CUDA Toolkit if I only use realesrgan-ncnn-vulkan for SR?**  
A: No. realesrgan uses **Vulkan** (not CUDA), so the display driver alone is sufficient.
The CUDA Toolkit is only required for the `CudaNppImagePipeline` class (resize/crop/denoise/sharpen on CUDA cores).

---

**Q: Can I use CUDA NPP without a NVIDIA GPU?**  
A: No. NPP requires CUDA, which requires NVIDIA hardware. On AMD/Intel GPUs, the
CPU fallback (SkiaSharp) is used automatically.

---

**Q: What is the `_13` in `nppisu64_13.dll`?**  
A: It is the **NPP library API major version**, not the CUDA Toolkit version.
NPP API 13 was introduced with CUDA Toolkit 12.2. It is unrelated to CUDA 13.x.

---

**Q: Is ManagedCuda thread-safe?**  
A: CUDA contexts are thread-local. Calls to `ctx.SetCurrent()` bind the context to the
calling thread. Use a mutex (`SemaphoreSlim`) to ensure only one thread uses the GPU at a time.

---

**Q: Can I run CUDA kernels (.cu files) without the Toolkit?**  
A: You can run **pre-compiled** CUDA kernels (`.cubin`/`.ptx`) using `CudaKernel` from
ManagedCuda — no Toolkit needed at runtime. NVRTC (`ManagedCuda.NVRTC`) compiles `.cu`
source at runtime, which requires `nvrtc64_120_0.dll` from the Toolkit.

---

**Q: What is the difference between `CudaContext` and `PrimaryContext`?**  
A: `PrimaryContext` is the shared context used by the NVIDIA display driver. It is the
correct choice for any application that also uses NPP or other NVIDIA libraries (cuBLAS,
cuDNN, etc.) — they all share the same primary context automatically. `CudaContext` creates
a new exclusive context, which can conflict with NPP on some driver versions.

---

*Document maintained alongside `Services/CudaNppImagePipeline.cs` — update both together.*
