// Included by native_host.cpp so the observer uses the same guarded paths.
namespace Playtime
{
constexpr wchar_t FileName[] = L"time.json";
constexpr DWORD Interval = 30000;
constexpr std::size_t Limit = 1024 * 1024;
constexpr std::uint64_t MaxDuration = INT64_MAX;

std::uint64_t Number(std::string_view value, std::uint64_t maximum)
{
    std::uint64_t result = 0;
    if (value.empty()) throw Failure(L"Missing playtime number");
    for (const auto digit : value)
    {
        if (digit < '0' || digit > '9' || result > (maximum - (digit - '0')) / 10)
            throw Failure(L"Invalid playtime number");
        result = result * 10 + digit - '0';
    }
    return result;
}

bool RepositoryCharacter(char c)
{
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
        c == '-' || c == '_' || c == '.';
}

bool ValidKey(std::string_view key)
{
    return !key.empty() && key.size() <= 100 && key != "." && key != ".." &&
        std::all_of(key.begin(), key.end(), RepositoryCharacter);
}

struct RepositoryLess
{
    bool operator()(std::string_view left, std::string_view right) const
    {
        const auto lower = [](char c) { return c >= 'A' && c <= 'Z' ? c - 'A' + 'a' : c; };
        return std::lexicographical_compare(left.begin(), left.end(), right.begin(), right.end(),
            [&](char a, char b) { return lower(a) < lower(b); });
    }
};

using Totals = std::map<std::string, std::uint64_t, RepositoryLess>;

// The time file is a flat JSON object: ASCII repository names and integer
// millisecond values. No other JSON value types belong in this file.
class TimeJson
{
public:
    explicit TimeJson(std::string_view data) : remaining_(data)
    {
        if (remaining_.substr(0, 3) == "\xef\xbb\xbf") remaining_.remove_prefix(3);
    }

    Totals Parse()
    {
        Require('{');
        Totals totals;
        if (!Take('}'))
        {
            do
            {
                auto key = ReadKey();
                Require(':');
                SkipWhitespace();
                const auto digits = remaining_.find_first_not_of("0123456789");
                const auto number = remaining_.substr(0, digits);
                if (number.size() > 1 && number.front() == '0') Invalid();
                if (!totals.emplace(std::move(key), Number(number, MaxDuration)).second) Invalid();
                remaining_.remove_prefix(number.size());
            } while (Take(','));
            Require('}');
        }
        SkipWhitespace();
        if (!remaining_.empty()) Invalid();
        return totals;
    }

private:
    [[noreturn]] static void Invalid() { throw Failure(L"Invalid playtime JSON"); }

    void SkipWhitespace()
    {
        const auto first = remaining_.find_first_not_of(" \t\r\n");
        remaining_.remove_prefix(std::min(first, remaining_.size()));
    }

    bool Take(char expected)
    {
        SkipWhitespace();
        if (remaining_.empty() || remaining_.front() != expected) return false;
        remaining_.remove_prefix(1);
        return true;
    }

    void Require(char expected) { if (!Take(expected)) Invalid(); }

    std::string ReadKey()
    {
        Require('"');
        std::string key;
        while (!remaining_.empty())
        {
            auto c = remaining_.front();
            remaining_.remove_prefix(1);
            if (c == '"')
            {
                if (!ValidKey(key)) Invalid();
                return key;
            }
            if (c == '\\')
            {
                if (remaining_.size() < 5 || remaining_.front() != 'u') Invalid();
                unsigned int value = 0;
                for (const auto digit : remaining_.substr(1, 4))
                {
                    value *= 16;
                    if (digit >= '0' && digit <= '9') value += digit - '0';
                    else if (digit >= 'a' && digit <= 'f') value += digit - 'a' + 10;
                    else if (digit >= 'A' && digit <= 'F') value += digit - 'A' + 10;
                    else Invalid();
                }
                if (value > 127) Invalid();
                c = static_cast<char>(value);
                remaining_.remove_prefix(5);
            }
            if (!RepositoryCharacter(c) || key.size() == 100) Invalid();
            key.push_back(c);
        }
        Invalid();
    }

