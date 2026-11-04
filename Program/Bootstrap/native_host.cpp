// Copyright (c) LANMinecraft contributors. Native bootstrap uses only Windows
// APIs before loading the private .NET hosting components from a verified cache.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include <shellapi.h>
#include <objbase.h>
#include <dwmapi.h>
#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <exception>
#include <map>
#include <memory>
#include <set>
#include <string>
#include <utility>
#include <vector>

namespace
{
constexpr DWORD PayloadResource = 101;
constexpr std::uint64_t MaximumPayload = 512ull * 1024 * 1024;
constexpr std::size_t HeaderSize = 72;
constexpr std::uint32_t MaximumFiles = 4096;
constexpr std::uint32_t MaximumPathBytes = 1024;
constexpr ULONG ObjectCaseInsensitive = 0x40;
constexpr ULONG ObjectDontReparse = 0x1000;
constexpr ULONG OpenExisting = 1;
constexpr ULONG CreateNew = 2;
constexpr ULONG OpenOrCreate = 3;
constexpr ULONG DirectoryFile = 0x00000001;
constexpr ULONG SynchronousIo = 0x00000020;
constexpr ULONG NonDirectoryFile = 0x00000040;
constexpr ULONG OpenReparsePoint = 0x00200000;
constexpr LONG StatusNameNotFound = static_cast<LONG>(0xC0000034u);
constexpr LONG StatusPathNotFound = static_cast<LONG>(0xC000003Au);
using Digest = std::array<unsigned char, 32>;

class Failure : public std::exception
{
public:
    explicit Failure(std::wstring text, unsigned long code = 0) : text_(std::move(text)), code_(code) {}
    const std::wstring& text() const noexcept { return text_; }
    unsigned long code() const noexcept { return code_; }
private:
    std::wstring text_;
    unsigned long code_;
};

[[noreturn]] void Fail(const wchar_t* operation, unsigned long code = GetLastError())
{
    throw Failure(std::wstring(operation) + L" (" + std::to_wstring(code) + L")", code);
}

class Handle
{
public:
    Handle() = default;
    explicit Handle(HANDLE value) : value_(value) {}
    ~Handle() { reset(); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    Handle(Handle&& other) noexcept : value_(other.release()) {}
    Handle& operator=(Handle&& other) noexcept { if (this != &other) { reset(); value_ = other.release(); } return *this; }
    HANDLE get() const noexcept { return value_; }
    explicit operator bool() const noexcept { return value_ != nullptr && value_ != INVALID_HANDLE_VALUE; }
    HANDLE release() noexcept { auto value = value_; value_ = nullptr; return value; }
    void reset() noexcept { if (*this) CloseHandle(value_); value_ = nullptr; }
private:
    HANDLE value_ = nullptr;
};

struct NativeUnicode { USHORT length; USHORT capacity; PWSTR buffer; };
struct NativeAttributes { ULONG length; HANDLE root; NativeUnicode* name; ULONG attributes; PVOID security; PVOID qos; };
struct NativeStatus { union { LONG status; PVOID pointer; }; ULONG_PTR information; };
using NtCreateFileFn = LONG(NTAPI*)(PHANDLE, ACCESS_MASK, NativeAttributes*, NativeStatus*, PLARGE_INTEGER,
    ULONG, ULONG, ULONG, ULONG, PVOID, ULONG);
NtCreateFileFn NtCreate = nullptr;

std::wstring ModulePath(HMODULE module)
{
    std::vector<wchar_t> buffer(32768);
    const DWORD size = GetModuleFileNameW(module, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (size == 0 || size >= buffer.size()) Fail(L"GetModuleFileNameW");
    return std::wstring(buffer.data(), size);
}

std::wstring LongPath(const std::wstring& path)
{
    if (path.rfind(L"\\\\?\\", 0) == 0) return path;
    if (path.rfind(L"\\\\", 0) == 0) return L"\\\\?\\UNC\\" + path.substr(2);
    return L"\\\\?\\" + path;
}

std::wstring FinalPath(HANDLE file)
{
    const DWORD size = GetFinalPathNameByHandleW(file, nullptr, 0, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (size == 0 || size > 32768) Fail(L"GetFinalPathNameByHandleW");
    std::vector<wchar_t> value(static_cast<std::size_t>(size) + 1);
    const DWORD written = GetFinalPathNameByHandleW(file, value.data(), static_cast<DWORD>(value.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (written == 0 || written >= value.size()) Fail(L"GetFinalPathNameByHandleW");
    std::wstring result(value.data(), written);
    while (!result.empty() && result.back() == L'\\') result.pop_back();
    return result;
}

bool EqualOrdinal(const std::wstring& left, const std::wstring& right)
{
    return CompareStringOrdinal(left.c_str(), static_cast<int>(left.size()), right.c_str(), static_cast<int>(right.size()), TRUE) == CSTR_EQUAL;
}

struct CaseLess
{
    bool operator()(const std::wstring& left, const std::wstring& right) const
    {
        return CompareStringOrdinal(left.c_str(), static_cast<int>(left.size()), right.c_str(), static_cast<int>(right.size()), TRUE) == CSTR_LESS_THAN;
    }
};

bool Under(const std::wstring& root, const std::wstring& path)
{
    return path.size() > root.size() && path[root.size()] == L'\\' &&
        CompareStringOrdinal(root.c_str(), static_cast<int>(root.size()), path.c_str(), static_cast<int>(root.size()), TRUE) == CSTR_EQUAL;
}

void Ordinary(HANDLE file, bool directory)
{
    FILE_ATTRIBUTE_TAG_INFO information{};
    if (!GetFileInformationByHandleEx(file, FileAttributeTagInfo, &information, sizeof(information))) Fail(L"GetFileInformationByHandleEx");
    if ((information.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 ||
        ((information.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) != directory)
        throw Failure(L"Reparse point or unexpected file type in the launcher runtime cache");
    if (!directory)
    {
        BY_HANDLE_FILE_INFORMATION identity{};
        if (!GetFileInformationByHandle(file, &identity)) Fail(L"GetFileInformationByHandle");
        if (identity.nNumberOfLinks != 1) throw Failure(L"Hard-linked file in the launcher runtime cache");
    }
}

Handle OpenRelative(HANDLE parent, const std::wstring& name, ACCESS_MASK access, ULONG share,
    ULONG disposition, bool directory, bool allowMissing = false)
{
    if (name.empty() || name.find_first_of(L"\\/:") != std::wstring::npos || name.size() > 255) throw Failure(L"Invalid relative runtime component");
    NativeUnicode unicode{ static_cast<USHORT>(name.size() * sizeof(wchar_t)), static_cast<USHORT>(name.size() * sizeof(wchar_t)), const_cast<PWSTR>(name.data()) };
    NativeAttributes attributes{ sizeof(NativeAttributes), parent, &unicode, ObjectCaseInsensitive | ObjectDontReparse, nullptr, nullptr };
    NativeStatus io{};
    HANDLE value = nullptr;
    const LONG result = NtCreate(&value, access, &attributes, &io, nullptr, FILE_ATTRIBUTE_NORMAL, share,
        disposition, (directory ? DirectoryFile : NonDirectoryFile) | SynchronousIo | OpenReparsePoint, nullptr, 0);
    if (result < 0)
    {
        if (allowMissing && (result == StatusNameNotFound || result == StatusPathNotFound)) return {};
        Fail(L"NtCreateFile", static_cast<unsigned long>(result));
    }
    Handle handle(value);
    Ordinary(handle.get(), directory);
    return handle;
}

class Sha256
{
public:
    Sha256()
    {
        if (BCryptOpenAlgorithmProvider(&algorithm_, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) Fail(L"BCryptOpenAlgorithmProvider");
        DWORD length = 0;
        DWORD written = 0;
        if (BCryptGetProperty(algorithm_, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&length), sizeof(length), &written, 0) < 0) Fail(L"BCryptGetProperty");
        object_.resize(length);
        if (BCryptCreateHash(algorithm_, &hash_, object_.data(), length, nullptr, 0, 0) < 0) Fail(L"BCryptCreateHash");
    }
    ~Sha256() { if (hash_ != nullptr) BCryptDestroyHash(hash_); if (algorithm_ != nullptr) BCryptCloseAlgorithmProvider(algorithm_, 0); }
    Sha256(const Sha256&) = delete;
    Sha256& operator=(const Sha256&) = delete;
    void Add(const unsigned char* bytes, std::size_t length)
    {
        while (length != 0)
        {
            const ULONG part = static_cast<ULONG>(std::min<std::size_t>(length, 1024 * 1024));
            if (BCryptHashData(hash_, const_cast<PUCHAR>(bytes), part, 0) < 0) Fail(L"BCryptHashData");
            bytes += part; length -= part;
        }
    }
    Digest Finish()
    {
        Digest result{};
        if (BCryptFinishHash(hash_, result.data(), static_cast<ULONG>(result.size()), 0) < 0) Fail(L"BCryptFinishHash");
        return result;
    }
private:
    BCRYPT_ALG_HANDLE algorithm_ = nullptr;
    BCRYPT_HASH_HANDLE hash_ = nullptr;
    std::vector<unsigned char> object_;
};

Digest HashBytes(const unsigned char* data, std::size_t length) { Sha256 hash; hash.Add(data, length); return hash.Finish(); }

Digest HashFile(HANDLE file)
{
    LARGE_INTEGER beginning{};
    if (!SetFilePointerEx(file, beginning, nullptr, FILE_BEGIN)) Fail(L"SetFilePointerEx");
    Sha256 hash;
    std::array<unsigned char, 65536> bytes{};
    DWORD count = 0;
    do
    {
        if (!ReadFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr)) Fail(L"ReadFile");
        hash.Add(bytes.data(), count);
    } while (count != 0);
    return hash.Finish();
}

std::wstring Hex(const Digest& digest)
{
    constexpr wchar_t alphabet[] = L"0123456789abcdef";
    std::wstring result;
    for (auto byte : digest) { result.push_back(alphabet[byte >> 4]); result.push_back(alphabet[byte & 15]); }
    return result;
}

class Reader
{
public:
    Reader(const unsigned char* begin, std::size_t size) : begin_(begin), size_(size) {}
    const unsigned char* Take(std::size_t count)
    {
        if (count > size_ - position_) throw Failure(L"Truncated launcher runtime packet");
        const auto* result = begin_ + position_; position_ += count; return result;
    }
    std::uint32_t U32() { const auto* p = Take(4); return std::uint32_t(p[0]) | std::uint32_t(p[1]) << 8 | std::uint32_t(p[2]) << 16 | std::uint32_t(p[3]) << 24; }
    std::uint64_t U64() { const auto low = U32(); const auto high = U32(); return low | std::uint64_t(high) << 32; }
    std::size_t Remaining() const { return size_ - position_; }
private:
    const unsigned char* begin_;
    std::size_t size_;
    std::size_t position_ = 0;
};

std::wstring DecodePath(const unsigned char* bytes, std::uint32_t length)
{
    if (length == 0 || length > MaximumPathBytes) throw Failure(L"Invalid runtime path length");
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes), static_cast<int>(length), nullptr, 0);
    if (count <= 0) throw Failure(L"Invalid UTF-8 runtime path");
    std::wstring result(static_cast<std::size_t>(count), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes), static_cast<int>(length), result.data(), count) != count) Fail(L"MultiByteToWideChar");
    std::size_t start = 0;
    for (std::size_t index = 0; index <= result.size(); ++index)
    {
        if (index < result.size())
        {
            const auto c = result[index];
            if (c < 32 || c == 127 || c == L'\\' || c == L':' || c == L'<' || c == L'>' || c == L'"' || c == L'|' || c == L'?' || c == L'*')
                throw Failure(L"Unsafe runtime path character");
            if (c != L'/') continue;
        }
        const auto part = result.substr(start, index - start);
        if (part.empty() || part.size() > 255 || part == L"." || part == L".." || part.front() == L' ' || part.back() == L'.' || part.back() == L' ')
            throw Failure(L"Non-canonical runtime path");
        const auto base = part.substr(0, part.find(L'.'));
        if (EqualOrdinal(base, L"CON") || EqualOrdinal(base, L"PRN") || EqualOrdinal(base, L"AUX") || EqualOrdinal(base, L"NUL") ||
            EqualOrdinal(base, L"CONIN$") || EqualOrdinal(base, L"CONOUT$") ||
            (base.size() == 4 && (EqualOrdinal(base.substr(0, 3), L"COM") || EqualOrdinal(base.substr(0, 3), L"LPT")) &&
                ((base[3] >= L'0' && base[3] <= L'9') || base[3] == L'\u00b9' || base[3] == L'\u00b2' || base[3] == L'\u00b3')))
            throw Failure(L"Reserved Windows runtime path");
        start = index + 1;
    }
    return result;
}

struct Entry { std::wstring path; std::uint64_t offset; std::uint64_t length; Digest digest; };
struct Packet { const unsigned char* data; std::uint64_t dataLength; std::wstring entry; Digest digest; std::vector<Entry> files; };

Packet ReadPacket(HMODULE module)
{
    HRSRC resource = FindResourceW(module, MAKEINTRESOURCEW(PayloadResource), RT_RCDATA);
    if (resource == nullptr) Fail(L"FindResourceW");
    const DWORD resourceSize = SizeofResource(module, resource);
    HGLOBAL loaded = LoadResource(module, resource);
    const auto* bytes = static_cast<const unsigned char*>(LockResource(loaded));
    if (bytes == nullptr || resourceSize < HeaderSize || resourceSize > MaximumPayload) throw Failure(L"Invalid launcher runtime packet size");
    Reader header(bytes, HeaderSize);
    if (std::memcmp(header.Take(8), "LMHOST01", 8) != 0 || header.U32() != 1) throw Failure(L"Unsupported launcher runtime packet");
    const auto count = header.U32();
    const auto entryLength = header.U32();
    if (header.U32() != 0 || count == 0 || count > MaximumFiles) throw Failure(L"Invalid launcher runtime packet header");
    const auto tableLength = header.U64();
    const auto dataLength = header.U64();
    Packet packet{};
    std::memcpy(packet.digest.data(), header.Take(packet.digest.size()), packet.digest.size());
    if (tableLength > resourceSize - HeaderSize || dataLength != resourceSize - HeaderSize - tableLength ||
        tableLength < entryLength || entryLength == 0 || entryLength > MaximumPathBytes) throw Failure(L"Invalid launcher runtime packet bounds");
    if (HashBytes(bytes + HeaderSize, resourceSize - HeaderSize) != packet.digest) throw Failure(L"Launcher runtime packet SHA256 mismatch");
    Reader table(bytes + HeaderSize, static_cast<std::size_t>(tableLength));
    packet.entry = DecodePath(table.Take(entryLength), entryLength);
    if (packet.entry.find(L'/') != std::wstring::npos || packet.entry.size() < 5 || !EqualOrdinal(packet.entry.substr(packet.entry.size() - 4), L".dll"))
        throw Failure(L"Invalid managed launcher entry point");
    packet.data = bytes + HeaderSize + static_cast<std::size_t>(tableLength);
    packet.dataLength = dataLength;
    std::set<std::wstring, CaseLess> paths;
    std::uint64_t nextOffset = 0;
    for (std::uint32_t index = 0; index < count; ++index)
    {
        const auto length = table.U32();
        Entry file{};
        file.path = DecodePath(table.Take(length), length);
        file.offset = table.U64(); file.length = table.U64();
        std::memcpy(file.digest.data(), table.Take(file.digest.size()), file.digest.size());
        if (!paths.insert(file.path).second || file.offset != nextOffset || file.length > dataLength - nextOffset)
            throw Failure(L"Duplicate, overlapping or out-of-bounds runtime file");
        if (HashBytes(packet.data + static_cast<std::size_t>(file.offset), static_cast<std::size_t>(file.length)) != file.digest)
            throw Failure(L"Embedded runtime file SHA256 mismatch");
        nextOffset += file.length;
        packet.files.push_back(std::move(file));
    }
    if (table.Remaining() != 0 || nextOffset != dataLength || paths.find(packet.entry) == paths.end() ||
        paths.find(L"hostfxr.dll") == paths.end() || paths.find(L"hostpolicy.dll") == paths.end() || paths.find(L"coreclr.dll") == paths.end())
        throw Failure(L"Incomplete launcher runtime packet");
    for (const auto& path : paths)
    {
        std::size_t separator = path.find(L'/');
        while (separator != std::wstring::npos)
        {
            if (paths.find(path.substr(0, separator)) != paths.end()) throw Failure(L"Runtime file conflicts with a directory");
            separator = path.find(L'/', separator + 1);
        }
    }
    return packet;
}

class Directories
{
public:
    explicit Directories(const std::wstring& root)
    {
        Handle handle(CreateFileW(LongPath(root).c_str(), FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        if (!handle) Fail(L"Open launcher root");
        Ordinary(handle.get(), true);
        root_ = FinalPath(handle.get());
        paths_.emplace(L"", std::move(handle));
    }
    HANDLE Get(const std::wstring& relative, bool writableStorage = false)
    {
        if (writableStorage && relative != L"Minecraft/Launcher/Temp" && relative != L"Minecraft/Launcher/DotNetBundles")
            throw Failure(L"Only temporary storage directories may allow write sharing");
        const auto found = paths_.find(relative);
        if (found != paths_.end()) return found->second.get();
        const auto position = relative.find_last_of(L'/');
        const auto parent = position == std::wstring::npos ? L"" : relative.substr(0, position);
        const auto leaf = position == std::wstring::npos ? relative : relative.substr(position + 1);
        HANDLE parentHandle = Get(parent);
        // NT relative opens do not re-traverse user-controlled ancestor paths.
        // No write/delete sharing prevents replacement/reparse conversion while held.
        auto handle = OpenRelative(parentHandle, leaf, FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_SHARE_READ | (writableStorage ? FILE_SHARE_WRITE : 0), OpenOrCreate, true);
        if (!Under(root_, FinalPath(handle.get()))) throw Failure(L"Runtime directory escaped the launcher root");
        const auto value = handle.get(); paths_.emplace(relative, std::move(handle)); return value;
    }
    std::wstring Absolute(const std::wstring& relative) const
    {
        std::wstring value = root_ + L"\\" + relative;
        std::replace(value.begin() + static_cast<std::ptrdiff_t>(root_.size() + 1), value.end(), L'/', L'\\');
        return value;
    }
    const std::wstring& Root() const { return root_; }
private:
    std::wstring root_;
    std::map<std::wstring, Handle, CaseLess> paths_;
};

Handle logHandle;
void Log(const std::wstring& message) noexcept
{
    if (!logHandle) return;
    OVERLAPPED lock{};
    if (!LockFileEx(logHandle.get(), LOCKFILE_EXCLUSIVE_LOCK, 0, MAXDWORD, MAXDWORD, &lock)) return;
    try
    {
        SYSTEMTIME time{}; GetSystemTime(&time);
        const std::wstring line = std::to_wstring(time.wYear) + L"-" + std::to_wstring(time.wMonth) + L"-" + std::to_wstring(time.wDay) + L" " +
            std::to_wstring(time.wHour) + L":" + std::to_wstring(time.wMinute) + L":" + std::to_wstring(time.wSecond) + L" " + message.substr(0, 8192) + L"\r\n";
        const int count = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), nullptr, 0, nullptr, nullptr);
        if (count > 0)
        {
            std::string bytes(static_cast<std::size_t>(count), '\0');
            WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), bytes.data(), count, nullptr, nullptr);
            LARGE_INTEGER size{};
            LARGE_INTEGER offset{};
            if (GetFileSizeEx(logHandle.get(), &size))
            {
                if (size.QuadPart + count > 1024 * 1024)
                {
                    if (SetFilePointerEx(logHandle.get(), offset, nullptr, FILE_BEGIN)) SetEndOfFile(logHandle.get());
                }
                if (SetFilePointerEx(logHandle.get(), offset, nullptr, FILE_END))
                {
                    DWORD written = 0; WriteFile(logHandle.get(), bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr);
                }
            }
        }
    }
    catch (...) { }
    UnlockFileEx(logHandle.get(), 0, MAXDWORD, MAXDWORD, &lock);
}

void OpenLog(Directories& directories)
{
    logHandle = OpenRelative(directories.Get(L"Minecraft/Launcher"), L"bootstrap.log",
        FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE, FILE_SHARE_READ | FILE_SHARE_WRITE, OpenOrCreate, false);
    if (!Under(directories.Root(), FinalPath(logHandle.get()))) throw Failure(L"Bootstrap log escaped launcher root");
}

bool DeleteOpened(HANDLE file) noexcept
{
    FILE_DISPOSITION_INFO disposition{ TRUE };
    return SetFileInformationByHandle(file, FileDispositionInfo, &disposition, sizeof(disposition)) != FALSE;
}

Handle VerifyFile(Directories& directories, HANDLE parent, const std::wstring& name, const Entry& entry)
{
    auto file = OpenRelative(parent, name, FILE_READ_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
        FILE_SHARE_READ, OpenExisting, false, true);
    if (!file) return {};
    if (!Under(directories.Root(), FinalPath(file.get()))) throw Failure(L"Cached file escaped launcher root");
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file.get(), &size)) Fail(L"GetFileSizeEx cached runtime");
    if (size.QuadPart < 0 || static_cast<std::uint64_t>(size.QuadPart) != entry.length || HashFile(file.get()) != entry.digest) return {};
    return file;
}

