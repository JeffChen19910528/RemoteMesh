# RemoteMesh

跨平台遠端桌面與遠端控制系統(類似 AnyDesk / TeamViewer),以 .NET 10 建構。設計目標:不需要固定 IP、不需要開放連接埠,以 Device ID 連線,先嘗試直連、失敗自動改走 Relay,全程端到端加密並以最小權限控制。

```
Device ID → Signaling → Authentication → NAT Traversal → Direct → Relay Fallback → Secure Remote Session
```

## 目前狀態(請先讀這一節)

**RemoteMesh 目前是一套經過安全審查、測試完整的「通訊、安全與工作階段基礎層」,還不是可以交給使用者的遠端桌面產品。**
完整的出貨檢查表與阻擋項目見 [docs/RELEASE_READINESS.md](docs/RELEASE_READINESS.md)。

| 已完成並有測試 | 尚未完成 |
|---|---|
| 設備身分與金鑰;註冊時以私鑰簽章證明身分 | **沒有使用者介面**(`RemoteDesktop.UI` 專案不存在) |
| 信令伺服器:連線請求、使用者核准、OTP、信任設備、權限 | **Agent 只是空殼**,沒有把各元件組成可執行的應用程式 |
| Relay 伺服器與客戶端傳輸(只轉送密文) | **沒有真實的 OS 後端**:螢幕擷取、輸入注入、剪貼簿(只有介面與測試用實作) |
| 端到端加密通道(重放防護、金鑰紀元) | **沒有真實影片編碼器**(H.264/VP9/AV1);現有的是無損參考編碼器 |
| 畫面串流管線(擷取→編碼→封包→加密→解碼→繪製) | **P2P 直連未實作**:目前一律走 Relay |
| 滑鼠、鍵盤、剪貼簿、檔案傳輸與權限強制 | 沒有資料庫持久化(狀態在記憶體,重啟即清空) |
| 重連、心跳、網路變更偵測、Session 恢復 | 沒有帶外的金鑰指紋驗證、前向保密、設備撤銷 |
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
./scripts/publish.ps1            # 測試後產生 win-x64 / linux-x64 套件到 ./artifacts(含 SHA256SUMS.txt)
```

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
├── RemoteDesktop.Agent          背景程式(目前是空殼)
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
    └── RemoteDesktop.Server.Persistence  (空,PostgreSQL 尚未實作)
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
| [TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | 症狀與處置 |
| [RELEASE_READINESS.md](docs/RELEASE_READINESS.md) | 出貨檢查表與阻擋項目 |
| [CHANGELOG.md](CHANGELOG.md) | 各階段變更紀錄 |

