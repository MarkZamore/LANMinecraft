// Included inside native_host.cpp's private namespace after Failure and Log.
// Uses Windows controls only; no CLR or downloaded asset is needed to prepare them.
#pragma once

class PreparationCancelled : public std::exception
{
public:
    const char* what() const noexcept override { return "Launcher preparation cancelled"; }
};

struct PreparationState
{
    std::atomic<bool> cancelled{ false };
    std::atomic<bool> visible{ false };
    std::atomic<bool> done{ false };
    std::mutex gate;
    std::mutex cancelGate;
    std::function<void()> cancelIo;
    std::wstring stage = L"Проверяем необходимые файлы";
    std::wstring error;
    std::exception_ptr failure;
    std::uint64_t downloaded = 0;
    std::uint64_t total = 0;
    std::uint64_t stageVersion = 0;
    bool retryable = false;
    Handle wake{ CreateEventW(nullptr, FALSE, FALSE, nullptr) };

    void Wake() noexcept { if (wake) SetEvent(wake.get()); }
    void Cancel() noexcept
    {
        cancelled.store(true);
        try
        {
            // Serialize callback invocation and removal. ClearCancel returning
            // guarantees that this UI will no longer invoke the removed callback.
            std::lock_guard<std::mutex> lock(cancelGate);
            if (cancelIo) cancelIo();
        }
        catch (...) { }
        Wake();
    }
};

class PreparationProgress
{
public:
    explicit PreparationProgress(PreparationState& state) : state_(state) { }
    void Stage(const std::wstring& text)
    {
        CheckCancelled();
        {
            std::lock_guard<std::mutex> lock(state_.gate);
            state_.stage = text;
            state_.downloaded = 0;
            state_.total = 0;
            ++state_.stageVersion;
        }
        state_.Wake();
    }
    void Bytes(std::uint64_t downloaded, std::uint64_t total)
    {
        CheckCancelled();
        {
            std::lock_guard<std::mutex> lock(state_.gate);
            state_.downloaded = downloaded;
            state_.total = total;
        }
        state_.Wake();
    }
    void Show() noexcept { state_.visible.store(true); state_.Wake(); }
    bool IsCancelled() const noexcept { return state_.cancelled.load(); }
    void CheckCancelled() const { if (IsCancelled()) throw PreparationCancelled(); }
    // The callback must not reenter RegisterCancel/ClearCancel. A shared owner
    // with an atomic exchange-to-null prevents double-closing the request handle.
    void RegisterCancel(std::function<void()> callback)
    {
        std::lock_guard<std::mutex> lock(state_.cancelGate);
        state_.cancelIo = std::move(callback);
        if (state_.cancelled.load() && state_.cancelIo) state_.cancelIo();
    }
    void ClearCancel()
    {
        std::lock_guard<std::mutex> lock(state_.cancelGate);
        state_.cancelIo = {};
    }
private:
    PreparationState& state_;
};

class PreparationUi
{
public:
    PreparationUi() = default;
    PreparationUi(const PreparationUi&) = delete;
    PreparationUi& operator=(const PreparationUi&) = delete;
    ~PreparationUi()
    {
        state_.Cancel();
        if (worker_.joinable()) worker_.join();
        CloseWindow();
        if (font_) DeleteObject(font_);
        if (headingFont_) DeleteObject(headingFont_);
        UnregisterClassW(L"LANMinecraft.BootstrapPreparation", GetModuleHandleW(nullptr));
        if (canvas_) DeleteObject(canvas_);
    }