Handle Materialize(Directories& directories, HANDLE parent, const std::wstring& name, const Entry& entry, const Packet& packet)
{
    auto existing = VerifyFile(directories, parent, name, entry);
    if (existing) return existing;
    // Never truncate an existing inode: a hard link inserted before an overwrite
    // must not cause writes outside this cache. Delete only an opened ordinary,
    // single-link cache file, then CREATE_NEW and fail closed on any collision.
    auto damaged = OpenRelative(parent, name, FILE_READ_DATA | FILE_READ_ATTRIBUTES | DELETE | SYNCHRONIZE,
        0, OpenExisting, false, true);
    if (damaged)
    {
        if (!Under(directories.Root(), FinalPath(damaged.get()))) throw Failure(L"Damaged runtime file escaped launcher root");
        if (!DeleteOpened(damaged.get())) Fail(L"Remove damaged runtime cache file");
        damaged.reset();
    }
    auto writtenFile = OpenRelative(parent, name, FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | DELETE | SYNCHRONIZE,
        0, CreateNew, false);
    try
    {
        if (!Under(directories.Root(), FinalPath(writtenFile.get()))) throw Failure(L"New runtime file escaped launcher root");
        std::uint64_t remaining = entry.length;
        const auto* bytes = packet.data + static_cast<std::size_t>(entry.offset);
        while (remaining != 0)
        {
            const DWORD part = static_cast<DWORD>(std::min<std::uint64_t>(remaining, 1024 * 1024));
            DWORD written = 0;
            if (!WriteFile(writtenFile.get(), bytes, part, &written, nullptr) || written != part) Fail(L"Write runtime file");
            bytes += written; remaining -= written;
        }
        if (!FlushFileBuffers(writtenFile.get()) || HashFile(writtenFile.get()) != entry.digest) throw Failure(L"Written runtime file SHA256 mismatch");
        // The preparation mutex and the already-held generation lease exclude
        // competing preparation/pruning. No DLL is loaded until EVERY final file
        // is reopened, hashed and held read-only. Crash-partial files are repaired
        // under the same protocol on the next startup.
        writtenFile.reset();
        auto verified = VerifyFile(directories, parent, name, entry);
        if (!verified) throw Failure(L"Published runtime file failed verification");
        return verified;
    }
    catch (...) { if (writtenFile) DeleteOpened(writtenFile.get()); throw; }
}

