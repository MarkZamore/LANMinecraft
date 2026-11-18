// Included by native_host.cpp before the pre-CLR windows.
#pragma once

class BootstrapFonts
{
public:
    BootstrapFonts() = default;
    BootstrapFonts(const BootstrapFonts&) = delete;
    BootstrapFonts& operator=(const BootstrapFonts&) = delete;
    ~BootstrapFonts() { Clear(); }

    HFONT Create(int pixelHeight, int weight)
    {
        const bool heading = weight == FW_SEMIBOLD;
        auto& resource = heading ? semibold_ : regular_;
        if (!resource) resource = Register(heading ? SemiboldResource : RegularResource);
        const auto create = [&](const wchar_t* face)
        {
            return CreateFontW(-pixelHeight, 0, 0, 0, weight, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, face);
        };
        if (resource)
        {
            // The static 600 face has its own legacy Windows family name.
            // Requesting weight 600 from "Roboto" can simulate bold on its 400 face.
            const auto font = create(heading ? L"Roboto SemiBold" : L"Roboto");
            if (font && Inspect(font, VerifiedRobotoWeight) == weight) return font;
            if (font) DeleteObject(font);
        }
        // The emergency UI must remain readable if either PE resource is damaged.
        // Let Windows select its default face without depending on any font name.
        return create(nullptr);
    }

    // GetFontData reads the selected physical font's OS/2 table. A synthetic
    // GDI weight on a Regular font cannot pass as a true Semibold.
    static int RobotoWeight(HDC dc)
    {
        if (!FaceIs(dc, L"Roboto") && !FaceIs(dc, L"Roboto SemiBold")) return 0;
        BYTE bytes[2]{};
        constexpr DWORD os2Tag = 0x322f534f;
        if (GetFontData(dc, os2Tag, 4, bytes, sizeof(bytes)) != sizeof(bytes)) return 0;
        return (static_cast<int>(bytes[0]) << 8) | bytes[1];
    }

    static int VerifiedRobotoWeight(HDC dc)
    {
        const int weight = RobotoWeight(dc);
        if (weight != FW_NORMAL && weight != FW_SEMIBOLD) return 0;
        DWORD size = 0;
        const auto source = static_cast<const BYTE*>(
            FontResource(weight == FW_SEMIBOLD ? SemiboldResource : RegularResource, size));
        if (!source || GetFontData(dc, 0, 0, nullptr, 0) != size) return 0;
        BYTE actual[16 * 1024];
        for (DWORD offset = 0; offset < size;)
        {
            const DWORD chunk = std::min(size - offset, static_cast<DWORD>(sizeof(actual)));
            if (GetFontData(dc, 0, offset, actual, chunk) != chunk ||
                std::memcmp(source + offset, actual, chunk) != 0) return 0;
            offset += chunk;
        }
        return weight;
    }

    // Call after the native windows and their selected HFONTs have been freed.
    void Clear() noexcept
    {
        if (semibold_) RemoveFontMemResourceEx(std::exchange(semibold_, nullptr));
        if (regular_) RemoveFontMemResourceEx(std::exchange(regular_, nullptr));
    }

private:
    static constexpr int RegularResource = 103;
    static constexpr int SemiboldResource = 104;
    HANDLE regular_ = nullptr;
    HANDLE semibold_ = nullptr;

    static void* FontResource(int id, DWORD& size)
    {
        const auto module = GetModuleHandleW(nullptr);
        const auto resource = FindResourceW(module, MAKEINTRESOURCEW(id), RT_RCDATA);
        if (!resource) return nullptr;
        size = SizeofResource(module, resource);
        const auto loaded = LoadResource(module, resource);
        return loaded ? LockResource(loaded) : nullptr;
    }

    static HANDLE Register(int id)
    {
        DWORD size = 0;
        const auto data = FontResource(id, size);
        if (!data || !size) return nullptr;
        DWORD count = 0;
        const auto font = AddFontMemResourceEx(data, size, nullptr, &count);
        if (font && count == 1) return font;
        if (font) RemoveFontMemResourceEx(font);
        return nullptr;
    }

    static bool FaceIs(HDC dc, const wchar_t* expected)
    {
        wchar_t actual[LF_FACESIZE]{};
        return GetTextFaceW(dc, LF_FACESIZE, actual) != 0 &&
            CompareStringOrdinal(actual, -1, expected, -1, TRUE) == CSTR_EQUAL;
    }

    template<class Read>
    static int Inspect(HFONT font, Read read)
    {
        const auto dc = GetDC(nullptr);
        if (!dc) return 0;
        const auto previous = SelectObject(dc, font);
        const bool selected = previous && previous != HGDI_ERROR;
        const auto result = selected ? read(dc) : 0;
        if (selected) SelectObject(dc, previous);
        ReleaseDC(nullptr, dc);
        return result;
    }
};
