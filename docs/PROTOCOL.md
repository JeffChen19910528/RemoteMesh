# 協定

本文件描述線路上的實際格式,與程式碼一致。除特別註明,多位元組整數都是 **big-endian**。

> **版本**:`ProtocolVersion.Current = "1.0"` 目前只是常數,**沒有在連線上協商**。格式變更需要客戶端與伺服器同步升級;在加入協商機制之前,請把本文件視為 1.0 的唯一定義。

## 1. 信令(SignalR hub:`/hubs/signaling`)

傳輸:SignalR(WebSocket,可退回 long polling)。單一訊息上限 64 KB;每個客戶端同時只處理一個呼叫。錯誤以 `HubException` 傳回,訊息刻意籠統(見 THREAT_MODEL)。

### 1.1 呼叫(客戶端 → 伺服器)

| 方法 | 參數 | 回傳 | 說明 |
|---|---|---|---|
| `BeginRegistration` | — | `string` base64 挑戰(32 B) | 取得綁定本連線的隨機挑戰;單次使用、30 秒有效 |
| `RegisterDevice` | `DeviceInfoDto dto`, `string signatureBase64` | — | 以私鑰簽章證明身分(§1.3);首次註冊釘住公鑰 |
| `ClaimShortId` | `string? preferred` | `string`(9 位數字) | 取得本設備的短 ID。已有就回傳原本的;否則 `preferred`(上次的號碼)若沒人持有就沿用;否則隨機配發。必須已註冊;10 次/分鐘 |
| `RequestSession` | `string target` | `SessionDto` | `target` 可以是短 ID(9 位數字,可含空白或連字號)或完整 ID。請求連線;信任設備路徑的 `Auth` 已填入;傳給目標的 `SessionDto` 帶有 `RequesterShortId` |
| `RespondToSession` | `string sessionId`, `bool accept`, `long grantedPermissions` | — | 僅目標可呼叫;權限只能含已定義位元 |
| `GenerateOtp` | — | `string`(6 位數) | 目標顯示的一次性代碼 |
| `RequestSessionWithOtp` | `string target`(短 ID 或完整 ID), `string otp` | `AuthResultDto` | 只授予檢視權限 |
| `AddTrustedDevice` | `string deviceId`, `string name`, `long defaultPermissions` | — | 權限不得含重啟/關機,不得信任自己 |
| `RemoveTrustedDevice` | `string deviceId` | — | |
| `GetTrustedDevices` | — | `List<TrustedDeviceDto>` | |
| `SendIceCandidate` | `string sessionId`, `string candidateJson` | — | 僅參與者、僅 `EstablishingTransport`/`Connected`;≤ 4096 字元 |
| `ReportConnectionMode` | `string sessionId`, `string mode`(`Direct`/`Relay`) | — | `EstablishingTransport → Connected`;首次成功時簽發恢復 token |
| `RequestRelayToken` | `string sessionId` | `AuthResultDto` | 參與者取得新的單次使用 Relay token |
| `Heartbeat` | — | — | 更新在線狀態 |
| `CloseSession` | `string sessionId`, `string? reason`(≤ 256 字元) | — | 僅參與者;撤銷 token、關閉 Relay |
| `ResumeSession` | `string sessionId`, `string token` | `SessionDto` | 以恢復 token 恢復 `Paused` 的 Session,或在只有 Relay 連線斷了時恢復仍是 `Connected` 的 Session;回覆的 `Auth.SessionToken` 是下一個恢復 token,`Epoch` 加一,並以 `SessionStateChanged`(`Connected`,新的 `Epoch`)通知對方。兩端收到新紀元後各自用 `RequestRelayToken` 取得新的 Relay token,以該紀元導出的金鑰重建 Relay 連線 |

未完成註冊的連線呼叫上述任何方法(`BeginRegistration`、`RegisterDevice`、`Heartbeat` 除外)都會被拒絕。

### 1.2 事件(伺服器 → 客戶端)

| 事件 | 參數 | 說明 |
|---|---|---|
| `IncomingSessionRequest` | `SessionDto` | 目標收到連線請求(顯示給使用者核准) |
| `SessionStateChanged` | `SessionDto` | 狀態變更(`Authorizing`、`Failed`、`Connected`、`Paused`…),含 `Epoch` |
| `AuthSuccess` | `AuthResultDto` | 請求方的 Relay token 與已授予權限 |
| `SessionAuthorized` | `AuthResultDto` | 目標在「互動核准」流程取得**自己的** Relay token |
| `SessionEstablished` | `AuthResultDto` | 目標在 OTP/信任設備流程取得**自己的** Relay token |
| `RecoveryTokenIssued` | `sessionId`, `token` | 連線成功後每位參與者各一個恢復 token |
| `IceCandidateReceived` | `sessionId`, `candidateJson` | 對端的 ICE 候選 |
| `SessionClosed` | `sessionId`, `reason` | 被關閉或逾時結束 |