Handle AcquireLease(Directories& directories, const std::wstring& key)
{
    std::string marker = "LMHOST01\n";
    for (auto character : key) marker.push_back(static_cast<char>(character));
    marker.push_back('\n');
    Entry lease{};
    lease.path = key + L".lease";
    lease.length = marker.size();
    lease.digest = HashBytes(reinterpret_cast<const unsigned char*>(marker.data()), marker.size());
    Packet owner{};
    owner.data = reinterpret_cast<const unsigned char*>(marker.data());
    owner.dataLength = marker.size();
    HANDLE parent = directories.Get(L"Minecraft/Launcher/Runtime");
    const auto started = GetTickCount64();
    while (true)
    {
        try { return Materialize(directories, parent, lease.path, lease, owner); }
        catch (const Failure& error)
        {
            // The cleaner can briefly hold an exclusive lease while removing an
            // inactive generation. Wait before touching/recreating that cache.
            if ((error.code() != 0xC0000043ul && error.code() != ERROR_SHARING_VIOLATION) || GetTickCount64() - started >= 30000) throw;
            Sleep(50);
        }
    }
}

bool OwnTemporary(const std::wstring& name)
{
    if (name.rfind(L".lmhost-", 0) != 0 || name.size() < 17 || name.substr(name.size() - 4) != L".tmp") return false;
    return std::all_of(name.begin() + 8, name.end() - 4, [](wchar_t c) { return (c >= L'0' && c <= L'9') || c == L'-'; });
}

