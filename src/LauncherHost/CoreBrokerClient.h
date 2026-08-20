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

    // Last weather reading the broker reported. Both fields are empty until the broker has
    // completed one successful refresh.
    struct WeatherSummary
    {
        // Rounded whole degrees Celsius, already formatted by the broker.
        std::wstring temperature;
        // A WeatherConditionContract token; the entry maps it to a drawn glyph.
        std::string conditionIconId;

        [[nodiscard]] bool HasReading() const noexcept
        {
            return !temperature.empty();
        }
    };

    // Last weather reading the broker reported, or an empty summary when there is none.
    // Safe to call from the UI thread; the value is refreshed on the worker thread.
    [[nodiscard]] WeatherSummary GetWeatherSummary() const;

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
    static std::wstring WideFromUtf8(std::string_view value);
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
    bool RefreshWeatherSummary();
    void WaitForWake(ULONGLONG milliseconds);
    void ClosePipe();

    HANDLE _pipe{INVALID_HANDLE_VALUE};
    ULONGLONG _nextConnectionAttemptTick{};
    ULONGLONG _nextHeartbeatTick{};
    ULONGLONG _nextWeatherTick{};
    mutable std::mutex _weatherMutex;
    WeatherSummary _weatherSummary;
    std::atomic<bool> _stopRequested{};
    std::atomic<bool> _connected{};
    std::condition_variable _wakeCondition;
    std::mutex _wakeMutex;
    std::thread _worker;
};
}
