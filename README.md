# RemoteMesh

跨平台遠端桌面與遠端控制系統(類似 AnyDesk / TeamViewer),以 .NET 10 建構。設計目標:不需要固定 IP、不需要開放連接埠,以 Device ID 連線,先嘗試直連、失敗自動改走 Relay,全程端到端加密並以最小權限控制。

```
Device ID → Signaling → Authentication → NAT Traversal → Direct → Relay Fallback → Secure Remote Session
```

## 功能

RemoteMesh 已有可以執行的桌面程式(Windows 的 `RemoteMesh.exe`,以及 Linux、macOS 的版本,見 [docs/PLATFORMS.md](docs/PLATFORMS.md)):開啟後有自己的 ID 與一次性密碼,輸入對方的 ID 就能遠端檢視並控制對方的電腦。

- **連線**:9 位數字短 ID(伺服器配發;設定 PostgreSQL 後持久化,重啟不會被搶走)或一次性密碼;被控端核准對話框可逐項選擇權限(預設只有「查看畫面」);信任設備;被控端可封鎖裝置
- **傳輸**:同網段或可直接到達的位址先走 **P2P 直連**,連不上自動改走 Relay;兩條路徑都包在 TLS(臨時金鑰,前向保密)與端到端 AES-GCM 之內,Relay 只看得到密文
- **安全**:設備以私鑰簽章證明身分;對方金鑰本機釘選、可用安全碼核對、裝置可用雙簽章換新金鑰;逐訊息的權限強制;限流與稽核;macOS 私鑰以 Keychain 保護
- **遠端畫面**:擷取→編碼→加密→解碼→繪製的串流管線(JPEG 分塊 + 碼率控制,靜止與一般操作很省頻寬);滑鼠、鍵盤、文字與圖片剪貼簿、檔案傳輸(可續傳、校驗)
- **穩定**:網路中斷、Wi-Fi 切換、連線被切斷後同一個連線自動接回(新的金鑰紀元);伺服器重啟(設定 PostgreSQL 時)進行中的連線自動接回
- **平台**:Windows(WPF)、Linux X11 與 macOS(Avalonia),相同功能;無介面的 Agent 供無人值守使用
- **打包**:伺服器套件、Docker/systemd/Windows 服務部署腳本、Windows MSI 安裝程式與程式碼簽章腳本(見下方「快速開始」)
- **品質**:1100 多個自動化測試(含真實伺服器 + 真實客戶端的端到端、攻擊情境與效能測試)、威脅模型

## 適用範圍與已知限制

這是早期版本(0.1.0),下列是目前的設計邊界,不是隱藏的缺陷:

- **畫面**:使用 JPEG 分塊編碼,沒有 H.264/VP9/AV1 這類動態補償的影片編碼;整屏連續變動(全螢幕影片)時畫面會變軟、約 5 fps,一般操作則很流暢。
- **直連範圍**:兩台電腦在同一個區網、其中一台有公開位址、或被控端轉發了 `DirectPort` 時走直連;兩邊都在不同 NAT 後面時沒有打洞或 TURN,自動使用 Relay(功能完整,只是流量經過你的伺服器)。
- **平台**:Linux 純 Wayland 工作階段不能被擷取或控制(需 X11 / XWayland);macOS 版沒有在真實 Mac 上完整驗證;只擷取主螢幕。
- **單一伺服器實例**:限流與在線狀態在記憶體,伺服器不能水平擴展(資料庫是持久副本,不是多實例的共同仲裁者)。
- **簽章**:發行物是否簽章取決於你提供的程式碼簽署憑證;沒有憑證時產生未簽章的套件,Windows SmartScreen 會警告。
- **安全審查**:只做過內部審查與攻擊測試,沒有第三方稽核。

出貨檢查表與逐項證據見 [docs/RELEASE_READINESS.md](docs/RELEASE_READINESS.md)。

## 快速開始

需求:.NET SDK 10。

```bash
dotnet build RemoteDesktop.slnx
dotnet test RemoteDesktop.slnx                       # 約 70 秒,含效能與驗收測試
dotnet run --project src/server/RemoteDesktop.Server.Api   # 開發模式:http://localhost:5222
curl http://localhost:5222/health
```

