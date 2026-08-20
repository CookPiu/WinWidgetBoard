#pragma once

#include <windows.h>

#include <string>

namespace winwidgetboard::launcher
{
// Binds every child process the launcher starts to the launcher's own lifetime. The kernel
// closes this handle even when the launcher is terminated without running any cleanup code,
// and closing the last handle kills whatever is still inside the job. That is the only way
// a force-killed launcher cannot leave a resident panel or a broker behind.
class ChildProcessJob final
{
public:
    ChildProcessJob() = default;
    ChildProcessJob(const ChildProcessJob&) = delete;
    ChildProcessJob& operator=(const ChildProcessJob&) = delete;

    ~ChildProcessJob();

    // Failure is not fatal: the launcher keeps working and still shuts its children down
    // explicitly, it only loses the guarantee that covers a force kill.
    bool Create(std::wstring& error);

    bool Assign(HANDLE process, std::wstring& error) const;

    void Close();

    [[nodiscard]] bool IsCreated() const noexcept
    {
        return _job != nullptr;
    }

private:
    HANDLE _job{};
};
}