    bool Run(const std::function<void(PreparationProgress&)>& work)
    {
        if (!state_.wake) Fail(L"Create preparation notification event");
        state_.visible.store(false);
        uiFailure_ = nullptr;
        work_ = work;
        StartWorker();
        while (true)
        {
            if (uiFailure_)
            {
                state_.Cancel();
                if (worker_.joinable()) worker_.join();
                CloseWindow();
                std::rethrow_exception(uiFailure_);
            }
            if (state_.done.load())
            {
                if (worker_.joinable()) worker_.join();
                if (state_.cancelled.load()) { CloseWindow(); return false; }
                std::exception_ptr failure;
                bool retryable;
                { std::lock_guard<std::mutex> lock(state_.gate); failure = state_.failure; retryable = state_.retryable; }
                if (!failure) { CloseWindow(); return true; }
                if (!retryable) { CloseWindow(); std::rethrow_exception(failure); }
                state_.visible.store(true);
            }
            if (state_.visible.load() && !window_) OpenWindow();
            if (window_) UpdateControls();
            HANDLE notification = state_.wake.get();
            const DWORD wait = MsgWaitForMultipleObjects(1, &notification, FALSE, 100, QS_ALLINPUT);
            if (wait == WAIT_FAILED) Fail(L"Wait for launcher preparation");
            MSG message{};
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            {
                if (message.message == WM_QUIT) { state_.Cancel(); continue; }
                if (!window_ || !IsDialogMessageW(window_, &message))
                { TranslateMessage(&message); DispatchMessageW(&message); }
            }
        }
    }

private:
    static constexpr int HeadingId = 1101;
    static constexpr int StageId = 1102;
    static constexpr int MetricsId = 1103;
    static constexpr int ProgressId = 1104;
    static constexpr COLORREF Canvas = RGB(32, 32, 32);
    static constexpr COLORREF Text = RGB(255, 255, 255);
    static constexpr COLORREF Muted = RGB(197, 197, 197);
    static constexpr COLORREF Surface = RGB(25, 25, 25);
    static constexpr COLORREF Primary = RGB(71, 181, 128);
    static constexpr DWORD WindowStyle = WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN;
    static constexpr DWORD WindowExtendedStyle = WS_EX_COMPOSITED;
    PreparationState state_;
    std::thread worker_;
    std::function<void(PreparationProgress&)> work_;
    HWND window_ = nullptr;
    HWND heading_ = nullptr;
    HWND stage_ = nullptr;
    HWND metrics_ = nullptr;
    HWND progress_ = nullptr;
    HWND cancel_ = nullptr;
    HWND retry_ = nullptr;
    HFONT font_ = nullptr;
    HFONT headingFont_ = nullptr;
    HBRUSH canvas_ = nullptr;
    UINT dpi_ = 96;
    bool errorShown_ = false;
    std::exception_ptr uiFailure_;
    std::uint64_t lastStage_ = UINT64_MAX;
    std::uint64_t lastBytes_ = 0;
    ULONGLONG sampledAt_ = 0;
    double speed_ = 0;
    int percent_ = 0;
    bool determinate_ = false;
    ULONGLONG renderedAt_ = 0;
    std::wstring shownStage_;
    std::wstring shownMetrics_;
    std::wstring shownProgress_;
    bool cancelEnabled_ = true;
    bool retryEnabled_ = false;
    bool retryVisible_ = false;
    bool changingDpi_ = false;
    int layoutWidth_ = 0;
    int layoutHeight_ = 0;
    UINT layoutDpi_ = 0;
#if defined(LANMINECRAFT_BOOTSTRAP_TEST_SOURCES)
    std::uint64_t mutations_ = 0;
    std::uint64_t draws_ = 0;
    std::uint64_t erases_ = 0;
#endif

