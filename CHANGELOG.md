# 變更紀錄

格式依開發階段。版本 0.1.0 尚未正式發行。

## 0.1.0 — 開發中

### 重構 — 信令層解耦
- 新增 `Server.Application`(不依賴 SignalR):`DeviceRegistrationService`、`SessionNegotiationService`、`SessionLifecycleService`、`TrustedDeviceService`、`SessionSupport`、`SessionRecoveryService`
- `SignalingHub` 由 829 行、14 個建構式依賴,縮成只轉發的薄層(4 個依賴);規則、限流、稽核、統一錯誤訊息全部在業務層
- 新增抽象 `IClientNotifier`(推送)、`ICallerContext`(呼叫端連線)、`SignalingException`(由 `SignalingExceptionFilter` 轉成 `HubException`,訊息不變);`IConnectionTracker` 移到 `Server.Core`,`InMemoryConnectionTracker` 移到 `Server.Infrastructure`
- `SessionRecoveryService` 不再直接依賴 `IHubContext`
- 行為不變:既有測試全數通過;業務層現在可不啟動伺服器直接測試(`Server.Application.Tests`)
- 新增 hub 方法 `GetPeerPublicKey`(僅已核准 Session 的參與者可取得對方公鑰,供 Agent 導出端到端金鑰),附 3 個測試

### Phase 14 — 發行準備
- 伺服器設定:限制值可由 `appsettings`/環境變數設定並在啟動時驗證;隨附設定檔與程式預設一致(有測試)
- 反向代理支援:`ForwardedHeaders`(預設關閉,啟用必須列出已知代理)
- 非開發環境對所有端點(`/health` 除外)強制 TLS(原本只有 Relay 檢查,信令 hub 在純 HTTP 下仍可使用)
- 服務化:Windows Service / systemd 生命週期整合
- 打包與部署:`scripts/publish.ps1`/`.sh`、Windows 服務與 Linux systemd 安裝腳本、Dockerfile 與 compose、nginx 範例
- 金鑰紀元:每次 Session 恢復導出新的工作階段金鑰,防止重連後重放舊流量
- 驗收測試:真實伺服器 + Relay + 端到端加密下的完整情境(畫面、輸入、剪貼簿、檔案、心跳、OTP、信任設備、權限、恢復、稽核)
- 修正:`TransportManager` 不再在沒有真實直連傳輸時回傳假的 Loopback 傳輸(一律走 Relay);直連建立失敗自動改走 Relay
- 修正:`PermissionGranted` 稽核事件補上目標設備與授予的權限
- 修正:`RelayTransport` 在對端已釋放 WebSocket 時關閉不再拋出例外
- 修正:Unix 上私鑰檔建立時就是僅限本人讀寫(原本先寫入再改權限)
- 文件:README、ARCHITECTURE、SECURITY、PROTOCOL、NETWORKING、DEPLOYMENT、CONFIGURATION、TESTING、TROUBLESHOOTING、RELEASE_READINESS

### Phase 13 — 效能
- 效能測試專案(延遲、FPS、CPU、記憶體、頻寬、大檔案、多 Session、伺服器負載)與 `docs/PERFORMANCE.md`
- 差分編碼器 `TileDeltaVideoEncoder/Decoder`(桌面閒置頻寬 −99.6%、一般操作 −95%)
- 減少 GC 壓力(緩衝區租用、去除多餘拷貝):720p 每幀配置 18.6 → 7.0 MB,gen-2 GC 7 → 0
- 資源上限補強:重組器單幀/總量上限、伺服器全域 Session 上限、Relay 配對數上限、Relay 位址速率可設定
- Session 到期由背景清掃強制;已結束 Session 從記憶體清除

### Phase 12 — 安全強化
- 設備認證:註冊時以私鑰簽章挑戰,首次註冊釘住公鑰
- 修正嚴重漏洞:可繞過目標核准直接進入 `Connected`;已關閉 Session 可被復活;任何設備可關閉別人的 Session;Relay 可被第三方搭上;Relay 實際轉送明文
- 端到端加密通道 `SecureChannel`(序號、AAD、重放視窗)
- Token 短效、單次使用;每方各自的 token;Relay 依角色配對並強制 Session 狀態
- 速率限制與列舉防護、統一錯誤訊息、輸入驗證、記憶體上限
- `docs/THREAT_MODEL.md`;63 個攻擊情境測試

### Phase 11 — 可靠性
- 連線狀態機、心跳(KeepAlive)、重連退避、網路變更偵測、`ConnectionSupervisor`、優雅斷線
- 伺服器端 Session 暫停/恢復(單次使用的恢復 token)與逾時清掃
- 信令客戶端自動重連後重新註冊並恢復 Session

### Phase 10 — 檔案傳輸
- 分塊串流、可續傳、SHA-256 校驗、取消、進度、沙盒根目錄與檔名驗證、權限與資源上限

### Phase 9 — 剪貼簿
- 文字與圖片;主機本地策略 ∩ Session 權限;預設關閉;大小與速率限制

### Phase 8 — 遠端輸入
- 滑鼠、鍵盤、滾輪;線路格式、驗證、權限強制、重放與速率限制

### Phase 7 — 遠端畫面
- 擷取介面、參考編碼器、封包化與重組、串流收發、適應性畫質控制器

### Phase 6 — Relay
- Relay 伺服器與客戶端傳輸;配對、轉送、頻寬與大小限制

### Phase 1–5 — 基礎
- 專案骨架、設備身分、信令、認證與授權(OTP、使用者核准、信任設備、權限)、NAT 候選收集與直連/Relay 選擇邏輯
