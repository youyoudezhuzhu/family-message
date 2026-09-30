// ============================================================================
//  家庭消息 —— 远程解锁 Credential Provider（Phase 3）
//
//  它由 LogonUI.exe 以 SYSTEM 身份加载到登录界面（安全桌面）进程里。
//  平时**完全隐形**（未 armed 时返回 0 个凭据）；只有 PC 端 Agent 收到了
//  服务端下发的解锁请求、并把一次性 arm 凭证写到
//    %ProgramData%\FamilyAgent\unlock-arm.json
//  之后，它才出现一个凭据并自动提交 ⇒ 会话真正解锁。
//
//  三条硬规矩（详见 docs/REMOTE-UNLOCK-PLAN.md §18）：
//    1. fail-closed：任何异常/超时都退回"0 个凭据"，绝不抛、绝不阻塞 LogonUI；
//    2. 不落明文：口令只在内存里，用完 SecureZeroMemory；
//    3. 一次性：arm 凭证消费即删，失败也删（不反复试，免得把账户打到锁定）。
//
//  与 C# 侧共享的常量（改一处必改另一处）：
//    · CLSID          = {8F1E6D2A-4C3B-45A7-9E10-2B7C5D91A3F4}
//    · 目录           = %ProgramData%\FamilyAgent
//    · 凭据文件       = credential.json  {"User","ProtectedSecretBase64","CreatedAt","VerifiedAt"}
//    · DPAPI 附加熵   = "FamilyAgent.RemoteUnlock.v1"（UTF-8，无 NUL）
//    · arm 凭证文件   = unlock-arm.json {"request_id","nonce","user","expires_unix"}
//    · 日志           = cp.log
//
//  实现参考了 Windows Credential Provider 的公开机制与同类的开源实现
//  （PsyChip/hodor，MIT；只参考做法，代码自己写）。
// ============================================================================

#define WIN32_LEAN_AND_MEAN
#define WIN32_NO_STATUS          // 先关掉 winnt.h 的 STATUS_*，再用 ntstatus.h 的（否则重复定义）
#define _WIN32_WINNT 0x0A00

#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <credentialprovider.h>
#include <ntsecapi.h>
#include <wtsapi32.h>
#include <wincrypt.h>
#include <shlwapi.h>
#include <strsafe.h>
#include <new>
#include <string>
#include <vector>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "secur32.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "wtsapi32.lib")
#pragma comment(lib, "shlwapi.lib")

// ─────────────────────────────── 常量 ───────────────────────────────

static const CLSID CLSID_FamilyAgentUnlock =
    { 0x8F1E6D2A, 0x4C3B, 0x45A7, { 0x9E, 0x10, 0x2B, 0x7C, 0x5D, 0x91, 0xA3, 0xF4 } };

static const wchar_t* kDirName      = L"FamilyAgent";
static const wchar_t* kCredFile     = L"credential.json";
static const wchar_t* kArmFile      = L"unlock-arm.json";
static const wchar_t* kLogFile      = L"cp.log";
static const char*    kEntropyBytes = "FamilyAgent.RemoteUnlock.v1";   // 25 字节
static const DWORD    kArmTtlSkew   = 5;      // 允许 5 秒时钟偏差
static const int      kPollMs       = 500;    // 轮询 arm 凭证的间隔

// provider 级字段（锁屏上那行说明文字）
static const DWORD kFieldMessage = 0;                 // CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR_FIELDID = DWORD
static const GUID kFieldMessageGuid =
    { 0xA1B2C3D4, 0x1111, 0x2222, { 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA } };

// 消息（工作线程 → LogonUI 线程）
#define WM_FA_ARMED (WM_APP + 1)

// ─────────────────────────────── 小工具 ───────────────────────────────