開發模式允許純 HTTP;其他環境強制 TLS(見 [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md))。

打包與部署:

```powershell
./scripts/publish.ps1                         # 伺服器 + Agent:win-x64 / linux-x64 套件到 ./artifacts(含 SHA256SUMS.txt)
./scripts/publish-client.ps1                  # 桌面程式:單一 RemoteMesh.exe(約 1.5 MB,需安裝 .NET 10 Desktop Runtime)
./scripts/publish-client.ps1 -SelfContained   # 同上但內含執行階段(約 135 MB,任何 Windows 10/11 x64 都能直接執行)
./scripts/build-installer.ps1 -SelfContained  # 使用者層級的 MSI 安裝程式(不需系統管理員)
./scripts/build-installer.ps1 -SelfContained -Thumbprint <憑證指紋>   # 同上並用你的程式碼簽署憑證簽章
```

## 怎麼用桌面程式

### 啟動與連線

1. 先有一台伺服器(見 [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md);開發時 `dotnet run --project src/server/RemoteDesktop.Server.Api` 即可)。
2. 兩台電腦各執行 `RemoteMesh.exe`(Windows)或 `RemoteMesh`(Linux/macOS)。第一次會要你填**伺服器網址**(非本機必須是 https)。
3. 想被控制的電腦把畫面上的 **ID**(9 位數字,例如 `482 913 057`)告訴對方。對方在「連線到另一台電腦」輸入後按「連線」(空白或連字號都可以):
   - 被控制的電腦會跳出**核准對話框**,顯示是誰要連,並讓你勾選要授予的權限(預設只有「查看畫面」);60 秒沒回應自動拒絕。
   - 或者:被控制的人把畫面上的**一次性密碼**告訴對方,對方一起輸入就不需要核准(只能查看畫面,密碼用過即失效)。
4. 連線後會開出遠端畫面視窗。被控制的電腦在主視窗的「正在控制這台電腦的對象」可以隨時按「中斷」。

### 遠端畫面視窗操作

連線成功後視窗功能:

| 操作 | 說明 |
|---|---|
| 滑鼠移動/點擊 | 直接在視窗內操作,會注入到遠端電腦 |
| 鍵盤輸入 | 視窗取得焦點後所有按鍵都傳到遠端 |
| 雙擊 | 正確傳送 double-click 事件(開啟檔案、選取文字均正常) |
| 滾輪 | 支援垂直捲動 |
| 剪貼簿文字 | 兩端複製/貼上互通(需要在核准時勾選「剪貼簿」權限,並在設定中開啟剪貼簿共用) |
| 剪貼簿圖片 | 支援在兩端之間複製貼上截圖、圖片(PNG 格式,最大 10 MB) |
| 檔案傳輸 | 可將檔案拖曳到視窗或使用選單傳送到遠端(需要「檔案傳輸」權限) |

### 剪貼簿共用設定

剪貼簿預設是**關閉**的(ClipboardPolicy.Disabled)。若需要開啟:

1. 核准連線時在對話框中勾選「剪貼簿」(ClipboardRead / ClipboardWrite)。
2. 在應用程式設定中將剪貼簿策略改為 `ReadOnly`、`WriteOnly` 或 `Bidirectional`。

各平台的剪貼簿圖片實作:
- **Windows**: 使用 `RegisterClipboardFormatW("PNG")`,與 Chrome、Firefox、Office 等主流應用互通。
- **macOS**: 使用 NSPasteboard (`public.png` UTI),與 Finder、Preview、Safari 等所有 macOS 應用互通。
- **Linux (X11)**: 使用 `xclip -t image/png`(需安裝 `xclip`)。
- **Linux (Wayland)**: 使用 `wl-paste/wl-copy --type image/png`(需安裝 `wl-clipboard`)。

### macOS 特別注意事項

macOS 版本需要兩個系統權限才能正常運作:

1. **螢幕錄製** — 沒有這個權限,擷取到的畫面只有桌布,看不到任何視窗。
   - 系統設定 → 隱私權與安全性 → 螢幕錄製 → 開啟 RemoteMesh
