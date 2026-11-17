#pragma once

// Included inside the bootstrap's private namespace, after the safe file and UI primitives.
constexpr std::size_t MaximumDescriptor = 64 * 1024;
constexpr std::size_t MaximumManifest = 8 * 1024 * 1024;

std::wstring Utf8(const unsigned char* bytes, std::uint32_t length, std::uint32_t maximum, bool empty = false)
{
    if (length > maximum || (!empty && length == 0)) throw Failure(L"Invalid bootstrap string length");
    if (length == 0) return {};
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes), static_cast<int>(length), nullptr, 0);
    if (count <= 0) throw Failure(L"Invalid bootstrap UTF-8");
    std::wstring result(static_cast<std::size_t>(count), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes), static_cast<int>(length), result.data(), count) != count)
        Fail(L"Decode bootstrap UTF-8");
    for (auto c : result) if (c < 32 || c == 127) throw Failure(L"Invalid bootstrap string character");
    return result;
}

std::wstring ReadString(Reader& reader, std::uint32_t maximum, bool empty = false)
{
    const auto length = reader.U32();
    return Utf8(reader.Take(length), length, maximum, empty);
}

struct Address { std::wstring host; std::wstring target; INTERNET_PORT port; bool secure; };

Address CrackAddress(const std::wstring& text, bool initial, bool api = false)
{
    URL_COMPONENTS parts{}; parts.dwStructSize = sizeof(parts);
    parts.dwHostNameLength = parts.dwUrlPathLength = parts.dwExtraInfoLength = parts.dwUserNameLength = parts.dwPasswordLength = static_cast<DWORD>(-1);
    if (!WinHttpCrackUrl(text.c_str(), static_cast<DWORD>(text.size()), 0, &parts)) throw Failure(L"Invalid bootstrap download URL");
    const std::wstring host(parts.lpszHostName, parts.dwHostNameLength);
    const std::wstring path(parts.lpszUrlPath, parts.dwUrlPathLength);
    const std::wstring extra = parts.dwExtraInfoLength ? std::wstring(parts.lpszExtraInfo, parts.dwExtraInfoLength) : L"";
    const bool secure = parts.nScheme == INTERNET_SCHEME_HTTPS;
    bool testSource = false;
#if defined(LANMINECRAFT_BOOTSTRAP_TEST_SOURCES)
    testSource = parts.nScheme == INTERNET_SCHEME_HTTP && (EqualOrdinal(host, L"127.0.0.1") || EqualOrdinal(host, L"localhost"));
#endif
    if ((!secure && !testSource) || parts.dwUserNameLength != 0 || parts.dwPasswordLength != 0 ||
        (!testSource && parts.nPort != INTERNET_DEFAULT_HTTPS_PORT) || text.find(L'#') != std::wstring::npos)
        throw Failure(L"Bootstrap downloads require approved HTTPS addresses");
    if (!testSource)
    {
        if (initial)
        {
            const std::wstring prefix = api ? L"/repos/MarkZamore/LANMinecraft-Components/releases/assets/" : L"/MarkZamore/LANMinecraft-Components/releases/download/";
            if (!EqualOrdinal(host, api ? L"api.github.com" : L"github.com") || path.rfind(prefix, 0) != 0 || !extra.empty())
                throw Failure(L"Bootstrap dependency URL is outside its immutable publisher");
            const auto suffix = path.substr(prefix.size());
            if (api)
            {
                if (suffix.empty() || !std::all_of(suffix.begin(), suffix.end(), [](wchar_t c) { return c >= L'0' && c <= L'9'; }))
                    throw Failure(L"Invalid bootstrap release asset API address");
            }
            else
            {
                const auto slash = suffix.find(L'/');
                if (slash == std::wstring::npos || slash == 0 || slash + 1 == suffix.size() || suffix.find(L'/', slash + 1) != std::wstring::npos ||
                    EqualOrdinal(suffix.substr(0, slash), L"latest") || suffix.find(L'%') != std::wstring::npos)
                    throw Failure(L"Bootstrap dependencies require immutable release coordinates");
                for (const auto& coordinate : { suffix.substr(0, slash), suffix.substr(slash + 1) })
                {
                    const auto alphaNumeric = [](wchar_t c) { return (c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') || (c >= L'0' && c <= L'9'); };
                    if (!alphaNumeric(coordinate.front()) || !std::all_of(coordinate.begin(), coordinate.end(), [&](wchar_t c)
                        { return alphaNumeric(c) || c == L'.' || c == L'_' || c == L'-' || c == L'+'; }))
                        throw Failure(L"Invalid immutable bootstrap release coordinate");
                }
            }
        }
        else if (!EqualOrdinal(host, L"github.com") && !EqualOrdinal(host, L"api.github.com") &&
            !EqualOrdinal(host, L"release-assets.githubusercontent.com") && !EqualOrdinal(host, L"objects.githubusercontent.com") &&
            !EqualOrdinal(host, L"github-releases.githubusercontent.com"))
            throw Failure(L"Bootstrap redirect is outside approved GitHub asset servers");
    }
    return { host, path + extra, parts.nPort, secure };
}

struct Dependency
{
    std::uint32_t kind;
    std::wstring kindName, id, url, apiUrl, entry;
    std::uint64_t size, manifestSize;
    Digest digest{}, manifestDigest{};
};

std::vector<Dependency> ReadDependencies(HMODULE module)
{
    HRSRC resource = FindResourceW(module, MAKEINTRESOURCEW(102), RT_RCDATA);
    if (resource == nullptr) Fail(L"Find bootstrap dependencies");
    const auto size = SizeofResource(module, resource);
    const auto* bytes = static_cast<const unsigned char*>(LockResource(LoadResource(module, resource)));
    if (bytes == nullptr || size < 16 || size > MaximumDescriptor) throw Failure(L"Invalid bootstrap dependency descriptor size");
    Reader reader(bytes, size);
    if (std::memcmp(reader.Take(8), "LMDESK01", 8) != 0 || reader.U32() != 1 || reader.U32() != 3)
        throw Failure(L"Unsupported bootstrap dependency descriptor");
    std::set<std::uint32_t> kinds;
    std::vector<Dependency> result;
    for (unsigned index = 0; index < 3; ++index)
    {
        Dependency item{}; item.kind = reader.U32();
        if (item.kind < 1 || item.kind > 3 || !kinds.insert(item.kind).second) throw Failure(L"Unknown or duplicate bootstrap dependency kind");
        item.kindName = item.kind == 1 ? L"desktop-runtime" : item.kind == 2 ? L"assets" : L"steam-native";
        item.id = ReadString(reader, 128);
        for (auto c : item.id)
            if (!((c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') || (c >= L'0' && c <= L'9') || c == L'.' || c == L'_' || c == L'-'))
                throw Failure(L"Invalid bootstrap dependency identifier");
        item.url = ReadString(reader, 4096);
        item.apiUrl = ReadString(reader, 4096, true);
        const auto entryLength = reader.U32(); item.entry = DecodePath(reader.Take(entryLength), entryLength);
        item.size = reader.U64(); std::memcpy(item.digest.data(), reader.Take(32), 32);
        item.manifestSize = reader.U64(); std::memcpy(item.manifestDigest.data(), reader.Take(32), 32);
        if (item.size < HeaderSize || item.size > MaximumPayload || item.manifestSize < HeaderSize ||
            item.manifestSize > MaximumManifest || item.manifestSize > item.size)
            throw Failure(L"Invalid bootstrap dependency bounds");
        (void)CrackAddress(item.url, true);
        if (!item.apiUrl.empty()) (void)CrackAddress(item.apiUrl, true, true);
        result.push_back(std::move(item));
    }
    if (reader.Remaining() != 0) throw Failure(L"Trailing bootstrap dependency descriptor bytes");
    std::sort(result.begin(), result.end(), [](const Dependency& a, const Dependency& b) { return a.kind < b.kind; });
    return result;
}

std::vector<unsigned char> ReadBytes(HANDLE file, std::uint64_t expected)
{
    if (expected > MaximumPayload) throw Failure(L"Oversized bootstrap file");
    LARGE_INTEGER size{}; if (!GetFileSizeEx(file, &size)) Fail(L"Read bootstrap file size");
    if (size.QuadPart < 0 || static_cast<std::uint64_t>(size.QuadPart) != expected) throw Failure(L"Bootstrap file length mismatch");
    LARGE_INTEGER start{}; if (!SetFilePointerEx(file, start, nullptr, FILE_BEGIN)) Fail(L"Rewind bootstrap file");
    std::vector<unsigned char> result(static_cast<std::size_t>(expected));
    std::size_t copied = 0;
    while (copied < result.size())
    {
        DWORD read = 0;
        const auto count = static_cast<DWORD>(std::min<std::size_t>(1024 * 1024, result.size() - copied));
        if (!ReadFile(file, result.data() + copied, count, &read, nullptr)) Fail(L"Read bootstrap file");
        if (read == 0) throw Failure(L"Truncated bootstrap file");
        copied += read;
    }
    return result;
}

std::string EncodeUtf8(const std::wstring& value)
{
    if (value.empty()) return {};
    const int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (size <= 0) Fail(L"Encode bootstrap context");
    std::string result(static_cast<std::size_t>(size), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), size, nullptr, nullptr) != size)
        Fail(L"Encode bootstrap context");
    return result;
}

Handle PublishCheckedMetadata(Directories& directories, const std::wstring& parent, const std::wstring& name,
    const unsigned char* bytes, std::size_t count)
{
    // Metadata identities are immutable. An exclusive writer flushes and hashes
    // every byte before a read-only verified handle is acquired. The context
    // environment/managed entry is published only after this guard exists.
    // A crash-partial prefix cannot match its pin and is repaired on retry.
    // Parent directory safety locks remain held; no rename or sharing relaxation.
    Entry entry{ name, 0, count, HashBytes(bytes, count) };
    Packet source{}; source.data = bytes; source.dataLength = count;
    return Materialize(directories, directories.Get(parent), name, entry, source);
}
Handle AcquireDependencyLease(Directories& directories, const std::wstring& kind, const std::wstring& hash, PreparationProgress& progress)
{
    const auto marker = EncodeUtf8(L"LMDESK01\n" + kind + L"\n" + hash + L"\n");
    Entry file{ hash + L".lease", 0, marker.size(), HashBytes(reinterpret_cast<const unsigned char*>(marker.data()), marker.size()) };
    Packet packet{}; packet.data = reinterpret_cast<const unsigned char*>(marker.data()); packet.dataLength = marker.size();
    const auto start = GetTickCount64();
    while (true)
    {
        progress.CheckCancelled();
        try { return Materialize(directories, directories.Get(L"Minecraft/Launcher/Bootstrap/" + kind), file.path, file, packet); }
        catch (const Failure& error)
        {
            if ((error.code() != 0xC0000043ul && error.code() != ERROR_SHARING_VIOLATION) || GetTickCount64() - start >= 30000) throw;
            Sleep(50);
        }
    }
}

class PreparationMutex
{
public:
    PreparationMutex(Directories& directories, const std::wstring& key, PreparationProgress& progress)
    {
        const auto rootHash = HashBytes(reinterpret_cast<const unsigned char*>(directories.Root().data()), directories.Root().size() * sizeof(wchar_t));
        const auto name = L"Local\\LANMinecraft.Bootstrap." + Hex(rootHash) + L"." + key;
        mutex_ = Handle(CreateMutexW(nullptr, FALSE, name.c_str()));
        if (!mutex_) Fail(L"Create bootstrap preparation mutex");
        while (true)
        {
            progress.CheckCancelled();
            const auto result = WaitForSingleObject(mutex_.get(), 100);
            if (result == WAIT_OBJECT_0 || result == WAIT_ABANDONED) { owned_ = true; return; }
            if (result != WAIT_TIMEOUT) Fail(L"Wait for bootstrap preparation mutex");
            progress.Show(); progress.Stage(L"Ожидаем завершения другой подготовки");
        }
    }
    ~PreparationMutex() { if (owned_) ReleaseMutex(mutex_.get()); }
private:
    Handle mutex_; bool owned_ = false;
};

struct PreparedDependency
{
    Dependency specification;
    std::wstring root, manifest;
    Handle lease;
    std::vector<Handle> files;
    std::wstring penEntry;
    std::wstring longestNativeEntry;
};

std::wstring DesktopPenEntry(const Packet& packet)
{
    std::wstring found;
    for (const auto& file : packet.files)
    {
        const std::wstring prefix = L"shared/Microsoft.WindowsDesktop.App/";
        if (file.path.rfind(prefix, 0) != 0) continue;
        const auto slash = file.path.find(L'/', prefix.size());
        if (slash == std::wstring::npos || !EqualOrdinal(file.path.substr(slash + 1), L"PenImc_cor3.dll")) continue;
        if (!found.empty()) throw Failure(L"Ambiguous desktop framework native input component");
        found = file.path;
    }
    if (found.empty()) throw Failure(L"The desktop runtime is missing its native input component");
    return found;
}

void ReadPeBytes(HANDLE file, std::uint64_t size, std::uint64_t offset, void* output, DWORD count)
{
    if (offset > size || count > size - offset) throw Failure(L"Invalid runtime PE bounds");
    LARGE_INTEGER position{}; position.QuadPart = static_cast<LONGLONG>(offset);
    if (!SetFilePointerEx(file, position, nullptr, FILE_BEGIN)) Fail(L"Seek verified runtime PE");
    DWORD read = 0;
    if (!ReadFile(file, output, count, &read, nullptr)) Fail(L"Read verified runtime PE");
    if (read != count) throw Failure(L"Truncated verified runtime PE");
}

bool IsNativeRuntimePe(HANDLE file, std::uint64_t size)
{
    if (size < sizeof(WORD)) return false;
    WORD magic = 0; ReadPeBytes(file, size, 0, &magic, sizeof(magic));
    if (magic != IMAGE_DOS_SIGNATURE) return false;
    IMAGE_DOS_HEADER dos{}; ReadPeBytes(file, size, 0, &dos, sizeof(dos));
    if (dos.e_lfanew < static_cast<LONG>(sizeof(dos))) throw Failure(L"Invalid runtime PE header offset");
    const auto nt = static_cast<std::uint64_t>(dos.e_lfanew);
    DWORD signature = 0; ReadPeBytes(file, size, nt, &signature, sizeof(signature));
    if (signature != IMAGE_NT_SIGNATURE) throw Failure(L"Invalid runtime PE signature");
    IMAGE_FILE_HEADER coff{}; ReadPeBytes(file, size, nt + sizeof(signature), &coff, sizeof(coff));
    if (coff.NumberOfSections == 0 || coff.NumberOfSections > 96) throw Failure(L"Invalid runtime PE section count");
    const auto optionalOffset = nt + sizeof(signature) + sizeof(coff);
    if (coff.SizeOfOptionalHeader > size || optionalOffset > size - coff.SizeOfOptionalHeader)
        throw Failure(L"Invalid runtime PE optional header bounds");
    ReadPeBytes(file, size, optionalOffset, &magic, sizeof(magic));
    const std::size_t directoryOffset = magic == IMAGE_NT_OPTIONAL_HDR64_MAGIC ? 112 : magic == IMAGE_NT_OPTIONAL_HDR32_MAGIC ? 96 : 0;
    if (directoryOffset == 0 || coff.SizeOfOptionalHeader < directoryOffset) throw Failure(L"Invalid runtime PE optional header");
    DWORD directoryCount = 0;
    ReadPeBytes(file, size, optionalOffset + directoryOffset - sizeof(DWORD), &directoryCount, sizeof(directoryCount));
    if (directoryCount <= IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR) return true;
    const auto cliOffset = directoryOffset + IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR * sizeof(IMAGE_DATA_DIRECTORY);
    if (coff.SizeOfOptionalHeader < cliOffset + sizeof(IMAGE_DATA_DIRECTORY)) throw Failure(L"Invalid runtime PE CLR directory");
    IMAGE_DATA_DIRECTORY cli{}; ReadPeBytes(file, size, optionalOffset + cliOffset, &cli, sizeof(cli));
    if (cli.VirtualAddress == 0 && cli.Size == 0) return true;
    if (cli.VirtualAddress == 0 || cli.Size < 72) throw Failure(L"Invalid runtime PE CLR header");

    std::vector<IMAGE_SECTION_HEADER> sections(coff.NumberOfSections);
    ReadPeBytes(file, size, optionalOffset + coff.SizeOfOptionalHeader, sections.data(),
        static_cast<DWORD>(sections.size() * sizeof(IMAGE_SECTION_HEADER)));
    DWORD headersSize = 0;
    ReadPeBytes(file, size, optionalOffset + 60, &headersSize, sizeof(headersSize));
    const auto locate = [&](DWORD rva, DWORD count)
    {
        std::uint64_t found = 0; bool present = false;
        if (rva < headersSize && count <= headersSize - rva) { found = rva; present = true; }
        for (const auto& section : sections)
        {
            if (rva < section.VirtualAddress) continue;
            const auto delta = static_cast<std::uint64_t>(rva) - section.VirtualAddress;
            if (delta >= std::max(section.Misc.VirtualSize, section.SizeOfRawData)) continue;
            if (present || delta > section.SizeOfRawData || count > section.SizeOfRawData - delta)
                throw Failure(L"Ambiguous or truncated runtime PE CLR address");
            found = static_cast<std::uint64_t>(section.PointerToRawData) + delta; present = true;
        }
        if (!present || found > size || count > size - found) throw Failure(L"Invalid runtime PE CLR address");
        return found;
    };
    std::array<DWORD, 18> cor{};
    ReadPeBytes(file, size, locate(cli.VirtualAddress, sizeof(cor)), cor.data(), sizeof(cor));
    if (cor[0] < sizeof(cor) || cor[0] > cli.Size) throw Failure(L"Invalid runtime PE CLR header size");
    constexpr DWORD IlOnly = 0x1, NativeEntryPoint = 0x10;
    if ((cor[4] & NativeEntryPoint) != 0) return true;
    if ((cor[4] & IlOnly) != 0) return false;
    // ReadyToRun has native code but the CLR maps it, unlike a mixed-mode PE
    // loaded through Win32. Its CLI ManagedNativeHeader begins with RTR\0.
    if (cor[16] != 0 && cor[17] >= 16)
    {
        std::array<DWORD, 4> readyToRun{};
        ReadPeBytes(file, size, locate(cor[16], cor[17]), readyToRun.data(), sizeof(readyToRun));
        if (readyToRun[0] == 0x00525452 && (readyToRun[1] & 0xFFFF) != 0) return false;
    }
    return true;
}

std::wstring LongestNativeRuntimeEntry(const Packet& packet, const std::vector<Handle>& files, PreparationProgress& progress)
{
    if (files.size() != packet.files.size()) throw Failure(L"Incomplete verified runtime PE inventory");
    std::wstring longest;
    for (std::size_t index = 0; index < packet.files.size(); ++index)
    {
        progress.CheckCancelled();
        const auto& entry = packet.files[index];
        if (IsNativeRuntimePe(files[index].get(), entry.length) && entry.path.size() > longest.size()) longest = entry.path;
    }
    if (longest.empty()) throw Failure(L"The desktop runtime is missing its native PE components");
    Log(L"Longest verified native runtime path: " + longest);
    return longest;
}

void PreparePacketFiles(Directories& directories, const std::wstring& root, const Packet& packet,
    std::vector<Handle>& files, bool write, PreparationProgress& progress)
{
    std::set<std::wstring, CaseLess> expectedFiles, expectedDirectories;
    for (const auto& entry : packet.files)
    {
        progress.CheckCancelled();
        expectedFiles.insert(entry.path);
        std::size_t at = entry.path.find(L'/');
        while (at != std::wstring::npos) { expectedDirectories.insert(entry.path.substr(0, at)); at = entry.path.find(L'/', at + 1); }
        const auto slash = entry.path.find_last_of(L'/');
        const auto parent = slash == std::wstring::npos ? root : root + L"/" + entry.path.substr(0, slash);
        const auto leaf = slash == std::wstring::npos ? entry.path : entry.path.substr(slash + 1);
        auto file = write ? Materialize(directories, directories.Get(parent), leaf, entry, packet)
            : VerifyFile(directories, directories.Get(parent), leaf, entry);
        if (!file) { files.clear(); return; }
        files.push_back(std::move(file));
    }
    Inventory(directories, root, L"", expectedFiles, expectedDirectories);
}

class InternetHandle
{
public:
    explicit InternetHandle(HINTERNET value = nullptr) : value_(value) {}
    ~InternetHandle() { if (value_ != nullptr) WinHttpCloseHandle(value_); }
    InternetHandle(const InternetHandle&) = delete;
    InternetHandle& operator=(const InternetHandle&) = delete;
    HINTERNET get() const { return value_; }
private:
    HINTERNET value_;
};

class CancelableRequest
{
public:
    explicit CancelableRequest(HINTERNET value) : value_(value) {}
    ~CancelableRequest() { Close(); }
    HINTERNET Get() const { return value_.load(); }
    void Close() { const auto value = value_.exchange(nullptr); if (value != nullptr) WinHttpCloseHandle(value); }
private:
    std::atomic<HINTERNET> value_;
};

std::wstring QueryHeader(HINTERNET request, DWORD code, bool optional = false)
{
    DWORD size = 0;
    if (WinHttpQueryHeaders(request, code, WINHTTP_HEADER_NAME_BY_INDEX, nullptr, &size, WINHTTP_NO_HEADER_INDEX)) return {};
    if (optional && GetLastError() == ERROR_WINHTTP_HEADER_NOT_FOUND) return {};
    if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || size > 32768) Fail(L"Read bootstrap HTTP header");
    std::vector<wchar_t> buffer((size + sizeof(wchar_t) - 1) / sizeof(wchar_t) + 1);
    if (!WinHttpQueryHeaders(request, code, WINHTTP_HEADER_NAME_BY_INDEX, buffer.data(), &size, WINHTTP_NO_HEADER_INDEX)) Fail(L"Read bootstrap HTTP header");
    return std::wstring(buffer.data());
}

std::uint64_t Decimal(const std::wstring& text)
{
    if (text.empty() || text.size() > 20) throw Failure(L"Invalid HTTP size");
    std::uint64_t value = 0;
    for (auto c : text)
    {
        if (c < L'0' || c > L'9' || value > (UINT64_MAX - (c - L'0')) / 10) throw Failure(L"Invalid HTTP size");
        value = value * 10 + c - L'0';
    }
    return value;
}

void TruncatePart(HANDLE file)
{
    LARGE_INTEGER zero{};
    if (!SetFilePointerEx(file, zero, nullptr, FILE_BEGIN) || !SetEndOfFile(file)) Fail(L"Restart bootstrap download");
}

void DownloadRoute(HANDLE part, const Dependency& dependency, const std::wstring& source, PreparationProgress& progress)
{
    LARGE_INTEGER size{}; if (!GetFileSizeEx(part, &size)) Fail(L"Read partial download size");
    if (size.QuadPart < 0 || static_cast<std::uint64_t>(size.QuadPart) > dependency.size) { TruncatePart(part); size.QuadPart = 0; }
    std::uint64_t downloaded = static_cast<std::uint64_t>(size.QuadPart);
    if (downloaded == dependency.size)
    {
        if (HashFile(part) == dependency.digest) return;
        TruncatePart(part); downloaded = 0;
    }
    InternetHandle session(WinHttpOpen(L"LANMinecraftBootstrap/1", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
        WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0));
    if (session.get() == nullptr) Fail(L"Open bootstrap HTTP session");
    if (!WinHttpSetTimeouts(session.get(), 10000, 15000, 15000, 15000)) Fail(L"Set bootstrap HTTP timeouts");
    std::wstring current = source;
    const auto started = GetTickCount64();
    for (unsigned redirect = 0; redirect <= 5; ++redirect)
    {
        progress.CheckCancelled();
        const auto address = CrackAddress(current, false);
        InternetHandle connection(WinHttpConnect(session.get(), address.host.c_str(), address.port, 0));
        if (connection.get() == nullptr) Fail(L"Connect to bootstrap publisher");
        const auto request = std::make_shared<CancelableRequest>(WinHttpOpenRequest(connection.get(), L"GET", address.target.c_str(),
            nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, address.secure ? WINHTTP_FLAG_SECURE : 0));
        if (request->Get() == nullptr) Fail(L"Create bootstrap download request");
        const DWORD policy = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
        if (!WinHttpSetOption(request->Get(), WINHTTP_OPTION_REDIRECT_POLICY, const_cast<DWORD*>(&policy), sizeof(policy))) Fail(L"Set bootstrap redirect policy");
        progress.RegisterCancel([request]() { request->Close(); });
        try
        {
            const auto headers = L"Accept: application/octet-stream\r\nAccept-Encoding: identity\r\nX-GitHub-Api-Version: 2022-11-28\r\n" +
                (downloaded == 0 ? std::wstring() : L"Range: bytes=" + std::to_wstring(downloaded) + L"-\r\n");
            progress.CheckCancelled();
            if (!WinHttpSendRequest(request->Get(), headers.c_str(), static_cast<DWORD>(headers.size()), WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
                !WinHttpReceiveResponse(request->Get(), nullptr)) Fail(L"Receive bootstrap download response");
            DWORD status = 0, statusSize = sizeof(status);
            if (!WinHttpQueryHeaders(request->Get(), WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                WINHTTP_HEADER_NAME_BY_INDEX, &status, &statusSize, WINHTTP_NO_HEADER_INDEX)) Fail(L"Read bootstrap HTTP status");
            if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
            {
                if (redirect == 5) throw Failure(L"Too many bootstrap redirects");
                auto location = QueryHeader(request->Get(), WINHTTP_QUERY_LOCATION);
                if (!location.empty() && location.front() == L'/' && (location.size() < 2 || location[1] != L'/'))
                    location = (address.secure ? L"https://" : L"http://") + address.host +
                        ((address.secure && address.port == 443) || (!address.secure && address.port == 80) ? L"" : L":" + std::to_wstring(address.port)) + location;
                (void)CrackAddress(location, false);
                current = std::move(location);
                progress.ClearCancel();
                continue;
            }
            if (status != 200 && status != 206) throw Failure(L"Bootstrap publisher returned HTTP " + std::to_wstring(status), status);
            if (status == 200 && downloaded != 0) { TruncatePart(part); downloaded = 0; }
            if (status == 206)
            {
                const auto range = QueryHeader(request->Get(), WINHTTP_QUERY_CONTENT_RANGE);
                const auto dash = range.find(L'-'), slash = range.find(L'/');
                if (range.rfind(L"bytes ", 0) != 0 || dash == std::wstring::npos || slash == std::wstring::npos || dash <= 6 || slash <= dash ||
                    Decimal(range.substr(6, dash - 6)) != downloaded || Decimal(range.substr(slash + 1)) != dependency.size ||
                    Decimal(range.substr(dash + 1, slash - dash - 1)) != dependency.size - 1)
                    throw Failure(L"Bootstrap response has an invalid byte range");
            }
            const auto contentLength = QueryHeader(request->Get(), WINHTTP_QUERY_CONTENT_LENGTH, true);
            if (!contentLength.empty() && Decimal(contentLength) != dependency.size - downloaded) throw Failure(L"Bootstrap response length differs from its pin");
            LARGE_INTEGER offset{}; offset.QuadPart = static_cast<LONGLONG>(downloaded);
            if (!SetFilePointerEx(part, offset, nullptr, FILE_BEGIN)) Fail(L"Seek partial bootstrap download");
            progress.Bytes(downloaded, dependency.size);
            std::array<unsigned char, 65536> buffer{};
            while (true)
            {
                progress.CheckCancelled();
                if (GetTickCount64() - started > 30ull * 60 * 1000) throw Failure(L"Bootstrap download deadline exceeded");
                DWORD read = 0;
                if (!WinHttpReadData(request->Get(), buffer.data(), static_cast<DWORD>(buffer.size()), &read)) Fail(L"Read bootstrap download");
                if (read == 0) break;
                if (read > dependency.size - downloaded) throw Failure(L"Oversized bootstrap download");
                DWORD written = 0;
                if (!WriteFile(part, buffer.data(), read, &written, nullptr) || written != read) Fail(L"Write bootstrap download");
                downloaded += read;
                progress.Bytes(downloaded, dependency.size);
            }
            if (downloaded != dependency.size) throw Failure(L"Truncated bootstrap download");
            if (!FlushFileBuffers(part)) Fail(L"Flush bootstrap download");
            if (HashFile(part) != dependency.digest) { TruncatePart(part); throw Failure(L"Bootstrap download SHA256 mismatch"); }
            progress.CheckCancelled();
            progress.ClearCancel();
            return;
        }
        catch (...)
        {
            progress.ClearCancel();
            progress.CheckCancelled();
            throw;
        }
    }
}

std::vector<unsigned char> DownloadPacket(Directories& directories, const Dependency& dependency,
    const std::wstring& parent, const std::wstring& name, PreparationProgress& progress)
{
    auto part = OpenRelative(directories.Get(parent), name, FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | DELETE | SYNCHRONIZE,
        0, OpenOrCreate, false);
    if (!Under(directories.Root(), FinalPath(part.get()))) throw Failure(L"Bootstrap download escaped launcher root");
    try
    {
        try { DownloadRoute(part.get(), dependency, dependency.url, progress); }
        catch (const Failure& error)
        {
            progress.CheckCancelled();
            if (dependency.apiUrl.empty()) throw;
            Log(L"Bootstrap primary download failed, trying publisher asset API: " + error.text());
            DownloadRoute(part.get(), dependency, dependency.apiUrl, progress);
        }
        return ReadBytes(part.get(), dependency.size);
    }
    catch (const PreparationCancelled&) { DeleteOpened(part.get()); throw; }
    catch (const Failure& error) { throw RetryablePreparationFailure(error.text(), error.code()); }
}

void RemovePart(Directories& directories, const std::wstring& parent, const std::wstring& name)
{
    auto file = OpenRelative(directories.Get(parent), name, DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, 0, OpenExisting, false, true);
    if (file && !DeleteOpened(file.get())) Fail(L"Remove bootstrap partial download");
}

void RemoveCancelledPart(Directories& directories, const Dependency& dependency) noexcept
{
    try
    {
        const auto key = Hex(dependency.digest);
        const auto rootHash = HashBytes(reinterpret_cast<const unsigned char*>(directories.Root().data()), directories.Root().size() * sizeof(wchar_t));
        const auto name = L"Local\\LANMinecraft.Bootstrap." + Hex(rootHash) + L"." + dependency.kindName + L"." + key;
        Handle mutex(CreateMutexW(nullptr, FALSE, name.c_str()));
        if (!mutex) return;
        const auto waited = WaitForSingleObject(mutex.get(), 0);
        if (waited != WAIT_OBJECT_0 && waited != WAIT_ABANDONED) return;
        try { RemovePart(directories, L"Minecraft/Launcher/Bootstrap/" + dependency.kindName, key + L".part"); }
        catch (...) { ReleaseMutex(mutex.get()); throw; }
        ReleaseMutex(mutex.get());
    }
    catch (...) { Log(L"Cancelled bootstrap partial could not be removed; kept for safe cleanup."); }
}

PreparedDependency PrepareDependency(Directories& directories, const Dependency& dependency, PreparationProgress& progress)
{
    const auto key = Hex(dependency.digest);
    PreparationMutex mutex(directories, dependency.kindName + L"." + key, progress);
    const auto parent = L"Minecraft/Launcher/Bootstrap/" + dependency.kindName;
    PreparedDependency prepared{ dependency, parent + L"/" + key, parent + L"/" + key + L".manifest", {}, {} };
    prepared.lease = AcquireDependencyLease(directories, dependency.kindName, key, progress);
    directories.Get(prepared.root);
    Entry manifest{ key + L".manifest", 0, dependency.manifestSize, dependency.manifestDigest };
    auto saved = VerifyFile(directories, directories.Get(parent), manifest.path, manifest);
    if (saved)
    {
        const auto bytes = ReadBytes(saved.get(), dependency.manifestSize);
        const auto packet = ParsePacket(bytes.data(), bytes.size(), dependency.size, false, false);
        if (packet.entry != dependency.entry) throw Failure(L"Bootstrap dependency entry differs from its pin");
        if (dependency.kind == 1) prepared.penEntry = DesktopPenEntry(packet);
        PreparePacketFiles(directories, prepared.root, packet, prepared.files, false, progress);
        if (prepared.files.size() == packet.files.size())
        {
            if (dependency.kind == 1) prepared.longestNativeEntry = LongestNativeRuntimeEntry(packet, prepared.files, progress);
            prepared.files.push_back(std::move(saved));
            Log(L"Reusing verified bootstrap dependency " + dependency.kindName + L" " + dependency.id);
            return prepared;
        }
    }
    saved.reset(); prepared.files.clear();
    progress.Show();
    progress.Stage(dependency.kind == 1 ? L"Загружаем среду .NET" : dependency.kind == 2 ? L"Загружаем оформление" : L"Загружаем библиотеку Steam");
    const auto partial = key + L".part";
    try
    {
        const auto bytes = DownloadPacket(directories, dependency, parent, partial, progress);
        const auto packet = ParsePacket(bytes.data(), bytes.size(), dependency.size, false, true);
        if (dependency.kind == 1) prepared.penEntry = DesktopPenEntry(packet);
        if (packet.entry != dependency.entry || dependency.manifestSize > bytes.size() ||
            HashBytes(bytes.data(), static_cast<std::size_t>(dependency.manifestSize)) != dependency.manifestDigest)
            throw Failure(L"Bootstrap dependency manifest differs from its pin");
        Reader header(bytes.data(), HeaderSize); header.Take(24); const auto tableLength = header.U64();
        if (HeaderSize + tableLength != dependency.manifestSize) throw Failure(L"Invalid bootstrap manifest prefix size");
        progress.CheckCancelled(); progress.Stage(L"Проверяем и подготавливаем файлы");
        PreparePacketFiles(directories, prepared.root, packet, prepared.files, true, progress);
        if (dependency.kind == 1) prepared.longestNativeEntry = LongestNativeRuntimeEntry(packet, prepared.files, progress);
        auto checkedManifest = PublishCheckedMetadata(directories, parent, manifest.path, bytes.data(), static_cast<std::size_t>(dependency.manifestSize));
        if (!checkedManifest) throw Failure(L"Published bootstrap manifest failed verification");
        prepared.files.push_back(std::move(checkedManifest));
        RemovePart(directories, parent, partial);
        Log(L"Prepared bootstrap dependency " + dependency.kindName + L" " + dependency.id);
        return prepared;
    }
    catch (const PreparationCancelled&) { try { RemovePart(directories, parent, partial); } catch (...) { } throw; }
    catch (const Failure& error)
    {
        if (RetryableIoCode(error.code()) != 0) throw RetryablePreparationFailure(error.text(), RetryableIoCode(error.code()));
        throw;
    }
}

struct PreparedApplication
{
    std::wstring root;
    Handle lease;
    std::vector<Handle> files;
};

PreparedApplication PrepareApplication(Directories& directories, const Packet& packet, PreparationProgress& progress)
{
    const auto key = Hex(packet.digest);
    PreparationMutex mutex(directories, L"app." + key, progress);
    PreparedApplication result{ L"Minecraft/Launcher/Bootstrap/app/" + key, {}, {} };
    result.lease = AcquireDependencyLease(directories, L"app", key, progress);
    directories.Get(result.root);
    progress.Stage(L"Подготавливаем лаунчер");
    PreparePacketFiles(directories, result.root, packet, result.files, true, progress);
    return result;
}

std::wstring PublicPath(const std::wstring& value)
{
    if (value.rfind(L"\\\\?\\UNC\\", 0) == 0) return L"\\\\" + value.substr(8);
    return value.rfind(L"\\\\?\\", 0) == 0 ? value.substr(4) : value;
}

std::wstring HostedPath(const std::wstring& root, const std::wstring& relative)
{
    auto result = root + L"\\" + relative;
    std::replace(result.begin(), result.end(), L'/', L'\\');
    return result;
}

struct HostingRuntime
{
    std::wstring path;
    Handle shortAlias;
};

HostingRuntime SelectHostingRuntime(Directories& directories, const PreparedDependency& runtime)
{
    const auto full = PublicPath(directories.Absolute(runtime.root));
    if (HostedPath(full, runtime.longestNativeEntry).size() < MAX_PATH) return { full, {} };
    const DWORD length = GetShortPathNameW(full.c_str(), nullptr, 0);
    if (length == 0 || length > 32768)
        throw Failure(L"Слишком длинный путь к папке лаунчера. Переместите всю папку лаунчера ближе к корню диска и запустите его снова.", ERROR_FILENAME_EXCED_RANGE);
    std::vector<wchar_t> buffer(static_cast<std::size_t>(length) + 1);
    const DWORD written = GetShortPathNameW(full.c_str(), buffer.data(), static_cast<DWORD>(buffer.size()));
    if (written == 0 || written >= buffer.size())
        throw Failure(L"Слишком длинный путь к папке лаунчера. Подходящее короткое имя недоступно. Переместите всю папку ближе к корню диска и запустите его снова.", ERROR_FILENAME_EXCED_RANGE);
    const std::wstring shortened(buffer.data(), written);
    if (HostedPath(shortened, runtime.longestNativeEntry).size() >= MAX_PATH)
        throw Failure(L"Слишком длинный путь к папке лаунчера. Переместите всю папку лаунчера ближе к корню диска и запустите его снова.", ERROR_FILENAME_EXCED_RANGE);
    Handle alias(CreateFileW(LongPath(shortened).c_str(), FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
        FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!alias) Fail(L"Verify short desktop runtime alias");
    Ordinary(alias.get(), true);
    const auto expected = FinalPath(directories.Get(runtime.root));
    const auto actual = FinalPath(alias.get());
    if (!EqualOrdinal(actual, expected) || !Under(directories.Root(), actual))
        throw Failure(L"The short desktop runtime alias escaped its verified cache");
    BY_HANDLE_FILE_INFORMATION expectedIdentity{}, aliasIdentity{};
    if (!GetFileInformationByHandle(directories.Get(runtime.root), &expectedIdentity) || !GetFileInformationByHandle(alias.get(), &aliasIdentity))
        Fail(L"Read short desktop runtime alias identity");
    if (expectedIdentity.dwVolumeSerialNumber != aliasIdentity.dwVolumeSerialNumber ||
        expectedIdentity.nFileIndexHigh != aliasIdentity.nFileIndexHigh || expectedIdentity.nFileIndexLow != aliasIdentity.nFileIndexLow)
        throw Failure(L"The short desktop runtime alias has a different file identity");
    Log(L"Using verified short desktop runtime alias for WPF native path limits.");
    return { shortened, std::move(alias) };
}

std::string JsonString(const std::wstring& value)
{
    const auto bytes = EncodeUtf8(value);
    std::string result = "\"";
    constexpr char hex[] = "0123456789abcdef";
    for (unsigned char c : bytes)
    {
        if (c == '"' || c == '\\') { result.push_back('\\'); result.push_back(static_cast<char>(c)); }
        else if (c < 32)
        {
            result += "\\u00"; result.push_back(hex[c >> 4]); result.push_back(hex[c & 15]);
        }
        else result.push_back(static_cast<char>(c));
    }
    result.push_back('"'); return result;
}

class BootstrapSession
{
public:
    BootstrapSession(Directories& directories, const std::wstring& executable, const PreparedApplication& application,
        const std::vector<PreparedDependency>& dependencies) : directories_(directories)
    {
        name_ = std::to_wstring(GetCurrentProcessId()) + L".json";
        const auto parent = L"Minecraft/Launcher/Bootstrap/sessions";
        file_ = std::wstring(parent) + L"/" + name_;
        const auto appRoot = PublicPath(directories_.Absolute(application.root));
        std::string json = "{\"schemaVersion\":1,\"exePath\":" + JsonString(PublicPath(executable)) + ",\"appRoot\":" +
            JsonString(appRoot) + ",\"components\":[";
        bool first = true;
        for (const auto& dependency : dependencies)
        {
            if (!first) json += ',';
            first = false;
            json += "{\"kind\":" + JsonString(dependency.specification.kindName) + ",\"id\":" + JsonString(dependency.specification.id) +
                ",\"root\":" + JsonString(PublicPath(directories_.Absolute(dependency.root))) + ",\"entryPath\":" + JsonString(dependency.specification.entry) +
                ",\"sha256\":" + JsonString(Hex(dependency.specification.digest)) + ",\"manifestPath\":" +
                JsonString(PublicPath(directories_.Absolute(dependency.manifest))) + ",\"manifestSha256\":" + JsonString(Hex(dependency.specification.manifestDigest)) + "}";
        }
        json += "]}";
        if (json.size() > 64 * 1024) throw Failure(L"Oversized bootstrap context");
        digest_ = HashBytes(reinterpret_cast<const unsigned char*>(json.data()), json.size()); size_ = json.size();
        try
        {
            held_ = PublishCheckedMetadata(directories_, parent, name_, reinterpret_cast<const unsigned char*>(json.data()), json.size());
            if (!held_) throw Failure(L"Bootstrap context verification failed");
            const auto path = PublicPath(directories_.Absolute(file_));
            if (!SetEnvironmentVariableW(L"LANMINECRAFT_BOOTSTRAP_CONTEXT", path.c_str())) Fail(L"Publish bootstrap process context");
        }
        catch (...) { Cleanup(); throw; }
    }
    ~BootstrapSession() { Cleanup(); }
    BootstrapSession(const BootstrapSession&) = delete;
    BootstrapSession& operator=(const BootstrapSession&) = delete;
private:
    Directories& directories_;
    std::wstring name_, file_;
    Digest digest_{};
    std::uint64_t size_ = 0;
    Handle held_;
    void Cleanup() noexcept
    {
        held_.reset();
        try
        {
            auto file = OpenRelative(directories_.Get(L"Minecraft/Launcher/Bootstrap/sessions"), name_,
                FILE_READ_DATA | FILE_READ_ATTRIBUTES | DELETE | SYNCHRONIZE, 0, OpenExisting, false, true);
            LARGE_INTEGER size{};
            if (file && GetFileSizeEx(file.get(), &size) && size.QuadPart >= 0 &&
                static_cast<std::uint64_t>(size.QuadPart) == size_ && HashFile(file.get()) == digest_) DeleteOpened(file.get());
        }
        catch (...) { }
    }
};

void ReportCancellation() noexcept
{
    Log(L"Launcher preparation cancelled");
    const auto error = GetStdHandle(STD_ERROR_HANDLE);
    if (error == nullptr || error == INVALID_HANDLE_VALUE) return;
    const char message[] = "LANMinecraft bootstrap cancelled\r\n";
    DWORD written = 0; WriteFile(error, message, static_cast<DWORD>(sizeof(message) - 1), &written, nullptr);
}