void Inventory(Directories& directories, const std::wstring& base, const std::wstring& relative,
    const std::set<std::wstring, CaseLess>& expectedFiles, const std::set<std::wstring, CaseLess>& expectedDirectories)
{
    const auto current = relative.empty() ? base : base + L"/" + relative;
    const auto search = directories.Absolute(current) + L"\\*";
    WIN32_FIND_DATAW information{};
    HANDLE found = FindFirstFileW(search.c_str(), &information);
    if (found == INVALID_HANDLE_VALUE) Fail(L"Enumerate runtime cache");
    try
    {
        do
        {
            const std::wstring name = information.cFileName;
            if (name == L"." || name == L"..") continue;
            const auto child = relative.empty() ? name : relative + L"/" + name;
            if ((information.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) throw Failure(L"Reparse point in runtime inventory");
            if ((information.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                if (expectedDirectories.find(child) == expectedDirectories.end()) throw Failure(L"Unexpected runtime cache directory");
                directories.Get(base + L"/" + child);
                Inventory(directories, base, child, expectedFiles, expectedDirectories);
            }
            else if (expectedFiles.find(child) == expectedFiles.end())
            {
                if (!OwnTemporary(name)) throw Failure(L"Unexpected runtime cache file");
                auto temporary = OpenRelative(directories.Get(current), name, DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, 0, OpenExisting, false);
                if (!DeleteOpened(temporary.get())) Fail(L"Remove interrupted runtime temporary file");
            }
        } while (FindNextFileW(found, &information));
        if (GetLastError() != ERROR_NO_MORE_FILES) Fail(L"Enumerate runtime cache");
    }
    catch (...) { FindClose(found); throw; }
    FindClose(found);
}

struct HostParameters { std::size_t size; const wchar_t* hostPath; const wchar_t* dotnetRoot; };
using HostHandle = void*;
using HostInitialize = std::int32_t(__cdecl*)(int, const wchar_t**, const HostParameters*, HostHandle*);
using HostRun = std::int32_t(__cdecl*)(HostHandle);
using HostClose = std::int32_t(__cdecl*)(HostHandle);
using HostWriter = void(__cdecl*)(const wchar_t*);
using SetHostWriter = HostWriter(__cdecl*)(HostWriter);
void __cdecl HostError(const wchar_t* message) { Log(std::wstring(L"hostfxr: ") + message); }

int RunManaged(const std::wstring& executable, Directories& directories, const std::wstring& runtime, const std::wstring& entry)
{
    if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_USER_DIRS)) Fail(L"SetDefaultDllDirectories");
    const auto runtimePath = directories.Absolute(runtime);
    const auto cookie = AddDllDirectory(runtimePath.c_str());
    if (cookie == nullptr) Fail(L"AddDllDirectory");
    const auto fxrPath = runtimePath + L"\\hostfxr.dll";
    HMODULE library = LoadLibraryExW(fxrPath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_USER_DIRS);
    if (library == nullptr) Fail(L"Load private hostfxr.dll");
    auto initialize = reinterpret_cast<HostInitialize>(GetProcAddress(library, "hostfxr_initialize_for_dotnet_command_line"));
    auto run = reinterpret_cast<HostRun>(GetProcAddress(library, "hostfxr_run_app"));
    auto close = reinterpret_cast<HostClose>(GetProcAddress(library, "hostfxr_close"));
    auto setWriter = reinterpret_cast<SetHostWriter>(GetProcAddress(library, "hostfxr_set_error_writer"));
    if (initialize == nullptr || run == nullptr || close == nullptr || setWriter == nullptr) throw Failure(L"Required private .NET hosting API is unavailable");
    setWriter(HostError);
    int argc = 0;
    auto argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argv == nullptr || argc == 0) Fail(L"CommandLineToArgvW");
    const auto appPath = runtimePath + L"\\" + entry;
    std::vector<const wchar_t*> arguments{ appPath.c_str() };
    for (int index = 1; index < argc; ++index) arguments.push_back(argv[index]);
    const HostParameters parameters{ sizeof(HostParameters), executable.c_str(), runtimePath.c_str() };
    HostHandle context = nullptr;
    const auto result = initialize(static_cast<int>(arguments.size()), arguments.data(), &parameters, &context);
    if (result < 0 || context == nullptr) { LocalFree(argv); Fail(L"Initialize private .NET runtime", static_cast<unsigned long>(result)); }
    Log(L"Running verified private .NET runtime " + runtime);
    const int appResult = run(context);
    close(context); LocalFree(argv); RemoveDllDirectory(cookie);
    return appResult;
}

