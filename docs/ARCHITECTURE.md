# 架構

## 1. 分層與信任邊界

```
 檢視端(請求方)                                              被控端(目標)
┌──────────────────────┐                                  ┌──────────────────────┐
│ 應用層               │                                  │ 應用層               │
│ 串流收/輸入送/剪貼簿 │                                  │ 串流送/輸入閘門/     │
│ /檔案客戶端/心跳     │                                  │ 剪貼簿主機/檔案主機  │
├──────────────────────┤                                  ├──────────────────────┤
│ SecureTransport      │◀═══ 端到端 AES-256-GCM(序號+AAD)═══▶│ SecureTransport      │
├──────────────────────┤                                  ├──────────────────────┤
│ RelayTransport       │      ┌───────────────────┐       │ RelayTransport       │
│ (WebSocket 多工)     │◀────▶│ Relay(只轉送密文) │◀─────▶│ (WebSocket 多工)     │
└──────────┬───────────┘      └───────────────────┘       └───────────┬──────────┘
           │              ┌─────────────────────────┐                 │
           └─────────────▶│ 信令伺服器(SignalR hub) │◀────────────────┘
                          │ 身分、授權、Session、   │
                          │ 權限、稽核、OTP、Token  │
                          └─────────────────────────┘
```

- **信任邊界**:裝置 ↔ 伺服器(信令 + Relay)↔ 裝置。伺服器視為「誠實但可能被入侵」,網路視為完全不可信。
- **內容保護**:螢幕、輸入、剪貼簿、檔案、控制訊息全部經 `SecureChannel` 加密後才進入傳輸層,Relay 與路徑上任何第三方只看到密文。
- **授權在伺服器、強制在被控端**:伺服器決定「誰可以與誰建立 Session、授予哪些權限」,被控端對每個輸入/剪貼簿/檔案請求再檢查一次(`InputGate`、`ClipboardHost`、`FileTransferHost`),權限撤銷即時生效。

## 2. 五個通道

一個 Session 同時承載五個獨立通道(`ChannelType`),在同一條 Relay WebSocket 上以第一個位元組多工:

| 通道 | 用途 | 方向 |
|---|---|---|
| Control | 心跳(KeepAlive)與控制訊息 | 雙向 |
| Desktop | 影像封包 | 被控端 → 檢視端 |
| Input | 滑鼠鍵盤事件 | 檢視端 → 被控端 |
| Clipboard | 剪貼簿請求與回應 | 雙向(請求式) |
| File | 檔案傳輸 | 雙向 |

每個通道各自有序號與重放視窗(`SecureChannel`),通道間不共用狀態。

## 3. 專案與職責

客戶端函式庫(`src/`):

| 專案 | 職責 |
|---|---|
| Core | `DeviceId`、`SessionId`、`SessionInfo`、`PermissionSet`、`ConnectionStateMachine`、稽核事件 |
| Protocol | `FramePacket`/`FramePacketizer`/`FrameReassembler`、`RegistrationProof`、DTO、`ChannelType` |
| Security | ECDSA P-256 金鑰與簽章、`IDeviceSigner`、`AesGcmSessionCrypto`(ECDH + HKDF + AES-GCM)、`OsKeyStore` |
| Transport | `ITransport`/`IChannel`、`SecureChannel`/`SecureTransport`/`ReplayWindow`、`KeepAliveService`、`ConnectionSupervisor`、`ReconnectPolicy`、`NetworkChangeDetector`、`GracefulDisconnect` |
| Signaling | SignalR 客戶端:註冊(挑戰簽章)、請求/回應、恢復、自動重連後重新註冊 |
| Nat | ICE 候選收集(SIPSorcery)、`TransportManager`(直連優先、Relay 備援) |
| Relay | `RelayClient`、`RelayTransport`(分片重組、大小上限、背壓) |
| Capture / Encoding / Streaming | 擷取介面、編碼器(`DeflateVideoEncoder`、`TileDeltaVideoEncoder`)、適應性畫質、串流收發 |
| Input / Clipboard / FileTransfer | 各自的線路格式、驗證、權限強制 |

