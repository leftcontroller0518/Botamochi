#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <nvEncodeAPI.h>
#include <cstdio>
#include <cstring>
#include <cmath>
#include <memory>
#include <vector>
#include <stdexcept>
#include "plasma.h"

using Microsoft::WRL::ComPtr;

static void check(long status, const char* operation)
{
    if (!status) return;
    char message[192];
    sprintf_s(message, "%s (0x%08lx)", operation, status);
    throw std::runtime_error(message);
}
#define CHECK(call) check((call), #call)

struct SharedHandle {
    HANDLE value = nullptr;
    ~SharedHandle() { if (value) CloseHandle(value); }
};

class Encoder {
    HMODULE library = nullptr;
    NV_ENCODE_API_FUNCTION_LIST api{NV_ENCODE_API_FUNCTION_LIST_VER};
    void* session = nullptr;
    struct Frame { UINT width, height; float time; UINT mode; UINT progress; };
    struct Slot {
        ComPtr<ID3D12Resource> surface, constants, source;
        ComPtr<ID3D12DescriptorHeap> views;
        ComPtr<ID3D12CommandAllocator> allocator;
        ComPtr<ID3D12GraphicsCommandList> commands;
        ComPtr<ID3D11Texture2D> sharedSurface;
        Frame* frame = nullptr;
        void* upload = nullptr;
        NV_ENC_REGISTERED_PTR registered = nullptr;
        NV_ENC_INPUT_PTR mapped = nullptr;
        NV_ENC_OUTPUT_PTR bitstream = nullptr;
        SharedHandle ready;
        bool eventRegistered = false, inFlight = false, locked = false;
    };
    std::vector<Slot> slots;
    UINT64 serial = 0, encoded = 0, consumed = 0;
    UINT width, height, mode;
    bool async = false;
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12Fence> fence;
    ComPtr<ID3D12RootSignature> signature;
    ComPtr<ID3D12PipelineState> pipeline;
    ComPtr<ID3D11Device5> video;

