using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Botamochi.Gpu;

/// <summary>
/// Silk.NET (Direct3D 12) を用いたオフスクリーンGPU描画パイプライン
/// </summary>
public sealed unsafe class RenderPipeline : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    // Direct3D 12 インターフェース
    private D3D12? _d3d12;
    private DXGI? _dxgi;
    private IDXGIFactory4* _factory;
    private ID3D12Device* _device;
    private ID3D12CommandQueue* _commandQueue;
    private ID3D12CommandAllocator* _commandAllocator;
    private ID3D12GraphicsCommandList* _commandList;
    private ID3D12Resource* _renderTarget;
    private ID3D12DescriptorHeap* _rtvHeap;

    public int Width => _width;
    public int Height => _height;

    public RenderPipeline(int width, int height)
    {
        _width = width;
        _height = height;

        InitializeDirect3D12();
    }

    private void InitializeDirect3D12()
    {
        _d3d12 = D3D12.GetApi();
        _dxgi = DXGI.GetApi();

        // 1. DXGI Factoryの生成
        IDXGIFactory4* factory = null;
        Guid factoryGuid = SilkMarshal.GuidOf<IDXGIFactory4>();
        SilkMarshal.ThrowHResult(_dxgi.CreateDXGIFactory2(0, &factoryGuid, (void**)&factory));
        _factory = factory;

        // 2. ハードウェアアダプターの列挙とD3D12デバイス生成
        ID3D12Device* device = null;
        Guid deviceGuid = SilkMarshal.GuidOf<ID3D12Device>();
        SilkMarshal.ThrowHResult(_d3d12.CreateDevice(null, D3DFeatureLevel.Level110, &deviceGuid, (void**)&device));
        _device = device;

        // 3. コマンドキュー作成
        CommandQueueDesc queueDesc = new()
        {
            Type = CommandListType.Direct,
            Priority = 0,
            Flags = CommandQueueFlags.None,
            NodeMask = 0
        };
        ID3D12CommandQueue* commandQueue = null;
        Guid queueGuid = SilkMarshal.GuidOf<ID3D12CommandQueue>();
        SilkMarshal.ThrowHResult(_device->CreateCommandQueue(&queueDesc, &queueGuid, (void**)&commandQueue));
        _commandQueue = commandQueue;

        // 4. コマンドアロケータとリスト作成
        ID3D12CommandAllocator* allocator = null;
        Guid allocGuid = SilkMarshal.GuidOf<ID3D12CommandAllocator>();
        SilkMarshal.ThrowHResult(_device->CreateCommandAllocator(CommandListType.Direct, &allocGuid, (void**)&allocator));
        _commandAllocator = allocator;

        ID3D12GraphicsCommandList* cmdList = null;
        Guid listGuid = SilkMarshal.GuidOf<ID3D12GraphicsCommandList>();
        SilkMarshal.ThrowHResult(_device->CreateCommandList(0, CommandListType.Direct, _commandAllocator, null, &listGuid, (void**)&cmdList));
        _commandList = cmdList;
        _commandList->Close();

        // 5. RTV (レンダーターゲットビュー) デスクリプタヒープ作成
        DescriptorHeapDesc rtvHeapDesc = new()
        {
            NumDescriptors = 1,
            Type = DescriptorHeapType.Rtv,
            Flags = DescriptorHeapFlags.None
        };
        ID3D12DescriptorHeap* rtvHeap = null;
        Guid heapGuid = SilkMarshal.GuidOf<ID3D12DescriptorHeap>();
        SilkMarshal.ThrowHResult(_device->CreateDescriptorHeap(&rtvHeapDesc, &heapGuid, (void**)&rtvHeap));
        _rtvHeap = rtvHeap;

        // 6. オフスクリーンレンダーターゲットテクスチャリソースの作成 (RGBA8 Unorm)
        HeapProperties heapProps = new(HeapType.Default);
        ResourceDesc resourceDesc = new()
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = (ulong)_width,
            Height = (uint)_height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.AllowRenderTarget
        };

        ID3D12Resource* renderTarget = null;
        Guid resGuid = SilkMarshal.GuidOf<ID3D12Resource>();
        SilkMarshal.ThrowHResult(_device->CreateCommittedResource(
            &heapProps,
            HeapFlags.None,
            &resourceDesc,
            ResourceStates.RenderTarget,
            null,
            &resGuid,
            (void**)&renderTarget
        ));
        _renderTarget = renderTarget;

        // RTV作成
        CpuDescriptorHandle rtvHandle = _rtvHeap->GetCPUDescriptorHandleForHeapStart();
        _device->CreateRenderTargetView(_renderTarget, null, rtvHandle);
    }

    /// <summary>
    /// オフスクリーンサーフェスへの描画コマンドを発行
    /// </summary>
    public void RenderScene(float timeInSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _commandAllocator->Reset();
        _commandList->Reset(_commandAllocator, null);

        CpuDescriptorHandle rtvHandle = _rtvHeap->GetCPUDescriptorHandleForHeapStart();
        _commandList->OMSetRenderTargets(1, &rtvHandle, false, null);

        // 時間経過で背景色が変化するテストレンダー
        float r = 0.5f + 0.5f * MathF.Sin(timeInSeconds);
        float g = 0.5f + 0.5f * MathF.Cos(timeInSeconds * 0.7f);
        float b = 0.5f + 0.5f * MathF.Sin(timeInSeconds * 1.3f);
        float* clearColor = stackalloc float[4] { r, g, b, 1.0f };

        _commandList->ClearRenderTargetView(rtvHandle, clearColor, 0, null);

        _commandList->Close();
        ID3D12CommandList** ppCommandLists = stackalloc ID3D12CommandList*[1] { (ID3D12CommandList*)_commandList };
        _commandQueue->ExecuteCommandLists(1, ppCommandLists);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_renderTarget != null) _renderTarget->Release();
        if (_rtvHeap != null) _rtvHeap->Release();
        if (_commandList != null) _commandList->Release();
        if (_commandAllocator != null) _commandAllocator->Release();
        if (_commandQueue != null) _commandQueue->Release();
        if (_device != null) _device->Release();
        if (_factory != null) _factory->Release();
    }
}