伺服器(`src/server/`):`Api`(進入點、設定、Relay 端點)、`Signaling`(hub、`SessionRecoveryService`)、`Session`、`Identity`、`Presence`、`Relay`、`Security`(`DeviceAuthenticator`、`OtpService`、`SessionTokenService`)、`Infrastructure`(`InMemoryRateLimiter`、`InMemoryAuditLog`)、`Persistence`(空)。

## 4. Session 生命週期

```
請求方                    信令伺服器                          目標
  │  BeginRegistration ─────▶│                                  │
  │◀──── 隨機挑戰            │      (目標端同樣先完成註冊)     │
  │  RegisterDevice(簽章) ──▶│ 驗證簽章、釘住公鑰               │
  │                          │                                  │
  │  RequestSession ────────▶│ 限流/列舉防護/上限檢查           │
  │                          │── IncomingSessionRequest ───────▶│ 使用者看到「誰要連線」
  │                          │◀─ RespondToSession(核准, 權限) ──│
  │◀── AuthSuccess(中繼 token)│── SessionAuthorized(各自的 token)▶│
  │                          │                                  │
  │ 兩端以各自 token 連 /ws/relay ─▶ Relay 依角色配對 ◀──────────│
  │ 兩端以裝置金鑰做 ECDH,導出 Session 金鑰(紀元 0)           │
  │  ReportConnectionMode ──▶│ EstablishingTransport → Connected│
  │◀── RecoveryTokenIssued ──│── RecoveryTokenIssued ──────────▶│
  │ ══════════ 五個加密通道上的畫面/輸入/剪貼簿/檔案 ═══════════│
  │  CloseSession ──────────▶│ 撤銷 token、關閉 Relay、稽核     │
```

其他入口:**OTP**(`GenerateOtp` + `RequestSessionWithOtp`,只授予檢視)、**信任設備**(目標預先授權,自動核准,權限不得含重啟/關機)。

### 伺服器端 Session 狀態

```
Created → Negotiating ──(目標接受)──▶ Authorizing → EstablishingTransport → Connected ⇄ Paused
              │(拒絕/逾時)                                                    │    │(逾時)
              ▼                                                                │    ▼
            Failed                                      Disconnecting/Disconnected   Expired
```

- 所有狀態轉換使用 compare-and-set(`ISessionManager.TryTransitionAsync`),只有一個請求能成功;已結束的 Session 不可復活;沒有經過目標核准不可能進入 `Connected`。
- `Paused`:對端斷線時暫停,恢復時限內(預設 60 秒)可用單次使用的恢復 token 回復並進入新的金鑰紀元,逾時則 `Expired`。
- 背景清掃(`SessionRecoveryReaper`)結束:逾時未核准的請求、超過恢復時限的暫停 Session、超過 `ExpiresAt` 的任何 Session(撤銷 token、關閉 Relay、通知雙方)、保留期滿的已結束 Session(釋放記憶體)。

### 客戶端連線狀態機(`ConnectionStateMachine`)

```
Disconnected → Connecting → Negotiating → Authenticating → Authorizing → EstablishingTransport → Connected
                                                                                  ▲                 │
                                                                    Reconnecting ◀────────────────┘
任何狀態 ──▶ Disconnecting ──▶ Disconnected
```

只有表中列出的轉換合法(測試逐一核對全部組合);沒有散落的 `isConnected` 之類布林值。`ConnectionSupervisor` 在連線中斷(心跳逾時或本機網路變更)時驅動 `Connected → Reconnecting → EstablishingTransport → Connected`,以退避重試,用盡次數或超過恢復時限則優雅斷線。

## 5. 金鑰與加密