static void LogLine(const wchar_t* fmt, ...)
{
    // 只追加、失败即忽略 —— CP 里不允许因为日志把登录界面搞坏
    wchar_t path[MAX_PATH] = {0};
    if (FAILED(StringCchPrintfW(path, MAX_PATH, L"%s\\%s", 
            []() -> const wchar_t* { static wchar_t buf[MAX_PATH] = {0};
                if (!buf[0]) { GetEnvironmentVariableW(L"ProgramData", buf, MAX_PATH); }
                return buf; }(), kDirName))) return;

    wchar_t file[MAX_PATH] = {0};
    if (FAILED(StringCchPrintfW(file, MAX_PATH, L"%s\\%s", path, kLogFile))) return;

    HANDLE h = CreateFileW(file, FILE_APPEND_DATA, FILE_SHARE_READ, nullptr,
                           OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;

    SYSTEMTIME st; GetLocalTime(&st);
    wchar_t line[1024] = {0};
    wchar_t body[768] = {0};
    va_list ap; va_start(ap, fmt);
    StringCchVPrintfW(body, 768, fmt, ap);
    va_end(ap);
    StringCchPrintfW(line, 1024, L"%04d-%02d-%02d %02d:%02d:%02d %s\r\n",
                     st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, body);
    DWORD wrote = 0;
    WriteFile(h, line, (DWORD)(wcslen(line) * sizeof(wchar_t)), &wrote, nullptr);
    CloseHandle(h);
}

static std::wstring ProgramDataPath(const wchar_t* leaf)
{
    wchar_t base[MAX_PATH] = {0};
    DWORD n = GetEnvironmentVariableW(L"ProgramData", base, MAX_PATH);
    std::wstring s;
    if (n == 0 || n >= MAX_PATH) return s;
    s = base; s += L"\\"; s += kDirName; s += L"\\"; s += leaf;
    return s;
}

static std::string ReadAllBytes(const std::wstring& path)
{
    std::string out;
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return out;
    char buf[4096];
    DWORD got = 0;
    while (ReadFile(h, buf, sizeof(buf), &got, nullptr) && got > 0)
        out.append(buf, got);
    CloseHandle(h);
    return out;
}

static bool TryGetJsonString(const std::string& json, const char* key, std::wstring& out)
{
    // 极简取值：找 "key" 后的第一个引号串。**不追求通用 JSON 解析** ——
    // 这两个文件都是我们自己写的，格式受控（说明见文件头）。
    std::string pat = "\""; pat += key; pat += "\"";
    size_t p = json.find(pat);
    if (p == std::string::npos) return false;
    size_t c = json.find(':', p + pat.size());
    if (c == std::string::npos) return false;
    size_t q = json.find('"', c + 1);
    if (q == std::string::npos) return false;
    size_t q2 = json.find('"', q + 1);
    if (q2 == std::string::npos) return false;
    std::string raw = json.substr(q + 1, q2 - q - 1);

    // UTF-8 → UTF-16（含 \uXXXX 之外的最小转义处理：\" \\ 已由上面的查找天然跳过）
    int need = MultiByteToWideChar(CP_UTF8, 0, raw.c_str(), (int)raw.size(), nullptr, 0);
    out.assign((size_t)(need > 0 ? need : 0), L'\0');
    if (need > 0) MultiByteToWideChar(CP_UTF8, 0, raw.c_str(), (int)raw.size(), &out[0], need);
    return true;
}

static bool TryGetJsonInt(const std::string& json, const char* key, long long& out)
{
    std::string pat = "\""; pat += key; pat += "\"";
    size_t p = json.find(pat);
    if (p == std::string::npos) return false;
    size_t c = json.find(':', p + pat.size());
    if (c == std::string::npos) return false;
    size_t i = c + 1;
    while (i < json.size() && (json[i] == ' ' || json[i] == '\t')) i++;
    bool neg = false;
    if (i < json.size() && json[i] == '-') { neg = true; i++; }
    long long v = 0; bool any = false;
    while (i < json.size() && json[i] >= '0' && json[i] <= '9') { v = v * 10 + (json[i] - '0'); i++; any = true; }
    if (!any) return false;
    out = neg ? -v : v;
    return true;
}

static std::vector<BYTE> Base64Decode(const std::string& in)
{
    static const signed char tbl[256] = { /* 'A'-'Z','a'-'z','0'-'9','+','/' */
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,62,-1,-1,-1,63,52,53,54,55,56,57,58,59,60,61,-1,-1,-1,-1,-1,-1,
        -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,-1,-1,-1,-1,-1,
        -1,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,50,51,-1,-1,-1,-1,-1,
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1
    };
    std::vector<BYTE> out;
    int val = 0, bits = -8;
    for (unsigned char ch : in)
    {
        if (ch == '=') break;
        signed char d = tbl[ch];
        if (d < 0) continue;                       // 跳过空白/换行
        val = (val << 6) + d; bits += 6;
        if (bits >= 0) { out.push_back((BYTE)((val >> bits) & 0xFF)); bits -= 8; }
    }
    return out;
}

// ─────────────────────── arm 凭证（一次性解锁指令）───────────────────────

struct ArmData
{
    std::wstring requestId, nonce, user;
    long long    expiresUnix = 0;
    bool valid = false;
};

static bool ReadArm(ArmData& out)
{
    out = ArmData();
    std::string json = ReadAllBytes(ProgramDataPath(kArmFile));
    if (json.empty()) return false;
    long long exp = 0;
    if (!TryGetJsonString(json, "nonce", out.nonce) || out.nonce.empty()) return false;
    if (!TryGetJsonString(json, "user", out.user) || out.user.empty()) return false;
    TryGetJsonString(json, "request_id", out.requestId);
    if (!TryGetJsonInt(json, "expires_unix", exp)) return false;
    out.expiresUnix = exp;

    SYSTEMTIME st; GetSystemTime(&st);
    FILETIME ft; SystemTimeToFileTime(&st, &ft);
    ULARGE_INTEGER u; u.LowPart = ft.dwLowDateTime; u.HighPart = ft.dwHighDateTime;
    long long nowUnix = (long long)((u.QuadPart / 10000000ULL) - 11644473600ULL);

    if (nowUnix > exp + kArmTtlSkew) { LogLine(L"[arm] 凭证已过期（exp=%lld now=%lld）", exp, nowUnix); return false; }
    out.valid = true;
    return true;
}

static void ConsumeArm()
{
    std::wstring p = ProgramDataPath(kArmFile);
    if (!p.empty()) DeleteFileW(p.c_str());
}

// ─────────────────────── 凭据文件（DPAPI LocalMachine）───────────────────────

static bool LoadCredential(std::wstring& user, std::wstring& secret)
{
    std::string json = ReadAllBytes(ProgramDataPath(kCredFile));
    if (json.empty()) { LogLine(L"[store] 读不到凭据文件"); return false; }
    std::wstring b64;
    if (!TryGetJsonString(json, "User", user) || user.empty()) { LogLine(L"[store] 凭据文件没有 User"); return false; }
    if (!TryGetJsonString(json, "ProtectedSecretBase64", b64) || b64.empty()) { LogLine(L"[store] 凭据文件没有密文"); return false; }

    std::string narrow(b64.begin(), b64.end());
    std::vector<BYTE> cipher = Base64Decode(narrow);
    if (cipher.empty()) { LogLine(L"[store] 密文 base64 解不开"); return false; }

    DATA_BLOB in { (DWORD)cipher.size(), cipher.data() };
    DATA_BLOB ent { (DWORD)strlen(kEntropyBytes), (BYTE*)kEntropyBytes };
    DATA_BLOB outBlob { 0, nullptr };
    // ⚠ 与 C# 的 DpapiSecretProtector 对齐：CRYPTPROTECT_LOCAL_MACHINE 指定**去哪个密钥库**
    //   找解密密钥（凭据是 LocalMachine 作用域存的，SYSTEM 身份的 LogonUI 才能解开）。
    if (!CryptUnprotectData(&in, nullptr, &ent, nullptr, nullptr,
                            CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE, &outBlob))
    {
        LogLine(L"[store] DPAPI 解密失败（err=%lu）", GetLastError());
        return false;
    }
    int need = MultiByteToWideChar(CP_UTF8, 0, (LPCCH)outBlob.pbData, (int)outBlob.cbData, nullptr, 0);
    secret.assign((size_t)(need > 0 ? need : 0), L'\0');
    if (need > 0) MultiByteToWideChar(CP_UTF8, 0, (LPCCH)outBlob.pbData, (int)outBlob.cbData, &secret[0], need);
    SecureZeroMemory(outBlob.pbData, outBlob.cbData);
    LocalFree(outBlob.pbData);
    outBlob.pbData = nullptr; outBlob.cbData = 0;
    return !secret.empty();
}

// ─────────────────────────── KERB 打包 ───────────────────────────

static HRESULT GetSessionLogonId(LUID* out)
{
    // 解锁场景要把"当前控制台会话"的登录 LUID 放进 KERB 结构里。
    // 拿法：WTSQueryUserToken（需要 SYSTEM + SE_TCB —— LogonUI 正好有）
    //       → GetTokenInformation(TokenStatistics) → AuthenticationId 就是 LUID。
    // ⚠ WTSINFOEX 里**没有** LogonId 字段（第一版写错了，CI 直接报 C2039），别再走那条路。
    DWORD session = WTSGetActiveConsoleSessionId();
    if (session == 0xFFFFFFFF) return E_FAIL;

    HANDLE token = nullptr;
    if (!WTSQueryUserToken(session, &token))
        return HRESULT_FROM_WIN32(GetLastError());

    TOKEN_STATISTICS stats;
    DWORD need = 0;
    BOOL ok = GetTokenInformation(token, TokenStatistics, &stats, sizeof(stats), &need);
    CloseHandle(token);
    if (!ok) return HRESULT_FROM_WIN32(GetLastError());

    *out = stats.AuthenticationId;
    return S_OK;
}

/// SHStrDupW 的替身（避免为它多带一个依赖；行为一致：CoTaskMemAlloc + 拷贝）。
static HRESULT CoTaskMemDupW(PCWSTR src, PWSTR* out)
{
    if (!out || !src) return E_INVALIDARG;
    size_t bytes = (wcslen(src) + 1) * sizeof(wchar_t);
    auto* p = (PWSTR)CoTaskMemAlloc(bytes);
    if (!p) return E_OUTOFMEMORY;
    memcpy(p, src, bytes);
    *out = p;
    return S_OK;
}

static void InitUnicodeString(UNICODE_STRING& us, const std::wstring& s)
{
    us.Buffer = (PWSTR)s.c_str();
    us.Length = (USHORT)(s.size() * sizeof(wchar_t));
    us.MaximumLength = (USHORT)((s.size() + 1) * sizeof(wchar_t));
}

static HRESULT PackKerbLogon(const std::wstring& domain, const std::wstring& user,
                             const std::wstring& password, CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus,
                             std::vector<BYTE>& outBlob)
{
    KERB_INTERACTIVE_UNLOCK_LOGON kiul;
    ZeroMemory(&kiul, sizeof(kiul));
    kiul.Logon.MessageType = KerbInteractiveLogon;
    InitUnicodeString(kiul.Logon.LogonDomainName, domain.empty() ? std::wstring(L".") : domain);
    InitUnicodeString(kiul.Logon.UserName, user);
    InitUnicodeString(kiul.Logon.Password, password);
    if (cpus == CPUS_UNLOCK_WORKSTATION)
    {
        if (FAILED(GetSessionLogonId(&kiul.LogonId)))
            LogLine(L"[kerb] 拿不到会话 LogonId（继续，置零）");
    }

    // 打包成一个连续缓冲：结构 + 各字符串，按 4 字节对齐
    const size_t header = sizeof(KERB_INTERACTIVE_UNLOCK_LOGON) - 3 * sizeof(UNICODE_STRING);
    size_t total = header + 3 * sizeof(UNICODE_STRING)
                 + kiul.Logon.LogonDomainName.MaximumLength
                 + kiul.Logon.UserName.MaximumLength
                 + kiul.Logon.Password.MaximumLength;
    outBlob.assign(total, 0);

    auto copyStr = [&](size_t& off, const std::wstring& src) -> UNICODE_STRING {
        UNICODE_STRING us;
        us.Length = (USHORT)(src.size() * sizeof(wchar_t));
        us.MaximumLength = us.Length + sizeof(wchar_t);
        us.Buffer = (PWSTR)(outBlob.data() + off);
        if (!src.empty()) memcpy(us.Buffer, src.c_str(), us.Length);
        off += us.MaximumLength;
        return us;
    };

    size_t off = sizeof(KERB_INTERACTIVE_UNLOCK_LOGON);
    KERB_INTERACTIVE_UNLOCK_LOGON* p = (KERB_INTERACTIVE_UNLOCK_LOGON*)outBlob.data();
    const std::wstring dom = domain.empty() ? std::wstring(L".") : domain;
    p->Logon.MessageType = KerbInteractiveLogon;
    p->Logon.LogonDomainName = copyStr(off, dom);
    p->Logon.UserName       = copyStr(off, user);
    p->Logon.Password       = copyStr(off, password);
    p->LogonId              = kiul.LogonId;
    (void)header;
    return S_OK;
}

static HRESULT LookupAuthPackage(ULONG* outPackage)
{
    // Windows 官方凭据提供程序示例用的也是这条：MSV1_0 接受 KERB 形状的序列化
    static ULONG cached = 0;
    if (cached) { *outPackage = cached; return S_OK; }

    HANDLE lsa = nullptr;
    NTSTATUS st = LsaConnectUntrusted(&lsa);
    if (st != 0 || !lsa) return HRESULT_FROM_NT(st);

    LSA_STRING name;
    name.Buffer = (PCHAR)"MICROSOFT_AUTHENTICATION_PACKAGE_V1_0";
    name.Length = (USHORT)strlen(name.Buffer);
    name.MaximumLength = name.Length + 1;

    ULONG pkg = 0;
    st = LsaLookupAuthenticationPackage(lsa, &name, &pkg);
    LsaDeregisterLogonProcess(lsa);
    if (st != 0) return HRESULT_FROM_NT(st);
    cached = pkg;
    *outPackage = pkg;
    return S_OK;
}

// ─────────────────────────── 引用计数 / 工厂 ───────────────────────────

static LONG g_objects = 0;
static HINSTANCE g_module = nullptr;

class CProvider;

static void SplitUser(const std::wstring& raw, std::wstring& domain, std::wstring& user)
{
    size_t p = raw.find(L'\\');
    if (p != std::wstring::npos && p > 0 && p + 1 < raw.size())
    {
        domain = raw.substr(0, p);
        user = raw.substr(p + 1);
        return;
    }
    domain.clear();                    // 裸账户名 → KERB 里用 "."（本机）
    user = raw;
}

// ─────────────────────────── 凭据对象 ───────────────────────────

class CCredential : public ICredentialProviderCredential
{
public:
    CCredential() { InterlockedIncrement(&g_objects); }
    virtual ~CCredential() { InterlockedDecrement(&g_objects); }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_ICredentialProviderCredential)
        {
            *ppv = static_cast<ICredentialProviderCredential*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_ref); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        LONG c = InterlockedDecrement(&_ref);
        if (c == 0) delete this;
        return c;
    }

    // ICredentialProviderCredential
    IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents* pcpce) override
    { _events = pcpce; return S_OK; }

    IFACEMETHODIMP UnAdvise() override { _events = nullptr; return S_OK; }

    IFACEMETHODIMP SetSelected(BOOL* pbAutoLogon) override
    { if (pbAutoLogon) *pbAutoLogon = TRUE; return S_OK; }      // 选中即自动提交

    IFACEMETHODIMP SetDeselected() override { return S_OK; }

    IFACEMETHODIMP GetFieldState(DWORD dwFieldID, CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
                                 CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis) override
    {
        if (!pcpfs || !pcpfis || dwFieldID != kFieldMessage) return E_INVALIDARG;
        *pcpfs = CPFS_DISPLAY_IN_SELECTED_TILE;
        *pcpfis = CPFIS_NONE;
        return S_OK;
    }

    IFACEMETHODIMP GetStringValue(DWORD dwFieldID, PWSTR* ppwsz) override
    {
        if (!ppwsz || dwFieldID != kFieldMessage) return E_INVALIDARG;
        return CoTaskMemDupW(L"正在远程解锁…", ppwsz);
    }

    IFACEMETHODIMP GetBitmapValue(DWORD, HBITMAP*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetCheckboxValue(DWORD, BOOL*, PWSTR*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetSubmitButtonValue(DWORD, DWORD*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetComboBoxValueCount(DWORD, DWORD*, DWORD*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetComboBoxValueAt(DWORD, DWORD, PWSTR*) override { return E_NOTIMPL; }
    IFACEMETHODIMP SetStringValue(DWORD, PCWSTR) override { return E_NOTIMPL; }
    IFACEMETHODIMP SetCheckboxValue(DWORD, BOOL) override { return E_NOTIMPL; }
    IFACEMETHODIMP SetComboBoxSelectedValue(DWORD, DWORD) override { return E_NOTIMPL; }
    IFACEMETHODIMP CommandLinkClicked(DWORD) override { return E_NOTIMPL; }

    IFACEMETHODIMP GetSerialization(CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* pcpgsr,
                                    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs,
                                    PWSTR* ppwszOptionalStatusText,
                                    CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override
    {
        if (!pcpgsr || !pcpcs) return E_POINTER;
        *pcpgsr = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
        if (ppwszOptionalStatusText) *ppwszOptionalStatusText = nullptr;
        if (pcpsiOptionalStatusIcon) *pcpsiOptionalStatusIcon = CPSI_NONE;
        ZeroMemory(pcpcs, sizeof(*pcpcs));

        // ① arm 凭证必须仍然有效（可能已被消费 / 已过期）
        ArmData arm;
        if (!ReadArm(arm) || !arm.valid)
        {
            LogLine(L"[serialize] 没有有效的 arm 凭证 → 不提供凭据");
            return S_OK;
        }

        // ② 读凭据（DPAPI LocalMachine）
        std::wstring user, secret;
        if (!LoadCredential(user, secret))
        {
            LogLine(L"[serialize] 凭据读不出 → 不提供凭据");
            ConsumeArm();
            return S_OK;
        }
        user = arm.user.empty() ? user : arm.user;      // arm 里的用户名优先（就是这次要解锁的账户）

        // ③ 打包成 LSA 能吃的 blob
        std::wstring domain, account;
        SplitUser(user, domain, account);
        std::vector<BYTE> blob;
        HRESULT hr = PackKerbLogon(domain, account, secret, _cpus, blob);
        SecureZeroMemory(secret.empty() ? nullptr : &secret[0], secret.size() * sizeof(wchar_t));

        if (FAILED(hr) || blob.empty())
        {
            LogLine(L"[serialize] 打包失败 hr=0x%08X", hr);
            ConsumeArm();
            return S_OK;
        }

        ULONG pkg = 0;
        if (FAILED(LookupAuthPackage(&pkg)))
        {
            LogLine(L"[serialize] 拿不到认证包 ID");
            ConsumeArm();
            return S_OK;
        }

        BYTE* buf = (BYTE*)CoTaskMemAlloc(blob.size());
        if (!buf) { ConsumeArm(); return E_OUTOFMEMORY; }
        memcpy(buf, blob.data(), blob.size());
        pcpcs->rgbSerialization = buf;
        pcpcs->cbSerialization = (ULONG)blob.size();
        pcpcs->ulAuthenticationPackage = pkg;
        pcpcs->clsidCredentialProvider = CLSID_FamilyAgentUnlock;
        *pcpgsr = CPGSR_RETURN_CREDENTIAL_FINISHED;

        ConsumeArm();                                   // 一次性：用掉就删
        LogLine(L"[serialize] 已提交凭据（%lu 字节，包=%lu，账户=%s）",
                pcpcs->cbSerialization, pkg, user.c_str());
        return S_OK;
    }

    IFACEMETHODIMP ReportResult(NTSTATUS ntsStatus, NTSTATUS ntsSubstatus,
                                PWSTR* ppwszOptionalStatusText,
                                CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override
    {
        // 失败也要把 arm 凭证删掉：下次不该再自动试（避免把账户打到锁定）
        LogLine(L"[result] 登录结果 status=0x%08X sub=0x%08X", ntsStatus, ntsSubstatus);
        ConsumeArm();
        if (ppwszOptionalStatusText) *ppwszOptionalStatusText = nullptr;
        if (pcpsiOptionalStatusIcon) *pcpsiOptionalStatusIcon = (ntsStatus == 0) ? CPSI_SUCCESS : CPSI_ERROR;
        return S_OK;
    }

    void SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus) { _cpus = cpus; }

private:
    LONG _ref = 1;
    ICredentialProviderCredentialEvents* _events = nullptr;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO _cpus = CPUS_LOGON;
};

// ─────────────────────────── Provider ───────────────────────────

class CProvider : public ICredentialProvider
{
public:
    CProvider() { InterlockedIncrement(&g_objects); }
    virtual ~CProvider()
    {
        StopWorker();
        if (_hwnd) { DestroyWindow(_hwnd); _hwnd = nullptr; }
        InterlockedDecrement(&g_objects);
    }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_ICredentialProvider)
        {
            *ppv = static_cast<ICredentialProvider*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_ref); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        LONG c = InterlockedDecrement(&_ref);
        if (c == 0) delete this;
        return c;
    }

    // ICredentialProvider
    IFACEMETHODIMP SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD) override
    {
        // 只支持"登录界面"与"锁屏解锁"；别的场景（UAC/CREDUI）一律不参与
        if (cpus != CPUS_LOGON && cpus != CPUS_UNLOCK_WORKSTATION)
            return E_NOTIMPL;
        _cpus = cpus;
        return S_OK;
    }

    IFACEMETHODIMP SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*) override
    { return E_NOTIMPL; }        // 我们不消费调用方给的序列化

    IFACEMETHODIMP Advise(ICredentialProviderEvents* pcpe, UINT_PTR upAdviseContext) override
    {
        _events = pcpe;
        _adviseContext = upAdviseContext;
        EnsureWindow();
        StartWorker();
        return S_OK;
    }

    IFACEMETHODIMP UnAdvise() override
    {
        _events = nullptr;
        _adviseContext = 0;
        StopWorker();
        return S_OK;
    }

    IFACEMETHODIMP GetFieldDescriptorCount(DWORD* pdwCount) override
    {
        if (!pdwCount) return E_POINTER;
        ArmData arm;
        *pdwCount = (ReadArm(arm) && arm.valid) ? 1 : 0;
        return S_OK;
    }

    IFACEMETHODIMP GetFieldDescriptorAt(DWORD dwIndex, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** ppcpfd) override
    {
        if (!ppcpfd) return E_POINTER;
        *ppcpfd = nullptr;
        if (dwIndex != kFieldMessage) return E_INVALIDARG;

        auto* d = (CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*)CoTaskMemAlloc(sizeof(*d));
        if (!d) return E_OUTOFMEMORY;
        ZeroMemory(d, sizeof(*d));
        d->dwFieldID = kFieldMessage;
        d->cpft = CPFT_LARGE_TEXT;
        d->guidFieldType = kFieldMessageGuid;
        if (FAILED(CoTaskMemDupW(L"远程解锁", &d->pszLabel)))
        {
            CoTaskMemFree(d);
            return E_OUTOFMEMORY;
        }
        *ppcpfd = d;
        return S_OK;
    }

    IFACEMETHODIMP GetCredentialCount(DWORD* pdwCount, DWORD* pdwDefault,
                                      BOOL* pbAutoLogonWithDefault) override
    {
        if (!pdwCount) return E_POINTER;
        ArmData arm;
        const bool armed = ReadArm(arm) && arm.valid;

        *pdwCount = armed ? 1 : 0;          // ★ 平时 0 个：锁屏界面与没装过一样
        if (pdwDefault) *pdwDefault = 0;
        if (pbAutoLogonWithDefault) *pbAutoLogonWithDefault = armed ? TRUE : FALSE;
        return S_OK;
    }

    IFACEMETHODIMP GetCredentialAt(DWORD dwIndex, ICredentialProviderCredential** ppcpc) override
    {
        if (!ppcpc) return E_POINTER;
        *ppcpc = nullptr;
        ArmData arm;
        if (dwIndex != 0 || !(ReadArm(arm) && arm.valid)) return E_INVALIDARG;

        auto* c = new (std::nothrow) CCredential();
        if (!c) return E_OUTOFMEMORY;
        c->SetUsageScenario(_cpus);
        *ppcpc = c;
        return S_OK;
    }

private:
    void EnsureWindow();
    void StartWorker();
    void StopWorker();
    void PollOnce();                 // 工作线程的"看一眼"（C++ 对象在这一帧里，__try 在外面）
    void FireCredentialsChanged();
    static LRESULT CALLBACK WndProc(HWND, UINT, WPARAM, LPARAM);

    LONG _ref = 1;
    ICredentialProviderEvents* _events = nullptr;
    UINT_PTR _adviseContext = 0;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO _cpus = CPUS_LOGON;
    HWND _hwnd = nullptr;
    HANDLE _thread = nullptr;
    volatile LONG _stop = 0;
    std::wstring _lastNonce;
};

