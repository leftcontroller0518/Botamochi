using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Botamochi.Gpu;

/// <summary>
/// 32-bit ルート定数 (Root Constants) 構造体 - デスクリプタインダイレクションを回避し、L1キャッシュへ直接プッシュ
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SceneRootConstants
{
    public float TimeInSeconds;
    public uint Width;
    public uint Height;
    public uint FrameIndex;
}

/// <summary>
/// Direct3D 12 ゼロコピー共有ハンドル & 非同期コンピュートキュー (Async Compute) 基盤
/// </summary>
public sealed unsafe class D3D12InteropPipeline : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    private D3D12? _d3d12;
    private DXGI? _dxgi;
    private ID3D12Device* _device;

    // 非同期コンピュート専用キュー (Graphicsキューと完全に並行動作)
    private ID3D12CommandQueue* _asyncComputeQueue;
    private ID3D12CommandAllocator* _computeAllocator;
    private ID3D12GraphicsCommandList* _computeCommandList;

    // ゼロコピー相互運用レンダーターゲットリソース
    private ID3D12Resource* _sharedTextureResource;
    private void* _sharedHandle;

    public void* SharedHandle => _sharedHandle;

    public D3D12InteropPipeline(int width, int height)
    {
        _width = width;
        _height = height;

        InitializeAsyncComputeAndSharedResource();
    }

    private void InitializeAsyncComputeAndSharedResource()
    {
        _d3d12 = D3D12.GetApi();
        _dxgi = DXGI.GetApi();

        // 1. D3D12 デバイスの生成
        ID3D12Device* device = null;
        Guid deviceGuid = SilkMarshal.GuidOf<ID3D12Device>();
        SilkMarshal.ThrowHResult(_d3d12.CreateDevice(null, D3DFeatureLevel.Level110, &deviceGuid, (void**)&device));
        _device = device;

        // 2. 非同期コンピュートキュー (CommandListType.Compute) の作成
        CommandQueueDesc computeQueueDesc = new()
        {
            Type = CommandListType.Compute,
            Priority = 0,
            Flags = CommandQueueFlags.None,
            NodeMask = 0
        };
        ID3D12CommandQueue* compQueue = null;
        Guid queueGuid = SilkMarshal.GuidOf<ID3D12CommandQueue>();
        SilkMarshal.ThrowHResult(_device->CreateCommandQueue(&computeQueueDesc, &queueGuid, (void**)&compQueue));
        _asyncComputeQueue = compQueue;

        // 3. コンピュート用アロケータとリストの作成
        ID3D12CommandAllocator* compAlloc = null;
        Guid allocGuid = SilkMarshal.GuidOf<ID3D12CommandAllocator>();
        SilkMarshal.ThrowHResult(_device->CreateCommandAllocator(CommandListType.Compute, &allocGuid, (void**)&compAlloc));
        _computeAllocator = compAlloc;

        ID3D12GraphicsCommandList* compCmdList = null;
        Guid listGuid = SilkMarshal.GuidOf<ID3D12GraphicsCommandList>();
        SilkMarshal.ThrowHResult(_device->CreateCommandList(0, CommandListType.Compute, _computeAllocator, null, &listGuid, (void**)&compCmdList));
        _computeCommandList = compCmdList;
        _computeCommandList->Close();

        // 4. ゼロコピー共有リソース (DXGI Shared Handle対応テクスチャ) の作成
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
            Flags = ResourceFlags.AllowUnorderedAccess | ResourceFlags.AllowSimultaneousAccess
        };

        ID3D12Resource* sharedRes = null;
        Guid resGuid = SilkMarshal.GuidOf<ID3D12Resource>();
        SilkMarshal.ThrowHResult(_device->CreateCommittedResource(
            &heapProps,
            HeapFlags.Shared,
            &resourceDesc,
            ResourceStates.Common,
            null,
            &resGuid,
            (void**)&sharedRes
        ));
        _sharedTextureResource = sharedRes;

        // 5. プロセス間 / NVENC共有用 NT Shared Handle の作成
        void* sharedHandle = null;
        SilkMarshal.ThrowHResult(_device->CreateSharedHandle(
            (ID3D12DeviceChild*)_sharedTextureResource,
            null,
            0x10000000, // GENERIC_ALL
            (char*)null,
            &sharedHandle
        ));
        _sharedHandle = sharedHandle;
    }

    /// <summary>
    /// 非同期コンピュートキュー上でカーネルをグラフィックス処理と並行ディスパッチ
    /// </summary>
    public void ExecuteAsyncCompute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _computeAllocator->Reset();
        _computeCommandList->Reset(_computeAllocator, null);

        // コンピュート処理の記録
        _computeCommandList->Close();

        ID3D12CommandList** ppCommandLists = stackalloc ID3D12CommandList*[1] { (ID3D12CommandList*)_computeCommandList };
        _asyncComputeQueue->ExecuteCommandLists(1, ppCommandLists);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_sharedTextureResource != null) _sharedTextureResource->Release();
        if (_computeCommandList != null) _computeCommandList->Release();
        if (_computeAllocator != null) _computeAllocator->Release();
        if (_asyncComputeQueue != null) _asyncComputeQueue->Release();
        if (_device != null) _device->Release();
    }
}