    std::string_view remaining_;
};

Totals Load(HANDLE parent)
{
    auto file = OpenRelative(parent, FileName, FILE_READ_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
        FILE_SHARE_READ | FILE_SHARE_DELETE, OpenExisting, false, true);
    if (!file) return {};
    LARGE_INTEGER length{};
    if (!GetFileSizeEx(file.get(), &length) || length.QuadPart <= 0 || length.QuadPart > Limit)
        throw Failure(L"Invalid playtime file size");
    std::string data(static_cast<std::size_t>(length.QuadPart), '\0');
    DWORD read = 0;
    if (!ReadFile(file.get(), data.data(), static_cast<DWORD>(data.size()), &read, nullptr) || read != data.size())
        throw Failure(L"Could not read playtime JSON");
    return TimeJson(data).Parse();
}

void Save(HANDLE parent, const Totals& totals)
{
    std::string data = "{";
    std::uint64_t sum = 0;
    for (const auto& [key, value] : totals)
    {
        if (value > MaxDuration - sum) throw Failure(L"Playtime total overflow");
        sum += value;
        if (data.size() != 1) data += ',';
        data += "\n  \"" + key + "\": " + std::to_string(value);
    }
    data += "\n}\n";
    if (data.size() > Limit) throw Failure(L"Playtime file too large");
    GUID guid{};
    wchar_t id[40]{};
    if (FAILED(CoCreateGuid(&guid)) || !StringFromGUID2(guid, id, 40)) throw Failure(L"Could not name playtime checkpoint");
    const auto name = std::wstring(L".time.json.") + id + L".tmp";
    auto file = OpenRelative(parent, name, FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | DELETE | SYNCHRONIZE,
        FILE_SHARE_READ | FILE_SHARE_DELETE, CreateNew, false);
    try
    {
        DWORD written = 0;
        if (!WriteFile(file.get(), data.data(), static_cast<DWORD>(data.size()), &written, nullptr) ||
            written != data.size() || !FlushFileBuffers(file.get())) Fail(L"Write playtime checkpoint");
        // A leaf name in FileRenameInformation is relative to the opened
        // source's parent and works while the directory guard is held.
        alignas(FILE_RENAME_INFO) std::array<unsigned char, sizeof(FILE_RENAME_INFO) + sizeof(FileName)> renameBuffer{};
        auto* rename = reinterpret_cast<FILE_RENAME_INFO*>(renameBuffer.data());
        rename->ReplaceIfExists = TRUE;
        rename->FileNameLength = sizeof(FileName) - sizeof(wchar_t);
        std::memcpy(rename->FileName, FileName, sizeof(FileName));
        using RenameFile = LONG(NTAPI*)(HANDLE, NativeStatus*, PVOID, ULONG, ULONG);
        static const auto renameFile = reinterpret_cast<RenameFile>(
            GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtSetInformationFile"));
        NativeStatus status{};
        const auto result = renameFile ? renameFile(file.get(), &status, rename, static_cast<ULONG>(renameBuffer.size()), 10)
            : static_cast<LONG>(0xc0000002);
        if (result < 0) Fail(L"Publish playtime checkpoint", static_cast<unsigned long>(result));
    }
    catch (...) { DeleteOpened(file.get()); throw; }
}

bool Acquire(HANDLE mutex, DWORD timeout)
{
    const auto result = WaitForSingleObject(mutex, timeout);
    return result == WAIT_OBJECT_0 || result == WAIT_ABANDONED;
}

class Observer
{
public:
    Observer(Directories& directories, std::string key, ULONGLONG start)
        : directories_(directories), parent_(directories.Get(L"Minecraft/Personal")),
          key_(std::move(key)), savedUntil_(start)
    {
        BY_HANDLE_FILE_INFORMATION identity{};
        if (!GetFileInformationByHandle(parent_, &identity)) Fail(L"Identify playtime directory");
        const auto name = L"Global\\LANMinecraft.Playtime.File." + std::to_wstring(identity.dwVolumeSerialNumber) +
            L"." + std::to_wstring(identity.nFileIndexHigh) + L"." + std::to_wstring(identity.nFileIndexLow);
        mutex_ = Handle(CreateMutexW(nullptr, FALSE, name.c_str()));
        if (!mutex_) Fail(L"Create playtime file lock");
    }

    static ULONGLONG Now()
    {
        ULONGLONG timestamp = 0;
        if (!QueryUnbiasedInterruptTime(&timestamp)) Fail(L"Read playtime clock");
        return timestamp;
    }

    bool Checkpoint(ULONGLONG until) noexcept
    {
        if (until < savedUntil_) return false;
        const auto delta = (until - savedUntil_) / 10000;
        if (written_ && delta == 0) return true;
        if (!Acquire(mutex_.get(), 250)) return false;
        bool success = false;
        try
        {
            auto totals = Load(parent_);
            auto [entry, inserted] = totals.try_emplace(key_, 0);
            if (delta > MaxDuration - entry->second) throw Failure(L"Playtime duration overflow");
            entry->second += delta;
            if (inserted || delta != 0) Save(parent_, totals);
            savedUntil_ += delta * 10000;
            written_ = success = true;
            reportedError_ = false;
        }
        catch (const Failure& error)
        {
            if (!reportedError_)
            {
                try { if (!logHandle) OpenLog(directories_); Log(L"Playtime checkpoint failed: " + error.text()); }
                catch (...) { }
                reportedError_ = true;
            }
        }
        catch (...) { }
        ReleaseMutex(mutex_.get());
        return success;
    }

    static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam)
    {
        if (message == WM_NCCREATE)
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(
                reinterpret_cast<CREATESTRUCTW*>(lparam)->lpCreateParams));
        auto* observer = reinterpret_cast<Observer*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_QUERYENDSESSION) return TRUE;
        if (message == WM_ENDSESSION && wparam && observer)
        {
            try { observer->Checkpoint(Now()); } catch (...) { }
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(window, message, wparam, lparam);
    }

private:
    Directories& directories_;
    HANDLE parent_;
    std::string key_;
    ULONGLONG savedUntil_;
    Handle mutex_;
    bool written_ = false;
    bool reportedError_ = false;
};