static CProvider* g_lastProvider = nullptr;      // 只为给窗口过程找回调（同一进程内）

void CProvider::EnsureWindow()
{
    if (_hwnd) return;
    static const wchar_t* kClass = L"FamilyAgentCpMsgWnd";
    static bool registered = false;
    if (!registered)
    {
        WNDCLASSEXW wc = {0};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = CProvider::WndProc;
        wc.hInstance = g_module;
        wc.lpszClassName = kClass;
        if (RegisterClassExW(&wc)) registered = true;
    }
    // 消息窗口：LogonUI 自己的消息泵会派发我们 PostMessage 过来的消息
    _hwnd = CreateWindowExW(0, kClass, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, g_module, nullptr);
    if (!_hwnd) LogLine(L"[provider] 创建消息窗口失败 err=%lu", GetLastError());
    g_lastProvider = this;
}

void CProvider::StartWorker()
{
    if (_thread) return;
    _stop = 0;
    _thread = CreateThread(nullptr, 0, [](LPVOID p) -> DWORD {
        auto* self = (CProvider*)p;
        while (InterlockedCompareExchange(&self->_stop, 0, 0) == 0)
        {
            // ★ __try 不能和需要析构的 C++ 对象同帧（C2712），所以把活儿交给
            //   PollOnce，这里只兜 SEH。
            __try { self->PollOnce(); }
            __except (EXCEPTION_EXECUTE_HANDLER) { /* worker 里绝不把异常带出去 */ }
            Sleep(kPollMs);
        }
        return 0;
    }, this, 0, nullptr);
}