    int Scale(int value) const { return MulDiv(value, static_cast<int>(dpi_), 96); }
    SIZE MinimumOuter() const
    {
        RECT area{ 0, 0, Scale(336), Scale(200) };
        const auto extended = window_ ? static_cast<DWORD>(GetWindowLongPtrW(window_, GWL_EXSTYLE)) : WindowExtendedStyle;
        AdjustWindowRectExForDpi(&area, WindowStyle, FALSE, extended, dpi_);
        return SIZE{ area.right - area.left, area.bottom - area.top };
    }
    static std::wstring ErrorMessage(unsigned long code)
    {
        if (code == ERROR_DISK_FULL || code == ERROR_HANDLE_DISK_FULL)
            return L"Недостаточно места для файлов лаунчера. Освободите место и повторите попытку.";
        if (code == ERROR_ACCESS_DENIED || code == ERROR_SHARING_VIOLATION)
            return L"Нет доступа к файлам лаунчера. Проверьте папку и повторите попытку.";
        return L"Не удалось загрузить файлы. Проверьте интернет и повторите попытку.";
    }
    void StartWorker()
    {
        if (worker_.joinable()) worker_.join();
        {
            std::lock_guard<std::mutex> lock(state_.gate);
            state_.failure = nullptr; state_.error.clear(); state_.retryable = false;
            state_.stage = L"Проверяем необходимые файлы";
            state_.downloaded = 0; state_.total = 0; ++state_.stageVersion;
        }
        state_.cancelled.store(false); state_.done.store(false);
        errorShown_ = false;
        lastStage_ = UINT64_MAX; sampledAt_ = 0; renderedAt_ = 0; speed_ = 0;
        worker_ = std::thread([this]
        {
            PreparationProgress progress(state_);
            try { work_(progress); progress.CheckCancelled(); }
            catch (const PreparationCancelled&) { state_.cancelled.store(true); }
            catch (const RetryablePreparationFailure& error)
            {
                Log(L"Preparation attempt failed: " + error.text());
                std::lock_guard<std::mutex> lock(state_.gate);
                state_.failure = std::current_exception(); state_.retryable = true;
                state_.error = ErrorMessage(error.code());
            }
            catch (...)
            {
                std::lock_guard<std::mutex> lock(state_.gate);
                state_.failure = std::current_exception(); state_.retryable = false;
            }
            progress.ClearCancel();
            state_.done.store(true); state_.Wake();
        });
    }
    void CloseWindow() noexcept
    {
        if (window_) DestroyWindow(window_);
        window_ = nullptr;
        heading_ = stage_ = metrics_ = progress_ = cancel_ = retry_ = nullptr;
        shownStage_.clear(); shownMetrics_.clear(); shownProgress_.clear();
        layoutWidth_ = layoutHeight_ = 0; layoutDpi_ = 0;
    }
    HFONT UiFont(int height, int weight)
    {
        auto create = [&](const wchar_t* family) { return CreateFontW(-Scale(height), 0, 0, 0, weight, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, family); };
        auto font = create(L"Segoe UI Variable Text");
        HDC dc = GetDC(nullptr);
        wchar_t actual[LF_FACESIZE]{};
        if (dc && font)
        {
            const auto previous = SelectObject(dc, font);
            GetTextFaceW(dc, LF_FACESIZE, actual);
            SelectObject(dc, previous);
        }
        if (dc) ReleaseDC(nullptr, dc);
        if (!EqualOrdinal(actual, L"Segoe UI Variable Text"))
        {
            if (font) DeleteObject(font);
            font = create(L"Segoe UI");
        }
        return font;
    }
    void Fonts()
    {
        const auto previous = font_;
        const auto previousHeading = headingFont_;
        font_ = UiFont(14, FW_NORMAL);
        headingFont_ = UiFont(18, FW_SEMIBOLD);
        for (auto control : { heading_, stage_, metrics_, progress_, cancel_, retry_ })
            if (control) SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(control == heading_ ? headingFont_ : font_), FALSE);
        if (previous) DeleteObject(previous);
        if (previousHeading) DeleteObject(previousHeading);
    }
    HWND Child(const wchar_t* type, const wchar_t* text, DWORD style, int id, bool visible = true)
    {
        auto child = CreateWindowExW(0, type, text, WS_CHILD | (visible ? WS_VISIBLE : 0) | style, 0, 0, 0, 0, window_,
            reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), GetModuleHandleW(nullptr), nullptr);
        if (!child) Fail(L"Create preparation control");
        return child;
    }
    void OpenWindow()
    {
        dpi_ = GetDpiForSystem();
        if (!dpi_) dpi_ = 96;
        if (!canvas_) canvas_ = CreateSolidBrush(Canvas);
        WNDCLASSW type{};
        type.lpfnWndProc = WindowProc; type.hInstance = GetModuleHandleW(nullptr);
        type.lpszClassName = L"LANMinecraft.BootstrapPreparation";
        type.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        type.hIcon = LoadIconW(type.hInstance, MAKEINTRESOURCEW(1));
        type.hbrBackground = canvas_;
        if (!RegisterClassW(&type) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) Fail(L"Register preparation window");
        const auto initial = MinimumOuter();
        const int width = initial.cx, height = initial.cy;
        POINT cursor{}; GetCursorPos(&cursor);
        MONITORINFO monitor{ sizeof(monitor) };
        GetMonitorInfoW(MonitorFromPoint(cursor, MONITOR_DEFAULTTOPRIMARY), &monitor);
        const int x = monitor.rcWork.left + (monitor.rcWork.right - monitor.rcWork.left - width) / 2;
        const int y = monitor.rcWork.top + (monitor.rcWork.bottom - monitor.rcWork.top - height) / 2;
        window_ = CreateWindowExW(WindowExtendedStyle, type.lpszClassName, L"LANMinecraft", WindowStyle,
            x, y, width, height, nullptr, nullptr, type.hInstance, this);
        if (!window_) Fail(L"Create preparation window");
        const auto actualDpi = GetDpiForWindow(window_);
        if (actualDpi) dpi_ = actualDpi;
        // Windows may add nonclient extended styles during creation. Measure
        // the resulting frame as well as the actual monitor DPI while hidden.
        const auto adjusted = MinimumOuter();
        const int adjustedWidth = adjusted.cx, adjustedHeight = adjusted.cy;
        if (!SetWindowPos(window_, nullptr,
            monitor.rcWork.left + (monitor.rcWork.right - monitor.rcWork.left - adjustedWidth) / 2,
            monitor.rcWork.top + (monitor.rcWork.bottom - monitor.rcWork.top - adjustedHeight) / 2,
            adjustedWidth, adjustedHeight, SWP_NOZORDER | SWP_NOACTIVATE)) Fail(L"Size preparation window");
        const int dark = 1; DwmSetWindowAttribute(window_, 20, &dark, sizeof(dark));
        heading_ = Child(L"STATIC", L"Подготавливаем первый запуск", SS_LEFT, HeadingId);
        stage_ = Child(L"STATIC", L"", SS_LEFT, StageId);
        metrics_ = Child(L"STATIC", L"", SS_LEFT, MetricsId);
        progress_ = Child(L"STATIC", L"", SS_OWNERDRAW, ProgressId);
        cancel_ = Child(L"BUTTON", L"Отмена", WS_TABSTOP | BS_OWNERDRAW, IDCANCEL);
        retry_ = Child(L"BUTTON", L"Повторить", WS_TABSTOP | BS_OWNERDRAW | WS_DISABLED, IDRETRY, false);
        cancelEnabled_ = true; retryEnabled_ = retryVisible_ = false;
        Fonts(); Layout(); UpdateControls();
        // ShowWindow's first invocation can honor STARTUPINFO's maximized or
        // minimized state. The preparation dialog always starts compact and
        // normal, with its children and monitor-specific fonts already ready.
        if (!SetWindowPos(window_, nullptr, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW)) Fail(L"Show preparation window");
        UpdateWindow(window_);
        SetFocus(retryVisible_ ? retry_ : cancel_);
        SetTimer(window_, 1, 100, nullptr);
    }
    void Layout()
    {
        if (!window_ || !heading_ || changingDpi_) return;
        RECT client{}; GetClientRect(window_, &client);
        if (layoutWidth_ == client.right && layoutHeight_ == client.bottom && layoutDpi_ == dpi_) return;
        layoutWidth_ = client.right; layoutHeight_ = client.bottom; layoutDpi_ = dpi_;
        const int edge = Scale(16), width = std::max(1, static_cast<int>(client.right) - edge * 2);
        const int bottom = client.bottom - edge - Scale(36);
        const int buttonWidth = Scale(112);
        auto batch = BeginDeferWindowPos(6);
        if (!batch) Fail(L"Begin preparation layout");
        const auto place = [&](HWND child, int x, int y, int w, int h)
        {
            batch = DeferWindowPos(batch, child, nullptr, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW);
            if (!batch) Fail(L"Place preparation control");
        };
        place(heading_, edge, Scale(16), width, Scale(28));
        place(stage_, edge, Scale(48), width, Scale(40));
        place(metrics_, edge, Scale(96), width, Scale(20));
        place(progress_, edge, Scale(128), width, Scale(8));
        place(cancel_, client.right - edge - buttonWidth, bottom, buttonWidth, Scale(36));
        place(retry_, client.right - edge - buttonWidth * 2 - Scale(8), bottom, buttonWidth, Scale(36));
        if (!EndDeferWindowPos(batch)) Fail(L"Finish preparation layout");
        // The parent fills only its clipped paint region. Descendant buffering
        // presents the whole layout together without an exposed erase frame.
        RedrawWindow(window_, nullptr, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN);
    }
    static std::wstring Amount(double bytes)
    {
        std::wostringstream text;
        if (bytes >= 1024 * 1024) text << std::fixed << std::setprecision(1) << bytes / (1024 * 1024) << L" МБ";
        else text << std::fixed << std::setprecision(0) << bytes / 1024 << L" КБ";
        auto value = text.str(); std::replace(value.begin(), value.end(), L'.', L','); return value;
    }
    void Changed() noexcept
    {
#if defined(LANMINECRAFT_BOOTSTRAP_TEST_SOURCES)
        ++mutations_;
#endif
    }
    void SetText(HWND child, std::wstring& previous, const std::wstring& next)
    {
        if (previous == next) return;
        if (!SetWindowTextW(child, next.c_str())) Fail(L"Update preparation text");
        previous = next;
        Changed();
    }
    void SetEnabled(HWND child, bool& previous, bool next)
    {
        if (previous == next) return;
        EnableWindow(child, next ? TRUE : FALSE);
        previous = next;
        Changed();
    }
    void SetRetryVisible(bool visible)
    {
        if (retryVisible_ == visible) return;
        if (!SetWindowPos(retry_, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE |
            (visible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW))) Fail(L"Update preparation retry visibility");
        retryVisible_ = visible;
        Changed();
    }
    void UpdateControls()
    {
        if (!window_ || !heading_) return;
        std::wstring stage, error;
        std::uint64_t downloaded, total, version;
        bool retryable;
        {
            std::lock_guard<std::mutex> lock(state_.gate);
            stage = state_.stage; error = state_.error;
            downloaded = state_.downloaded; total = state_.total; version = state_.stageVersion;
            retryable = state_.retryable && static_cast<bool>(state_.failure);
        }
        const bool cancelled = state_.cancelled.load();
        const bool failed = state_.done.load() && retryable && !cancelled;
        const auto now = GetTickCount64();
        const bool stageChanged = version != lastStage_;
        if (stageChanged || !sampledAt_ || downloaded < lastBytes_ || (!determinate_ && total > 0))
        { lastStage_ = version; lastBytes_ = downloaded; sampledAt_ = now; speed_ = 0; }
        else if (!failed && !cancelled && now - sampledAt_ >= 250)
        {
            const double measured = (downloaded - lastBytes_) * 1000.0 / static_cast<double>(now - sampledAt_);
            speed_ = speed_ == 0 ? measured : speed_ * 0.65 + measured * 0.35;
            lastBytes_ = downloaded; sampledAt_ = now;
        }
        if (failed || cancelled) speed_ = 0;
        const bool stateChanged = retryVisible_ != failed || cancelEnabled_ != !cancelled;
        SetText(stage_, shownStage_, cancelled ? L"Отменяем подготовку…" : failed ? error : stage);
        SetEnabled(cancel_, cancelEnabled_, !cancelled);
        SetRetryVisible(failed);
        SetEnabled(retry_, retryEnabled_, failed);
        // Worker notifications can arrive thousands of times per second.
        // Render ordinary byte changes at most ten times per second, while
        // stage, cancellation and error transitions remain immediate.
        if (!renderedAt_ || stageChanged || stateChanged || now - renderedAt_ >= 100)
        {
            const bool determinate = total > 0;
            const int percent = determinate ? static_cast<int>(std::min<std::uint64_t>(100, downloaded * 100 / total)) : 0;
            const auto progress = determinate ? std::to_wstring(percent) + L"%" : L"";
            std::wstring metrics;
            if (determinate)
                metrics = progress + L" · " + Amount(static_cast<double>(downloaded)) + L" / " +
                    Amount(static_cast<double>(total)) + L" · " + Amount(speed_) + L"/с";
            SetText(metrics_, shownMetrics_, metrics);
            SetText(progress_, shownProgress_, progress);
            if (determinate_ != determinate || percent_ != percent)
            {
                determinate_ = determinate; percent_ = percent;
                InvalidateRect(progress_, nullptr, FALSE);
                Changed();
            }
            renderedAt_ = now;
        }
        if (failed && !errorShown_) { errorShown_ = true; SetFocus(retry_); }
    }
    void Retry()
    {
        if (!state_.done.load() || state_.cancelled.load()) return;
        bool allowed;
        { std::lock_guard<std::mutex> lock(state_.gate); allowed = state_.retryable && static_cast<bool>(state_.failure); }
        if (allowed) { StartWorker(); UpdateControls(); SetFocus(cancel_); }
    }
    LRESULT Draw(const DRAWITEMSTRUCT& item)
    {
#if defined(LANMINECRAFT_BOOTSTRAP_TEST_SOURCES)
        ++draws_;
#endif
        auto fill = [&](COLORREF color, const RECT& area)
        {
            SetDCBrushColor(item.hDC, color);
            FillRect(item.hDC, &area, static_cast<HBRUSH>(GetStockObject(DC_BRUSH)));
        };
        if (item.CtlID == ProgressId)
        {
            fill(Surface, item.rcItem);
            RECT amount = item.rcItem;
            amount.right = amount.left + (amount.right - amount.left) * percent_ / 100;
            if (determinate_ && percent_ > 0) fill(Primary, amount);
            return TRUE;
        }
        const bool primary = item.CtlID == IDRETRY;
        const bool disabled = (item.itemState & ODS_DISABLED) != 0;
        const bool focused = (item.itemState & (ODS_FOCUS | ODS_SELECTED)) != 0;
        fill(primary ? Primary : focused ? RGB(48, 48, 48) : Surface, item.rcItem);
        SetBkMode(item.hDC, TRANSPARENT);
        SetTextColor(item.hDC, disabled ? Muted : primary ? RGB(0, 0, 0) : Text);
        const auto previous = SelectObject(item.hDC, font_);
        RECT text = item.rcItem; text.left += Scale(8); text.right -= Scale(8);
        DrawTextW(item.hDC, primary ? L"Повторить" : L"Отмена", -1, &text, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        SelectObject(item.hDC, previous);
        return TRUE;
    }
    static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam)
    {
        auto self = reinterpret_cast<PreparationUi*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_NCCREATE)
        {
            self = static_cast<PreparationUi*>(reinterpret_cast<CREATESTRUCTW*>(lparam)->lpCreateParams);
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
            self->window_ = window;
        }
        if (!self) return DefWindowProcW(window, message, wparam, lparam);
        try
        {
        switch (message)
        {
#if defined(LANMINECRAFT_BOOTSTRAP_TEST_SOURCES)
        case WM_APP + 0x52:
            // Scalar counters only, for the exact owned process. No GDI or
            // process-private pointer crosses the test message boundary.
            if (wparam == 0) return static_cast<LRESULT>(self->mutations_);
            if (wparam == 1) return static_cast<LRESULT>(self->draws_);
            if (wparam == 2) return static_cast<LRESULT>(self->erases_);
            return -1;
        case WM_APP + 0x53:
        {
            // Exercise the real user-facing error formatter without filling a
            // disk or altering ACLs. An actual retryable failure is required.
            if (wparam != 0 && wparam != ERROR_DISK_FULL && wparam != ERROR_ACCESS_DENIED) return -1;
            {
                std::lock_guard<std::mutex> lock(self->state_.gate);
                if (!self->state_.done.load() || !self->state_.retryable || !self->state_.failure) return -1;
                self->state_.error = ErrorMessage(static_cast<unsigned long>(wparam));
            }
            self->UpdateControls();
            return 1;
        }
        case WM_APP + 0x51:
        {
            // Scalar-only measurement for the owned test process. HFONT values
            // cannot be used by the test runner in a different process.
            const int id = static_cast<int>(wparam);
            if (id == ProgressId) return 0; // The owner-drawn track paints no text.
            if (id != HeadingId && id != StageId && id != MetricsId && id != IDCANCEL && id != IDRETRY) return -1;
            const auto child = GetDlgItem(window, id);
            if (child == nullptr) return -1;
            const int length = GetWindowTextLengthW(child);
            if (length < 0 || length > 8192) return -1;
            std::wstring text(static_cast<std::size_t>(length) + 1, L'\0');
            const auto copied = GetWindowTextW(child, text.data(), static_cast<int>(text.size()));
            text.resize(static_cast<std::size_t>(std::max(0, copied)));
            const auto font = reinterpret_cast<HFONT>(SendMessageW(child, WM_GETFONT, 0, 0));
            HDC dc = GetDC(child);
            if (dc == nullptr || font == nullptr) { if (dc) ReleaseDC(child, dc); return -1; }
            const auto previous = SelectObject(dc, font);
            if (previous == nullptr || previous == HGDI_ERROR) { ReleaseDC(child, dc); return -1; }
            RECT area{}; GetClientRect(child, &area);
            area.top = 0; area.bottom = 0;
            const auto flags = DT_CALCRECT | DT_NOPREFIX | ((id == IDCANCEL || id == IDRETRY) ? DT_SINGLELINE : DT_WORDBREAK);
            const int height = text.empty() ? 0 : DrawTextW(dc, text.c_str(), static_cast<int>(text.size()), &area, flags);
            SelectObject(dc, previous); ReleaseDC(child, dc);
            const auto width = std::max<LONG>(0, area.right - area.left);
            return static_cast<LRESULT>((static_cast<unsigned long>(std::min(height, 65535)) << 16) |
                static_cast<unsigned long>(std::min<LONG>(width, 65535)));
        }
#endif
        case WM_CLOSE: self->state_.Cancel(); self->UpdateControls(); return 0;
        case WM_COMMAND:
            if (LOWORD(wparam) == IDCANCEL) { self->state_.Cancel(); self->UpdateControls(); return 0; }
            if (LOWORD(wparam) == IDRETRY) { self->Retry(); return 0; }
            break;
        case WM_KEYDOWN:
            if (wparam == VK_ESCAPE) { self->state_.Cancel(); return 0; }
            if (wparam == VK_RETURN) { self->Retry(); return 0; }
            break;
        case WM_TIMER: self->UpdateControls(); return 0;
        case WM_SIZE: self->Layout(); return 0;
        case WM_DPICHANGED:
        {
            self->dpi_ = LOWORD(wparam) ? LOWORD(wparam) : 96;
            const auto suggested = reinterpret_cast<RECT*>(lparam);
            const auto minimum = self->MinimumOuter();
            self->changingDpi_ = true;
            SetWindowPos(window, nullptr, suggested->left, suggested->top,
                std::max(minimum.cx, suggested->right - suggested->left),
                std::max(minimum.cy, suggested->bottom - suggested->top), SWP_NOZORDER | SWP_NOACTIVATE);
            self->Fonts();
            self->changingDpi_ = false;
            self->layoutDpi_ = 0;
            self->Layout(); return 0;
        }
        case WM_GETMINMAXINFO:
        {
            const auto minimum = self->MinimumOuter();
            auto limits = reinterpret_cast<MINMAXINFO*>(lparam);
            limits->ptMinTrackSize = POINT{ minimum.cx, minimum.cy }; return 0;
        }
        case WM_WINDOWPOSCHANGING:
        {
            auto position = reinterpret_cast<WINDOWPOS*>(lparam);
            if ((position->flags & SWP_NOSIZE) == 0)
            { const auto minimum = self->MinimumOuter(); position->cx = std::max(position->cx, static_cast<int>(minimum.cx)); position->cy = std::max(position->cy, static_cast<int>(minimum.cy)); }
            break;
        }
        case WM_CTLCOLORSTATIC:
            SetBkColor(reinterpret_cast<HDC>(wparam), Canvas);
            SetTextColor(reinterpret_cast<HDC>(wparam), GetDlgCtrlID(reinterpret_cast<HWND>(lparam)) == MetricsId ? Muted : Text);
            return reinterpret_cast<LRESULT>(self->canvas_);
        case WM_ERASEBKGND:
#if defined(LANMINECRAFT_BOOTSTRAP_TEST_SOURCES)
            ++self->erases_;
#endif
            // WM_PAINT fills the clipped region inside the composited frame.
            // A separate background erase would expose an intermediate frame.
            return 1;
        case WM_PAINT:
        {
            PAINTSTRUCT paint{};
            const auto dc = BeginPaint(window, &paint);
            if (dc) FillRect(dc, &paint.rcPaint, self->canvas_);
            EndPaint(window, &paint);
            return 0;
        }
        case WM_DRAWITEM: return self->Draw(*reinterpret_cast<DRAWITEMSTRUCT*>(lparam));
        case WM_DESTROY: KillTimer(window, 1); self->window_ = nullptr; return 0;
        }
        }
        catch (...)
        {
            // No C++ exception may cross the Win32 callback boundary.
            self->uiFailure_ = std::current_exception();
            self->state_.Cancel();
            return 0;
        }
        return DefWindowProcW(window, message, wparam, lparam);
    }
};