    void openDevices(int gpu)
    {
        ComPtr<IDXGIFactory1> factory;
        CHECK(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
        ComPtr<IDXGIAdapter1> adapter;
        for (UINT i = 0; ; ++i) {
            CHECK(factory->EnumAdapters1(i, adapter.ReleaseAndGetAddressOf()));
            DXGI_ADAPTER_DESC1 desc{};
            CHECK(adapter->GetDesc1(&desc));
            if (desc.VendorId == 0x10de && !(desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) && gpu-- == 0) break;
        }
        CHECK(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)));
        ComPtr<ID3D11Device> base;
        ComPtr<ID3D11DeviceContext> immediate;
        CHECK(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &base, nullptr, &immediate));
        CHECK(base.As(&video));
        D3D12_COMMAND_QUEUE_DESC desc{};
        CHECK(device->CreateCommandQueue(&desc, IID_PPV_ARGS(&queue)));
    }

    void createSurface(Slot& slot)
    {
        D3D12_HEAP_PROPERTIES heap{};
        D3D11_TEXTURE2D_DESC videoDesc{};
        videoDesc.Width = width;
        videoDesc.Height = height;
        videoDesc.MipLevels = videoDesc.ArraySize = videoDesc.SampleDesc.Count = 1;
        videoDesc.Format = DXGI_FORMAT_NV12;
        videoDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
        videoDesc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED;
        CHECK(video->CreateTexture2D(&videoDesc, nullptr, &slot.sharedSurface));
        ComPtr<IDXGIResource1> dxgiSurface;
        CHECK(slot.sharedSurface.As(&dxgiSurface));
        SharedHandle textureHandle;
        CHECK(dxgiSurface->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
            nullptr, &textureHandle.value));
        CHECK(device->OpenSharedHandle(textureHandle.value, IID_PPV_ARGS(&slot.surface)));
        D3D12_DESCRIPTOR_HEAP_DESC descriptors{D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV, 2,
            D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE};
        CHECK(device->CreateDescriptorHeap(&descriptors, IID_PPV_ARGS(&slot.views)));
        auto handle = slot.views->GetCPUDescriptorHandleForHeapStart();
        for (UINT i = 0; i < 2; ++i) {
            D3D12_UNORDERED_ACCESS_VIEW_DESC view{};
            view.Format = i ? DXGI_FORMAT_R8G8_UNORM : DXGI_FORMAT_R8_UNORM;
            view.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
            view.Texture2D.PlaneSlice = i;
            device->CreateUnorderedAccessView(slot.surface.Get(), nullptr, &view, handle);
            handle.ptr += device->GetDescriptorHandleIncrementSize(descriptors.Type);
        }
        heap.Type = D3D12_HEAP_TYPE_UPLOAD;
        heap.CreationNodeMask = heap.VisibleNodeMask = 1;
        D3D12_RESOURCE_DESC desc{};
        desc.DepthOrArraySize = desc.MipLevels = desc.SampleDesc.Count = 1;
        desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        desc.Format = DXGI_FORMAT_UNKNOWN;
        desc.Width = 256;
        desc.Height = 1;
        desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        desc.Flags = D3D12_RESOURCE_FLAG_NONE;
        CHECK(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
            D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&slot.constants)));
        D3D12_RANGE noRead{};
        desc.Width = mode == 1 ? UINT64(width) * height * 4 : mode == 3 ? UINT64(width + height) * sizeof(float) : 4;
        CHECK(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
            D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&slot.source)));
        CHECK(slot.source->Map(0, &noRead, &slot.upload));
        if (mode == 3) {
            // Match the CPU's correctly rounded divisions once, including non-power-of-two sizes.
            float* coordinates = static_cast<float*>(slot.upload);
            for (UINT x = 0; x < width; ++x) coordinates[x] = static_cast<float>(x) / width;
            for (UINT y = 0; y < height; ++y) coordinates[width + y] = static_cast<float>(y) / height;
        }
        CHECK(slot.constants->Map(0, &noRead, reinterpret_cast<void**>(&slot.frame)));
    }

    void createPipeline()
    {
        D3D12_DESCRIPTOR_RANGE range{D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 2, 0, 0, 0};
        D3D12_ROOT_PARAMETER parameters[3]{};
        parameters[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
        parameters[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        parameters[1].DescriptorTable = {1, &range};
        parameters[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
        D3D12_ROOT_SIGNATURE_DESC desc{3, parameters};
        ComPtr<ID3DBlob> blob;
        CHECK(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &blob, nullptr));
        CHECK(device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&signature)));
        D3D12_COMPUTE_PIPELINE_STATE_DESC state{};
        state.pRootSignature = signature.Get();
        state.CS = {plasma, sizeof(plasma)};
        CHECK(device->CreateComputePipelineState(&state, IID_PPV_ARGS(&pipeline)));
    }

    void record(Slot& slot)
    {
        CHECK(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&slot.allocator)));
        CHECK(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, slot.allocator.Get(), pipeline.Get(), IID_PPV_ARGS(&slot.commands)));
        slot.commands->SetComputeRootSignature(signature.Get());
        ID3D12DescriptorHeap* heaps[]{slot.views.Get()};
        slot.commands->SetDescriptorHeaps(1, heaps);
        slot.commands->SetComputeRootConstantBufferView(0, slot.constants->GetGPUVirtualAddress());
        slot.commands->SetComputeRootDescriptorTable(1, slot.views->GetGPUDescriptorHandleForHeapStart());
        // Simultaneous-access textures promote COMMON -> UAV and decay after execution.
        slot.commands->SetComputeRootShaderResourceView(2, slot.source->GetGPUVirtualAddress());
        slot.commands->Dispatch((width + 31) / 32, height, 1);
        CHECK(slot.commands->Close());
    }