// A native startup failure cannot use the WPF dialogs it failed to load. This
// small dark window uses normal system chrome and never starts another process.
LRESULT CALLBACK ErrorWindow(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam)
{
    if (message == WM_CLOSE || (message == WM_KEYDOWN && (wparam == VK_RETURN || wparam == VK_ESCAPE))) { DestroyWindow(hwnd); return 0; }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    if (message == WM_PAINT)
    {
        PAINTSTRUCT paint{}; HDC dc = BeginPaint(hwnd, &paint); RECT area{}; GetClientRect(hwnd, &area);
        SetBkColor(dc, RGB(32, 32, 32)); SetTextColor(dc, RGB(255, 255, 255));
        SetDCBrushColor(dc, RGB(32, 32, 32));
        FillRect(dc, &area, static_cast<HBRUSH>(GetStockObject(DC_BRUSH)));
        area.left += 24; area.top += 24; area.right -= 24; area.bottom -= 24;
        auto font = CreateFontW(-16, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
            CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        auto old = SelectObject(dc, font);
        constexpr wchar_t text[] = L"Не удалось подготовить файлы лаунчера.\n\nПроверьте доступ к папке LANMinecraft и запустите его снова. Подробности сохранены в Minecraft\\Launcher\\bootstrap.log, если папка доступна для записи.\n\nЗакройте это окно или нажмите Enter.";
        DrawTextW(dc, text, -1, &area, DT_LEFT | DT_WORDBREAK | DT_NOPREFIX);
        SelectObject(dc, old); DeleteObject(font); EndPaint(hwnd, &paint); return 0;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

void ShowFailure()
{
    WNDCLASSW type{}; type.lpfnWndProc = ErrorWindow; type.hInstance = GetModuleHandleW(nullptr); type.lpszClassName = L"LANMinecraft.BootstrapError";
    type.hCursor = LoadCursorW(nullptr, IDC_ARROW); type.hIcon = LoadIconW(type.hInstance, MAKEINTRESOURCEW(1));
    if (RegisterClassW(&type) == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return;
    HWND window = CreateWindowExW(0, type.lpszClassName, L"LANMinecraft", WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU,
        CW_USEDEFAULT, CW_USEDEFAULT, 600, 280, nullptr, nullptr, type.hInstance, nullptr);
    if (window == nullptr) return;
    const int dark = 1; DwmSetWindowAttribute(window, 20, &dark, sizeof(dark));
    ShowWindow(window, SW_SHOWNORMAL);
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
}

bool ReportFailureToRedirectedOutput(const std::wstring& detail) noexcept
{
    HANDLE error = GetStdHandle(STD_ERROR_HANDLE);
    if (error == nullptr || error == INVALID_HANDLE_VALUE) return false;
    const auto type = GetFileType(error);
    if (type != FILE_TYPE_PIPE && type != FILE_TYPE_DISK) return false;
    try
    {
        const std::wstring message = L"LANMinecraft bootstrap failed: " + detail + L"\r\n";
        const int length = WideCharToMultiByte(CP_UTF8, 0, message.c_str(), static_cast<int>(message.size()), nullptr, 0, nullptr, nullptr);
        if (length > 0)
        {
            std::string bytes(static_cast<std::size_t>(length), '\0');
            WideCharToMultiByte(CP_UTF8, 0, message.c_str(), static_cast<int>(message.size()), bytes.data(), length, nullptr, nullptr);
            DWORD written = 0; WriteFile(error, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr);
        }
    }
    catch (...) { }
    return true;
}
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    std::wstring failure = L"Unknown native error";
    try
    {
        const auto native = GetModuleHandleW(L"ntdll.dll");
        NtCreate = reinterpret_cast<NtCreateFileFn>(GetProcAddress(native, "NtCreateFile"));
        if (NtCreate == nullptr) throw Failure(L"Required Windows file APIs are unavailable");
        const auto executable = ModulePath(nullptr);
        const auto separator = executable.find_last_of(L"\\/");
        if (separator == std::wstring::npos) throw Failure(L"Cannot determine launcher directory");
        Directories directories(executable.substr(0, separator));
        directories.Get(L"Minecraft/Launcher");
        OpenLog(directories);
        Log(L"Native startup PID " + std::to_wstring(GetCurrentProcessId()));
        directories.Get(L"Minecraft/Launcher/Temp", true);
        directories.Get(L"Minecraft/Launcher/DotNetBundles", true);
        const auto temporary = directories.Absolute(L"Minecraft/Launcher/Temp");
        const auto bundles = directories.Absolute(L"Minecraft/Launcher/DotNetBundles");
        if (!SetEnvironmentVariableW(L"TEMP", temporary.c_str()) || !SetEnvironmentVariableW(L"TMP", temporary.c_str()) ||
            !SetEnvironmentVariableW(L"DOTNET_BUNDLE_EXTRACT_BASE_DIR", bundles.c_str())) Fail(L"Set portable runtime environment");
        const auto packet = ReadPacket(instance);
        const auto key = Hex(packet.digest);
        const auto rootHash = HashBytes(reinterpret_cast<const unsigned char*>(directories.Root().data()), directories.Root().size() * sizeof(wchar_t));
        const auto mutexName = L"Local\\LANMinecraft.Runtime." + Hex(rootHash) + L"." + key;
        Handle mutex(CreateMutexW(nullptr, FALSE, mutexName.c_str()));
        if (!mutex) Fail(L"Create runtime preparation mutex");
        const auto waited = WaitForSingleObject(mutex.get(), 30000);
        if (waited != WAIT_OBJECT_0 && waited != WAIT_ABANDONED) throw Failure(L"Another launcher could not finish preparing its runtime");
        const auto runtime = L"Minecraft/Launcher/Runtime/" + key;
        Handle activeLease;
        std::vector<Handle> verifiedFiles;
        try
        {
            activeLease = AcquireLease(directories, key);
            directories.Get(runtime);
            std::set<std::wstring, CaseLess> expectedFiles;
            std::set<std::wstring, CaseLess> expectedDirectories;
            for (const auto& file : packet.files)
            {
                expectedFiles.insert(file.path);
                std::size_t at = file.path.find(L'/');
                while (at != std::wstring::npos) { expectedDirectories.insert(file.path.substr(0, at)); at = file.path.find(L'/', at + 1); }
                const auto slash = file.path.find_last_of(L'/');
                const auto parent = slash == std::wstring::npos ? runtime : runtime + L"/" + file.path.substr(0, slash);
                const auto leaf = slash == std::wstring::npos ? file.path : file.path.substr(slash + 1);
                verifiedFiles.push_back(Materialize(directories, directories.Get(parent), leaf, file, packet));
            }
            Inventory(directories, runtime, L"", expectedFiles, expectedDirectories);
        }
        catch (...) { ReleaseMutex(mutex.get()); throw; }
        ReleaseMutex(mutex.get());
        const HRESULT apartment = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        if (FAILED(apartment)) Fail(L"Initialize STA", static_cast<unsigned long>(apartment));
        const int result = RunManaged(executable, directories, runtime, packet.entry);
        CoUninitialize();
        Log(L"Managed application exited " + std::to_wstring(result));
        return result;
    }
    catch (const Failure& error) { failure = error.text(); Log(L"Startup failed: " + failure); }
    catch (const std::exception&) { failure = L"Native exception"; Log(L"Startup failed: native exception"); }
    catch (...) { Log(L"Startup failed: unknown native error"); }
    if (!ReportFailureToRedirectedOutput(failure)) ShowFailure();
    return 120;
}