1. 每台設備有一對 ECDSA P-256 金鑰(`OsKeyStore`;Windows 用 DPAPI 保護當前使用者,Unix 用 0600 檔案權限)。
2. 註冊時簽章證明持有私鑰;首次註冊釘住公鑰。
3. 工作階段金鑰:雙方以各自私鑰與對方公鑰做 ECDH,再以 HKDF-SHA256 導出 256 位元金鑰,info = SessionId ‖ 紀元。每次 Session 恢復紀元加一,舊紀元的封包在新金鑰下驗證必然失敗。
4. 每個訊息:`[序號 8][nonce 12][tag 16][密文]`,AAD = 通道類型 ‖ 方向 ‖ 序號;接收端以 64 項滑動視窗拒絕重放,驗證通過後才消耗序號。
5. 全部使用 .NET BCL 的成熟元件,沒有自創演算法、金鑰交換或 MAC。

**已知限制**:金鑰是靜態 ECDH(沒有前向保密);對方公鑰由伺服器提供,客戶端會釘選見過的金鑰並提供安全碼核對,但第一次接觸仍是首次使用即信任。見 [THREAT_MODEL.md](THREAT_MODEL.md) §4。

## 6. 擴充點(尚待實作的真實後端)

| 介面 | 現況 | 要接上的東西 |
|---|---|---|
| `IScreenCapture` | `SyntheticScreenCapture`(測試圖樣);Windows GDI、Linux X11、macOS CoreGraphics(見 PLATFORMS.md) | Windows Desktop Duplication / WGC;Linux PipeWire(Wayland);macOS ScreenCaptureKit |
| `IInputController` | 測試用記錄器;Windows `SendInput`、Linux XTEST、macOS CGEvent | Linux libei(Wayland) |
| `IClipboardManager` | `InMemoryClipboardManager` | 作業系統剪貼簿 |
| `IVideoEncoder`/`IVideoDecoder` | Deflate、TileDelta(無損參考) | H.264 / VP9 / AV1(原生函式庫或硬體) |
| `IDirectTransportFactory` | 無(模擬用 `LoopbackDirectTransportFactory` 僅供測試) | 以 ICE 選出的候選配對建立 P2P 傳輸 |
| `IFrameRenderer` | 測試用收集器 | UI 繪製(注意:畫面像素是租用的,只在呼叫期間有效) |
| `IDeviceRegistry`、`IShortIdDirectory`、`ITrustedDeviceStore`、`IAuditLog` | 記憶體實作;設定 `RemoteMesh:Database:ConnectionString` 後換成 PostgreSQL(`Server.Persistence`) | — |
| `ISessionManager`、`ISessionTokenService` | 記憶體實作;設定資料庫後換成寫入式包裝(記憶體仍是仲裁者,每次變更寫進 PostgreSQL,啟動時載回) | 多實例需要在資料庫裡仲裁的管理器 |
| `IPresenceService`、`IRateLimiter`、OTP 服務 | 記憶體實作 | 共享快取(多實例必須) |

## 7. 重要設計決定

- **加密放在傳輸層之上(`SecureTransport` 包裝任何 `ITransport`)**:直連與 Relay 用同一套保護,Relay 不需要被信任。
- **Relay 只轉送**:不解析、不記錄內容;授權依 Session 狀態、角色與單次使用 token,而不是到達順序。
- **授權決定用 compare-and-set**:取代「先讀後寫」,消除競態與狀態復活。
- **錯誤訊息不洩漏存在性**:未知/離線/自己/額滿、「不存在」與「不是你的」、各種 OTP 失敗,回覆相同訊息;細節只進稽核。
- **資源上限內建**:每個會接收不可信輸入的元件(重組器、Relay、速率限制器、稽核日誌、token 表)都有大小或數量上限。
- **單一行程、記憶體狀態**:目前伺服器只能單實例執行,重啟會中斷所有 Session。
