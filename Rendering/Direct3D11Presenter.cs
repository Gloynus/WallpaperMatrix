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

    private readonly SharedMatrixScene _scene;
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
    private ID3D11Buffer? _instanceBuffer;
    private ID3D11Texture2D? _atlasTexture;
    private ID3D11ShaderResourceView? _atlasView;
    private ID3D11Texture2D? _transitionTexture;
    private ID3D11ShaderResourceView? _transitionView;
    private int _instanceCapacity;
    private long _uploadedVersion = -1;
    private long _uploadedAtlasVersion = -1;
    private float _desktopOpacity;
    private float _glyphOpacity = 1;
    private float _attackStreamCutoff;
    private float _attackFilterEnabled;
    private float _screenshotInfluence;
    private bool _disposed;

    private Direct3D11Presenter(
        IntPtr window,
        int targetWidth,
        int targetHeight,
        SharedMatrixScene scene)
    {
        if (window == IntPtr.Zero)
            throw new ArgumentException("Окно вывода D3D11 не создано.", nameof(window));
        if (targetWidth <= 0 || targetHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetWidth));

        _scene = scene;

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
            AlphaMode.Ignore,
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
            + $"buffers={BackBufferCount}; alpha=Ignore.");
    }

    public static Direct3D11Presenter Create(
        IntPtr window,
        int targetWidth,
        int targetHeight,
        SharedMatrixScene scene) =>
        new(window, targetWidth, targetHeight, scene);

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

    public void SetTransitionBackground(CapturedDesktopFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0
            || frame.Height <= 0
            || frame.Pixels.Length != checked(frame.Width * frame.Height * 4))
        {
            throw new ArgumentException(
                "Снимок перехода имеет неверный размер.",
                nameof(frame));
        }

        _context.PSSetShaderResource(
            0,
            (ID3D11ShaderResourceView)null!);
        _transitionView?.Dispose();
        _transitionTexture?.Dispose();

        Texture2DDescription description = new(
            Format.B8G8R8A8_UNorm,
            (uint)frame.Width,
            (uint)frame.Height,
            arraySize: 1,
            mipLevels: 1,
            BindFlags.ShaderResource,
            ResourceUsage.Immutable);
        GCHandle pixels = GCHandle.Alloc(
            frame.Pixels,
            GCHandleType.Pinned);
        try
        {
            uint rowPitch = checked((uint)(frame.Width * 4));
            SubresourceData initialData = new(
                pixels.AddrOfPinnedObject(),
                rowPitch,
                checked(rowPitch * (uint)frame.Height));
            _transitionTexture = _device.CreateTexture2D(
                description,
                initialData);
            _transitionView =
                _device.CreateShaderResourceView(_transitionTexture);
        }
        finally
        {
            pixels.Free();
        }
    }

    public void SetTransitionState(
        double desktopOpacity,
        double glyphOpacity)
    {
        _desktopOpacity = (float)Math.Clamp(desktopOpacity, 0.0, 1.0);
        _glyphOpacity = (float)Math.Clamp(glyphOpacity, 0.0, 1.0);
    }

    public void SetAttackGlyphState(
        long existingStreamCutoff,
        double screenshotInfluence)
    {
        _attackStreamCutoff = existingStreamCutoff;
        _attackFilterEnabled = 1;
        _screenshotInfluence =
            (float)Math.Clamp(screenshotInfluence, 0.0, 1.0);
    }

    public bool Present(
        int targetWidth,
        int targetHeight,
        IReadOnlyList<DrawingRectangle> viewports)
    {
        if (_disposed || targetWidth <= 0 || targetHeight <= 0)
            return false;

        lock (_scene.SyncRoot)
        {
            UploadAtlasIfNeeded();
            long version = _scene.Version;
            if (_uploadedVersion != version)
            {
                UploadInstances();
                _uploadedVersion = version;
            }

            Draw(targetWidth, targetHeight, viewports);
            Result presentResult = _swapChain.Present(0, PresentFlags.None);
            if (presentResult.Failure)
            {
                throw new InvalidOperationException(
                    $"Direct3D 11 не передал кадр композитору; HRESULT={presentResult.Code:X8}.");
            }
            return true;
        }
    }

    private void UploadAtlasIfNeeded()
    {
        long atlasVersion = _scene.AtlasVersion;
        if (_uploadedAtlasVersion == atlasVersion)
            return;

        GlyphAtlasData atlas = _scene.Atlas;
        if (atlas.Width <= 0 || atlas.Height <= 0 || atlas.Pixels.Length == 0)
            throw new InvalidOperationException("Атлас символов пуст.");

        _context.PSSetShaderResource(
            0,
            (ID3D11ShaderResourceView)null!);
        _atlasView?.Dispose();
        _atlasTexture?.Dispose();

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
            _atlasTexture = _device.CreateTexture2D(
                description,
                initialData);
            _atlasView = _device.CreateShaderResourceView(_atlasTexture);
        }
        finally
        {
            pixels.Free();
        }

        _uploadedAtlasVersion = atlasVersion;
    }

    private unsafe void UploadInstances()
    {
        int count = _scene.InstanceCount;
        if (count <= 0)
            return;

        EnsureInstanceCapacity(count);
        MappedSubresource mapped = _context.Map(
            _instanceBuffer!,
            0,
            MapMode.WriteDiscard,
            Vortice.Direct3D11.MapFlags.None);
        GCHandle instances = GCHandle.Alloc(
            _scene.Instances,
            GCHandleType.Pinned);
        try
        {
            long byteCount = checked((long)count * InstanceStride);
            long destinationSize = checked((long)_instanceCapacity * InstanceStride);
            Buffer.MemoryCopy(
                instances.AddrOfPinnedObject().ToPointer(),
                mapped.DataPointer.ToPointer(),
                destinationSize,
                byteCount);
        }
        finally
        {
            instances.Free();
            _context.Unmap(_instanceBuffer!, 0);
        }
    }

    private void EnsureInstanceCapacity(int requiredCount)
    {
        if (_instanceBuffer is not null && _instanceCapacity >= requiredCount)
            return;

        _instanceBuffer?.Dispose();
        _instanceCapacity = Math.Max(
            requiredCount,
            Math.Max(1024, _instanceCapacity * 2));
        uint byteWidth = checked((uint)(_instanceCapacity * InstanceStride));
        _instanceBuffer = _device.CreateBuffer(
            new BufferDescription(
                byteWidth,
                BindFlags.VertexBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write));
    }

    private void Draw(
        int targetWidth,
        int targetHeight,
        IReadOnlyList<DrawingRectangle> viewports)
    {
        MatrixRenderParameters parameters = _scene.Parameters;
        GlyphAtlasData atlas = _scene.Atlas;
        if (_atlasView is null)
            return;

        _context.OMSetRenderTargets(_renderTargetView);
        _context.OMSetBlendState(_blendState);
        _context.ClearRenderTargetView(
            _renderTargetView,
            new Color4(
                (float)parameters.BackgroundRed,
                (float)parameters.BackgroundGreen,
                (float)parameters.BackgroundBlue,
                1));

        DrawTransitionBackground(
            targetWidth,
            targetHeight,
            parameters);

        _context.IASetInputLayout(_inputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        if (_instanceBuffer is not null)
        {
            _context.IASetVertexBuffers(
                0,
                [_quadBuffer, _instanceBuffer],
                [QuadStride, InstanceStride],
                [0, 0]);
        }
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShaderResource(0, _atlasView);
        _context.PSSetShaderResource(1, _transitionView!);
        _context.PSSetSampler(0, _sampler);

        double sourceAspect =
            parameters.SourceWidth / (double)parameters.SourceHeight;
        foreach (DrawingRectangle viewport in viewports)
        {
            if (viewport.Width <= 0 || viewport.Height <= 0)
                continue;

            double viewportAspect =
                viewport.Width / (double)viewport.Height;
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
                parameters,
                atlas.GlyphCount,
                aspectScaleX,
                aspectScaleY,
                targetWidth,
                targetHeight,
                _glyphOpacity,
                _attackStreamCutoff,
                _attackFilterEnabled,
                _screenshotInfluence);
            UpdateConstantBuffer(constants);
            if (_scene.InstanceCount > 0 && _instanceBuffer is not null)
            {
                _context.DrawInstanced(
                    4,
                    (uint)_scene.InstanceCount,
                    0,
                    0);
            }
        }

        _context.PSSetShaderResource(
            0,
            (ID3D11ShaderResourceView)null!);
        _context.PSSetShaderResource(
            1,
            (ID3D11ShaderResourceView)null!);
    }

    private void DrawTransitionBackground(
        int targetWidth,
        int targetHeight,
        MatrixRenderParameters parameters)
    {
        if (_transitionView is null || _desktopOpacity <= 0.0001f)
            return;

        _context.RSSetViewport(
            new Viewport(
                0,
                0,
                targetWidth,
                targetHeight,
                0,
                1));
        _context.IASetInputLayout(_transitionInputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.IASetVertexBuffer(0, _quadBuffer, QuadStride);
        _context.VSSetShader(_transitionVertexShader);
        _context.PSSetShader(_transitionPixelShader);
        _context.PSSetConstantBuffer(0, _transitionConstantBuffer);
        _context.PSSetShaderResource(0, _transitionView);
        _context.PSSetSampler(0, _sampler);
        UpdateTransitionConstantBuffer(
            new TransitionShaderConstants(parameters, _desktopOpacity));
        _context.Draw(4, 0);
        _context.PSSetShaderResource(
            0,
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

        _atlasView?.Dispose();
        _atlasTexture?.Dispose();
        _transitionView?.Dispose();
        _transitionTexture?.Dispose();
        _instanceBuffer?.Dispose();
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
        public readonly Vector2 AspectScale;
        public readonly Vector2 TargetSize;
        public readonly float GlyphCount;
        public readonly float HeadBrightness;
        public readonly float GlyphOpacity;
        public readonly float AttackFilterEnabled;
        public readonly float AttackStreamCutoff;
        public readonly float ScreenshotInfluence;
        private readonly Vector2 _padding0;
        public readonly Vector3 SignalColor;
        private readonly float _padding1;
        public readonly Vector3 BackgroundColor;
        private readonly float _padding2;

        public ShaderConstants(
            MatrixRenderParameters parameters,
            int glyphCount,
            float aspectScaleX,
            float aspectScaleY,
            int targetWidth,
            int targetHeight,
            float glyphOpacity,
            float attackStreamCutoff,
            float attackFilterEnabled,
            float screenshotInfluence)
        {
            SourceSize = new(
                parameters.SourceWidth,
                parameters.SourceHeight);
            CellSize = new(
                parameters.CellWidth,
                parameters.CellHeight);
            AspectScale = new(aspectScaleX, aspectScaleY);
            TargetSize = new(targetWidth, targetHeight);
            GlyphCount = glyphCount;
            HeadBrightness = (float)parameters.HeadBrightness;
            GlyphOpacity = glyphOpacity;
            AttackFilterEnabled = attackFilterEnabled;
            AttackStreamCutoff = attackStreamCutoff;
            ScreenshotInfluence = screenshotInfluence;
            _padding0 = Vector2.Zero;
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
            float2 AspectScale;
            float2 TargetSize;
            float GlyphCount;
            float HeadBrightness;
            float GlyphOpacity;
            float AttackFilterEnabled;
            float AttackStreamCutoff;
            float ScreenshotInfluence;
            float2 Padding0;
            float3 SignalColor;
            float Padding1;
            float3 BackgroundColor;
            float Padding2;
        };

        Texture2D<float> Atlas : register(t0);
        Texture2D<float4> AttackDesktop : register(t1);
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
            float2 clip = float2(
                pixel.x / SourceSize.x * 2.0 - 1.0,
                1.0 - pixel.y / SourceSize.y * 2.0);
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
            if (AttackFilterEnabled > 0.5)
                clip(input.StreamId - AttackStreamCutoff - 0.5);

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
            if (ScreenshotInfluence > 0.001)
            {
                float2 screenPosition = saturate(
                    input.Position.xy / max(TargetSize, 1.0));
                float3 desktop = AttackDesktop.Sample(
                    AtlasSampler,
                    screenPosition).rgb;
                float luminance = dot(
                    desktop,
                    float3(0.2126, 0.7152, 0.0722));
                float imageLevel =
                    pow(smoothstep(0.035, 0.92, luminance), 0.82);
                float encodedLevel =
                    max(level * 0.18, imageLevel);
                level = lerp(
                    level,
                    encodedLevel,
                    ScreenshotInfluence);
            }

            float isImage = step(2.5, input.Style);
            float softLight = nearLight * 0.68 + wideLight * 0.32;
            float glowAmount =
                pow(clamp(softLight, 0.0, 1.0), 0.72)
                * (1.0 - center)
                * clamp(input.Glow, 0.0, 2.0)
                * clamp(level, 0.0, 1.0)
                * (1.0 - isImage)
                * 1.65;
            glowAmount = min(glowAmount, 1.35);
            float alpha = clamp(max(center, glowAmount), 0.0, 1.0);
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
            float bodyShare =
                center / max(center + glowAmount, 0.0001);
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

        Texture2D<float4> Desktop : register(t0);
        SamplerState DesktopSampler : register(s0);

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
            float3 desktop = Desktop.Sample(
                DesktopSampler,
                input.TexturePosition).rgb;
            return float4(
                lerp(BackgroundColor, desktop, DesktopOpacity),
                1.0);
        }
        """;
}
