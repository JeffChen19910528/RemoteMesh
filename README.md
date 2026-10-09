# RemoteMesh

跨平台遠端桌面與遠端控制系統(類似 AnyDesk / TeamViewer),以 .NET 10 建構。設計目標:不需要固定 IP、不需要開放連接埠,以 Device ID 連線,先嘗試直連、失敗自動改走 Relay,全程端到端加密並以最小權限控制。

```
Device ID → Signaling → Authentication → NAT Traversal → Direct → Relay Fallback → Secure Remote Session
```

## 目前狀態(請先讀這一節)

**RemoteMesh 已經有可以執行的桌面程式(Windows 的 `RemoteMesh.exe`,以及 Linux、macOS 的版本,見 [docs/PLATFORMS.md](docs/PLATFORMS.md);macOS 版沒有在真實 Mac 上跑過,Linux 純 Wayland 不能被控):開啟後有自己的 ID 與一次性密碼,輸入對方的 ID 就能遠端檢視並控制對方的電腦。但它仍是早期版本,還不能當成正式產品交給一般使用者**(沒有動態補償的影片編碼——整屏連續變動時畫面會變軟、只有約 5 fps;沒有 P2P 直連、沒有簽章與安裝程式,見下表與 [docs/RELEASE_READINESS.md](docs/RELEASE_READINESS.md))。
完整的出貨檢查表與阻擋項目見 [docs/RELEASE_READINESS.md](docs/RELEASE_READINESS.md)。

| 已完成並有測試 | 尚未完成 |
|---|---|
| 設備身分與金鑰;註冊時以私鑰簽章證明身分 | 短 ID(9 位數字)由伺服器配發;設定 PostgreSQL 後號碼歸屬**持久化**,重啟後別人搶不走(沒設定資料庫則仍在記憶體,見 THREAT_MODEL §4) |
| 信令伺服器:連線請求、使用者核准、OTP、信任設備、權限 | 重啟伺服器時所有連線中斷(狀態在記憶體);客戶端會乾淨地結束連線並自動重新註冊,但**進行中的連線無法保留** |
| Relay 伺服器與客戶端傳輸(只轉送密文) | Windows、Linux(X11)、macOS 都有螢幕擷取、輸入注入、文字剪貼簿後端(見 [docs/PLATFORMS.md](docs/PLATFORMS.md));**Linux 純 Wayland 工作階段不能被擷取;macOS 後端沒有在真實 Mac 上跑過** |
| 端到端加密通道(重放防護、金鑰紀元) | **沒有動態補償的影片編碼器**(H.264/VP9/AV1);現有 JPEG 分塊 + 碼率控制:靜止與一般操作很省,整屏連續變動時約 5 fps |
| 畫面串流管線(擷取→編碼→封包→加密→解碼→繪製) | **P2P 直連未實作**:目前一律走 Relay |
| 滑鼠、鍵盤、剪貼簿、檔案傳輸與權限強制;PostgreSQL 持久化設備、短 ID、信任設備、稽核、Session 與恢復 token(選用);伺服器重啟後進行中的連線自動接回 | 限流與在線狀態仍在記憶體,所以還不能多實例 |
| `RemoteMeshClient`:同時是被控端與控制端的完整流程(已用真實進程驗證) | |
| 斷線自動重連:網路中斷、Wi-Fi 切換、Relay 連線被切斷後,同一個連線自動接回(新的金鑰紀元),畫面與操作恢復;心跳、網路變更偵測、Session 恢復 | 對方金鑰有本機釘選與安全碼核對(首次接觸仍是首次使用即信任),裝置可以用雙簽章換新金鑰;連線經過 TLS(臨時金鑰,錄下的流量事後解不開);沒有設備撤銷 |
| WPF 桌面程式:ID/密碼、連線、核准對話框(可選權限)、遠端畫面視窗(滑鼠鍵盤剪貼簿檔案)、被控制名單與一鍵中斷 | 沒有簽章、沒有安裝程式;Windows 用 WPF,Linux/macOS 用 Avalonia 版本(同樣的功能與畫面) |
| 威脅模型與攻擊測試、效能量測、打包與部署腳本 | |

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
```

## 怎麼用桌面程式

1. 先有一台伺服器(見 [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md);開發時 `dotnet run --project src/server/RemoteDesktop.Server.Api` 即可)。
2. 兩台電腦各執行 `RemoteMesh.exe`。第一次會要你填**伺服器網址**(非本機必須是 https)。
3. 想被控制的電腦把畫面上的 **ID**(9 位數字,例如 `482 913 057`)告訴對方。對方在「連線到另一台電腦」輸入後按「連線」(空白或連字號都可以):
   - 被控制的電腦會跳出**核准對話框**,顯示是誰要連,並讓你勾選要授予的權限(預設只有「查看畫面」);60 秒沒回應自動拒絕。
   - 或者:被控制的人把畫面上的**一次性密碼**告訴對方,對方一起輸入就不需要核准(只能查看畫面,密碼用過即失效)。
4. 連線後會開出遠端畫面視窗。被控制的電腦在主視窗的「正在控制這台電腦的對象」可以隨時按「中斷」。

`REMOTEMESH_DATA` 環境變數可指定設定與身分的存放資料夾(可攜式使用;不同資料夾可同時開多個執行個體)。

## 專案結構

```
src/
├── RemoteDesktop.Core           共用模型、狀態機、Session/權限
├── RemoteDesktop.Protocol       線路格式與 DTO
├── RemoteDesktop.Security       金鑰、簽章、AES-GCM 工作階段加密
├── RemoteDesktop.Transport      傳輸抽象;Secure/(端到端加密)Reliability/(心跳、重連)
├── RemoteDesktop.Signaling      信令客戶端(SignalR)
├── RemoteDesktop.Nat            ICE/STUN 候選收集、直連/Relay 選擇
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
├── RemoteDesktop.Platform.MacOS    macOS 後端:CoreGraphics、CGEvent、pbcopy/pbpaste
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
tests/                           12 個測試專案(見 docs/TESTING.md)
docs/                            文件
deploy/  scripts/                部署檔與打包腳本
```

## 文件

| 文件 | 內容 |
|---|---|
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | 架構、元件、Session 生命週期、狀態機、擴充點 |
| [SECURITY.md](docs/SECURITY.md) | 安全模型、防護總覽、安全部署檢查表 |
| [THREAT_MODEL.md](docs/THREAT_MODEL.md) | 威脅分析、審查發現、殘留風險 |
| [PROTOCOL.md](docs/PROTOCOL.md) | 信令、Relay、加密通道、各通道的線路格式 |
| [NETWORKING.md](docs/NETWORKING.md) | 連線模型、NAT/ICE 現況、Relay、連接埠與逾時 |
| [DEPLOYMENT.md](docs/DEPLOYMENT.md) | 打包、Windows/Linux/Docker 部署、TLS 與反向代理 |
| [CONFIGURATION.md](docs/CONFIGURATION.md) | 所有設定項目、預設值、驗證規則 |
| [PERFORMANCE.md](docs/PERFORMANCE.md) | 效能量測、瓶頸與最佳化 |
| [TESTING.md](docs/TESTING.md) | 測試策略、如何執行、各專案涵蓋範圍 |
| [PLATFORMS.md](docs/PLATFORMS.md) | Windows / Linux / macOS 支援、需求、限制、如何驗證與打包 |
| [TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | 症狀與處置 |
| [RELEASE_READINESS.md](docs/RELEASE_READINESS.md) | 出貨檢查表與阻擋項目 |
| [CHANGELOG.md](CHANGELOG.md) | 各階段變更紀錄 |

