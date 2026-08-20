#pragma once

#include <windows.h>

#include <string>

namespace winwidgetboard::launcher
{
// Shared by every child process the launcher starts. Kept in one place so the panel and the
// broker cannot drift apart on path resolution or environment reading.

// The session token travels only through the environment, never through a command line:
// command lines are readable by any process on the machine, environment blocks are not.
inline constexpr wchar_t kSessionTokenEnvironmentVariable[] =
    L"WINWIDGETBOARD_COREBROKER_SESSION_TOKEN";

bool IsUsableExecutablePath(const std::wstring& path);

bool GetEnvironmentValue(const wchar_t* name, std::wstring& value);

bool GetModuleDirectory(std::wstring& directory, std::wstring& error);

std::wstring QuoteExecutablePath(const std::wstring& path);

// Resolves a child executable, in order: an explicit environment override, then beside
// LauncherHost, then a `componentDirectory` subfolder next to it. The override exists for
// development builds, where the three projects write to three different output directories;
// the subfolder is the installed layout, which keeps each .NET application's dependencies
// in its own directory instead of flattening three of them into one.
bool ResolveChildExecutablePath(
    const wchar_t* overrideEnvironmentVariable,
    const wchar_t* componentDirectory,
    const wchar_t* executableName,
    std::wstring& path,
    std::wstring& error);
}