void CProvider::PollOnce()
{
    ArmData arm;
    if (ReadArm(arm) && arm.valid && arm.nonce != _lastNonce)
    {
        _lastNonce = arm.nonce;
        LogLine(L"[worker] 发现 arm 凭证（nonce=%s…）→ 通知 LogonUI 重枚举",
                arm.nonce.substr(0, 8).c_str());
        if (_hwnd) PostMessageW(_hwnd, WM_FA_ARMED, 0, 0);
    }
}

void CProvider::StopWorker()
{
    if (!_thread) return;
    InterlockedExchange(&_stop, 1);
    WaitForSingleObject(_thread, 2000);
    CloseHandle(_thread);
    _thread = nullptr;
}

LRESULT CALLBACK CProvider::WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_FA_ARMED)
    {
        if (g_lastProvider) g_lastProvider->FireCredentialsChanged();
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

void CProvider::FireCredentialsChanged()
{
    // ★ 必须在 LogonUI 的 STA 线程上调用（这里就是：消息由它的泵派发）
    __try
    {
        if (_events && _adviseContext)
        {
            _events->CredentialsChanged(_adviseContext);
            LogLine(L"[provider] 已发 CredentialsChanged");
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        LogLine(L"[provider] CredentialsChanged 抛了，忽略");
    }
}

// ─────────────────────────── 类工厂 / 导出 ───────────────────────────

class CClassFactory : public IClassFactory
{
public:
    CClassFactory() { InterlockedIncrement(&g_objects); }
    virtual ~CClassFactory() { InterlockedDecrement(&g_objects); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_ref); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        LONG c = InterlockedDecrement(&_ref);
        if (c == 0) delete this;
        return c;
    }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto* p = new (std::nothrow) CProvider();
        if (!p) return E_OUTOFMEMORY;
        HRESULT hr = p->QueryInterface(riid, ppv);
        p->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_objects);
        else      InterlockedDecrement(&g_objects);
        return S_OK;
    }

