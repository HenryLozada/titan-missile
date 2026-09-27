using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Titan.App.Rendering;

/// <summary>
/// Draws one fullscreen pixel shader into a SwapChainPanel. The whole visual is the shader:
/// no textures, no meshes, one constant buffer. Frames are only produced when <see cref="Render"/> is called,
/// so a hidden window costs nothing.
/// </summary>
public sealed unsafe class ShaderSurface<T> : IDisposable where T : unmanaged
{
    private static readonly Lazy<(ID3D11Device Device, ID3D11DeviceContext Context)> SharedDevice = new(CreateDevice);

    private readonly SwapChainPanel _panel;
    private readonly float _quality;
    private ID3D11Device Device => SharedDevice.Value.Device;
    private ID3D11DeviceContext Context => SharedDevice.Value.Context;

    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11Buffer? _cb;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private int _width, _height;

    public int PixelWidth => _width;
    public int PixelHeight => _height;
    public bool Ready => _rtv is not null;
    public string? Error { get; private set; }

    /// <param name="quality">Render scale relative to physical pixels (the panel stretches it back up).</param>
    public ShaderSurface(SwapChainPanel panel, string shaderResource, float quality = 1f)
    {
        _panel = panel;
        _quality = quality;
        try
        {
            string source = LoadResource(shaderResource);
            _vs = Device.CreateVertexShader(Compile(source, "VS", "vs_5_0").Span);
            _ps = Device.CreatePixelShader(Compile(source, "PS", "ps_5_0").Span);
            _cb = Device.CreateBuffer(new BufferDescription((uint)((sizeof(T) + 15) / 16 * 16), BindFlags.ConstantBuffer));
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return;
        }
        _panel.SizeChanged += (_, _) => Resize();
        _panel.CompositionScaleChanged += (_, _) => Resize();
        _panel.Loaded += (_, _) => Resize();
        Resize();
    }

    public void Render(in T parameters)
    {
        if (_rtv is null || _swapChain is null || _cb is null) return;
        var ctx = Context;
        ctx.UpdateSubresource(in parameters, _cb);
        ctx.OMSetRenderTargets(_rtv);
        ctx.RSSetViewport(new Viewport(0, 0, _width, _height));
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.PSSetConstantBuffer(0, _cb);
        ctx.Draw(3, 0);
        _swapChain.Present(0, PresentFlags.None);   // composition swap chain: never blocks the UI thread
    }

    private void Resize()
    {
        double sx = _panel.CompositionScaleX, sy = _panel.CompositionScaleY;
        int w = Math.Max(1, (int)(_panel.ActualWidth * sx * _quality));
        int h = Math.Max(1, (int)(_panel.ActualHeight * sy * _quality));
        if (_panel.ActualWidth < 1 || _panel.ActualHeight < 1) return;
        if (w == _width && h == _height && _swapChain is not null) return;
        _width = w; _height = h;

        _rtv?.Dispose(); _rtv = null;
        if (_swapChain is null)
        {
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            var desc = new SwapChainDescription1
            {
                Width = (uint)w,
                Height = (uint)h,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
            };
            _swapChain = factory.CreateSwapChainForComposition(Device, desc);
            SwapChainPanelInterop.SetSwapChain(_panel, _swapChain.NativePointer);
        }
        else
        {
            _swapChain.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        }

        // Undo the DPI scale and the quality downscale so the panel shows the buffer at its layout size.
        using (var sc2 = _swapChain.QueryInterface<IDXGISwapChain2>())
            sc2.MatrixTransform = Matrix3x2.CreateScale((float)(1 / (sx * _quality)), (float)(1 / (sy * _quality)));

        using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = Device.CreateRenderTargetView(back);
    }

    private static (ID3D11Device, ID3D11DeviceContext) CreateDevice()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        return (device, context);
    }

    private static ReadOnlyMemory<byte> Compile(string source, string entry, string profile)
    {
        var result = Compiler.Compile(source, entry, "titan.hlsl", profile, out Blob blob, out Blob? errors);
        if (result.Failure)
        {
            string msg = errors is null ? result.ToString() : Marshal.PtrToStringAnsi(errors.BufferPointer) ?? result.ToString();
            errors?.Dispose();
            throw new InvalidOperationException($"Shader {entry}: {msg}");
        }
        var bytes = blob.AsBytes();
        blob.Dispose();
        return bytes;
    }

    private static string LoadResource(string name)
    {
        var asm = typeof(ShaderSurface<T>).Assembly;
        string full = asm.GetManifestResourceNames().First(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(full)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public void Dispose()
    {
        _rtv?.Dispose();
        _swapChain?.Dispose();
        _cb?.Dispose();
        _ps?.Dispose();
        _vs?.Dispose();
    }
}

/// <summary>ISwapChainPanelNative::SetSwapChain, called through the vtable (no extra interop package).</summary>
internal static unsafe class SwapChainPanelInterop
{
    private static readonly Guid IID_ISwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    public static void SetSwapChain(SwapChainPanel panel, nint swapChain)
    {
        nint unknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
        Guid iid = IID_ISwapChainPanelNative;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint native));
        try
        {
            var setSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(*(void***)native + 3));
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }
}
