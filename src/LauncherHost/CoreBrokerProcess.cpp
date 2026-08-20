#include "CoreBrokerProcess.h"

#include "ProcessSupport.h"

#include <bcrypt.h>

#include <array>

namespace winwidgetboard::launcher
{
namespace
{
constexpr wchar_t kBrokerExecutableName[] = L"WinWidgetBoard.CoreBroker.exe";
constexpr wchar_t kBrokerPathEnvironmentVariable[] = L"WINWIDGETBOARD_COREBROKER";
// The installed layout keeps each .NET application in its own directory.
constexpr wchar_t kBrokerComponentDirectory[] = L"CoreBroker";
constexpr DWORD kBrokerSettleMilliseconds = 400;
constexpr size_t kSessionTokenByteCount = 32;
}

bool CreateSessionToken(std::wstring& token, std::wstring& error)
{
    error.clear();
    token.clear();

    std::array<unsigned char, kSessionTokenByteCount> bytes{};
    const NTSTATUS status = BCryptGenRandom(
        nullptr,
        bytes.data(),
        static_cast<ULONG>(bytes.size()),
        BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    if (status < 0)
    {
        error = L"BCryptGenRandom failed: " + std::to_wstring(status);
        return false;
    }

    constexpr wchar_t digits[] = L"0123456789abcdef";
    token.reserve(bytes.size() * 2);
    for (const unsigned char byte : bytes)
    {
        token.push_back(digits[byte >> 4]);
        token.push_back(digits[byte & 0x0F]);
    }

    return true;
}

CoreBrokerProcess::~CoreBrokerProcess()
{
    Shutdown();
}

bool CoreBrokerProcess::EnsureStarted(
    const ChildProcessJob& job,
    std::wstring& diagnostic)
{
    diagnostic.clear();

    std::wstring existingToken;
    if (GetEnvironmentValue(kSessionTokenEnvironmentVariable, existingToken))
    {
        diagnostic = L"session was provisioned externally; CoreBroker is not auto-started";
        return false;
    }

    std::wstring executablePath;
    if (!ResolveChildExecutablePath(
            kBrokerPathEnvironmentVariable,
            kBrokerComponentDirectory,
            kBrokerExecutableName,
            executablePath,
            diagnostic))
    {
        return false;
    }

    std::wstring token;
    if (!CreateSessionToken(token, diagnostic))
    {
        return false;
    }

    // Set it on ourselves first: our own broker client reads it from here, and both the
    // broker and the panel inherit this environment block when we create them.
    if (SetEnvironmentVariableW(
            kSessionTokenEnvironmentVariable,
            token.c_str()) == FALSE)
    {
        diagnostic = L"SetEnvironmentVariableW for the session token failed: " +
            std::to_wstring(GetLastError());
        return false;
    }
    SecureZeroMemory(token.data(), token.size() * sizeof(wchar_t));

    std::wstring commandLine = QuoteExecutablePath(executablePath);
    STARTUPINFOW startupInfo{};
    startupInfo.cb = sizeof(startupInfo);
    startupInfo.dwFlags = STARTF_USESHOWWINDOW;
    startupInfo.wShowWindow = SW_HIDE;

    PROCESS_INFORMATION processInfo{};
    if (CreateProcessW(
            executablePath.c_str(),
            commandLine.data(),
            nullptr,
            nullptr,
            FALSE,
            CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW | CREATE_SUSPENDED,
            nullptr,
            nullptr,
            &startupInfo,
            &processInfo) == FALSE)
    {
        diagnostic = L"CreateProcessW for CoreBroker failed: " +
            std::to_wstring(GetLastError());
        return false;
    }

    // Assign before the first instruction runs, so the broker cannot outlive the job even
    // if the launcher dies during startup.
    std::wstring jobError;
    if (!job.Assign(processInfo.hProcess, jobError))
    {
        diagnostic = L"CoreBroker was not bound to the launcher job: " + jobError;
    }

    ResumeThread(processInfo.hThread);
    CloseHandle(processInfo.hThread);
    _process = processInfo.hProcess;

    if (WaitForSingleObject(_process, kBrokerSettleMilliseconds) == WAIT_OBJECT_0)
    {
        DWORD exitCode = 0;
        GetExitCodeProcess(_process, &exitCode);
        Reap();
        diagnostic = L"CoreBroker exited immediately with code " +
            std::to_wstring(exitCode);
        return false;
    }

    if (diagnostic.empty())
    {
        diagnostic = L"CoreBroker started";
    }

    return true;
}

void CoreBrokerProcess::Shutdown()
{
    if (_process == nullptr)
    {
        return;
    }

    if (WaitForSingleObject(_process, 0) != WAIT_OBJECT_0)
    {
        // CoreBroker is a console-less worker with no window to close, and the job object
        // would kill it anyway; terminating here just makes the ordinary exit deterministic.
        TerminateProcess(_process, 0);
        WaitForSingleObject(_process, 2000);
    }

    Reap();
}

void CoreBrokerProcess::Reap()
{
    if (_process != nullptr)
    {
        CloseHandle(_process);
        _process = nullptr;
    }
}
}
