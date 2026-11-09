#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mmdeviceapi.h>
#include <audiopolicy.h>
#include <endpointvolume.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mfreadwrite.h>
#include <wrl/client.h>
#include <array>
#include <fstream>
#include <iostream>
#include <map>

using Microsoft::WRL::ComPtr;

static void Check(HRESULT hr)
{
    if (FAILED(hr)) throw hr;
}

static void ListAudioProcesses(bool withLevels)
{
    ComPtr<IMMDeviceEnumerator> enumerator;
    Check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&enumerator)));
    ComPtr<IMMDeviceCollection> devices;
    Check(enumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &devices));
    UINT count = 0;
    Check(devices->GetCount(&count));
    std::map<DWORD, float> processes;
    for (UINT i = 0; i < count; ++i)
    {
        ComPtr<IMMDevice> device;
        ComPtr<IAudioSessionManager2> manager;
        ComPtr<IAudioSessionEnumerator> sessions;
        if (FAILED(devices->Item(i, &device)) ||
            FAILED(device->Activate(__uuidof(IAudioSessionManager2), CLSCTX_ALL, nullptr, &manager)) ||
            FAILED(manager->GetSessionEnumerator(&sessions))) continue;
        int sessionCount = 0;
        if (FAILED(sessions->GetCount(&sessionCount))) continue;
        for (int index = 0; index < sessionCount; ++index)
        {
            ComPtr<IAudioSessionControl> session;
            ComPtr<IAudioSessionControl2> details;
            DWORD pid = 0;
            AudioSessionState state;
            if (SUCCEEDED(sessions->GetSession(index, &session)) &&
                SUCCEEDED(session->GetState(&state)) && state != AudioSessionStateExpired &&
                SUCCEEDED(session.As(&details)) && SUCCEEDED(details->GetProcessId(&pid)) &&
                pid != 0 && details->IsSystemSoundsSession() != S_OK)
            {
                float peak = 0;
                ComPtr<IAudioMeterInformation> meter;
                if (withLevels && state == AudioSessionStateActive && SUCCEEDED(session.As(&meter)))
                    meter->GetPeakValue(&peak);
                auto& maximum = processes[pid];
                if (peak > maximum) maximum = peak;
            }
        }
    }
    for (const auto& entry : processes)
    {
        std::cout << entry.first;
        if (withLevels) std::cout << ' ' << entry.second;
        std::cout << '\n';
    }
}

static constexpr DWORD SampleRate = 48000;
static constexpr DWORD BlockAlign = 4;

static ComPtr<IMFMediaType> AudioType(bool aac)
{
    ComPtr<IMFMediaType> type;
    Check(MFCreateMediaType(&type));
    Check(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
    Check(type->SetGUID(MF_MT_SUBTYPE, aac ? MFAudioFormat_AAC : MFAudioFormat_PCM));
    Check(type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2));
    Check(type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, SampleRate));
    Check(type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
    Check(type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, aac ? 20000 : SampleRate * BlockAlign));
    if (aac)
    {
        Check(type->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0));
        Check(type->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29));
    }
    else
    {
        Check(type->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, BlockAlign));
        Check(type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
    }
    return type;
}

class PcmSamples
{
public:
    explicit PcmSamples(const wchar_t* path)
    {
        if (wcscmp(path, L"-") != 0)
        {
            file.open(path, std::ios::binary);
            if (!file) throw HRESULT_FROM_WIN32(ERROR_OPEN_FAILED);
        }
    }

    bool IsPresent() const { return file.is_open(); }
    LONGLONG Time() const { return frames * 10000000 / SampleRate; }

