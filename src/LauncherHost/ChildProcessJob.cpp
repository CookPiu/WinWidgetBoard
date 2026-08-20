#include "ChildProcessJob.h"

namespace winwidgetboard::launcher
{
ChildProcessJob::~ChildProcessJob()
{
    Close();
}

bool ChildProcessJob::Create(std::wstring& error)
{
    error.clear();
    if (_job != nullptr)
    {
        return true;
    }

    _job = CreateJobObjectW(nullptr, nullptr);
    if (_job == nullptr)
    {
        error = L"CreateJobObjectW failed: " + std::to_wstring(GetLastError());
        return false;
    }

    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (SetInformationJobObject(
            _job,
            JobObjectExtendedLimitInformation,
            &limits,
            sizeof(limits)) == FALSE)
    {
        error = L"SetInformationJobObject(KILL_ON_JOB_CLOSE) failed: " +
            std::to_wstring(GetLastError());
        CloseHandle(_job);
        _job = nullptr;
        return false;
    }

    return true;
}

bool ChildProcessJob::Assign(const HANDLE process, std::wstring& error) const
{
    error.clear();
    if (_job == nullptr)
    {
        error = L"child process job is unavailable";
        return false;
    }

    // Nested jobs have been supported since Windows 8, so this still succeeds when the
    // launcher itself was started inside somebody else's job.
    if (AssignProcessToJobObject(_job, process) == FALSE)
    {
        error = L"AssignProcessToJobObject failed: " + std::to_wstring(GetLastError());
        return false;
    }

    return true;
}

void ChildProcessJob::Close()
{
    if (_job != nullptr)
    {
        CloseHandle(_job);
        _job = nullptr;
    }
}
}