int Run(HINSTANCE instance, Directories& directories, const std::vector<std::wstring>& args)
{
    if (args.size() != 5) return ERROR_INVALID_PARAMETER;
    const auto ascii = [](const std::wstring& value)
    {
        if (value.size() > 4096 || !std::all_of(value.begin(), value.end(), [](wchar_t ch) { return ch < 128; }))
            throw Failure(L"Invalid playtime argument");
        std::string text;
        text.reserve(value.size());
        for (const auto ch : value) text.push_back(static_cast<char>(ch));
        return text;
    };
    const auto pid = static_cast<DWORD>(Number(ascii(args[1]), MAXDWORD));
    const auto birth = Number(ascii(args[2]), UINT64_MAX);
    const auto start = Number(ascii(args[3]), UINT64_MAX);
    const auto key = ascii(args[4]);
    if (!ValidKey(key) || start > Observer::Now()) return ERROR_INVALID_PARAMETER;
    Handle game(OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!game || !GetProcessTimes(game.get(), &created, &exited, &kernel, &user) ||
        (static_cast<std::uint64_t>(created.dwHighDateTime) << 32 | created.dwLowDateTime) != birth)
        return ERROR_NOT_FOUND;
    const auto name = L"Local\\LANMinecraft.Playtime.Game." + std::to_wstring(pid) + L"." + std::to_wstring(birth);
    Handle single(CreateMutexW(nullptr, FALSE, name.c_str()));
    if (!single) return ERROR_ACCESS_DENIED;
    if (!Acquire(single.get(), 0)) return ERROR_ALREADY_EXISTS;
    Observer observer(directories, key, start);
    WNDCLASSW windowClass{};
    windowClass.hInstance = instance;
    windowClass.lpszClassName = L"LANMinecraft.Playtime";
    windowClass.lpfnWndProc = Observer::WindowProc;
    if (!RegisterClassW(&windowClass)) Fail(L"Register playtime shutdown notification");
    const auto window = CreateWindowExW(WS_EX_TOOLWINDOW, windowClass.lpszClassName, L"", WS_POPUP,
        0, 0, 0, 0, nullptr, nullptr, instance, &observer);
    if (!window) Fail(L"Create playtime shutdown notification");
    observer.Checkpoint(start);
    const auto handle = game.get();
    auto next = start + Interval * 10000ULL;
    for (;;)
    {
        const auto now = Observer::Now();
        const auto wait = static_cast<DWORD>(now >= next ? 0 : (next - now + 9999) / 10000);
        const auto result = MsgWaitForMultipleObjectsEx(1, &handle, wait, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        if (result == WAIT_OBJECT_0)
        {
            const auto stop = Observer::Now();
            for (int attempt = 0; attempt < 3 && !observer.Checkpoint(stop); ++attempt) { }
            break;
        }
        if (result == WAIT_TIMEOUT)
        {
            const auto checkpoint = Observer::Now();
            observer.Checkpoint(checkpoint);
            next = checkpoint + Interval * 10000ULL;
        }
        else if (result == WAIT_OBJECT_0 + 1)
        {
            MSG message{};
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            {
                if (message.message == WM_QUIT) return 0;
                DispatchMessageW(&message);
            }
        }
        else Fail(L"Wait for game process");
    }
    DestroyWindow(window);
    ReleaseMutex(single.get());
    return 0;
}
}
