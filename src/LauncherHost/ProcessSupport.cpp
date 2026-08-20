#include "ProcessSupport.h"

#include <array>
#include <vector>

namespace winwidgetboard::launcher
{
bool IsUsableExecutablePath(const std::wstring& path)
{
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES &&
        (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

bool GetEnvironmentValue(const wchar_t* const name, std::wstring& value)
{
    const DWORD requiredLength = GetEnvironmentVariableW(name, nullptr, 0);
    if (requiredLength == 0)
    {
        value.clear();
        return false;
    }

    std::vector<wchar_t> buffer(requiredLength);
    const DWORD copiedLength = GetEnvironmentVariableW(
        name,
        buffer.data(),
        static_cast<DWORD>(buffer.size()));
    if (copiedLength == 0 || copiedLength >= buffer.size())
    {
        value.clear();
        return false;
    }

    value.assign(buffer.data(), copiedLength);
    return true;
}

bool GetModuleDirectory(std::wstring& directory, std::wstring& error)
{
    std::array<wchar_t, 32768> buffer{};
    const DWORD length = GetModuleFileNameW(
        nullptr,
        buffer.data(),
        static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size())
    {
        error = L"GetModuleFileNameW failed";
        return false;
    }

    const std::wstring modulePath(buffer.data(), length);
    const std::wstring::size_type separator = modulePath.find_last_of(L"\\/");
    if (separator == std::wstring::npos)
    {
        error = L"LauncherHost module directory is unavailable";
        return false;
    }

    directory = modulePath.substr(0, separator);
    return true;
}

std::wstring QuoteExecutablePath(const std::wstring& path)
{
    return L"\"" + path + L"\"";
}

bool ResolveChildExecutablePath(
    const wchar_t* const overrideEnvironmentVariable,
    const wchar_t* const componentDirectory,
    const wchar_t* const executableName,
    std::wstring& path,
    std::wstring& error)
{
    std::wstring overridePath;
    if (GetEnvironmentValue(overrideEnvironmentVariable, overridePath))
    {
        if (!IsUsableExecutablePath(overridePath))
        {
            error = std::wstring(overrideEnvironmentVariable) +
                L" does not point to a file";
            return false;
        }

        path = overridePath;
        return true;
    }

    std::wstring moduleDirectory;
    if (!GetModuleDirectory(moduleDirectory, error))
    {
        return false;
    }

    const std::wstring separator(1, L'\\');
    path = moduleDirectory + separator + executableName;
    if (IsUsableExecutablePath(path))
    {
        return true;
    }

    path = moduleDirectory + separator + componentDirectory + separator + executableName;
    if (IsUsableExecutablePath(path))
    {
        return true;
    }

    error = std::wstring(executableName) +
        L" was not found beside LauncherHost or under " +
        std::wstring(componentDirectory) +
        L"; set " +
        std::wstring(overrideEnvironmentVariable) +
        L" for a development build";
    return false;
}
}