2. **輔助使用 (Accessibility)** — 沒有這個權限,滑鼠與鍵盤事件注入會被系統拒絕。
   - 系統設定 → 隱私權與安全性 → 輔助使用 → 開啟 RemoteMesh

應用程式啟動時如果偵測到缺少權限,會提示你並可直接點擊按鈕跳到對應的設定頁面。

### 被控制名單與封鎖

主視窗「正在控制這台電腦的對象」區塊:
- 顯示所有目前連線中的控制端裝置名稱與 ID。
- 「中斷」按鈕立刻結束該裝置的工作階段。
- 「封鎖」按鈕永久拒絕該裝置的未來連線請求(本機封鎖清單,不需要伺服器介入)。

`REMOTEMESH_DATA` 環境變數可指定設定與身分的存放資料夾(可攜式使用;不同資料夾可同時開多個執行個體)。

## 專案結構

```
src/
├── RemoteDesktop.Core           共用模型、狀態機、Session/權限
├── RemoteDesktop.Protocol       線路格式與 DTO
├── RemoteDesktop.Security       金鑰、簽章、AES-GCM 工作階段加密
├── RemoteDesktop.Transport      傳輸抽象;Secure/(端到端加密)Reliability/(心跳、重連)
├── RemoteDesktop.Signaling      信令客戶端(SignalR)
├── RemoteDesktop.Nat            STUN 候選收集、傳輸模式選擇
├── RemoteDesktop.Relay          Relay 客戶端傳輸
├── RemoteDesktop.Capture        螢幕擷取介面與測試來源
├── RemoteDesktop.Encoding       編碼器/解碼器(Deflate 與差分參考編碼)、適應性畫質
├── RemoteDesktop.Streaming      畫面串流收發
├── RemoteDesktop.Input          輸入事件、驗證與權限閘門
├── RemoteDesktop.Clipboard      剪貼簿同步(策略 + 權限)
├── RemoteDesktop.FileTransfer   分塊、可續傳、校驗的檔案傳輸
├── RemoteDesktop.Platform          選擇本機後端(Agent 與桌面程式共用)
├── RemoteDesktop.Platform.Windows  Windows 後端:螢幕擷取、輸入注入、剪貼簿
├── RemoteDesktop.Platform.Linux    Linux 後端:X11 擷取、XTEST、剪貼簿工具
├── RemoteDesktop.Platform.MacOS    macOS 後端:CoreGraphics、CGEvent、NSPasteboard(圖片)、pbcopy/pbpaste
├── RemoteDesktop.Platform.Skia     Linux/macOS 的 JPEG 編解碼
├── RemoteDesktop.UI.Core           介面邏輯(與視窗工具箱無關)
├── RemoteDesktop.UI.Avalonia       跨平台桌面程式(Windows、Linux、macOS)
├── RemoteDesktop.Client         客戶端核心(無介面):註冊、核准、Relay、加密、五個通道,被控端與控制端
├── RemoteDesktop.Agent          無介面被控端:設定檔白名單核准(須在使用者工作階段執行)
└── server/
    ├── RemoteDesktop.Server.Api          進入點:SignalR hub、Relay 端點、設定
    ├── RemoteDesktop.Server.Core         伺服器介面與選項
    ├── RemoteDesktop.Server.Application  信令業務規則(不依賴 SignalR):註冊、協商、生命週期、恢復
    ├── RemoteDesktop.Server.Signaling    SignalR 轉接層:薄 hub、推送通知器、例外轉換
    ├── RemoteDesktop.Server.Identity     設備登錄、信任設備(記憶體)
    ├── RemoteDesktop.Server.Session      Session 管理(記憶體)
    ├── RemoteDesktop.Server.Presence     在線狀態(記憶體)
    ├── RemoteDesktop.Server.Relay        Relay 服務
    ├── RemoteDesktop.Server.Security     設備認證、OTP、Token
    ├── RemoteDesktop.Server.Infrastructure  限流、稽核(記憶體)
    └── RemoteDesktop.Server.Persistence  PostgreSQL:設備、短 ID、信任設備、稽核、Session、token(選用)
tests/                           20 個測試專案(見 docs/TESTING.md)
docs/                            文件
deploy/  scripts/                部署檔與打包腳本
```

