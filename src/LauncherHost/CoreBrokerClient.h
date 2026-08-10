#pragma once

#include <windows.h>

#include <atomic>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>

namespace winwidgetboard::launcher
{
class CoreBrokerClient final
{
public:
    CoreBrokerClient() = default;
    CoreBrokerClient(const CoreBrokerClient&) = delete;
    CoreBrokerClient& operator=(const CoreBrokerClient&) = delete;

    ~CoreBrokerClient();

    void Poll();
    void Close();

    [[nodiscard]] bool IsConnected() const noexcept
    {
        return _connected.load(std::memory_order_acquire);
    }

    bool RunSmokeTest(std::wstring& error);

private:
    bool ConnectAndHandshake(const std::wstring& sessionToken);
    bool SendRequest(
        std::string_view method,
        std::string_view payload,
        std::string& response);
    bool WriteFrame(std::string_view payload);
    bool ReadFrame(std::string& payload);
    bool WriteExact(const void* buffer, size_t size);
    bool ReadExact(void* buffer, size_t size);
    bool Transfer(bool write, void* buffer, DWORD size);
    bool ValidateResponse(
        std::string_view response,
        std::string_view method,
        std::string_view correlationId) const;

    static bool ReadEnvironmentValue(
        const wchar_t* name,
        std::wstring& value);
    static std::string Utf8FromWide(std::wstring_view value);
    static std::string EscapeJson(std::string_view value);
    static std::string CreateGuidText();
    static std::string CreateUtcTimestamp();
    static std::string CreateEnvelope(
        std::string_view method,
        std::string_view payload,
        std::string_view messageId);
    static std::string CreatePingEnvelope(
        std::string_view messageId);
    static bool FindJsonString(
        std::string_view json,
        std::string_view field,
        std::string& value);
    static bool HasJsonNull(
        std::string_view json,
        std::string_view field);
    static bool HasJsonObject(
        std::string_view json,
        std::string_view field);
    void RunLoop();
    void WaitForWake(ULONGLONG milliseconds);
    void ClosePipe();

    HANDLE _pipe{INVALID_HANDLE_VALUE};
    ULONGLONG _nextConnectionAttemptTick{};
    ULONGLONG _nextHeartbeatTick{};
    std::atomic<bool> _stopRequested{};
    std::atomic<bool> _connected{};
    std::condition_variable _wakeCondition;
    std::mutex _wakeMutex;
    std::thread _worker;
};
}
