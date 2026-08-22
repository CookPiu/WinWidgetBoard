#include "CoreBrokerClient.h"

#include "ProcessSupport.h"

#include <objbase.h>

#include <array>
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cwctype>
#include <exception>
#include <string>
#include <system_error>
#include <vector>

namespace winwidgetboard::launcher
{
namespace
{
constexpr wchar_t kPipePath[] = L"\\\\.\\pipe\\WinWidgetBoard.CoreBroker.v1";
constexpr DWORD kConnectTimeoutMilliseconds = 50;
constexpr DWORD kTransferTimeoutMilliseconds = 100;
constexpr ULONGLONG kConnectionStartupTimeoutMilliseconds = 1000;
constexpr DWORD kConnectionRetryDelayMilliseconds = 25;
constexpr ULONGLONG kConnectionRetryMilliseconds = 1000;
constexpr ULONGLONG kHeartbeatIntervalMilliseconds = 5000;
// The broker refreshes weather hourly while the panel is closed, so once a minute is already
// far more often than the projection can change. Before the first reading exists - broker
// just started, network still coming up - poll faster so the entry fills in promptly.
constexpr ULONGLONG kWeatherPollIntervalMilliseconds = 60000;
// The hardware monitor is the one reading where a stale number is a wrong number, so it polls
// at the provider's own cadence rather than at the weather's leisurely one.
constexpr ULONGLONG kMonitorPollIntervalMilliseconds = 2000;
constexpr ULONGLONG kWeatherInitialPollIntervalMilliseconds = 3000;
constexpr uint32_t kMaxMessageBytes = 1024 * 1024;

bool IsWhitespace(const char value)
{
    return value == ' ' || value == '\t' || value == '\r' || value == '\n';
}

size_t SkipWhitespace(const std::string_view value, size_t position)
{
    while (position < value.size() && IsWhitespace(value[position]))
    {
        ++position;
    }
    return position;
}

bool HasFieldPrefix(
    const std::string_view json,
    const std::string_view field,
    size_t& valuePosition)
{
    const std::string key = "\"" + std::string(field) + "\"";
    const size_t keyPosition = json.find(key);
    if (keyPosition == std::string_view::npos)
    {
        return false;
    }

    size_t position = SkipWhitespace(json, keyPosition + key.size());
    if (position >= json.size() || json[position] != ':')
    {
        return false;
    }

    valuePosition = SkipWhitespace(json, position + 1);
    return valuePosition < json.size();
}

bool IsTransientPipeConnectError(const DWORD error)
{
    return error == ERROR_FILE_NOT_FOUND ||
        error == ERROR_PIPE_BUSY ||
        error == ERROR_SEM_TIMEOUT;
}

}

CoreBrokerClient::~CoreBrokerClient()
{
    Close();
}

void CoreBrokerClient::Poll()
{
    std::wstring sessionToken;
    if (!ReadEnvironmentValue(
            kSessionTokenEnvironmentVariable,
            sessionToken))
    {
        return;
    }

    if (_worker.joinable())
    {
        return;
    }

    _stopRequested.store(false, std::memory_order_release);
    try
    {
        _worker = std::thread(&CoreBrokerClient::RunLoop, this);
    }
    catch (const std::system_error&)
    {
        _stopRequested.store(true, std::memory_order_release);
    }
}

void CoreBrokerClient::Close()
{
    _stopRequested.store(true, std::memory_order_release);
    _wakeCondition.notify_all();
    if (_worker.joinable())
    {
        _worker.join();
    }
    ClosePipe();
}

void CoreBrokerClient::RunLoop()
{
    while (!_stopRequested.load(std::memory_order_acquire))
    {
        std::wstring sessionToken;
        if (!ReadEnvironmentValue(
                kSessionTokenEnvironmentVariable,
                sessionToken))
        {
            ClosePipe();
            WaitForWake(kConnectionRetryMilliseconds);
            continue;
        }

        const ULONGLONG now = GetTickCount64();
        if (!IsConnected())
        {
            if (now < _nextConnectionAttemptTick)
            {
                WaitForWake(_nextConnectionAttemptTick - now);
                continue;
            }

            if (!ConnectAndHandshake(sessionToken))
            {
                _nextConnectionAttemptTick = now + kConnectionRetryMilliseconds;
                WaitForWake(kConnectionRetryMilliseconds);
                continue;
            }

            _nextHeartbeatTick = now + kHeartbeatIntervalMilliseconds;
        }
        else if (now >= _nextHeartbeatTick)
        {
            std::string response;
            if (!SendRequest("session.ping", "{}", response))
            {
                ClosePipe();
                _nextConnectionAttemptTick = now + kConnectionRetryMilliseconds;
            }
            else
            {
                _nextHeartbeatTick = now + kHeartbeatIntervalMilliseconds;
            }
        }

        if (IsConnected() &&
            _monitorEnabled.load(std::memory_order_acquire) &&
            now >= _nextMonitorTick)
        {
            RefreshSystemMonitorSummary();
            _nextMonitorTick = now + kMonitorPollIntervalMilliseconds;
        }

        if (IsConnected() && now >= _nextWeatherTick)
        {
            const bool hasReading = RefreshWeatherSummary();
            _nextWeatherTick = now + (hasReading
                ? kWeatherPollIntervalMilliseconds
                : kWeatherInitialPollIntervalMilliseconds);
        }

        WaitForWake(100);
    }

    ClosePipe();
}

bool CoreBrokerClient::RefreshWeatherSummary()
{
    std::string response;
    if (!SendRequest(
            "weather.summary.get",
            "{\"instanceId\":\"demo.weather\"}",
            response))
    {
        ClosePipe();
        return false;
    }

    // The location is deliberately not read: the entry shows a condition glyph and the
    // temperature, because the user already knows which place they configured.
    std::string temperature;
    std::string conditionIconId;
    WeatherSummary summary;
    if (FindJsonString(response, "temperatureText", temperature) &&
        !temperature.empty())
    {
        summary.temperature = WideFromUtf8(temperature);
        if (FindJsonString(response, "conditionIconId", conditionIconId))
        {
            summary.conditionIconId = std::move(conditionIconId);
        }
    }

    const bool hasReading = summary.HasReading();
    std::lock_guard lock(_weatherMutex);
    _weatherSummary = std::move(summary);
    return hasReading;
}

void CoreBrokerClient::SetSystemMonitorEnabled(const bool enabled)
{
    const bool previous = _monitorEnabled.exchange(enabled, std::memory_order_acq_rel);
    if (previous == enabled)
    {
        return;
    }

    if (!enabled)
    {
        std::lock_guard lock(_monitorMutex);
        _monitorSegments.clear();
        return;
    }

    // Ask on the next loop pass rather than waiting out a full interval, so switching the
    // entry to hardware does not show an empty strip for two seconds.
    _nextMonitorTick = 0;
    _wakeCondition.notify_all();
}

std::vector<CoreBrokerClient::MonitorSegment>
CoreBrokerClient::GetSystemMonitorSegments() const
{
    std::lock_guard lock(_monitorMutex);
    return _monitorSegments;
}

bool CoreBrokerClient::RefreshSystemMonitorSummary()
{
    std::string response;
    if (!SendRequest(
            "sysmon.summary.get",
            "{\"instanceId\":\"demo.sysmon\"}",
            response))
    {
        ClosePipe();
        return false;
    }

    std::vector<MonitorSegment> segments;
    if (!ParseMonitorSegments(response, segments))
    {
        return false;
    }

    std::lock_guard lock(_monitorMutex);
    _monitorSegments = std::move(segments);
    return true;
}

// A deliberately small reader for one known shape: the segments array of
// sysmon.summary.get. LauncherHost has no JSON library and is not getting one for this.
bool CoreBrokerClient::ParseMonitorSegments(
    const std::string_view response,
    std::vector<MonitorSegment>& segments)
{
    constexpr std::string_view kSegmentsKey = "\"segments\":[";
    const size_t arrayStart = response.find(kSegmentsKey);
    if (arrayStart == std::string_view::npos)
    {
        return false;
    }

    size_t cursor = arrayStart + kSegmentsKey.size();
    while (cursor < response.size())
    {
        const size_t objectStart = response.find('{', cursor);
        if (objectStart == std::string_view::npos)
        {
            break;
        }

        const size_t objectEnd = response.find('}', objectStart);
        if (objectEnd == std::string_view::npos)
        {
            break;
        }

        // Stop at the end of this array rather than wandering into the next object in the
        // envelope: a closing bracket before the next brace means the array is done.
        const size_t arrayEnd = response.find(']', cursor);
        if (arrayEnd != std::string_view::npos && arrayEnd < objectStart)
        {
            break;
        }

        const std::string_view object =
            response.substr(objectStart, objectEnd - objectStart + 1);
        std::string iconId;
        std::string text;
        if (FindJsonString(object, "iconId", iconId) &&
            FindJsonString(object, "text", text))
        {
            MonitorSegment segment;
            segment.iconId = std::move(iconId);
            segment.text = WideFromUtf8(UnescapeJson(text));
            // Absent or malformed history is not an error: the reading still displays, just
            // without a graph behind it.
            FindJsonNumberArray(object, "history", segment.history);
            segments.push_back(std::move(segment));
        }

        cursor = objectEnd + 1;
    }

    return true;
}

std::string CoreBrokerClient::UnescapeJson(const std::string_view value)
{
    std::string result;
    result.reserve(value.size());
    for (size_t index = 0; index < value.size(); ++index)
    {
        if (value[index] != '\\' || index + 1 >= value.size())
        {
            result.push_back(value[index]);
            continue;
        }

        const char next = value[index + 1];
        switch (next)
        {
        case '"':
        case '\\':
        case '/':
            result.push_back(next);
            ++index;
            break;
        case 'n':
            result.push_back('\n');
            ++index;
            break;
        case 't':
            result.push_back('\t');
            ++index;
            break;
        default:
            result.push_back(value[index]);
            break;
        }
    }

    return result;
}

std::wstring CoreBrokerClient::WideFromUtf8(const std::string_view value)
{
    if (value.empty())
    {
        return {};
    }

    const int required = MultiByteToWideChar(
        CP_UTF8,
        0,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0);
    if (required <= 0)
    {
        return {};
    }

    std::wstring wide;
    wide.resize(static_cast<size_t>(required));
    if (MultiByteToWideChar(
            CP_UTF8,
            0,
            value.data(),
            static_cast<int>(value.size()),
            wide.data(),
            required) != required)
    {
        return {};
    }

    return wide;
}

CoreBrokerClient::WeatherSummary CoreBrokerClient::GetWeatherSummary() const
{
    std::lock_guard lock(_weatherMutex);
    return _weatherSummary;
}

void CoreBrokerClient::WaitForWake(const ULONGLONG milliseconds)
{
    const ULONGLONG boundedMilliseconds = std::min<ULONGLONG>(
        milliseconds,
        1000);
    std::unique_lock lock(_wakeMutex);
    _wakeCondition.wait_for(
        lock,
        std::chrono::milliseconds(boundedMilliseconds),
        [this]
        {
            return _stopRequested.load(std::memory_order_acquire);
        });
}

void CoreBrokerClient::ClosePipe()
{
    _connected.store(false, std::memory_order_release);
    if (_pipe == INVALID_HANDLE_VALUE)
    {
        return;
    }

    CancelIoEx(_pipe, nullptr);
    CloseHandle(_pipe);
    _pipe = INVALID_HANDLE_VALUE;
}

bool CoreBrokerClient::RunSmokeTest(std::wstring& error)
{
    error.clear();
    std::wstring sessionToken;
    if (!ReadEnvironmentValue(
            kSessionTokenEnvironmentVariable,
            sessionToken))
    {
        error = L"WINWIDGETBOARD_COREBROKER_SESSION_TOKEN is missing";
        return false;
    }

    Close();
    if (!ConnectAndHandshake(sessionToken))
    {
        error = L"CoreBroker session.hello failed";
        Close();
        return false;
    }

    std::string response;
    if (!SendRequest("session.ping", "{}", response))
    {
        error = L"CoreBroker session.ping failed";
        Close();
        return false;
    }

    Close();
    return true;
}

bool CoreBrokerClient::ConnectAndHandshake(const std::wstring& sessionToken)
{
    ClosePipe();
    const ULONGLONG deadline =
        GetTickCount64() + kConnectionStartupTimeoutMilliseconds;
    while (true)
    {
        if (!WaitNamedPipeW(kPipePath, kConnectTimeoutMilliseconds))
        {
            const DWORD error = GetLastError();
            if (!IsTransientPipeConnectError(error) || GetTickCount64() >= deadline)
            {
                return false;
            }

            Sleep(kConnectionRetryDelayMilliseconds);
            continue;
        }

        _pipe = CreateFileW(
            kPipePath,
            GENERIC_READ | GENERIC_WRITE,
            0,
            nullptr,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED,
            nullptr);
        if (_pipe != INVALID_HANDLE_VALUE)
        {
            break;
        }

        const DWORD error = GetLastError();
        if (!IsTransientPipeConnectError(error) || GetTickCount64() >= deadline)
        {
            return false;
        }

        Sleep(kConnectionRetryDelayMilliseconds);
    }

    const std::string messageId = CreateGuidText();
    const std::string sessionTokenUtf8 = Utf8FromWide(sessionToken);
    if (messageId.empty() || sessionTokenUtf8.empty())
    {
        ClosePipe();
        return false;
    }

    const std::string request = CreateEnvelope(
        "session.hello",
        "{\"clientType\":\"launcher-host\",\"clientVersion\":\"0.1.0\",\"processId\":" +
            std::to_string(GetCurrentProcessId()) +
            ",\"architecture\":\"x64\",\"sessionToken\":\"" +
            EscapeJson(sessionTokenUtf8) +
        "\",\"supportedProtocolRange\":{\"min\":\"1.0\",\"max\":\"1.0\"}}",
        messageId);

    if (!WriteFrame(request))
    {
        ClosePipe();
        return false;
    }

    std::string response;
    if (!ReadFrame(response) ||
        !ValidateResponse(response, "session.hello", messageId))
    {
        ClosePipe();
        return false;
    }

    _connected.store(true, std::memory_order_release);
    return true;
}

bool CoreBrokerClient::SendRequest(
    const std::string_view method,
    const std::string_view payload,
    std::string& response)
{
    if (!IsConnected())
    {
        return false;
    }

    const std::string messageId = CreateGuidText();
    if (messageId.empty())
    {
        return false;
    }

    const std::string request = method == "session.ping"
        ? CreatePingEnvelope(messageId)
        : CreateEnvelope(method, payload, messageId);
    if (!WriteFrame(request) || !ReadFrame(response))
    {
        return false;
    }

    return ValidateResponse(response, method, messageId);
}

bool CoreBrokerClient::WriteFrame(const std::string_view payload)
{
    if (payload.empty() || payload.size() > kMaxMessageBytes)
    {
        return false;
    }

    const uint32_t length = static_cast<uint32_t>(payload.size());
    std::array<unsigned char, sizeof(uint32_t)> header{
        static_cast<unsigned char>(length & 0xff),
        static_cast<unsigned char>((length >> 8) & 0xff),
        static_cast<unsigned char>((length >> 16) & 0xff),
        static_cast<unsigned char>((length >> 24) & 0xff),
    };
    return WriteExact(header.data(), header.size()) &&
        WriteExact(payload.data(), payload.size());
}

bool CoreBrokerClient::ReadFrame(std::string& payload)
{
    std::array<unsigned char, sizeof(uint32_t)> header{};
    if (!ReadExact(header.data(), header.size()))
    {
        return false;
    }

    const uint32_t length =
        static_cast<uint32_t>(header[0]) |
        (static_cast<uint32_t>(header[1]) << 8) |
        (static_cast<uint32_t>(header[2]) << 16) |
        (static_cast<uint32_t>(header[3]) << 24);
    if (length == 0 || length > kMaxMessageBytes)
    {
        return false;
    }

    std::vector<char> buffer(length);
    if (!ReadExact(buffer.data(), buffer.size()))
    {
        return false;
    }

    payload.assign(buffer.data(), buffer.size());
    return true;
}

bool CoreBrokerClient::WriteExact(const void* buffer, const size_t size)
{
    if (size == 0 || size > kMaxMessageBytes)
    {
        return false;
    }

    return Transfer(
        true,
        const_cast<void*>(buffer),
        static_cast<DWORD>(size));
}

bool CoreBrokerClient::ReadExact(void* buffer, const size_t size)
{
    if (size == 0 || size > kMaxMessageBytes)
    {
        return false;
    }

    return Transfer(
        false,
        buffer,
        static_cast<DWORD>(size));
}

bool CoreBrokerClient::Transfer(
    const bool write,
    void* buffer,
    const DWORD size)
{
    if (_pipe == INVALID_HANDLE_VALUE || buffer == nullptr || size == 0)
    {
        return false;
    }

    DWORD offset = 0;
    while (offset < size)
    {
        HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (event == nullptr)
        {
            return false;
        }

        OVERLAPPED overlapped{};
        overlapped.hEvent = event;
        DWORD transferred = 0;
        const DWORD remaining = size - offset;
        auto* current = static_cast<unsigned char*>(buffer) + offset;
        const BOOL completed = write
            ? WriteFile(
                _pipe,
                current,
                remaining,
                &transferred,
                &overlapped)
            : ReadFile(
                _pipe,
                current,
                remaining,
                &transferred,
                &overlapped);

        if (!completed)
        {
            const DWORD error = GetLastError();
            if (error != ERROR_IO_PENDING)
            {
                CloseHandle(event);
                return false;
            }

            const DWORD waitResult = WaitForSingleObject(
                event,
                kTransferTimeoutMilliseconds);
            if (waitResult != WAIT_OBJECT_0)
            {
                CancelIoEx(_pipe, &overlapped);
                WaitForSingleObject(event, INFINITE);
                CloseHandle(event);
                return false;
            }

            if (!GetOverlappedResult(
                    _pipe,
                    &overlapped,
                    &transferred,
                    FALSE))
            {
                CloseHandle(event);
                return false;
            }
        }

        CloseHandle(event);
        if (transferred == 0)
        {
            return false;
        }
        offset += transferred;
    }

    return true;
}

bool CoreBrokerClient::ValidateResponse(
    const std::string_view response,
    const std::string_view method,
    const std::string_view correlationId) const
{
    std::string value;
    return response.size() <= kMaxMessageBytes &&
        !response.empty() &&
        response.front() == '{' &&
        response.back() == '}' &&
        FindJsonString(response, "protocolVersion", value) &&
        value == "1.0" &&
        FindJsonString(response, "messageType", value) &&
        value == "response" &&
        FindJsonString(response, "messageId", value) &&
        !value.empty() &&
        FindJsonString(response, "sentAtUtc", value) &&
        !value.empty() &&
        FindJsonString(response, "method", value) &&
        value == method &&
        FindJsonString(response, "correlationId", value) &&
        value == correlationId &&
        HasJsonObject(response, "payload") &&
        HasJsonNull(response, "error");
}

bool CoreBrokerClient::ReadEnvironmentValue(
    const wchar_t* name,
    std::wstring& value)
{
    value.clear();
    const DWORD requiredLength = GetEnvironmentVariableW(name, nullptr, 0);
    if (requiredLength == 0)
    {
        return false;
    }

    std::vector<wchar_t> buffer(static_cast<size_t>(requiredLength) + 1);
    const DWORD copiedLength = GetEnvironmentVariableW(
        name,
        buffer.data(),
        static_cast<DWORD>(buffer.size()));
    if (copiedLength == 0 || copiedLength >= buffer.size())
    {
        return false;
    }

    value.assign(buffer.data(), copiedLength);
    return !value.empty();
}

std::string CoreBrokerClient::Utf8FromWide(const std::wstring_view value)
{
    if (value.empty())
    {
        return {};
    }

    const int requiredLength = WideCharToMultiByte(
        CP_UTF8,
        WC_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0,
        nullptr,
        nullptr);
    if (requiredLength <= 0)
    {
        return {};
    }

    std::string result(static_cast<size_t>(requiredLength), '\0');
    if (WideCharToMultiByte(
            CP_UTF8,
            WC_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            result.data(),
            requiredLength,
            nullptr,
            nullptr) != requiredLength)
    {
        return {};
    }

    return result;
}

std::string CoreBrokerClient::EscapeJson(const std::string_view value)
{
    constexpr char hex[] = "0123456789abcdef";
    std::string result;
    result.reserve(value.size());
    for (const unsigned char character : value)
    {
        switch (character)
        {
        case '"':
            result += "\\\"";
            break;
        case '\\':
            result += "\\\\";
            break;
        case '\b':
            result += "\\b";
            break;
        case '\f':
            result += "\\f";
            break;
        case '\n':
            result += "\\n";
            break;
        case '\r':
            result += "\\r";
            break;
        case '\t':
            result += "\\t";
            break;
        default:
            if (character < 0x20)
            {
                result += "\\u00";
                result += hex[(character >> 4) & 0x0f];
                result += hex[character & 0x0f];
            }
            else
            {
                result.push_back(static_cast<char>(character));
            }
            break;
        }
    }
    return result;
}

std::string CoreBrokerClient::CreateGuidText()
{
    GUID guid{};
    if (FAILED(CoCreateGuid(&guid)))
    {
        return {};
    }

    wchar_t buffer[40]{};
    const int length = StringFromGUID2(guid, buffer, _countof(buffer));
    if (length <= 3)
    {
        return {};
    }

    std::wstring value(buffer + 1, static_cast<size_t>(length - 3));
    std::transform(
        value.begin(),
        value.end(),
        value.begin(),
        [](const wchar_t character)
        {
            return static_cast<wchar_t>(towlower(character));
        });
    return Utf8FromWide(value);
}

std::string CoreBrokerClient::CreateUtcTimestamp()
{
    FILETIME fileTime{};
    SYSTEMTIME systemTime{};
    GetSystemTimeAsFileTime(&fileTime);
    if (!FileTimeToSystemTime(&fileTime, &systemTime))
    {
        return {};
    }

    char buffer[32]{};
    const int length = sprintf_s(
        buffer,
        sizeof(buffer),
        "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
        systemTime.wYear,
        systemTime.wMonth,
        systemTime.wDay,
        systemTime.wHour,
        systemTime.wMinute,
        systemTime.wSecond,
        systemTime.wMilliseconds);
    return length > 0 ? std::string(buffer, static_cast<size_t>(length)) : std::string{};
}

std::string CoreBrokerClient::CreateEnvelope(
    const std::string_view method,
    const std::string_view payload,
    const std::string_view messageId)
{
    const std::string timestamp = CreateUtcTimestamp();
    if (timestamp.empty())
    {
        return {};
    }

    std::string envelope =
        "{\"protocolVersion\":\"1.0\",\"messageType\":\"request\",\"messageId\":\"";
    envelope += messageId;
    envelope += "\",\"correlationId\":null,\"sentAtUtc\":\"";
    envelope += timestamp;
    envelope += "\",\"method\":\"";
    envelope += EscapeJson(method);
    envelope += "\",\"payload\":";
    envelope += payload;
    envelope += ",\"error\":null}";
    return envelope;
}

std::string CoreBrokerClient::CreatePingEnvelope(const std::string_view messageId)
{
    return CreateEnvelope("session.ping", "{}", messageId);
}

bool CoreBrokerClient::FindJsonString(
    const std::string_view json,
    const std::string_view field,
    std::string& value)
{
    value.clear();
    size_t position = 0;
    if (!HasFieldPrefix(json, field, position) || json[position] != '"')
    {
        return false;
    }

    ++position;
    while (position < json.size())
    {
        const char character = json[position++];
        if (character == '"')
        {
            return true;
        }
        if (character == '\\' || static_cast<unsigned char>(character) < 0x20)
        {
            return false;
        }
        value.push_back(character);
    }

    value.clear();
    return false;
}

bool CoreBrokerClient::HasJsonNull(
    const std::string_view json,
    const std::string_view field)
{
    size_t position = 0;
    if (!HasFieldPrefix(json, field, position))
    {
        return false;
    }
    return json.substr(position, 4) == "null";
}

// Reads a flat array of JSON numbers. Deliberately minimal, like the rest of this reader: the
// launcher links no JSON library on purpose (ADR-0008), and the one array it needs contains
// nothing but plain decimals the broker produced itself.
bool CoreBrokerClient::FindJsonNumberArray(
    const std::string_view json,
    const std::string_view field,
    std::vector<double>& values)
{
    values.clear();
    size_t position = 0;
    if (!HasFieldPrefix(json, field, position) || json[position] != '[')
    {
        return false;
    }

    ++position;
    while (position < json.size() && json[position] != ']')
    {
        if (json[position] == ',' || json[position] == ' ')
        {
            ++position;
            continue;
        }

        const size_t numberStart = position;
        while (position < json.size() &&
            json[position] != ',' &&
            json[position] != ']')
        {
            ++position;
        }

        const std::string number(json.substr(numberStart, position - numberStart));
        try
        {
            size_t consumed = 0;
            const double value = std::stod(number, &consumed);
            if (consumed != number.size())
            {
                values.clear();
                return false;
            }

            values.push_back(value);
        }
        catch (const std::exception&)
        {
            values.clear();
            return false;
        }
    }

    return position < json.size();
}

bool CoreBrokerClient::HasJsonObject(
    const std::string_view json,
    const std::string_view field)
{
    size_t position = 0;
    if (!HasFieldPrefix(json, field, position))
    {
        return false;
    }
    return json[position] == '{';
}
}