    bool WriteNext(IMFSinkWriter* writer, DWORD stream, LONGLONG endTime = -1)
    {
        if (!file) return false;
        auto requested = data.size();
        if (endTime >= 0)
        {
            LONGLONG remaining = endTime * SampleRate / 10000000 - frames;
            if (remaining <= 0) return false;
            if (remaining < static_cast<LONGLONG>(requested / BlockAlign))
                requested = static_cast<size_t>(remaining) * BlockAlign;
        }
        file.read(data.data(), requested);
        DWORD bytes = static_cast<DWORD>(file.gcount());
        if (file.bad() || bytes % BlockAlign != 0) throw HRESULT_FROM_WIN32(ERROR_READ_FAULT);
        if (bytes == 0) return false;
        ComPtr<IMFMediaBuffer> buffer;
        Check(MFCreateMemoryBuffer(bytes, &buffer));
        BYTE* destination = nullptr;
        Check(buffer->Lock(&destination, nullptr, nullptr));
        memcpy(destination, data.data(), bytes);
        Check(buffer->Unlock());
        Check(buffer->SetCurrentLength(bytes));
        ComPtr<IMFSample> sample;
        Check(MFCreateSample(&sample));
        Check(sample->AddBuffer(buffer.Get()));
        LONGLONG time = Time();
        frames += bytes / BlockAlign;
        Check(sample->SetSampleTime(time));
        Check(sample->SetSampleDuration(Time() - time));
        Check(writer->WriteSample(stream, sample.Get()));
        return true;
    }

private:
    std::ifstream file;
    std::array<char, 1024 * BlockAlign> data;
    LONGLONG frames = 0;
};

static void Mux(const wchar_t* videoPath, const wchar_t* pcmPath, const wchar_t* outputPath)
{
    constexpr DWORD FirstVideo = static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
    ComPtr<IMFSourceReader> reader;
    Check(MFCreateSourceReaderFromURL(videoPath, nullptr, &reader));
    Check(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE));
    Check(reader->SetStreamSelection(FirstVideo, TRUE));
    ComPtr<IMFMediaType> videoType;
    Check(reader->GetNativeMediaType(FirstVideo, 0, &videoType));
    GUID subtype = GUID_NULL;
    Check(videoType->GetGUID(MF_MT_SUBTYPE, &subtype));
    if (subtype != MFVideoFormat_H264) throw MF_E_INVALIDMEDIATYPE;
    Check(reader->SetCurrentMediaType(FirstVideo, nullptr, videoType.Get()));

    ComPtr<IMFAttributes> attributes;
    Check(MFCreateAttributes(&attributes, 2));
    Check(attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE));
    Check(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, FALSE));
    ComPtr<IMFSinkWriter> writer;
    Check(MFCreateSinkWriterFromURL(outputPath, nullptr, attributes.Get(), &writer));
    DWORD video = 0;
    Check(writer->AddStream(videoType.Get(), &video));
    Check(writer->SetInputMediaType(video, videoType.Get(), nullptr));

    PcmSamples pcm(pcmPath);
    bool audioReady = pcm.IsPresent();
    DWORD audio = 0;
    if (audioReady)
    {
        Check(writer->AddStream(AudioType(true).Get(), &audio));
        Check(writer->SetInputMediaType(audio, AudioType(false).Get(), nullptr));
    }
    Check(writer->BeginWriting());
    LONGLONG videoEnd = 0;
    for (;;)
    {
        ComPtr<IMFSample> sample;
        DWORD flags = 0;
        LONGLONG timestamp = 0;
        Check(reader->ReadSample(FirstVideo, 0, nullptr, &flags, &timestamp, &sample));
        if (flags & MF_SOURCE_READERF_ERROR) throw E_FAIL;
        while (audioReady && pcm.Time() <= timestamp) audioReady = pcm.WriteNext(writer.Get(), audio);
        if (sample)
        {
            LONGLONG duration = 0;
            sample->GetSampleDuration(&duration);
            if (timestamp + duration > videoEnd) videoEnd = timestamp + duration;
            Check(writer->WriteSample(video, sample.Get()));
        }
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
    }
    while (audioReady) audioReady = pcm.WriteNext(writer.Get(), audio, videoEnd);
    Check(writer->Finalize());
}

int wmain(int argc, wchar_t** argv)
{
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) return 1;
    int result = 0;
    try
    {
        if (argc == 2 && wcscmp(argv[1], L"apps") == 0) ListAudioProcesses(false);
        else if (argc == 2 && wcscmp(argv[1], L"levels") == 0) ListAudioProcesses(true);
        else if (argc == 5 && wcscmp(argv[1], L"mux") == 0)
        {
            Check(MFStartup(MF_VERSION));
            try { Mux(argv[2], argv[3], argv[4]); }
            catch (...) { MFShutdown(); throw; }
            MFShutdown();
        }
        else return 2;
    }
    catch (HRESULT failure)
    {
        std::cerr << "Media Foundation: 0x" << std::hex << static_cast<unsigned long>(failure) << '\n';
        result = 1;
    }
    catch (const std::exception& error)
    {
        std::cerr << error.what() << '\n';
        result = 1;
    }
    CoUninitialize();
    return result;
}
