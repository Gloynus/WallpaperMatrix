using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using WallpaperMatrix.Models;
using WallpaperMatrix.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WallpaperMatrix.Rendering;

internal sealed record MatrixScenePresentation(
    SharedMatrixScene Scene,
    DrawingRectangle TargetBounds,
    DrawingRectangle SourceBounds,
    long AttackStreamCutoff = -1);

internal sealed record AttackFrameSnapshot(
    SharedMatrixScene PrimaryScene,
    IReadOnlyList<MatrixScenePresentation> Presentations,
    long LatestStreamId);

/// <summary>
/// Draws the shared glyph scene with Direct3D 11 and presents it through a
/// DirectComposition swap chain. The compositor path works for both ordinary
/// WorkerW hosts and the layered desktop surface used by current Windows 11.
/// </summary>
internal sealed class Direct3D11Presenter : IDisposable
{
    private const uint InstanceStride = 32;
    private const uint QuadStride = 8;
    private const int BackBufferCount = 2;

    private static readonly FeatureLevel[] RequiredFeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0
    ];

    private readonly SharedMatrixScene _defaultScene;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IDCompositionDevice _compositionDevice;
    private readonly IDCompositionTarget _compositionTarget;
    private readonly IDCompositionVisual _compositionVisual;
    private readonly ID3D11RenderTargetView _renderTargetView;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11VertexShader _transitionVertexShader;
    private readonly ID3D11PixelShader _transitionPixelShader;
    private readonly ID3D11InputLayout _transitionInputLayout;
    private readonly ID3D11Buffer _quadBuffer;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11Buffer _transitionConstantBuffer;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11BlendState _blendState;
    private readonly bool _transparentSurface;
    private readonly Dictionary<SharedMatrixScene, SceneGpuResources>
        _sceneResources =
            new(ReferenceEqualityComparer.Instance);
    private float _desktopOpacity;
    private float _glyphOpacity = 1;
    private float _surfaceBackgroundOpacity = 1;
    private float _surfaceGlyphOpacity = 1;
    private float _attackStreamCutoff;
    private float _attackModeEnabled;
    private float _attackHaloFactor = 1;
    private long _lastSlowPresentReportTimestamp;
    private bool _disposed;

    private Direct3D11Presenter(
        IntPtr window,
        int targetWidth,
        int targetHeight,
        SharedMatrixScene scene,
        bool transparentSurface)
    {
        if (window == IntPtr.Zero)
            throw new ArgumentException("Окно вывода D3D11 не создано.", nameof(window));
        if (targetWidth <= 0 || targetHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetWidth));

        _defaultScene = scene;
        _transparentSurface = transparentSurface;

        Result deviceResult = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            RequiredFeatureLevels,
            out ID3D11Device device,
            out FeatureLevel featureLevel,
            out ID3D11DeviceContext context);
        if (deviceResult.Failure)
        {
            throw new InvalidOperationException(
                $"Видеодрайвер не создал аппаратное устройство Direct3D 11; HRESULT={deviceResult.Code:X8}.");
        }

        _device = device;
        _context = context;

        using IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

        SwapChainDescription1 swapChainDescription = new(
            (uint)targetWidth,
            (uint)targetHeight,
            Format.B8G8R8A8_UNorm,
            stereo: false,
            Usage.RenderTargetOutput,
            BackBufferCount,
            Scaling.Stretch,
            SwapEffect.FlipSequential,
            transparentSurface
                ? AlphaMode.Premultiplied
                : AlphaMode.Ignore,
            SwapChainFlags.None);
        _swapChain = factory.CreateSwapChainForComposition(
            _device,
            swapChainDescription);

        _compositionDevice =
            DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _compositionDevice.CreateTargetForHwnd(
            window,
            true,
            out _compositionTarget).CheckError();
        _compositionVisual = _compositionDevice.CreateVisual();
        _compositionVisual.SetContent(_swapChain).CheckError();
        _compositionTarget.SetRoot(_compositionVisual).CheckError();
        _compositionDevice.Commit().CheckError();

        using ID3D11Texture2D backBuffer =
            _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _device.CreateRenderTargetView(backBuffer);

        ReadOnlyMemory<byte> vertexBytecode = Compiler.Compile(
            ShaderSource,
            "VSMain",
            "WallpaperMatrix.Direct3D11.hlsl",
            "vs_4_0",
            ShaderFlags.OptimizationLevel3);
        ReadOnlyMemory<byte> pixelBytecode = Compiler.Compile(
            ShaderSource,
            "PSMain",
            "WallpaperMatrix.Direct3D11.hlsl",
            "ps_4_0",
            ShaderFlags.OptimizationLevel3);

        _vertexShader = _device.CreateVertexShader(vertexBytecode.Span);
        _pixelShader = _device.CreatePixelShader(pixelBytecode.Span);
        InputElementDescription[] inputElements =
        [
            new(
                "CORNER",
                0,
                Format.R32G32_Float,
                0,
                0,
                InputClassification.PerVertexData,
                0),
            new(
                "CELL",
                0,
                Format.R32G32B32A32_Float,
                0,
                1,
                InputClassification.PerInstanceData,
                1),
            new(
                "DETAIL",
                0,
                Format.R32G32B32A32_Float,
                16,
                1,
                InputClassification.PerInstanceData,
                1)
        ];
        _inputLayout = _device.CreateInputLayout(
            inputElements,
            vertexBytecode.Span);
        ReadOnlyMemory<byte> transitionVertexBytecode = Compiler.Compile(
            TransitionShaderSource,
            "VSMain",
            "WallpaperMatrix.AttackTransition.hlsl",
            "vs_4_0",
            ShaderFlags.OptimizationLevel3);
        ReadOnlyMemory<byte> transitionPixelBytecode = Compiler.Compile(
            TransitionShaderSource,
            "PSMain",
            "WallpaperMatrix.AttackTransition.hlsl",
            "ps_4_0",
            ShaderFlags.OptimizationLevel3);
        _transitionVertexShader =
            _device.CreateVertexShader(transitionVertexBytecode.Span);
        _transitionPixelShader =
            _device.CreatePixelShader(transitionPixelBytecode.Span);
        _transitionInputLayout = _device.CreateInputLayout(
            [
                new(
                    "CORNER",
                    0,
                    Format.R32G32_Float,
                    0,
                    0,
                    InputClassification.PerVertexData,
                    0)
            ],
            transitionVertexBytecode.Span);

        float[] quad =
        [
            0, 0,
            1, 0,
            0, 1,
            1, 1
        ];
        _quadBuffer = _device.CreateBuffer(
            quad,
            BindFlags.VertexBuffer,
            ResourceUsage.Immutable);
        _constantBuffer = _device.CreateConstantBuffer<ShaderConstants>();
        _transitionConstantBuffer =
            _device.CreateConstantBuffer<TransitionShaderConstants>();
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _blendState = _device.CreateBlendState(
            BlendDescription.NonPremultiplied);

        DiagnosticLog.Write(
            $"Direct3D 11 создан: featureLevel={FormatFeatureLevel(featureLevel)}; "
            + $"DirectComposition=True; swapChain={targetWidth}x{targetHeight}; "
            + $"buffers={BackBufferCount}; "
            + $"alpha={(transparentSurface ? "Premultiplied" : "Ignore")}.");
    }

    public static Direct3D11Presenter Create(
        IntPtr window,
        int targetWidth,
        int targetHeight,
        SharedMatrixScene scene,
        bool transparentSurface = false) =>
        new(
            window,
            targetWidth,
            targetHeight,
            scene,
            transparentSurface);

    public static void ValidateShaders()
    {
        if (Unsafe.SizeOf<GlyphInstance>() != InstanceStride)
        {
            throw new InvalidOperationException(
                $"Формат экземпляра символа имеет размер "
                + $"{Unsafe.SizeOf<GlyphInstance>()}, ожидалось "
                + $"{InstanceStride}.");
        }
        if (Unsafe.SizeOf<ShaderConstants>() % 16 != 0)
        {
            throw new InvalidOperationException(
                "Буфер констант D3D11 не выровнен по 16 байтам.");
        }
        Compiler.Compile(
            ShaderSource,
            "VSMain",
            "WallpaperMatrix.Direct3D11.hlsl",
            "vs_4_0",
            ShaderFlags.OptimizationLevel3);
        Compiler.Compile(
            ShaderSource,
            "PSMain",
            "WallpaperMatrix.Direct3D11.hlsl",
            "ps_4_0",
            ShaderFlags.OptimizationLevel3);
        Compiler.Compile(
            TransitionShaderSource,
            "VSMain",
            "WallpaperMatrix.AttackTransition.hlsl",
            "vs_4_0",
            ShaderFlags.OptimizationLevel3);
        Compiler.Compile(
            TransitionShaderSource,
            "PSMain",
            "WallpaperMatrix.AttackTransition.hlsl",
            "ps_4_0",
            ShaderFlags.OptimizationLevel3);
    }

    public void SetTransitionState(
        double desktopOpacity,
        double glyphOpacity)
    {
        _desktopOpacity = (float)Math.Clamp(desktopOpacity, 0.0, 1.0);
        _glyphOpacity = (float)Math.Clamp(glyphOpacity, 0.0, 1.0);
    }

    public void SetSurfaceReveal(
        double backgroundOpacity,
        double glyphOpacity)
    {
        _surfaceBackgroundOpacity =
            (float)Math.Clamp(backgroundOpacity, 0.0, 1.0);
        _surfaceGlyphOpacity =
            (float)Math.Clamp(glyphOpacity, 0.0, 1.0);
    }

    public void SetAttackGlyphState(
        long existingStreamCutoff,
        double haloFactor)
    {
        _attackStreamCutoff = existingStreamCutoff;
        _attackModeEnabled = 1;
        _attackHaloFactor =
            (float)Math.Clamp(haloFactor, 0.0, 1.0);
    }

    public bool Present(
        int targetWidth,
        int targetHeight,
        IReadOnlyList<DrawingRectangle> viewports) =>
        Present(
            targetWidth,
            targetHeight,
            viewports.Select(viewport => new MatrixScenePresentation(
                    _defaultScene,
                    viewport,
                    new DrawingRectangle(
                        0,
                        0,
                        _defaultScene.Width,
                        _defaultScene.Height)))
                .ToArray());

    public bool Present(
        int targetWidth,
        int targetHeight,
        IReadOnlyList<MatrixScenePresentation> presentations)
    {
        if (_disposed || targetWidth <= 0 || targetHeight <= 0)
            return false;

        long frameStartedAt = Stopwatch.GetTimestamp();
        List<SceneDrawState> drawStates = [];
        foreach (MatrixScenePresentation presentation in presentations)
        {
            SharedMatrixScene scene = presentation.Scene;
            SceneGpuResources resources = ResourcesFor(scene);
            lock (scene.SyncRoot)
            {
                UploadAtlasIfNeeded(scene, resources);
                long version = scene.Version;
                if (resources.UploadedVersion != version)
                {
                    UploadInstances(scene, resources);
                    resources.UploadedVersion = version;
                }
                drawStates.Add(new SceneDrawState(
                    presentation,
                    resources,
                    scene.Parameters,
                    scene.Atlas,
                    scene.InstanceCount));
            }
        }
        long uploadFinishedAt = Stopwatch.GetTimestamp();

        // Present can occasionally wait for the desktop compositor. Never hold
        // the shared scene lock while it does: the simulation and another
        // presentation surface must remain free to consume the next frame.
        Draw(
            targetWidth,
            targetHeight,
            drawStates);
        Result presentResult = _swapChain.Present(0, PresentFlags.None);
        if (presentResult.Failure)
        {
            throw new InvalidOperationException(
                $"Direct3D 11 не передал кадр композитору; HRESULT={presentResult.Code:X8}.");
        }
        ReportSlowPresent(
            frameStartedAt,
            uploadFinishedAt,
            Stopwatch.GetTimestamp());
        return true;
    }

    private void ReportSlowPresent(
        long frameStartedAt,
        long uploadFinishedAt,
        long frameFinishedAt)
    {
        TimeSpan total = Stopwatch.GetElapsedTime(
            frameStartedAt,
            frameFinishedAt);
        if (total < TimeSpan.FromMilliseconds(120))
            return;

        long previous = _lastSlowPresentReportTimestamp;
        if (previous != 0
            && Stopwatch.GetElapsedTime(previous, frameFinishedAt)
                < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _lastSlowPresentReportTimestamp = frameFinishedAt;
        TimeSpan upload = Stopwatch.GetElapsedTime(
            frameStartedAt,
            uploadFinishedAt);
        TimeSpan drawAndPresent = Stopwatch.GetElapsedTime(
            uploadFinishedAt,
            frameFinishedAt);
        DiagnosticLog.Write(
            $"Медленная передача кадра D3D11: "
            + $"всего={total.TotalMilliseconds:0} мс; "
            + $"снимок сцены={upload.TotalMilliseconds:0} мс; "
            + $"отрисовка/композитор={drawAndPresent.TotalMilliseconds:0} мс; "
            + $"поверхность={(_transparentSurface ? "АТАКА" : "рабочий стол")}.");
    }

    private SceneGpuResources ResourcesFor(SharedMatrixScene scene)
    {
        if (_sceneResources.TryGetValue(
            scene,
            out SceneGpuResources? resources))
        {
            return resources;
        }
        resources = new SceneGpuResources();
        _sceneResources.Add(scene, resources);
        return resources;
    }

    public void ReleaseScene(SharedMatrixScene scene)
    {
        if (!_sceneResources.Remove(
                scene,
                out SceneGpuResources? resources))
        {
            return;
        }

        resources.Dispose();
    }

    private void UploadAtlasIfNeeded(
        SharedMatrixScene scene,
        SceneGpuResources resources)
    {
        long atlasVersion = scene.AtlasVersion;
        if (resources.UploadedAtlasVersion == atlasVersion)
            return;

        GlyphAtlasData atlas = scene.Atlas;
        if (atlas.Width <= 0 || atlas.Height <= 0 || atlas.Pixels.Length == 0)
            throw new InvalidOperationException("Атлас символов пуст.");

        _context.PSSetShaderResource(
            0,
            (ID3D11ShaderResourceView)null!);
        resources.AtlasView?.Dispose();
        resources.AtlasTexture?.Dispose();

        Texture2DDescription description = new(
            Format.R8_UNorm,
            (uint)atlas.Width,
            (uint)atlas.Height,
            arraySize: 1,
            mipLevels: 1,
            BindFlags.ShaderResource,
            ResourceUsage.Immutable);
        GCHandle pixels = GCHandle.Alloc(atlas.Pixels, GCHandleType.Pinned);
        try
        {
            SubresourceData initialData = new(
                pixels.AddrOfPinnedObject(),
                (uint)atlas.Width,
                (uint)(atlas.Width * atlas.Height));
            resources.AtlasTexture = _device.CreateTexture2D(
                description,
                initialData);
            resources.AtlasView = _device.CreateShaderResourceView(
                resources.AtlasTexture);
        }
        finally
        {
            pixels.Free();
        }

        resources.UploadedAtlasVersion = atlasVersion;
    }

    private unsafe void UploadInstances(
        SharedMatrixScene scene,
        SceneGpuResources resources)
    {
        int count = scene.InstanceCount;
        if (count <= 0)
            return;

        EnsureInstanceCapacity(resources, count);
        MappedSubresource mapped = _context.Map(
            resources.InstanceBuffer!,
            0,
            MapMode.WriteDiscard,
            Vortice.Direct3D11.MapFlags.None);
        GCHandle instances = GCHandle.Alloc(
            scene.Instances,
            GCHandleType.Pinned);
        try
        {
            long byteCount = checked((long)count * InstanceStride);
            long destinationSize = checked(
                (long)resources.InstanceCapacity * InstanceStride);
            Buffer.MemoryCopy(
                instances.AddrOfPinnedObject().ToPointer(),
                mapped.DataPointer.ToPointer(),
                destinationSize,
                byteCount);
        }
        finally
        {
            instances.Free();
            _context.Unmap(resources.InstanceBuffer!, 0);
        }
    }

    private void EnsureInstanceCapacity(
        SceneGpuResources resources,
        int requiredCount)
    {
        if (resources.InstanceBuffer is not null
            && resources.InstanceCapacity >= requiredCount)
        {
            return;
        }

        resources.InstanceBuffer?.Dispose();
        resources.InstanceCapacity = Math.Max(
            requiredCount,
            Math.Max(1024, resources.InstanceCapacity * 2));
        uint byteWidth = checked(
            (uint)(resources.InstanceCapacity * InstanceStride));
        resources.InstanceBuffer = _device.CreateBuffer(
            new BufferDescription(
                byteWidth,
                BindFlags.VertexBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write));
    }

    private void Draw(
        int targetWidth,
        int targetHeight,
        IReadOnlyList<SceneDrawState> states)
    {
        _context.OMSetRenderTargets(_renderTargetView);
        _context.OMSetBlendState(_blendState);
        _context.ClearRenderTargetView(
            _renderTargetView,
            _transparentSurface
                ? new Color4(0, 0, 0, 0)
                : new Color4(0, 0, 0, 1));

        foreach (SceneDrawState state in states)
        {
            if (state.Presentation.TargetBounds.Width <= 0
                || state.Presentation.TargetBounds.Height <= 0
                || state.Resources.AtlasView is null)
            {
                continue;
            }

            if (_attackModeEnabled > 0.5f)
            {
                // The attack surface starts fully transparent. The real
                // desktop remains visible below it while this veil grows.
                DrawBackground(
                    state.Presentation.TargetBounds,
                    state.Parameters,
                    1.0f - _desktopOpacity);
            }
            else
            {
                DrawBackground(
                    state.Presentation.TargetBounds,
                    state.Parameters,
                    _surfaceBackgroundOpacity);
            }
            DrawGlyphPass(
                state,
                streamFilterMode:
                    _attackModeEnabled > 0.5f ? 1 : 0,
                solidBody:
                    _attackModeEnabled > 0.5f
                        ? _desktopOpacity
                        : 0,
                haloFactor:
                    _attackModeEnabled > 0.5f
                        ? _attackHaloFactor
                        : 1);
        }
    }

    private void DrawGlyphPass(
        SceneDrawState state,
        float streamFilterMode,
        float solidBody,
        float haloFactor)
    {
        if (state.Resources.InstanceBuffer is null
            || state.InstanceCount <= 0
            || state.Resources.AtlasView is null)
        {
            return;
        }

        _context.IASetInputLayout(_inputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.IASetVertexBuffers(
            0,
            [_quadBuffer, state.Resources.InstanceBuffer],
            [QuadStride, InstanceStride],
            [0, 0]);
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShaderResource(
            0,
            state.Resources.AtlasView);
        _context.PSSetSampler(0, _sampler);
        DrawingRectangle viewport = state.Presentation.TargetBounds;
        DrawingRectangle source = state.Presentation.SourceBounds;
        double sourceAspect = source.Width / (double)source.Height;
        double viewportAspect = viewport.Width / (double)viewport.Height;
        float aspectScaleX = viewportAspect < sourceAspect
            ? (float)(sourceAspect / viewportAspect)
            : 1;
        float aspectScaleY = viewportAspect > sourceAspect
            ? (float)(viewportAspect / sourceAspect)
            : 1;

        _context.RSSetViewport(
            new Viewport(
                viewport.Left,
                viewport.Top,
                viewport.Width,
                viewport.Height,
                0,
                1));
        ShaderConstants constants = new(
            state.Parameters,
            state.Atlas.GlyphCount,
            aspectScaleX,
            aspectScaleY,
            _glyphOpacity * _surfaceGlyphOpacity,
            _attackModeEnabled > 0.5f
                && state.Presentation.AttackStreamCutoff >= 0
                    ? state.Presentation.AttackStreamCutoff
                    : _attackStreamCutoff,
            streamFilterMode,
            solidBody,
            haloFactor,
            source.Left,
            source.Top,
            source.Width,
            source.Height);
        UpdateConstantBuffer(constants);
        _context.DrawInstanced(
            4,
            (uint)state.InstanceCount,
            0,
            0);

        _context.PSSetShaderResource(
            0,
            (ID3D11ShaderResourceView)null!);
    }

    private void DrawBackground(
        DrawingRectangle target,
        MatrixRenderParameters parameters,
        float opacity)
    {
        if (opacity <= 0.0001f
            || target.Width <= 0
            || target.Height <= 0)
        {
            return;
        }

        _context.RSSetViewport(
            new Viewport(
                target.Left,
                target.Top,
                target.Width,
                target.Height,
                0,
                1));
        _context.IASetInputLayout(_transitionInputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.IASetVertexBuffer(0, _quadBuffer, QuadStride);
        _context.VSSetShader(_transitionVertexShader);
        _context.PSSetShader(_transitionPixelShader);
        _context.PSSetConstantBuffer(0, _transitionConstantBuffer);
        UpdateTransitionConstantBuffer(
            new TransitionShaderConstants(parameters, opacity));
        _context.Draw(4, 0);
    }

    private void UpdateConstantBuffer(ShaderConstants constants)
    {
        MappedSubresource mapped = _context.Map(
            _constantBuffer,
            0,
            MapMode.WriteDiscard,
            Vortice.Direct3D11.MapFlags.None);
        try
        {
            Marshal.StructureToPtr(
                constants,
                mapped.DataPointer,
                false);
        }
        finally
        {
            _context.Unmap(_constantBuffer, 0);
        }
    }

    private void UpdateTransitionConstantBuffer(
        TransitionShaderConstants constants)
    {
        MappedSubresource mapped = _context.Map(
            _transitionConstantBuffer,
            0,
            MapMode.WriteDiscard,
            Vortice.Direct3D11.MapFlags.None);
        try
        {
            Marshal.StructureToPtr(
                constants,
                mapped.DataPointer,
                false);
        }
        finally
        {
            _context.Unmap(_transitionConstantBuffer, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _context.ClearState();
            _context.Flush();
        }
        catch
        {
            // A removed graphics device must not prevent desktop restoration.
        }

        foreach (SceneGpuResources resources in _sceneResources.Values)
            resources.Dispose();
        _sceneResources.Clear();
        _blendState.Dispose();
        _sampler.Dispose();
        _transitionConstantBuffer.Dispose();
        _constantBuffer.Dispose();
        _quadBuffer.Dispose();
        _transitionInputLayout.Dispose();
        _transitionPixelShader.Dispose();
        _transitionVertexShader.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        _renderTargetView.Dispose();
        _compositionVisual.Dispose();
        _compositionTarget.Dispose();
        _compositionDevice.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private sealed class SceneGpuResources : IDisposable
    {
        public ID3D11Buffer? InstanceBuffer;
        public ID3D11Texture2D? AtlasTexture;
        public ID3D11ShaderResourceView? AtlasView;
        public int InstanceCapacity;
        public long UploadedVersion = -1;
        public long UploadedAtlasVersion = -1;

        public void Dispose()
        {
            AtlasView?.Dispose();
            AtlasTexture?.Dispose();
            InstanceBuffer?.Dispose();
            AtlasView = null;
            AtlasTexture = null;
            InstanceBuffer = null;
        }
    }

    private sealed record SceneDrawState(
        MatrixScenePresentation Presentation,
        SceneGpuResources Resources,
        MatrixRenderParameters Parameters,
        GlyphAtlasData Atlas,
        int InstanceCount);

    private static string FormatFeatureLevel(FeatureLevel level) =>
        level switch
        {
            FeatureLevel.Level_11_1 => "11.1",
            FeatureLevel.Level_11_0 => "11.0",
            FeatureLevel.Level_10_1 => "10.1",
            FeatureLevel.Level_10_0 => "10.0",
            _ => level.ToString()
        };

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct ShaderConstants
    {
        public readonly Vector2 SourceSize;
        public readonly Vector2 CellSize;
        public readonly Vector2 SourceOrigin;
        public readonly Vector2 SourceViewportSize;
        public readonly Vector2 AspectScale;
        private readonly Vector2 _paddingTarget;
        public readonly float GlyphCount;
        public readonly float HeadBrightness;
        public readonly float GlyphOpacity;
        public readonly float StreamFilterMode;
        public readonly float AttackStreamCutoff;
        public readonly float SolidBody;
        public readonly float HaloFactor;
        private readonly float _padding0;
        public readonly Vector3 SignalColor;
        private readonly float _padding1;
        public readonly Vector3 BackgroundColor;
        private readonly float _padding2;

        public ShaderConstants(
            MatrixRenderParameters parameters,
            int glyphCount,
            float aspectScaleX,
            float aspectScaleY,
            float glyphOpacity,
            float attackStreamCutoff,
            float streamFilterMode,
            float solidBody,
            float haloFactor,
            float sourceLeft,
            float sourceTop,
            float sourceWidth,
            float sourceHeight)
        {
            SourceSize = new(
                parameters.SourceWidth,
                parameters.SourceHeight);
            CellSize = new(
                parameters.CellWidth,
                parameters.CellHeight);
            SourceOrigin = new(sourceLeft, sourceTop);
            SourceViewportSize = new(sourceWidth, sourceHeight);
            AspectScale = new(aspectScaleX, aspectScaleY);
            _paddingTarget = Vector2.Zero;
            GlyphCount = glyphCount;
            HeadBrightness = (float)parameters.HeadBrightness;
            GlyphOpacity = glyphOpacity;
            StreamFilterMode = streamFilterMode;
            AttackStreamCutoff = attackStreamCutoff;
            SolidBody = solidBody;
            HaloFactor = haloFactor;
            _padding0 = 0;
            SignalColor = new(
                (float)parameters.SignalRed,
                (float)parameters.SignalGreen,
                (float)parameters.SignalBlue);
            _padding1 = 0;
            BackgroundColor = new(
                (float)parameters.BackgroundRed,
                (float)parameters.BackgroundGreen,
                (float)parameters.BackgroundBlue);
            _padding2 = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct TransitionShaderConstants
    {
        public readonly Vector3 BackgroundColor;
        public readonly float DesktopOpacity;

        public TransitionShaderConstants(
            MatrixRenderParameters parameters,
            float desktopOpacity)
        {
            BackgroundColor = new(
                (float)parameters.BackgroundRed,
                (float)parameters.BackgroundGreen,
                (float)parameters.BackgroundBlue);
            DesktopOpacity = desktopOpacity;
        }
    }

    private const string ShaderSource = """
        cbuffer MatrixConstants : register(b0)
        {
            float2 SourceSize;
            float2 CellSize;
            float2 SourceOrigin;
            float2 SourceViewportSize;
            float2 AspectScale;
            float2 PaddingTarget;
            float GlyphCount;
            float HeadBrightness;
            float GlyphOpacity;
            float StreamFilterMode;
            float AttackStreamCutoff;
            float SolidBody;
            float HaloFactor;
            float Padding0;
            float3 SignalColor;
            float Padding1;
            float3 BackgroundColor;
            float Padding2;
        };

        Texture2D<float> Atlas : register(t0);
        SamplerState AtlasSampler : register(s0);

        struct VertexInput
        {
            float2 Corner : CORNER;
            float4 CellGlyphLevel : CELL;
            float4 Detail : DETAIL;
        };

        struct PixelInput
        {
            float4 Position : SV_POSITION;
            float2 Local : TEXCOORD0;
            nointerpolation float2 AtlasOrigin : TEXCOORD1;
            float Level : TEXCOORD2;
            float Style : TEXCOORD3;
            float Emphasis : TEXCOORD4;
            float Glow : TEXCOORD5;
            nointerpolation float StreamId : TEXCOORD6;
        };

        PixelInput VSMain(VertexInput input)
        {
            PixelInput output;
            float2 expansion = min(
                float2(0.45, 0.45),
                4.5 / CellSize * clamp(input.Detail.z, 0.0, 2.0));
            float2 localCorner =
                input.Corner * (1.0 + expansion * 2.0) - expansion;
            float2 pixel =
                (input.CellGlyphLevel.xy + localCorner) * CellSize;
            float2 localPixel = pixel - SourceOrigin;
            float2 clip = float2(
                localPixel.x / SourceViewportSize.x * 2.0 - 1.0,
                1.0 - localPixel.y / SourceViewportSize.y * 2.0);
            output.Position = float4(clip * AspectScale, 0.0, 1.0);

            float atlasStyle = 0.0;
            if (input.Detail.x > 0.5 && input.Detail.x < 1.5)
                atlasStyle = 1.0;
            else if (input.Detail.x > 1.5 && input.Detail.x < 2.5)
                atlasStyle = 2.0;
            else if (input.Detail.x > 2.5 && input.Detail.x < 3.5)
                atlasStyle = 3.0;
            else if (input.Detail.x > 4.5)
                atlasStyle = 4.0;

            output.AtlasOrigin = float2(
                input.CellGlyphLevel.z / GlyphCount,
                atlasStyle / 5.0);
            output.Local = localCorner;
            output.Level = input.CellGlyphLevel.w;
            output.Style = input.Detail.x;
            output.Emphasis = input.Detail.y;
            output.Glow = input.Detail.z;
            output.StreamId = input.Detail.w;
            return output;
        }

        float SampleGlyphAtStyle(
            PixelInput input,
            float2 localPosition,
            float atlasStyle)
        {
            if (localPosition.x < 0.0 || localPosition.x > 1.0
                || localPosition.y < 0.0 || localPosition.y > 1.0)
            {
                return 0.0;
            }

            float2 safeLocal = clamp(
                localPosition,
                float2(0.015, 0.015),
                float2(0.985, 0.985));
            float2 atlasCell = float2(1.0 / GlyphCount, 1.0 / 5.0);
            float2 origin = float2(
                input.AtlasOrigin.x,
                atlasStyle / 5.0);
            return Atlas.Sample(
                AtlasSampler,
                origin + safeLocal * atlasCell).r;
        }

        float SampleGlyph(PixelInput input, float2 localPosition)
        {
            float currentStyle =
                floor(input.AtlasOrigin.y * 5.0 + 0.5);
            float current =
                SampleGlyphAtStyle(input, localPosition, currentStyle);
            float isHead =
                1.0 - step(0.5, abs(currentStyle - 1.0));
            float normal =
                SampleGlyphAtStyle(input, localPosition, 0.0);
            return lerp(
                current,
                lerp(normal, current, input.Emphasis),
                isHead);
        }

        float4 PSMain(PixelInput input) : SV_TARGET
        {
            if (StreamFilterMode > 0.5
                && StreamFilterMode < 1.5)
            {
                clip(input.StreamId - AttackStreamCutoff - 0.5);
            }
            else if (StreamFilterMode > 1.5)
            {
                clip(AttackStreamCutoff + 0.5 - input.StreamId);
            }

            float center = SampleGlyph(input, input.Local);
            float2 nearStep = 1.35 / CellSize;
            float2 wideStep = 3.60 / CellSize;

            float nearLight = 0.0;
            nearLight += SampleGlyph(
                input,
                input.Local + float2(nearStep.x, 0.0));
            nearLight += SampleGlyph(
                input,
                input.Local - float2(nearStep.x, 0.0));
            nearLight += SampleGlyph(
                input,
                input.Local + float2(0.0, nearStep.y));
            nearLight += SampleGlyph(
                input,
                input.Local - float2(0.0, nearStep.y));
            nearLight += SampleGlyph(input, input.Local + nearStep);
            nearLight += SampleGlyph(input, input.Local - nearStep);
            nearLight += SampleGlyph(
                input,
                input.Local + float2(nearStep.x, -nearStep.y));
            nearLight += SampleGlyph(
                input,
                input.Local + float2(-nearStep.x, nearStep.y));
            nearLight *= 0.125;

            float wideLight = 0.0;
            wideLight += SampleGlyph(
                input,
                input.Local + float2(wideStep.x, 0.0));
            wideLight += SampleGlyph(
                input,
                input.Local - float2(wideStep.x, 0.0));
            wideLight += SampleGlyph(
                input,
                input.Local + float2(0.0, wideStep.y));
            wideLight += SampleGlyph(
                input,
                input.Local - float2(0.0, wideStep.y));
            wideLight *= 0.25;

            float level = input.Level;

            float isImage = step(2.5, input.Style);
            float softLight = nearLight * 0.68 + wideLight * 0.32;
            float glowAmount =
                pow(clamp(softLight, 0.0, 1.0), 0.72)
                * (1.0 - center)
                * clamp(input.Glow, 0.0, 2.0)
                * HaloFactor
                * clamp(level, 0.0, 1.0)
                * (1.0 - isImage)
                * 1.65;
            glowAmount = min(glowAmount, 1.35);
            float bodyAlpha = lerp(
                center,
                step(0.075, center),
                SolidBody);
            float alpha = clamp(
                max(bodyAlpha, glowAmount),
                0.0,
                1.0);
            clip(alpha - 0.006);

            float3 body = lerp(
                BackgroundColor,
                SignalColor,
                pow(clamp(level, 0.0, 1.0), 1.12));
            float3 headSignal = min(
                float3(1.0, 1.0, 1.0),
                lerp(SignalColor, float3(1.0, 1.0, 1.0), 0.24)
                    * 1.08);
            float3 headWhite = lerp(
                float3(1.0, 1.0, 1.0),
                SignalColor,
                0.04);
            float3 headBody = lerp(
                headSignal,
                headWhite,
                HeadBrightness);
            body = lerp(
                body,
                headBody,
                clamp(input.Emphasis, 0.0, 1.0));
            float3 glowColor = SignalColor * 0.88;
            float blendedBodyShare =
                bodyAlpha / max(bodyAlpha + glowAmount, 0.0001);
            float bodyShare = lerp(
                blendedBodyShare,
                bodyAlpha,
                SolidBody);
            return float4(
                lerp(glowColor, body, bodyShare),
                alpha * GlyphOpacity);
        }
        """;

    private const string TransitionShaderSource = """
        cbuffer TransitionConstants : register(b0)
        {
            float3 BackgroundColor;
            float DesktopOpacity;
        };

        struct VertexInput
        {
            float2 Corner : CORNER;
        };

        struct PixelInput
        {
            float4 Position : SV_POSITION;
        };

        PixelInput VSMain(VertexInput input)
        {
            PixelInput output;
            output.Position = float4(
                input.Corner.x * 2.0 - 1.0,
                1.0 - input.Corner.y * 2.0,
                0.0,
                1.0);
            return output;
        }

        float4 PSMain(PixelInput input) : SV_TARGET
        {
            return float4(BackgroundColor, DesktopOpacity);
        }
        """;
}