### 1.3 註冊證明

簽章內容(每個欄位前加 4 位元組長度):

```
LP("RemoteMesh.DeviceRegistration.v1") ‖ LP(挑戰 32 B) ‖ LP(UTF-8 DeviceId,格式 "D") ‖ LP(UTF-8 SignalR ConnectionId)
```

簽章演算法:ECDSA P-256 + SHA-256,**IEEE P1363**(r‖s,64 B),base64 傳送。公鑰為 SubjectPublicKeyInfo DER(必須是格式正確、無多餘位元組的 P-256 公鑰,≤ 256 B)。`DeviceName` ≤ 64 字元(無控制字元)、`Platform` ≤ 32 字元。

### 1.4 DTO

```
DeviceInfoDto   { DeviceId, DeviceName, Platform, PublicKeyBase64 }
SessionDto      { SessionId, RequesterDeviceId, TargetDeviceId, CreatedAt, ExpiresAt, State, ConnectionMode, Epoch, Auth? }
AuthResultDto   { SessionId, SessionToken, State, GrantedPermissions }
TrustedDeviceDto{ DeviceId, DeviceName, TrustedSince, DefaultPermissions }
```

### 1.5 權限位元(`Permission`)

| 位元 | 值 | | 位元 | 值 |
|---|---|---|---|---|
| ViewScreen | 1 | | FileUpload | 32 |
| ControlMouse | 2 | | FileDownload | 64 |
| ControlKeyboard | 4 | | RemoteRestart | 128 |
| ClipboardRead | 8 | | RemoteShutdown | 256 |
| ClipboardWrite | 16 | | | |

`ViewOnly` = 1;`FullControl` = 127(不含重啟/關機)。預設授予 `ViewOnly`。

## 2. Relay(`/ws/relay`)

```
GET /ws/relay?sessionId=<guid>&deviceId=<guid>        (WebSocket 升級)
X-Relay-Token: <token>                                (建議;查詢字串 token 僅作相容後備)
```

- 每個 Session 最多兩個連線:請求方與目標各一個角色;token 單次使用、綁定 (Session, 設備)、預設 2 分鐘失效。
- 訊息:二進位,`[通道類型 1 B][內容]`,內容已是端到端密文。**只接受二進位**。單一訊息(含分片重組後)≤ 1 MB。
- 關閉碼:

| 碼 | 意義 |
|---|---|
| 1000 `Peer timeout` | 等不到對端(預設 30 秒);或正常結束 |
| 1003 | 收到文字訊框 |
| 1008 `Unauthorized` | 任何授權失敗(token 錯誤/已用/過期、非參與者、Session 狀態不符、角色重複)——一律相同 |
| 1009 | 訊息過大 |
| 1011 `Server busy` | Relay 已滿(不消耗 token) |

- HTTP 狀態:`400` 缺少/格式錯誤的參數或非 WebSocket 請求;`429` 來源位址被限流或鎖定。
- 閒置 5 分鐘關閉;Session 關閉/到期時伺服器立即中斷。

## 3. 端到端加密通道(`SecureChannel`)

每個通道的每個訊息:

```
┌───────────┬───────────┬──────────┬────────────────┐
│ 序號 (8)  │ nonce (12)│ tag (16) │ 密文 (= 明文長) │
└───────────┴───────────┴──────────┴────────────────┘
AEAD = AES-256-GCM,AAD = 通道類型 (1) ‖ 方向 (1) ‖ 序號 (8)
```

- 方向:發起方(請求方)= 0,另一方 = 1;接收端用對方的方向驗證,因此反射的封包會失敗。
- 序號從 1 遞增;接收端 64 項滑動視窗拒絕重複與過舊的序號,**驗證成功後才記錄序號**。
- 驗證失敗、太短或重放的封包被丟棄並計數(`DroppedFrames`/`ReplayedFrames`),不會交給應用層。
- **金鑰**:`ECDH(本機私鑰, 對方公鑰)` → `HKDF-SHA256(salt 無, info = SessionId(16 B, Guid 位元組) ‖ 紀元(4 B)`;紀元 0 的 info 只有 SessionId)→ 32 B。

## 4. 通道內容格式

### 4.1 Control — 心跳

5 位元組:`0xF0` ping 或 `0xF1` pong,後接 uint32 序號。對端立即回 pong;任何收到的訊息都視為存活證明;預設 5 秒一次、15 秒無任何流量即判定中斷。非心跳訊息交給 `ControlMessage` 事件。

### 4.2 Desktop — 影像封包(`FramePacket`,18 B 標頭)

```
FrameNumber int64 ‖ Codec u8 ‖ IsKeyframe u8 ‖ ChunkIndex u16 ‖ ChunkCount u16 ‖ Width u16 ‖ Height u16 ‖ 內容
```

