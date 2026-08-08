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
    MatrixSceneLayer Layer = MatrixSceneLayer.Standard);

internal enum MatrixSceneLayer
{
    Standard,
    AttackBase,
    AttackForeground
}

internal sealed record AttackFrameSnapshot(
    SharedMatrixScene PrimaryScene,
    IReadOnlyList<MatrixScenePresentation> Presentations,
    int CapturedInterfaceSamples,
    double StreamTraversalSeconds);

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
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DesktopCaptureWarmupFrames = 2;

    private static readonly FeatureLevel[] RequiredFeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0
    ];

    private static readonly Lazy<ShaderBytecodes> CompiledShaders = new(
        CompileShaders,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly SharedMatrixScene _defaultScene;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IDCompositionDevice _compositionDevice;
    private readonly IDCompositionTarget _compositionTarget;
    private readonly IDCompositionVisual _compositionVisual;
    private readonly ID3D11Texture2D _backBufferTexture;
    private readonly ID3D11RenderTargetView _renderTargetView;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11VertexShader _transitionVertexShader;
    private readonly ID3D11PixelShader _transitionPixelShader;
    private readonly ID3D11PixelShader _desktopDifferencePixelShader;
    private readonly ID3D11InputLayout _transitionInputLayout;
    private readonly ID3D11Buffer _quadBuffer;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11Buffer _transitionConstantBuffer;
    private readonly ID3D11Buffer _desktopDifferenceConstantBuffer;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11SamplerState _maskSampler;
    private readonly ID3D11BlendState _blendState;
    private readonly ID3D11BlendState _captureBlendState;
    private readonly bool _transparentSurface;
    private readonly Dictionary<SharedMatrixScene, SceneGpuResources>
        _sceneResources =
            new(ReferenceEqualityComparer.Instance);
    private float _glyphOpacity = 1;
    private float _surfaceRevealProgress = 1;
    private float _surfaceGlyphOpacity = 1;
    private float _attackBackgroundProgress;
    private float _attackBackgroundOpacity = 1;
    private float _attackModeEnabled;
    private float _attackHaloFactor = 1;
    private int _currentTargetWidth = 1;
    private int _currentTargetHeight = 1;
    private ID3D11Texture2D? _attackInterfaceTexture;
    private ID3D11ShaderResourceView? _attackInterfaceView;
    private ID3D11Texture2D? _captureReferenceTexture;
    private ID3D11ShaderResourceView? _captureReferenceView;
    private readonly List<DesktopDuplicationSource> _desktopDuplications = [];
    private string _desktopDuplicationTopology = string.Empty;
    private int _desktopCaptureWarmupCursor;
    private bool _desktopCapturePrepared;
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

        _backBufferTexture =
            _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _device.CreateRenderTargetView(
            _backBufferTexture);

        ShaderBytecodes shaderBytecodes = CompiledShaders.Value;
        ReadOnlyMemory<byte> vertexBytecode =
            shaderBytecodes.MatrixVertex;
        ReadOnlyMemory<byte> pixelBytecode =
            shaderBytecodes.MatrixPixel;

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
        ReadOnlyMemory<byte> transitionVertexBytecode =
            shaderBytecodes.TransitionVertex;
        ReadOnlyMemory<byte> transitionPixelBytecode =
            shaderBytecodes.TransitionPixel;
        _transitionVertexShader =
            _device.CreateVertexShader(transitionVertexBytecode.Span);
        _transitionPixelShader =
            _device.CreatePixelShader(transitionPixelBytecode.Span);
        _desktopDifferencePixelShader = _device.CreatePixelShader(
            shaderBytecodes.DesktopDifferencePixel.Span);
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
        _desktopDifferenceConstantBuffer =
            _device.CreateConstantBuffer<DesktopDifferenceConstants>();
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _maskSampler = _device.CreateSamplerState(
            new SamplerDescription(
                Filter.MinMagMipPoint,
                TextureAddressMode.Clamp,
                mipLODBias: 0,
                maxAnisotropy: 1,
                ComparisonFunction.Never,
                minLOD: 0,
                maxLOD: float.MaxValue));
        // The shader returns straight (non-premultiplied) RGB, but the
        // DirectComposition swap chain stores premultiplied alpha.  The
        // built-in NonPremultiplied state also multiplies the alpha channel
        // by source alpha; antialiased glyph edges would therefore punch
        // translucent holes back through an already opaque background.
        // Preserve accumulated destination alpha instead.
        _blendState = _device.CreateBlendState(
            new BlendDescription(
                Blend.SourceAlpha,
                Blend.InverseSourceAlpha,
                Blend.One,
                Blend.InverseSourceAlpha));
        _captureBlendState = _device.CreateBlendState(
            BlendDescription.Opaque);

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
        if (Unsafe.SizeOf<TransitionShaderConstants>() % 16 != 0
            || Unsafe.SizeOf<DesktopDifferenceConstants>() % 16 != 0)
        {
            throw new InvalidOperationException(
                "Служебный буфер констант D3D11 не выровнен по 16 байтам.");
        }
        _ = CompiledShaders.Value;
    }

    private static ShaderBytecodes CompileShaders() =>
        new(
            Compiler.Compile(
                ShaderSource,
                "VSMain",
                "WallpaperMatrix.Direct3D11.hlsl",
                "vs_4_0",
                ShaderFlags.OptimizationLevel3),
            Compiler.Compile(
                ShaderSource,
                "PSMain",
                "WallpaperMatrix.Direct3D11.hlsl",
                "ps_4_0",
                ShaderFlags.OptimizationLevel3),
            Compiler.Compile(
                TransitionShaderSource,
                "VSMain",
                "WallpaperMatrix.AttackTransition.hlsl",
                "vs_4_0",
                ShaderFlags.OptimizationLevel3),
            Compiler.Compile(
                TransitionShaderSource,
                "PSMain",
                "WallpaperMatrix.AttackTransition.hlsl",
                "ps_4_0",
                ShaderFlags.OptimizationLevel3),
            Compiler.Compile(
                DesktopDifferenceShaderSource,
                "PSMain",
                "WallpaperMatrix.DesktopDifference.hlsl",
                "ps_4_0",
                ShaderFlags.OptimizationLevel3));

    public void SetAttackTransitionState(AttackTransitionState state)
    {
        _attackBackgroundProgress =
            (float)Math.Clamp(state.BackgroundProgress, 0.0, 1.0);
        _attackBackgroundOpacity =
            (float)Math.Clamp(state.BackgroundOpacity, 0.0, 1.0);
        _glyphOpacity =
            (float)Math.Clamp(state.GlyphOpacity, 0.0, 1.0);
    }

    public void SetAttackInterfaceFrame(AttackInterfaceFrame? frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _context.PSSetShaderResource(
            1,
            (ID3D11ShaderResourceView)null!);
        _attackInterfaceView?.Dispose();
        _attackInterfaceTexture?.Dispose();
        _attackInterfaceView = null;
        _attackInterfaceTexture = null;
        // A failed desktop capture must degrade to a fully transparent attack
        // layer. Disabling mask clipping here would instead let the overlay
        // cover the entire desktop with an unqualified stream.
        frame ??= new AttackInterfaceFrame(
            new byte[2],
            1,
            1,
            0);
        if (frame.Width <= 0
            || frame.Height <= 0
            || frame.Samples.Length
                != checked(frame.Width * frame.Height * 2))
        {
            throw new ArgumentException(
                "Карта интерфейса АТАКИ имеет неверный размер.",
                nameof(frame));
        }

        Texture2DDescription description = new(
            Format.R8G8_UNorm,
            (uint)frame.Width,
            (uint)frame.Height,
            arraySize: 1,
            mipLevels: 1,
            BindFlags.ShaderResource,
            ResourceUsage.Immutable);
        GCHandle samples = GCHandle.Alloc(
            frame.Samples,
            GCHandleType.Pinned);
        try
        {
            uint rowPitch = checked((uint)(frame.Width * 2));
            SubresourceData initialData = new(
                samples.AddrOfPinnedObject(),
                rowPitch,
                checked(rowPitch * (uint)frame.Height));
            _attackInterfaceTexture = _device.CreateTexture2D(
                description,
                initialData);
            _attackInterfaceView = _device.CreateShaderResourceView(
                _attackInterfaceTexture);
        }
        finally
        {
            samples.Free();
        }
    }

    public void SetSurfaceReveal(
        double revealProgress,
        double glyphOpacity)
    {
        _surfaceRevealProgress =
            (float)Math.Clamp(revealProgress, 0.0, 1.0);
        _surfaceGlyphOpacity =
            (float)Math.Clamp(glyphOpacity, 0.0, 1.0);
    }

    public void SetAttackGlyphState(double haloFactor)
    {
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
        IReadOnlyList<MatrixScenePresentation> presentations) =>
        PresentCore(
            targetWidth,
            targetHeight,
            presentations,
            captureBounds: null,
            captureSampleScale: 0,
            out _);

    public AttackInterfaceFrame PresentAndCapture(
        int targetWidth,
        int targetHeight,
        IReadOnlyList<MatrixScenePresentation> presentations,
        DrawingRectangle virtualBounds,
        int sampleScale)
    {
        try
        {
            if (!PresentCore(
                    targetWidth,
                    targetHeight,
                    presentations,
                    virtualBounds,
                    Math.Max(1, sampleScale),
                    out AttackInterfaceFrame? frame)
                || frame is null)
            {
                throw new InvalidOperationException(
                    "Direct3D 11 не вернул визуальную карту интерфейса.");
            }
            return frame;
        }
        finally
        {
            // The duplication session is only needed while Attack starts.
            // Releasing it avoids a permanent capture observer, its textures,
            // and any interaction with protected video surfaces.
            DisposeDesktopDuplications();
            _desktopDuplicationTopology = string.Empty;
            _captureReferenceView?.Dispose();
            _captureReferenceTexture?.Dispose();
            _captureReferenceView = null;
            _captureReferenceTexture = null;
        }
    }

    /// <summary>
    /// Warms DXGI Desktop Duplication without waiting for a frame. A newly
    /// created duplication may initially return a surface from before the
    /// wallpaper reached DWM. Two discarded presentation cycles establish a
    /// steady reference before the real interface capture is accepted.
    /// </summary>
    public bool PrepareAttackInterfaceCapture(
        DrawingRectangle virtualBounds,
        int targetWidth,
        int targetHeight)
    {
        if (_disposed || targetWidth <= 0 || targetHeight <= 0)
            return false;
        if (_desktopCapturePrepared)
            return true;

        EnsureDesktopDuplications(virtualBounds);
        EnsureCaptureReference(targetWidth, targetHeight);
        if (_desktopDuplications.Count == 0)
            return false;

        int sourceCount = _desktopDuplications.Count;
        for (int offset = 0; offset < sourceCount; offset++)
        {
            int sourceIndex =
                (_desktopCaptureWarmupCursor + offset) % sourceCount;
            DesktopDuplicationSource source =
                _desktopDuplications[sourceIndex];
            if (source.WarmupFrames >= DesktopCaptureWarmupFrames)
                continue;

            _desktopCaptureWarmupCursor =
                (sourceIndex + 1) % sourceCount;
            Result acquireResult = source.Duplication.AcquireNextFrame(
                0,
                out _,
                out IDXGIResource desktopResource);
            if (acquireResult.Failure)
            {
                if (acquireResult.Code == DxgiErrorAccessLost)
                {
                    DisposeDesktopDuplications();
                    _desktopDuplicationTopology = string.Empty;
                }
                else if (acquireResult.Code != DxgiErrorWaitTimeout)
                {
                    DiagnosticLog.Write(
                        "DXGI не подготовил кадр захвата интерфейса: "
                        + $"device={source.DeviceName}; "
                        + $"HRESULT=0x{acquireResult.Code:X8}.");
                }
                return false;
            }

            try
            {
                using (desktopResource)
                using (ID3D11Texture2D desktopTexture =
                       desktopResource.QueryInterface<ID3D11Texture2D>())
                {
                    // Also create the reusable GPU copy before the visible
                    // transition, keeping its allocation off the Attack path.
                    source.CopyDesktopFrame(
                        _device,
                        _context,
                        desktopTexture);
                }
                source.WarmupFrames++;
            }
            finally
            {
                source.Duplication.ReleaseFrame();
            }
            break;
        }

        if (_desktopDuplications.All(source =>
                source.WarmupFrames >= DesktopCaptureWarmupFrames))
        {
            _desktopCapturePrepared = true;
            DiagnosticLog.Write(
                "DXGI-захват интерфейса прогрет неблокирующими кадрами DWM: "
                + $"выходов={_desktopDuplications.Count}; "
                + $"кадров={DesktopCaptureWarmupFrames}.");
        }

        // Complete a pending request on the next render iteration. Thus one
        // ordinary Present follows the final discarded warm-up frame.
        return false;
    }

    private bool PresentCore(
        int targetWidth,
        int targetHeight,
        IReadOnlyList<MatrixScenePresentation> presentations,
        DrawingRectangle? captureBounds,
        int captureSampleScale,
        out AttackInterfaceFrame? capturedFrame)
    {
        capturedFrame = null;
        if (_disposed || targetWidth <= 0 || targetHeight <= 0)
            return false;

        bool captureDesktop = captureBounds.HasValue
            && captureSampleScale > 0;
        if (captureDesktop)
        {
            EnsureDesktopDuplications(captureBounds!.Value);
            EnsureCaptureReference(targetWidth, targetHeight);
        }

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
        if (captureDesktop)
        {
            _context.CopyResource(
                _captureReferenceTexture!,
                _backBufferTexture);
        }
        Result presentResult = _swapChain.Present(0, PresentFlags.None);
        if (presentResult.Failure)
        {
            throw new InvalidOperationException(
                $"Direct3D 11 не передал кадр композитору; HRESULT={presentResult.Code:X8}.");
        }
        if (captureDesktop)
        {
            capturedFrame = CaptureDesktopDifferences(
                captureBounds!.Value,
                targetWidth,
                targetHeight,
                captureSampleScale);
        }
        ReportSlowPresent(
            frameStartedAt,
            uploadFinishedAt,
            Stopwatch.GetTimestamp());
        return true;
    }

    private void EnsureCaptureReference(int width, int height)
    {
        if (_captureReferenceTexture is not null)
        {
            Texture2DDescription existing =
                _captureReferenceTexture.Description;
            if (existing.Width == (uint)width
                && existing.Height == (uint)height)
            {
                return;
            }
        }

        _captureReferenceView?.Dispose();
        _captureReferenceTexture?.Dispose();
        _captureReferenceTexture = _device.CreateTexture2D(
            new Texture2DDescription(
                Format.B8G8R8A8_UNorm,
                (uint)width,
                (uint)height,
                arraySize: 1,
                mipLevels: 1,
                BindFlags.ShaderResource,
                ResourceUsage.Default));
        _captureReferenceView = _device.CreateShaderResourceView(
            _captureReferenceTexture);
    }

    private void EnsureDesktopDuplications(DrawingRectangle virtualBounds)
    {
        string topology = string.Join(
            ";",
            System.Windows.Forms.Screen.AllScreens
                .OrderBy(screen => screen.DeviceName)
                .Select(screen =>
                    $"{screen.DeviceName}:{screen.Bounds.Left},"
                    + $"{screen.Bounds.Top},{screen.Bounds.Width},"
                    + $"{screen.Bounds.Height}"));
        topology = $"{virtualBounds.Left},{virtualBounds.Top},"
            + $"{virtualBounds.Width},{virtualBounds.Height}|{topology}";
        if (_desktopDuplications.Count > 0
            && string.Equals(
                topology,
                _desktopDuplicationTopology,
                StringComparison.Ordinal))
        {
            return;
        }

        DisposeDesktopDuplications();
        _desktopDuplicationTopology = topology;
        _desktopCaptureWarmupCursor = 0;
        _desktopCapturePrepared = false;
        HashSet<string> capturedDevices = new(
            StringComparer.OrdinalIgnoreCase);
        using IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        string adapterName = adapter.Description.Description;
        for (uint outputIndex = 0; ; outputIndex++)
        try
        {
            Result outputResult = adapter.EnumOutputs(
                outputIndex,
                out IDXGIOutput output);
            if (outputResult.Failure)
                break;
            using (output)
            {
                OutputDescription description = output.Description;
                if (!description.AttachedToDesktop)
                    continue;
                using IDXGIOutput1 output1 =
                    output.QueryInterface<IDXGIOutput1>();
                IDXGIOutputDuplication duplication =
                    output1.DuplicateOutput(_device);
                DrawingRectangle desktopBounds = ToRectangle(
                    description.DesktopCoordinates);
                _desktopDuplications.Add(
                    new DesktopDuplicationSource(
                        description.DeviceName,
                        desktopBounds,
                        Convert.ToInt32(description.Rotation),
                        duplication));
                capturedDevices.Add(description.DeviceName);
                DiagnosticLog.Write(
                    "DXGI-захват интерфейса подключён: "
                    + $"device={description.DeviceName}; "
                    + $"bounds={desktopBounds.Left},{desktopBounds.Top} "
                    + $"{desktopBounds.Width}x{desktopBounds.Height}; "
                    + $"rotation={description.Rotation}; "
                    + $"adapter={adapterName}.");
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write(
                $"DXGI не создал дублирование выхода {outputIndex} "
                + $"на адаптере {adapterName}; монитор исключён из отпечатка АТАКИ.",
                exception);
        }

        foreach (System.Windows.Forms.Screen screen in
                 System.Windows.Forms.Screen.AllScreens)
        {
            if (capturedDevices.Contains(screen.DeviceName))
                continue;
            DiagnosticLog.Write(
                "DXGI не видит монитор на адаптере обоев; отпечаток "
                + "АТАКИ на нём безопасно отключён: "
                + $"device={screen.DeviceName}; "
                + $"bounds={screen.Bounds.Left},{screen.Bounds.Top} "
                + $"{screen.Bounds.Width}x{screen.Bounds.Height}; "
                + $"wallpaperAdapter={adapterName}.");
        }
    }

    private AttackInterfaceFrame CaptureDesktopDifferences(
        DrawingRectangle virtualBounds,
        int targetWidth,
        int targetHeight,
        int sampleScale)
    {
        int sampleWidth = checked(
            (targetWidth + sampleScale - 1) / sampleScale);
        int sampleHeight = checked(
            (targetHeight + sampleScale - 1) / sampleScale);
        byte[] samples = new byte[checked(sampleWidth * sampleHeight * 2)];
        int capturedOutputs = 0;
        int protectedOutputs = 0;
        bool rebuildDuplications = false;
        List<string> failures = [];
        long startedAt = Stopwatch.GetTimestamp();

        foreach (DesktopDuplicationSource source in _desktopDuplications)
        {
            Result acquireResult = source.Duplication.AcquireNextFrame(
                0,
                out OutduplFrameInfo frameInfo,
                out IDXGIResource desktopResource);
            if (acquireResult.Failure)
            {
                failures.Add(
                    $"{source.DeviceName}=0x{acquireResult.Code:X8}");
                rebuildDuplications |=
                    acquireResult.Code == DxgiErrorAccessLost;
                continue;
            }

            try
            {
                using (desktopResource)
                using (ID3D11Texture2D desktopTexture =
                       desktopResource.QueryInterface<ID3D11Texture2D>())
                {
                    source.CopyDesktopFrame(
                        _device,
                        _context,
                        desktopTexture);
                }
                if (frameInfo.ProtectedContentMaskedOut)
                    protectedOutputs++;
                RenderDesktopDifference(
                    source,
                    virtualBounds,
                    targetWidth,
                    targetHeight,
                    sampleScale,
                    sampleWidth,
                    sampleHeight,
                    samples);
                capturedOutputs++;
            }
            catch (Exception exception)
            {
                failures.Add(
                    $"{source.DeviceName}={exception.GetBaseException().Message}");
            }
            finally
            {
                source.Duplication.ReleaseFrame();
            }
        }

        if (capturedOutputs == 0)
            Array.Clear(samples);
        int influenced = 0;
        for (int index = 1; index < samples.Length; index += 2)
        {
            if (samples[index] >= 128)
                influenced++;
        }
        long finishedAt = Stopwatch.GetTimestamp();
        DiagnosticLog.Write(
            "Визуальная карта интерфейса АТАКИ подготовлена через "
            + "DXGI Desktop Duplication: "
            + $"{sampleWidth}x{sampleHeight}; "
            + $"выходов={capturedOutputs}/{_desktopDuplications.Count}; "
            + $"маска={influenced * 100.0 / Math.Max(1, sampleWidth * sampleHeight):0.##}%; "
            + $"protected={protectedOutputs}; "
            + $"время={Stopwatch.GetElapsedTime(startedAt, finishedAt).TotalMilliseconds:0} мс"
            + (failures.Count == 0
                ? "."
                : $"; пропуски={string.Join(", ", failures)}."));
        if (rebuildDuplications)
        {
            DisposeDesktopDuplications();
            _desktopDuplicationTopology = string.Empty;
            DiagnosticLog.Write(
                "DXGI Desktop Duplication потерял доступ к рабочему столу; "
                + "контур будет создан заново при следующей АТАКЕ.");
        }
        return new AttackInterfaceFrame(
            samples,
            sampleWidth,
            sampleHeight,
            influenced);
    }

    private unsafe void RenderDesktopDifference(
        DesktopDuplicationSource source,
        DrawingRectangle virtualBounds,
        int targetWidth,
        int targetHeight,
        int sampleScale,
        int globalSampleWidth,
        int globalSampleHeight,
        byte[] destination)
    {
        DrawingRectangle monitorOnSurface = new(
            source.DesktopBounds.Left - virtualBounds.Left,
            source.DesktopBounds.Top - virtualBounds.Top,
            source.DesktopBounds.Width,
            source.DesktopBounds.Height);
        DrawingRectangle clipped = DrawingRectangle.Intersect(
            monitorOnSurface,
            new DrawingRectangle(0, 0, targetWidth, targetHeight));
        if (clipped.Width <= 0 || clipped.Height <= 0)
            return;

        int sampleLeft = Math.Clamp(
            clipped.Left / sampleScale,
            0,
            globalSampleWidth);
        int sampleTop = Math.Clamp(
            clipped.Top / sampleScale,
            0,
            globalSampleHeight);
        int sampleRight = Math.Clamp(
            (clipped.Right + sampleScale - 1) / sampleScale,
            sampleLeft,
            globalSampleWidth);
        int sampleBottom = Math.Clamp(
            (clipped.Bottom + sampleScale - 1) / sampleScale,
            sampleTop,
            globalSampleHeight);
        int localWidth = sampleRight - sampleLeft;
        int localHeight = sampleBottom - sampleTop;
        if (localWidth <= 0 || localHeight <= 0)
            return;

        CaptureGpuResources resources = source.EnsureReductionResources(
            _device,
            localWidth,
            localHeight);
        Texture2DDescription desktopDescription =
            source.DesktopTexture!.Description;
        DesktopDifferenceConstants constants = new(
            targetWidth,
            targetHeight,
            desktopDescription.Width,
            desktopDescription.Height,
            monitorOnSurface.Left,
            monitorOnSurface.Top,
            monitorOnSurface.Width,
            monitorOnSurface.Height,
            sampleLeft,
            sampleTop,
            sampleScale,
            source.Rotation);
        UpdateDesktopDifferenceConstantBuffer(constants);

        _context.OMSetRenderTargets(resources.RenderTargetView);
        _context.OMSetBlendState(_captureBlendState);
        _context.RSSetViewport(
            new Viewport(0, 0, localWidth, localHeight, 0, 1));
        _context.IASetInputLayout(_transitionInputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.IASetVertexBuffer(0, _quadBuffer, QuadStride);
        _context.VSSetShader(_transitionVertexShader);
        _context.PSSetShader(_desktopDifferencePixelShader);
        _context.PSSetConstantBuffer(
            0,
            _desktopDifferenceConstantBuffer);
        _context.PSSetShaderResource(0, source.DesktopView!);
        _context.PSSetShaderResource(1, _captureReferenceView!);
        _context.Draw(4, 0);
        _context.PSSetShaderResource(
            0,
            (ID3D11ShaderResourceView)null!);
        _context.PSSetShaderResource(
            1,
            (ID3D11ShaderResourceView)null!);
        _context.CopyResource(
            resources.StagingTexture,
            resources.RenderTexture);

        MappedSubresource mapped = _context.Map(
            resources.StagingTexture,
            0,
            MapMode.Read,
            Vortice.Direct3D11.MapFlags.None);
        int localInfluenced = 0;
        try
        {
            byte* sourceBytes = (byte*)mapped.DataPointer;
            for (int row = 0; row < localHeight; row++)
            {
                int globalRow = sampleTop + row;
                byte* rowSource = sourceBytes + row * mapped.RowPitch;
                for (int column = 0; column < localWidth; column++)
                {
                    int globalColumn = sampleLeft + column;
                    int localIndex = column * 2;
                    int globalIndex =
                        (globalRow * globalSampleWidth + globalColumn) * 2;
                    byte mask = rowSource[localIndex + 1];
                    if (mask >= 128)
                        localInfluenced++;
                    if (mask <= destination[globalIndex + 1])
                        continue;
                    destination[globalIndex] = rowSource[localIndex];
                    destination[globalIndex + 1] = mask;
                }
            }
        }
        finally
        {
            _context.Unmap(resources.StagingTexture, 0);
        }
        DiagnosticLog.Write(
            "DXGI-карта выхода подготовлена: "
            + $"device={source.DeviceName}; "
            + $"rotation={source.Rotation}; "
            + $"samples={localWidth}x{localHeight}; "
            + $"mask={localInfluenced * 100.0 / Math.Max(1, localWidth * localHeight):0.##}%.");
    }

    private void UpdateDesktopDifferenceConstantBuffer(
        DesktopDifferenceConstants constants)
    {
        MappedSubresource mapped = _context.Map(
            _desktopDifferenceConstantBuffer,
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
            _context.Unmap(_desktopDifferenceConstantBuffer, 0);
        }
    }

    private static DrawingRectangle ToRectangle(Vortice.RawRect rectangle) =>
        new(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right - rectangle.Left,
            rectangle.Bottom - rectangle.Top);

    private void DisposeDesktopDuplications()
    {
        foreach (DesktopDuplicationSource source in _desktopDuplications)
            source.Dispose();
        _desktopDuplications.Clear();
        _desktopCaptureWarmupCursor = 0;
        _desktopCapturePrepared = false;
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
        _currentTargetWidth = Math.Max(1, targetWidth);
        _currentTargetHeight = Math.Max(1, targetHeight);
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

            bool attackPresentation = _attackModeEnabled > 0.5f;
            bool baseLayer = attackPresentation
                && state.Presentation.Layer == MatrixSceneLayer.AttackBase;
            bool foregroundLayer = attackPresentation
                && state.Presentation.Layer == MatrixSceneLayer.AttackForeground;
            if (baseLayer)
            {
                // The overlay owns a complete, opaque copy of the already
                // running wallpaper scene. It reuses the same SharedMatrixScene
                // and therefore creates neither a second simulation nor a
                // second stream generator. Windows below the overlay can no
                // longer leak through transparent attack cells.
                DrawBackground(
                    state.Presentation.TargetBounds,
                    state.Parameters,
                    _glyphOpacity,
                    _attackBackgroundProgress,
                    useInterfaceMask: false);
            }
            else if (foregroundLayer)
            {
                // The front of the veil moves from the top edge to the
                // bottom. At progress 0 every pixel is transparent; at 1 the
                // complete virtual desktop has reached the attack colour.
                DrawBackground(
                    state.Presentation.TargetBounds,
                    state.Parameters,
                    _attackBackgroundOpacity,
                    _attackBackgroundProgress,
                    useInterfaceMask: true);
            }
            else
            {
                DrawBackground(
                    state.Presentation.TargetBounds,
                    state.Parameters,
                    opacity: 1,
                    topDownProgress: _surfaceRevealProgress);
            }
            // While the real wallpaper is still visible below our
            // alpha-capable startup surface, keep glyph bodies honest and
            // suppress translucent halos. Antialiasing and phosphor return
            // only during the final opaque quarter of the background reveal,
            // so desktop colours cannot tint portrait or scaled viewports.
            float startupGlyphBlend = Math.Clamp(
                (_surfaceRevealProgress - 0.75f) * 4.0f,
                0.0f,
                1.0f);
            if (baseLayer)
            {
                DrawGlyphPass(
                    state,
                    solidBody: 0,
                    haloFactor: 1,
                    useInterfaceMask: false,
                    topDownRevealProgress: _attackBackgroundProgress);
            }
            else if (foregroundLayer)
            {
                // Only streams born after the attack boundary are allowed on
                // the foreground surface. The original wallpaper remains the
                // sole owner of every pre-existing stream and image cell.
                // Their glyphs are never clipped by the background timeline:
                // the simulation itself is the only reveal mask for a stream.
                float backgroundProgress = _attackBackgroundProgress;
                float solidBody = 1.0f - Math.Clamp(
                    (backgroundProgress - 0.76f) / 0.24f,
                    0.0f,
                    1.0f);
                solidBody = solidBody * solidBody
                    * (3.0f - 2.0f * solidBody);
                DrawGlyphPass(
                    state,
                    solidBody: solidBody,
                    haloFactor: _attackHaloFactor,
                    useInterfaceMask: true,
                    topDownRevealProgress: -1);
            }
            else
            {
                DrawGlyphPass(
                    state,
                    solidBody: 1.0f - startupGlyphBlend,
                    haloFactor: startupGlyphBlend,
                    useInterfaceMask: false,
                    topDownRevealProgress: -1);
            }
        }
    }

    private void DrawGlyphPass(
        SceneDrawState state,
        float solidBody,
        float haloFactor,
        bool useInterfaceMask,
        float topDownRevealProgress)
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
        _context.PSSetShaderResource(
            1,
            _attackInterfaceView
                ?? (ID3D11ShaderResourceView)null!);
        _context.PSSetSampler(0, _sampler);
        _context.PSSetSampler(1, _maskSampler);
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
            _glyphOpacity
                * _surfaceGlyphOpacity,
            solidBody,
            haloFactor,
            source.Left,
            source.Top,
            source.Width,
            source.Height,
            viewport.Left,
            viewport.Top,
            viewport.Width,
            viewport.Height,
            _currentTargetWidth,
            _currentTargetHeight,
            useInterfaceMask && _attackInterfaceView is not null,
            topDownRevealProgress);
        UpdateConstantBuffer(constants);
        _context.DrawInstanced(
            4,
            (uint)state.InstanceCount,
            0,
            0);

        _context.PSSetShaderResource(
            0,
            (ID3D11ShaderResourceView)null!);
        _context.PSSetShaderResource(
            1,
            (ID3D11ShaderResourceView)null!);
    }

    private void DrawBackground(
        DrawingRectangle target,
        MatrixRenderParameters parameters,
        float opacity,
        float topDownProgress = -1,
        bool useInterfaceMask = false)
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
        _context.PSSetShaderResource(
            1,
            _attackInterfaceView
                ?? (ID3D11ShaderResourceView)null!);
        _context.PSSetSampler(1, _maskSampler);
        UpdateTransitionConstantBuffer(
            new TransitionShaderConstants(
                parameters,
                opacity,
                topDownProgress,
                _currentTargetWidth,
                _currentTargetHeight,
                target,
                useInterfaceMask && _attackInterfaceView is not null));
        _context.Draw(4, 0);
        _context.PSSetShaderResource(
            1,
            (ID3D11ShaderResourceView)null!);
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
        DisposeDesktopDuplications();
        _captureReferenceView?.Dispose();
        _captureReferenceTexture?.Dispose();
        _captureReferenceView = null;
        _captureReferenceTexture = null;
        _attackInterfaceView?.Dispose();
        _attackInterfaceTexture?.Dispose();
        _captureBlendState.Dispose();
        _blendState.Dispose();
        _maskSampler.Dispose();
        _sampler.Dispose();
        _desktopDifferenceConstantBuffer.Dispose();
        _transitionConstantBuffer.Dispose();
        _constantBuffer.Dispose();
        _quadBuffer.Dispose();
        _transitionInputLayout.Dispose();
        _desktopDifferencePixelShader.Dispose();
        _transitionPixelShader.Dispose();
        _transitionVertexShader.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        _renderTargetView.Dispose();
        _backBufferTexture.Dispose();
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

    private sealed class CaptureGpuResources : IDisposable
    {
        public int Width { get; }
        public int Height { get; }
        public ID3D11Texture2D RenderTexture { get; }
        public ID3D11RenderTargetView RenderTargetView { get; }
        public ID3D11Texture2D StagingTexture { get; }

        public CaptureGpuResources(
            int width,
            int height,
            ID3D11Texture2D renderTexture,
            ID3D11RenderTargetView renderTargetView,
            ID3D11Texture2D stagingTexture)
        {
            Width = width;
            Height = height;
            RenderTexture = renderTexture;
            RenderTargetView = renderTargetView;
            StagingTexture = stagingTexture;
        }

        public void Dispose()
        {
            RenderTargetView.Dispose();
            StagingTexture.Dispose();
            RenderTexture.Dispose();
        }
    }

    private sealed class DesktopDuplicationSource : IDisposable
    {
        public string DeviceName { get; }
        public DrawingRectangle DesktopBounds { get; }
        public int Rotation { get; }
        public IDXGIOutputDuplication Duplication { get; }
        public ID3D11Texture2D? DesktopTexture { get; private set; }
        public ID3D11ShaderResourceView? DesktopView { get; private set; }
        public int WarmupFrames { get; set; }
        private CaptureGpuResources? _reductionResources;

        public DesktopDuplicationSource(
            string deviceName,
            DrawingRectangle desktopBounds,
            int rotation,
            IDXGIOutputDuplication duplication)
        {
            DeviceName = deviceName;
            DesktopBounds = desktopBounds;
            Rotation = rotation;
            Duplication = duplication;
        }

        public void CopyDesktopFrame(
            ID3D11Device device,
            ID3D11DeviceContext context,
            ID3D11Texture2D source)
        {
            Texture2DDescription sourceDescription = source.Description;
            if (DesktopTexture is null
                || DesktopTexture.Description.Width
                    != sourceDescription.Width
                || DesktopTexture.Description.Height
                    != sourceDescription.Height
                || DesktopTexture.Description.Format
                    != sourceDescription.Format)
            {
                DesktopView?.Dispose();
                DesktopTexture?.Dispose();
                DesktopTexture = device.CreateTexture2D(
                    new Texture2DDescription(
                        sourceDescription.Format,
                        sourceDescription.Width,
                        sourceDescription.Height,
                        arraySize: 1,
                        mipLevels: 1,
                        BindFlags.ShaderResource,
                        ResourceUsage.Default));
                DesktopView = device.CreateShaderResourceView(
                    DesktopTexture);
            }
            context.CopyResource(DesktopTexture, source);
        }

        public CaptureGpuResources EnsureReductionResources(
            ID3D11Device device,
            int width,
            int height)
        {
            if (_reductionResources is not null
                && _reductionResources.Width == width
                && _reductionResources.Height == height)
            {
                return _reductionResources;
            }

            _reductionResources?.Dispose();
            ID3D11Texture2D? renderTexture = null;
            ID3D11RenderTargetView? renderTargetView = null;
            ID3D11Texture2D? stagingTexture = null;
            try
            {
                renderTexture = device.CreateTexture2D(
                    new Texture2DDescription(
                        Format.R8G8_UNorm,
                        (uint)width,
                        (uint)height,
                        arraySize: 1,
                        mipLevels: 1,
                        BindFlags.RenderTarget,
                        ResourceUsage.Default));
                renderTargetView =
                    device.CreateRenderTargetView(renderTexture);
                stagingTexture = device.CreateTexture2D(
                    new Texture2DDescription(
                        Format.R8G8_UNorm,
                        (uint)width,
                        (uint)height,
                        arraySize: 1,
                        mipLevels: 1,
                        BindFlags.None,
                        ResourceUsage.Staging,
                        CpuAccessFlags.Read));
                _reductionResources = new CaptureGpuResources(
                    width,
                    height,
                    renderTexture,
                    renderTargetView,
                    stagingTexture);
                return _reductionResources;
            }
            catch
            {
                stagingTexture?.Dispose();
                renderTargetView?.Dispose();
                renderTexture?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _reductionResources?.Dispose();
            _reductionResources = null;
            DesktopView?.Dispose();
            DesktopTexture?.Dispose();
            DesktopView = null;
            DesktopTexture = null;
            Duplication.Dispose();
        }
    }

    private sealed record SceneDrawState(
        MatrixScenePresentation Presentation,
        SceneGpuResources Resources,
        MatrixRenderParameters Parameters,
        GlyphAtlasData Atlas,
        int InstanceCount);

    private readonly record struct ShaderBytecodes(
        ReadOnlyMemory<byte> MatrixVertex,
        ReadOnlyMemory<byte> MatrixPixel,
        ReadOnlyMemory<byte> TransitionVertex,
        ReadOnlyMemory<byte> TransitionPixel,
        ReadOnlyMemory<byte> DesktopDifferencePixel);

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
        public readonly float SolidBody;
        public readonly float HaloFactor;
        private readonly Vector3 _padding0;
        public readonly Vector3 SignalColor;
        private readonly float _padding1;
        public readonly Vector3 BackgroundColor;
        private readonly float _padding2;
        public readonly Vector2 TargetSurfaceSize;
        public readonly Vector2 TargetViewportOrigin;
        public readonly Vector2 TargetViewportSize;
        public readonly float InterfaceMaskEnabled;
        public readonly float TopDownRevealProgress;

        public ShaderConstants(
            MatrixRenderParameters parameters,
            int glyphCount,
            float aspectScaleX,
            float aspectScaleY,
            float glyphOpacity,
            float solidBody,
            float haloFactor,
            float sourceLeft,
            float sourceTop,
            float sourceWidth,
            float sourceHeight,
            float viewportLeft,
            float viewportTop,
            float viewportWidth,
            float viewportHeight,
            float targetWidth,
            float targetHeight,
            bool interfaceMaskEnabled,
            float topDownRevealProgress)
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
            SolidBody = solidBody;
            HaloFactor = haloFactor;
            _padding0 = Vector3.Zero;
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
            TargetSurfaceSize = new(targetWidth, targetHeight);
            TargetViewportOrigin = new(viewportLeft, viewportTop);
            TargetViewportSize = new(viewportWidth, viewportHeight);
            InterfaceMaskEnabled = interfaceMaskEnabled ? 1 : 0;
            TopDownRevealProgress = topDownRevealProgress;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct DesktopDifferenceConstants
    {
        public readonly Vector2 ReferenceSize;
        public readonly Vector2 DesktopSize;
        public readonly Vector2 MonitorOrigin;
        public readonly Vector2 MonitorSize;
        public readonly Vector2 SampleOrigin;
        public readonly float SampleScale;
        public readonly float Rotation;

        public DesktopDifferenceConstants(
            int referenceWidth,
            int referenceHeight,
            uint desktopWidth,
            uint desktopHeight,
            int monitorLeft,
            int monitorTop,
            int monitorWidth,
            int monitorHeight,
            int sampleLeft,
            int sampleTop,
            int sampleScale,
            int rotation)
        {
            ReferenceSize = new(referenceWidth, referenceHeight);
            DesktopSize = new(desktopWidth, desktopHeight);
            MonitorOrigin = new(monitorLeft, monitorTop);
            MonitorSize = new(monitorWidth, monitorHeight);
            SampleOrigin = new(sampleLeft, sampleTop);
            SampleScale = sampleScale;
            Rotation = rotation;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct TransitionShaderConstants
    {
        public readonly Vector3 BackgroundColor;
        public readonly float DesktopOpacity;
        public readonly float GradientProgress;
        public readonly float GradientEnabled;
        public readonly float InterfaceMaskEnabled;
        private readonly float _padding0;
        public readonly Vector2 TargetSurfaceSize;
        public readonly Vector2 TargetViewportOrigin;
        public readonly Vector2 TargetViewportSize;
        private readonly Vector2 _padding1;

        public TransitionShaderConstants(
            MatrixRenderParameters parameters,
            float desktopOpacity,
            float topDownProgress,
            float targetSurfaceWidth,
            float targetSurfaceHeight,
            DrawingRectangle targetViewport,
            bool interfaceMaskEnabled)
        {
            BackgroundColor = new(
                (float)parameters.BackgroundRed,
                (float)parameters.BackgroundGreen,
                (float)parameters.BackgroundBlue);
            DesktopOpacity = desktopOpacity;
            GradientProgress = Math.Clamp(topDownProgress, 0, 1);
            GradientEnabled = topDownProgress >= 0 ? 1 : 0;
            InterfaceMaskEnabled = interfaceMaskEnabled ? 1 : 0;
            _padding0 = 0;
            TargetSurfaceSize = new(
                Math.Max(1, targetSurfaceWidth),
                Math.Max(1, targetSurfaceHeight));
            TargetViewportOrigin = new(
                targetViewport.Left,
                targetViewport.Top);
            TargetViewportSize = new(
                targetViewport.Width,
                targetViewport.Height);
            _padding1 = Vector2.Zero;
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
            float SolidBody;
            float HaloFactor;
            float3 Padding0;
            float3 SignalColor;
            float Padding1;
            float3 BackgroundColor;
            float Padding2;
            float2 TargetSurfaceSize;
            float2 TargetViewportOrigin;
            float2 TargetViewportSize;
            float InterfaceMaskEnabled;
            float TopDownRevealProgress;
        };

        Texture2D<float> Atlas : register(t0);
        Texture2D<float2> AttackInterface : register(t1);
        SamplerState AtlasSampler : register(s0);
        SamplerState InterfaceMaskSampler : register(s1);

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
            float2 surfaceSize = max(
                TargetSurfaceSize,
                float2(1.0, 1.0));
            float2 fragmentUv = saturate(input.Position.xy / surfaceSize);
            float2 captured = float2(0.0, 1.0);
            if (InterfaceMaskEnabled > 0.5)
            {
                captured = AttackInterface.Sample(
                    InterfaceMaskSampler,
                    fragmentUv);
            }
            if (InterfaceMaskEnabled > 0.5)
                clip(captured.g - 0.5);

            float reveal = 1.0;
            if (TopDownRevealProgress >= 0.0)
            {
                const float revealFeather = 0.12;
                float revealFront = lerp(
                    -revealFeather,
                    1.0 + revealFeather,
                    saturate(TopDownRevealProgress));
                reveal = 1.0 - smoothstep(
                    revealFront - revealFeather,
                    revealFront + revealFeather,
                    input.Position.y / max(TargetSurfaceSize.y, 1.0));
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
                1.0) * reveal;
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

    private const string DesktopDifferenceShaderSource = """
        cbuffer DesktopDifferenceConstants : register(b0)
        {
            float2 ReferenceSize;
            float2 DesktopSize;
            float2 MonitorOrigin;
            float2 MonitorSize;
            float2 SampleOrigin;
            float SampleScale;
            float Rotation;
        };

        Texture2D<float4> DesktopFrame : register(t0);
        Texture2D<float4> WallpaperReference : register(t1);

        struct PixelInput
        {
            float4 Position : SV_POSITION;
            float2 TexturePosition : TEXCOORD0;
        };

        float4 PSMain(PixelInput input) : SV_TARGET
        {
            int scale = max(1, (int)round(SampleScale));
            int2 tile = int2(input.Position.xy);
            int2 globalOrigin =
                (int2(SampleOrigin) + tile) * scale;
            float luminanceTotal = 0.0;
            float sampledCount = 0.0;
            float mask = 0.0;

            [loop]
            for (int offsetY = 0; offsetY < 8; offsetY++)
            {
                [loop]
                for (int offsetX = 0; offsetX < 8; offsetX++)
                {
                    if (offsetX >= scale || offsetY >= scale)
                        continue;
                    int2 globalPixel = globalOrigin
                        + int2(offsetX, offsetY);
                    int2 local = globalPixel - int2(MonitorOrigin);
                    if (local.x < 0 || local.y < 0
                        || local.x >= (int)MonitorSize.x
                        || local.y >= (int)MonitorSize.y
                        || globalPixel.x < 0 || globalPixel.y < 0
                        || globalPixel.x >= (int)ReferenceSize.x
                        || globalPixel.y >= (int)ReferenceSize.y)
                    {
                        continue;
                    }

                    int2 desktopPixel = local;
                    int rotation = (int)round(Rotation);
                    if (rotation == 2)
                    {
                        desktopPixel = int2(
                            local.y,
                            (int)DesktopSize.y - 1 - local.x);
                    }
                    else if (rotation == 3)
                    {
                        desktopPixel = int2(
                            (int)DesktopSize.x - 1 - local.x,
                            (int)DesktopSize.y - 1 - local.y);
                    }
                    else if (rotation == 4)
                    {
                        desktopPixel = int2(
                            (int)DesktopSize.x - 1 - local.y,
                            local.x);
                    }
                    if (desktopPixel.x < 0 || desktopPixel.y < 0
                        || desktopPixel.x >= (int)DesktopSize.x
                        || desktopPixel.y >= (int)DesktopSize.y)
                    {
                        continue;
                    }

                    float4 wallpaper = WallpaperReference.Load(
                        int3(globalPixel, 0));
                    if (wallpaper.a < 0.98)
                        continue;
                    float3 desktop = DesktopFrame.Load(
                        int3(desktopPixel, 0)).rgb;
                    luminanceTotal += dot(
                        desktop,
                        float3(0.2126, 0.7152, 0.0722));
                    sampledCount += 1.0;
                    float3 difference = abs(desktop - wallpaper.rgb);
                    float maximumDifference = max(
                        difference.r,
                        max(difference.g, difference.b));
                    float totalDifference =
                        difference.r + difference.g + difference.b;
                    bool changed = maximumDifference >= (14.0 / 255.0)
                        || totalDifference >= (28.0 / 255.0);
                    if (!changed)
                        continue;

                    mask = 1.0;
                }
            }

            float luminance = sampledCount > 0.0
                ? luminanceTotal / sampledCount
                : 0.0;
            return float4(luminance, mask, 0.0, 1.0);
        }
        """;

    private const string TransitionShaderSource = """
        cbuffer TransitionConstants : register(b0)
        {
            float3 BackgroundColor;
            float DesktopOpacity;
            float GradientProgress;
            float GradientEnabled;
            float InterfaceMaskEnabled;
            float Padding0;
            float2 TargetSurfaceSize;
            float2 TargetViewportOrigin;
            float2 TargetViewportSize;
            float2 Padding1;
        };

        Texture2D<float2> AttackInterface : register(t1);
        SamplerState InterfaceMaskSampler : register(s1);

        struct VertexInput
        {
            float2 Corner : CORNER;
        };

        struct PixelInput
        {
            float4 Position : SV_POSITION;
            float2 TexturePosition : TEXCOORD0;
        };

        PixelInput VSMain(VertexInput input)
        {
            PixelInput output;
            output.Position = float4(
                input.Corner.x * 2.0 - 1.0,
                1.0 - input.Corner.y * 2.0,
                0.0,
                1.0);
            output.TexturePosition = input.Corner;
            return output;
        }

        float4 PSMain(PixelInput input) : SV_TARGET
        {
            float alpha = DesktopOpacity;
            if (GradientEnabled > 0.5)
            {
                const float feather = 0.12;
                float front = lerp(
                    -feather,
                    1.0 + feather,
                    GradientProgress);
                alpha *= 1.0 - smoothstep(
                    front - feather,
                    front + feather,
                    input.Position.y / max(TargetSurfaceSize.y, 1.0));
            }
            if (InterfaceMaskEnabled > 0.5)
            {
                float2 captureUv = (
                    TargetViewportOrigin
                    + input.TexturePosition * TargetViewportSize)
                    / max(TargetSurfaceSize, float2(1.0, 1.0));
                alpha *= step(
                    0.5,
                    AttackInterface.Sample(
                        InterfaceMaskSampler,
                        saturate(captureUv)).g);
            }
            return float4(BackgroundColor, alpha);
        }
        """;
}