public:
    Encoder(UINT w, UINT h, UINT sceneMode, size_t depth) : slots(depth), width(w), height(h), mode(sceneMode) {}
    void initialize(int fps, int gpu, bool lossless)
    {
        openDevices(gpu);
        CHECK(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)));
        createPipeline();
        library = LoadLibraryExW(L"nvEncodeAPI64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!library) throw std::runtime_error("NVENC driver unavailable");
        auto create = reinterpret_cast<decltype(&NvEncodeAPICreateInstance)>(GetProcAddress(library, "NvEncodeAPICreateInstance"));
        if (!create) throw std::runtime_error("NvEncodeAPICreateInstance unavailable");
        CHECK(create(&api));
        NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open{NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER};
        open.apiVersion = NVENCAPI_VERSION;
        open.device = video.Get();
        open.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX;
        CHECK(api.nvEncOpenEncodeSessionEx(&open, &session));
        NV_ENC_PRESET_CONFIG preset{NV_ENC_PRESET_CONFIG_VER};
        preset.presetCfg.version = NV_ENC_CONFIG_VER;
        auto tuning = lossless ? NV_ENC_TUNING_INFO_LOSSLESS : NV_ENC_TUNING_INFO_LOW_LATENCY;
        CHECK(api.nvEncGetEncodePresetConfigEx(session, NV_ENC_CODEC_H264_GUID, NV_ENC_PRESET_P1_GUID,
            tuning, &preset));
        auto& config = preset.presetCfg;
        config.frameIntervalP = 1;
        config.rcParams.enableLookahead = 0;
        if (lossless) {
            config.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CONSTQP;
            config.rcParams.constQP = {};
            config.encodeCodecConfig.h264Config.qpPrimeYZeroTransformBypassFlag = 1;
        } else {
            config.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CBR;
            config.rcParams.averageBitRate = 12000000;
            config.rcParams.maxBitRate = 15000000;
        }
        config.encodeCodecConfig.h264Config.repeatSPSPPS = 1;
        NV_ENC_INITIALIZE_PARAMS init{NV_ENC_INITIALIZE_PARAMS_VER};
        init.encodeGUID = NV_ENC_CODEC_H264_GUID;
        init.presetGUID = NV_ENC_PRESET_P1_GUID;
        init.tuningInfo = tuning;
        init.encodeWidth = init.darWidth = width;
        init.encodeHeight = init.darHeight = height;
        init.frameRateNum = fps;
        init.frameRateDen = 1;
        init.enablePTD = 1;
        init.encodeConfig = &config;
        if (slots.size() > 1) {
            NV_ENC_CAPS_PARAM caps{NV_ENC_CAPS_PARAM_VER};
            caps.capsToQuery = NV_ENC_CAPS_ASYNC_ENCODE_SUPPORT;
            int supported = 0;
            CHECK(api.nvEncGetEncodeCaps(session, NV_ENC_CODEC_H264_GUID, &caps, &supported));
            async = supported != 0;
            init.enableEncodeAsync = async;
        }
        CHECK(api.nvEncInitializeEncoder(session, &init));
        for (auto& slot : slots) {
            createSurface(slot);
            record(slot);
            registerSlot(slot);
        }
    }

private:
    void registerSlot(Slot& slot)
    {
        NV_ENC_REGISTER_RESOURCE reg{NV_ENC_REGISTER_RESOURCE_VER};
        reg.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
        reg.width = width;
        reg.height = height;
        reg.resourceToRegister = slot.sharedSurface.Get();
        reg.bufferFormat = NV_ENC_BUFFER_FORMAT_NV12;
        CHECK(api.nvEncRegisterResource(session, &reg));
        slot.registered = reg.registeredResource;
        NV_ENC_CREATE_BITSTREAM_BUFFER out{NV_ENC_CREATE_BITSTREAM_BUFFER_VER};
        CHECK(api.nvEncCreateBitstreamBuffer(session, &out));
        slot.bitstream = out.bitstreamBuffer;
        if (async) {
            slot.ready.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!slot.ready.value) throw std::runtime_error("NVENC completion event creation failed");
            NV_ENC_EVENT_PARAMS event{NV_ENC_EVENT_PARAMS_VER};
            event.completionEvent = slot.ready.value;
            CHECK(api.nvEncRegisterAsyncEvent(session, &event));
            slot.eventRegistered = true;
        }
    }

    void submitNext()
    {
        Slot& slot = slots[encoded % slots.size()];
        // D3D11 NVENC cannot consume the texture until its D3D12 writes complete.
        if (fence->GetCompletedValue() < encoded + 1)
            CHECK(fence->SetEventOnCompletion(encoded + 1, nullptr));
        NV_ENC_MAP_INPUT_RESOURCE map{NV_ENC_MAP_INPUT_RESOURCE_VER};
        map.registeredResource = slot.registered;
        CHECK(api.nvEncMapInputResource(session, &map));
        slot.mapped = map.mappedResource;
        NV_ENC_PIC_PARAMS pic{NV_ENC_PIC_PARAMS_VER};
        pic.inputBuffer = slot.mapped;
        pic.bufferFmt = NV_ENC_BUFFER_FORMAT_NV12;
        pic.inputWidth = width;
        pic.inputHeight = height;
        pic.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
        pic.outputBitstream = slot.bitstream;
        pic.inputTimeStamp = encoded;
        if (async) {
            ResetEvent(slot.ready.value);
            pic.completionEvent = slot.ready.value;
        }
        CHECK(api.nvEncEncodePicture(session, &pic));
        slot.inFlight = true;
        ++encoded;
    }

    void receive(void** data, int* size)
    {
        Slot& slot = slots[consumed % slots.size()];
        if (async && WaitForSingleObject(slot.ready.value, INFINITE) != WAIT_OBJECT_0)
            throw std::runtime_error("NVENC output wait failed");
        NV_ENC_LOCK_BITSTREAM lock{NV_ENC_LOCK_BITSTREAM_VER};
        lock.outputBitstream = slot.bitstream;
        CHECK(api.nvEncLockBitstream(session, &lock));
        slot.locked = true;
        slot.inFlight = false;
        CHECK(api.nvEncUnmapInputResource(session, slot.mapped));
        slot.mapped = nullptr;
        *data = lock.bitstreamBufferPtr;
        *size = static_cast<int>(lock.bitstreamSizeInBytes);
        ++consumed;
    }