- `Codec` = `CodecType` 列舉值:`H264`=0、`VP8`=1、`VP9`=2、`AV1`=3、`DeflateReference`=4、`TileDeltaReference`=5。
- 每個區塊內容 ≤ 16 KiB;每幀 ≤ 4096 個區塊。接收端:最多 8 個未完成幀、單幀 ≤ 64 MB、未完成總量 ≤ 256 MB,超過就丟棄最舊的;更舊的未完成幀在較新的幀完成時丟棄。
- **DeflateReference**:`[w u16][h u16][原始 DEFLATE(BGRA 像素)]`;解壓後長度必須恰為 w×h×4;尺寸 ≤ 8192。
- **TileDeltaReference**:

```
[w u16][h u16][flags u8: bit0=關鍵幀][seq u32][tileCount u16][tileIndex u16 × tileCount][DEFLATE(依序的區塊像素,逐列)]
```

64×64 區塊,邊緣區塊較小;關鍵幀列出全部區塊,差分幀只列出有變化的區塊(索引必須嚴格遞增且在範圍內);`seq` 必須比上一幀大 1,否則解碼器要求關鍵幀(`KeyframeRequiredException`)。編碼端在首幀、解析度變更、被要求時、以及每 `KeyframeIntervalSeconds × Fps` 幀送出關鍵幀。

### 4.3 Input — 輸入事件(固定 49 B)

```
EventId (16, .NET Guid 位元組) ‖ 時間戳 (8, UTC ticks int64) ‖ 類型 (1) ‖ X, Y, Button, Delta, KeyCode, Modifiers (各 4, int32)
```

類型:`MouseMove`=0、`MouseButtonDown`=1、`MouseButtonUp`=2、`MouseWheel`=3、`KeyDown`=4、`KeyUp`=5。被控端的 `InputGate` 依序檢查:Session `Connected` 且未到期 → 滑鼠類需 `ControlMouse`、鍵盤類需 `ControlKeyboard` → 座標 0–32767、按鍵 0–4、滾輪 |Δ| ≤ 1200、鍵碼 1–65535 → 時間戳與現在相差 ≤ 30 秒 → EventId 未見過(視窗 4096)→ 速率(預設 1000/秒)。

### 4.4 Clipboard

`[類型 u8][格式 u8][內容]`。類型:`ReadRequest`=0、`Data`=1、`WriteRequest`=2、`Ack`=3、`Denied`=4;格式:`Text`=0(嚴格 UTF-8)、`Image`=1。`Denied` 內容為 1 位元組結果碼:`Ok`=0、`PolicyDenied`=1、`PermissionDenied`=2、`SessionNotActive`=3、`TooLarge`=4、`Empty`=5、`RateLimited`=6、`Malformed`=7。上限:文字 1 MiB、圖片 10 MiB。被控端本機策略(`Disabled` 預設、`ReadOnly`、`WriteOnly`、`Bidirectional`)與 Session 權限(`ClipboardRead`/`ClipboardWrite`)必須同時允許。沒有任何自動同步。

### 4.5 File

`[類型 u8][transferId 16 B][內容]`,訊息上限 17 + 8 + 65536 B。

| 類型 | 值 | 內容 |
|---|---|---|
| UploadStart | 1 | total (8) ‖ SHA-256 (32) ‖ 檔名 (UTF-8,≤ 1020 B) |
| UploadAccept | 2 | 續傳起點 offset (8) |
| Chunk | 3 | offset (8) ‖ 資料(≤ 64 KiB) |
| ChunkAck | 4 | 已寫入位元組數 (8) |
| End | 5 | — |
| Complete | 6 | — |
| Reject | 7 | 結果碼 (1) |
| Cancel | 8 | —(明確取消會丟棄暫存檔) |
| DownloadRequest | 9 | offset (8) ‖ 檔名 |
| DownloadInfo | 10 | total (8) ‖ SHA-256 (32) |

結果碼(`FileTransferResult`):`Ok`=0、`SessionNotActive`=1、`PermissionDenied`=2、`InvalidName`=3、`TooLarge`=4、`Exists`=5、`NotFound`=6、`BadOffset`=7、`ChecksumMismatch`=8、`Busy`=9、`Cancelled`=10、`Malformed`=11、`SizeMismatch`=12、`OutOfOrder`=13、`Timeout`=14、`ConnectionClosed`=15、`IoError`=16。

檔名規則:單純檔名,無路徑分隔符/磁碟代號/`..`/開頭的點/結尾的點或空白/Windows 裝置名稱/控制字元,≤ 255 字元。上傳先寫入 `.partial/<sha256>.part`,SHA-256 相符才發佈;不覆蓋既有檔案;預設單檔 ≤ 1 GB、同時 ≤ 4 個傳輸。上傳以最多 16 個未確認區塊做流量控制。
