#pragma once

#include "ChildProcessJob.h"

#include <windows.h>

#include <string>

namespace winwidgetboard::launcher
{
// Starts CoreBroker so the entry is self-sufficient: a plain shortcut to LauncherHost brings
// the whole product up, with no wrapper script and no console window. The launcher still
// never links SQLite, HTTP or a managed runtime - it only creates the process.
class CoreBrokerProcess final
{
public:
    CoreBrokerProcess() = default;
    CoreBrokerProcess(const CoreBrokerProcess&) = delete;
    CoreBrokerProcess& operator=(const CoreBrokerProcess&) = delete;

    ~CoreBrokerProcess();

    // Starts the broker unless the session was already provisioned from outside - the
    // development scripts set the token themselves and start their own broker, and taking
    // over would give them two brokers with two different tokens. `diagnostic` explains
    // what happened either way and never contains the token.
    bool EnsureStarted(const ChildProcessJob& job, std::wstring& diagnostic);

    void Shutdown();

    [[nodiscard]] bool IsRunning() const noexcept
    {
        return _process != nullptr;
    }

private:
    void Reap();

    HANDLE _process{};
};

// Fills `token` with a fresh cryptographically random session token. Exposed for the smoke
// test; the value is opaque to both sides of the pipe.
bool CreateSessionToken(std::wstring& token, std::wstring& error);
}