public:
    void* rgbaUpload() const { return mode == 1 ? slots[serial % slots.size()].upload : nullptr; }

    void encode(float time, void** data, int* size)
    {
        *data = nullptr;
        *size = 0;
        Slot& slot = slots[serial % slots.size()];
        if (slot.locked) {
            CHECK(api.nvEncUnlockBitstream(session, slot.bitstream));
            slot.locked = false;
        }
        UINT progress = mode == 2 ? static_cast<UINT>(std::fmod(time, 5.0f) / 5.0f * width) + 1 : 0;
        *slot.frame = {width, height, time, mode, progress};
        ID3D12CommandList* lists[]{slot.commands.Get()};
        queue->ExecuteCommandLists(1, lists);
        CHECK(queue->Signal(fence.Get(), serial + 1));
        ++serial;

        // Draw N while NVENC processes N-1. Reclaim only the oldest completed slot.
        if (slots.size() == 1 || serial - encoded > 1) submitNext();
        if (serial - consumed == slots.size()) receive(data, size);
    }

    void drain(void** data, int* size)
    {
        *data = nullptr;
        *size = 0;
        while (encoded < serial) submitNext();
        if (consumed < encoded) receive(data, size);
    }

    ~Encoder()
    {
        if (serial && fence->GetCompletedValue() < serial) fence->SetEventOnCompletion(serial, nullptr);
        for (auto& slot : slots) {
            if (slot.inFlight) {
                if (async) WaitForSingleObject(slot.ready.value, INFINITE);
                NV_ENC_LOCK_BITSTREAM lock{NV_ENC_LOCK_BITSTREAM_VER};
                lock.outputBitstream = slot.bitstream;
                slot.locked = api.nvEncLockBitstream(session, &lock) == NV_ENC_SUCCESS;
            }
            if (slot.locked) api.nvEncUnlockBitstream(session, slot.bitstream);
            if (slot.mapped) api.nvEncUnmapInputResource(session, slot.mapped);
            if (slot.eventRegistered) {
                NV_ENC_EVENT_PARAMS event{NV_ENC_EVENT_PARAMS_VER};
                event.completionEvent = slot.ready.value;
                api.nvEncUnregisterAsyncEvent(session, &event);
            }
            if (slot.registered) api.nvEncUnregisterResource(session, slot.registered);
            if (slot.bitstream) api.nvEncDestroyBitstreamBuffer(session, slot.bitstream);
        }
        if (session) api.nvEncDestroyEncoder(session);
        if (library) FreeLibrary(library);
    }
};

static thread_local char lastError[512];
static void* openEncoder(int w, int h, int fps, int gpu, int rgba, int lossless, size_t depth)
{
    try {
        auto encoder = std::make_unique<Encoder>(w, h, rgba, depth);
        encoder->initialize(fps, gpu, lossless != 0);
        return encoder.release();
    } catch (const std::exception& ex) { strncpy_s(lastError, ex.what(), _TRUNCATE); return nullptr; }
}
extern "C" __declspec(dllexport) void* createEncoder(int w, int h, int fps, int gpu, int mode, int lossless)
{
    return openEncoder(w, h, fps, gpu, mode, lossless, 1);
}
extern "C" __declspec(dllexport) void* createPipelinedEncoder(int w, int h, int fps, int gpu, int mode, int lossless)
{
    return openEncoder(w, h, fps, gpu, mode, lossless, 4);
}
extern "C" __declspec(dllexport) int drainEncoder(Encoder* encoder, void** data, int* size)
{
    try { encoder->drain(data, size); return 0; }
    catch (const std::exception& ex) { strncpy_s(lastError, ex.what(), _TRUNCATE); return -1; }
}
extern "C" __declspec(dllexport) void* rgbaUpload(Encoder* encoder) { return encoder->rgbaUpload(); }
extern "C" __declspec(dllexport) int encodeFrame(Encoder* encoder, float time, void** data, int* size)
{
    try { encoder->encode(time, data, size); return 0; }
    catch (const std::exception& ex) { strncpy_s(lastError, ex.what(), _TRUNCATE); return -1; }
}
extern "C" __declspec(dllexport) const char* encoderError() { return lastError; }
extern "C" __declspec(dllexport) void destroyEncoder(Encoder* encoder) { delete encoder; }