private:
    LONG _ref = 1;
};

// ⚠ 用 SDK 自己的声明形状（STDAPI = EXTERN_C HRESULT STDAPICALLTYPE）：
//   objbase.h 已经声明过 DllGetClassObject，写成 `extern "C" __declspec(dllexport)` 会
//   报 C2375「redefinition; different linkage」（CI 实测）。导出交给 .def 文件。
STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (!IsEqualCLSID(rclsid, CLSID_FamilyAgentUnlock)) return CLASS_E_CLASSNOTAVAILABLE;
    auto* f = new (std::nothrow) CClassFactory();
    if (!f) return E_OUTOFMEMORY;
    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

STDAPI DllCanUnloadNow(void)
{
    return (InterlockedCompareExchange(&g_objects, 0, 0) == 0) ? S_OK : S_FALSE;
}

BOOL APIENTRY DllMain(HINSTANCE inst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = inst;
        DisableThreadLibraryCalls(inst);
    }
    return TRUE;
}

// ───────────────────── 自检出口（给 --cp-selftest 用，不碰登录界面）─────────────────────

// 把"读 arm → 读凭据 → 打包"整条链走一遍并把结果写成字符串。
// 安装后先用它验证逻辑，再去锁屏试 —— 免得拿登录界面当试验场。
static HRESULT DoSelfTest(PWSTR out, DWORD cchOut)
{
    ArmData arm;
    const bool armed = ReadArm(arm) && arm.valid;

    std::wstring user, secret;
    const bool haveCred = LoadCredential(user, secret);

    ULONG pkg = 0;
    const bool havePkg = SUCCEEDED(LookupAuthPackage(&pkg));

    std::vector<BYTE> blob;
    HRESULT packHr = E_FAIL;
    if (haveCred && havePkg)
    {
        std::wstring dom, acc;
        SplitUser(armed && !arm.user.empty() ? arm.user : user, dom, acc);
        packHr = PackKerbLogon(dom, acc, secret, CPUS_UNLOCK_WORKSTATION, blob);
    }
    SecureZeroMemory(secret.empty() ? nullptr : &secret[0], secret.size() * sizeof(wchar_t));

    StringCchPrintfW(out, cchOut,
        L"arm=%s cred=%s(账户=%s) authPkg=%s(%lu) pack=%s(%zu 字节)\r\n"
        L"（这一步不碰登录界面；arm=False 只说明现在没有待解锁请求，属正常）",
        armed ? L"True" : L"False",
        haveCred ? L"True" : L"False",
        user.empty() ? L"-" : user.c_str(),
        havePkg ? L"True" : L"False", pkg,
        SUCCEEDED(packHr) && !blob.empty() ? L"True" : L"False", blob.size());

    SecureZeroMemory(blob.empty() ? nullptr : blob.data(), blob.size());
    return S_OK;
}

// 这一层**刻意不含任何需要析构的对象**（C2712：__try 不能和 C++ 对象同帧）
static void SelfTestThunk(PWSTR out, DWORD cchOut)
{
    __try { DoSelfTest(out, cchOut); }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        StringCchCopyW(out, cchOut, L"自检异常（已捕获，不影响登录界面）");
    }
}

extern "C" __declspec(dllexport) HRESULT STDAPICALLTYPE FamilyAgentCpSelfTest(
    PWSTR out, DWORD cchOut)
{
    if (!out || cchOut < 128) return E_INVALIDARG;
    out[0] = L'\0';
    SelfTestThunk(out, cchOut);
    return out[0] ? S_OK : E_FAIL;
}
