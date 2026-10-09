# 變更紀錄

格式依開發階段。版本 0.1.0 尚未正式發行。

## 0.1.0 — 開發中

### 被控端封鎖名單
- 被控端的人可以封鎖一個裝置:它進行中的連線立刻結束,之後它再怎麼要求(信任裝置、一次性密碼也一樣)都被直接拒絕、不再詢問;名單存在這台電腦(`blocked-peers.json`),重啟後仍在,介面有「封鎖」按鈕(先確認)與「已封鎖的裝置」清單可以解除(WPF 與 Avalonia)
- `RemoteMeshClient.BlockPeerAsync` / `UnblockPeer` / `BlockedPeers` / `IsBlocked`、`PeerBlockList`
- 驗證:`PeerBlockListTests`、`BlockTests`(連線中封鎖、不經詢問、信任裝置與一次性密碼路徑、解除、重啟、不影響他人)、介面邏輯測試;變異檢查(信任路徑不檢查、仍然詢問、不結束進行中的連線,測試都會失敗)
- 已知限制:以裝置 ID 為準;只管這一台;Agent 沒有封鎖指令(它的設定檔白名單本來就只放行列出的裝置)

### 前向保密(TLS)
- 兩台裝置之間經過 Relay 的連線包在平台的 TLS 裡(`RelayTls`、`TlsWebSocket`):用裝置金鑰包成的一次性自簽憑證做雙向驗證,只接受事先釘選的公鑰;臨時金鑰交換(TLS 1.3 或 ECDHE 的 TLS 1.2),握手後驗證套件;每次重連都是新的握手。沒有自行設計協定或密碼學,原本的端到端 AES-GCM 通道保留在 TLS 之內
- 修正一個潛在的競態:`RelayTransport` 現在在讀取第一個訊息之前就建立好所有通道。對方一連上就說話時(例如檢視端的 Hello、第一個輸入事件),訊息可能在通道開啟前到達而被丟掉——這是之前偶發失敗的測試(第一個輸入事件遺失、Hello 遺失而退回無損畫面)的原因;TLS 讓握手後的第一個訊息更早到達,使它從偶發變成常見而被抓到
- `SessionBase.TransportSecurity` 顯示協商出的 TLS 版本與套件
- 驗證:`RelayTlsTests`(10 個)、`RelayTransportEarlyMessageTests`、端對端確認兩端都是前向保密的套件;變異檢查(接受任何金鑰、客戶端不跑 TLS,測試都會失敗);Windows(SChannel)與 Linux(OpenSSL,WSL)上的完整 Integration 與 Client 測試、Linux 真實進程(Agent + 檢視端)
- 已知限制:macOS 的 TLS 沒有在實機驗證;內層 AES-GCM 金鑰仍來自靜態 ECDH

### Linux 與 macOS 版本
- 新後端:`Platform.Linux`(X11 `XGetImage` 擷取、XTEST 輸入、`xclip`/`xsel`/`wl-clipboard` 剪貼簿)、`Platform.MacOS`(CoreGraphics 擷取、CGEvent 輸入、`pbcopy`/`pbpaste` 剪貼簿、權限檢查)、`Platform.Skia`(Linux/macOS 的 JPEG)、`Platform.Common`(外部工具剪貼簿與指令執行器)、`Platform`(`PlatformHost` 依系統選擇後端,Agent 與桌面程式共用)
- 跨平台桌面程式 `RemoteDesktop.UI.Avalonia`:與 Windows 版相同的功能(ID/一次性密碼、連線、核准對話框、檢視視窗含滑鼠鍵盤剪貼簿檔案、重新連線橫幅、安全碼核對、被控名單、更換金鑰、設定);共用的介面邏輯移到 `RemoteDesktop.UI.Core`,Windows 的 WPF 版繼續存在
- `IShell` 的設定對話框與確認改為非同步(Avalonia 的對話框是非同步的)
- `scripts/publish-desktop.ps1`:為 `linux-x64`/`linux-arm64`/`osx-arm64`/`osx-x64` 打包自含的桌面程式與 Agent(`.tar.gz`,權限由套件工具明確設定;macOS 是 `RemoteMesh.app`),可在 Windows 上建置所有目標
- `PlatformHost.CreateJpegCodec` 先用一張小圖自我測試:原生函式庫缺失時退回無損畫面,而不是在連線中途壞掉
- 驗證:Linux 在 WSL + Xvfb 上 150 個平台測試 0 略過;真實進程(打包後的自含 Agent + 檢視端):畫面顏色正確、滑鼠到達指定像素、按鍵被 X 伺服器看見、雙向剪貼簿;Avalonia 桌面程式在 Linux/Xvfb 與 Windows 上啟動並完成註冊;Avalonia 無頭視窗測試 47 個(含真實伺服器的檢視視窗)
- 已知限制:Linux 純 Wayland 不能擷取或被控;**macOS 沒有在真實 Mac 上執行過**;沒有簽章與公證;沒有 deb/rpm/dmg

### 裝置金鑰輪替
- 裝置可以換成新金鑰(`RotateDeviceKey`):舊金鑰與新金鑰對同一份聲明各簽一次(`KeyRotationProof`),伺服器驗證後原子換鑰並記錄歷史(`DeviceKeyRotations`,PostgreSQL 為單一交易;有進行中的 Session 時拒絕;每小時 3 次、最多 100 次)
- 已釘選舊金鑰的客戶端遇到新金鑰時向伺服器要歷史(`GetPeerKeyRotations`),自己驗證每一步的雙簽章與接續,才改釘新金鑰;已核對的維持已核對,安全碼改變;伺服器偽造的歷史過不了
- 客戶端 `RotateKeyAsync`:先暫存新金鑰、伺服器接受後提交;中斷時下次啟動依伺服器實際狀態收尾;UI「更換金鑰」按鈕(先確認)
- `KeyChecks`(安全的 P-256 金鑰/簽章檢查,伺服器驗證與客戶端驗證共用)
- 已知限制:偷到私鑰的人也能輪替;沒有撤銷與被控端黑名單

### 對方金鑰的釘選與安全碼核對
- 客戶端第一次見到某個設備時把它的金鑰雜湊存在本機(`known-peers.json`,只存 SHA-256),之後每次連線與伺服器給的金鑰比對;不同就在金鑰協商前中止(`PeerKeyChangedException`),關閉伺服器上的 Session,被控端同樣拒絕;`ForgetPeer` 才會接受新金鑰
- 安全碼:雙方公鑰的 SHA-256 衍生的六組五位數字,兩邊畫面相同,念給對方核對;核對後記住(只對那把金鑰有效)。檢視視窗頂端與被控端清單都顯示,可按「已核對」
- 伺服器:新的 `GetPeerIdentity`(對方的裝置 ID、名稱與金鑰),規則與 `GetPeerPublicKey` 相同(僅限參與者,核准後才給,不洩漏存在與否)
- 驗證:`KeyFingerprintTests`、`PeerPinStoreTests`(含損毀檔、上限、並行)、`PeerKeyEndToEndTests`(含模擬伺服器在第一次之後換金鑰)、UI 測試,變異檢查(拿掉比對、比對恆為相同,測試都會失敗);真實 `RemoteMesh.exe` 以 UI Automation 驗證顯示、核對後記住、金鑰被換時的警告
- 已知限制:第一次接觸仍是首次使用即信任(沒核對安全碼就抓不到);核准提示在連線前還沒有安全碼

### 資料庫遷移
- 資料表改由 EF 遷移建立與升級(`InitialSchema`),取代 `EnsureCreated`;啟動時自動套用尚未套用的遷移,重複啟動不改變資料
- 測試:空資料庫建出所有資料表、重複套用保留資料、模型與遷移同步(改了實體卻沒加遷移會讓測試失敗)
- 注意:開發期間用舊版(`EnsureCreated`)建立的資料庫需要刪掉重建

### Session 與恢復 token 持久化
- 設定資料庫後,Session(狀態、權限、紀元、連線模式)與恢復 token 也存進 PostgreSQL:`PersistentSessionManager` / `PersistentSessionTokenService` 包住記憶體實作,每次變更寫入,啟動時載回(`InitializePersistenceAsync`,在伺服器接收第一個請求之前)
- 重啟後的 Session:連線中 → 已暫停(恢復時限從重啟起算)、已暫停 → 維持原本起點、協商/核准中 → 失敗、已結束或過期 → 不載回;雙方客戶端用手上的恢復 token 自動接回(新的金鑰紀元)
- token 只存 SHA-256(記憶體索引也是);用掉或撤銷的 token 在呼叫回傳前就從資料庫刪除
- `ISessionManager.SaveAsync`:直接改在 `SessionInfo` 上的欄位(權限、連線模式、請求者短 ID)用它寫入
- 驗證:13 個新資料庫測試(含重啟還原、用掉的 token 不復活、平行恢復後資料庫不落後、資料庫沒有 token 本體);變異檢查(不轉成已暫停、已用 token 的資料庫列不刪、恢復不寫入,測試都會失敗);真實進程:真實伺服器 + Agent + 檢視端,殺掉伺服器、6 秒後重啟,檢視端約 1 秒內接回,Session 沒有結束
- 已知限制:仍只能單實例(資料庫是持久副本,不是多實例的仲裁者);限流與鎖定計數重啟後重置;沒有遷移

### PostgreSQL 持久化(身分與稽核)
- `Server.Persistence`:EF Core + Npgsql。設定 `RemoteMesh:Database:ConnectionString` 後,設備與公鑰、短 ID、信任設備、稽核紀錄存進 PostgreSQL,重啟後仍在;沒設定則維持記憶體
- 短 ID 歸屬由資料庫的兩個唯一約束仲裁(一個號碼一個裝置、一個裝置一個號碼):平行申請、兩個伺服器實例同時申請同一個號碼,都只有一個成功;重啟後別的裝置搶不走原主人的號碼
- 註冊與信任寫入是單一 `INSERT … ON CONFLICT` 敘述,同時註冊不會互撞
- 稽核紀錄預設保留 90 天(每天清理,`AuditRetentionDays`),單次查詢上限 10,000 筆,原因欄位上限 1,024 字元
- `IShortIdDirectory` 改成非同步(`AssignAsync`/`ResolveAsync`/`GetAsync`),`ClaimShortId` 隨之非同步
- `deploy/docker-compose.yml` 加入 PostgreSQL 服務(密碼由 `POSTGRES_PASSWORD` 提供,不對外開放連接埠)
- 驗證:21 個資料庫測試(對真實 PostgreSQL 17,每個測試用獨立資料庫;未設定 `RM_TEST_PG` 時略過),變異檢查(拿掉唯一約束仲裁、公鑰不更新,測試都會失敗);真實進程:伺服器重啟後,新裝置要求原主人的短 ID 被拒絕、原主人拿回同一個號碼
- 已知限制:稽核不防竄改

### 斷線自動重連
- 網路中斷、Wi-Fi 切換或 Relay 連線被切斷後,`RemoteMeshClient` 的 Session 自動接回:兩端以新的金鑰紀元重建 Relay 連線,畫面(關鍵影格)與滑鼠操作恢復,Session 不中斷;檢視視窗在重連期間保留最後的畫面並顯示「正在重新連線」
- 通道可替換(`ResilientChannel`):畫面、輸入、剪貼簿、檔案、心跳等迴圈持有的通道物件不變,底下的連線換了就接著用;斷線期間送出會等待,輸入事件則直接丟棄
- 誰負責恢復:自己斷過伺服器連線的一方立即以恢復 token 恢復;只是 Relay 斷了則先等對方一小段時間(被控端等兩倍);對方已回報斷線(`Paused`)就等它回來,不自行恢復
- 信令連線一直重試到成功(原本約 45 秒後放棄),間隔 2–5 秒隨機,避免伺服器重啟時同時湧入;重新註冊失敗會重試
- 伺服器:`ResumeSession` 也接受 `Connected` 的 Session(只有 Relay 斷了時)並進入新紀元;Relay 配對記錄紀元,新紀元取代舊紀元仍掛著的連線
- `KeepAliveService.Reset()`:換了連線後,斷線期間的沉默不算在新連線頭上
- 重連失敗(伺服器重啟、超過 `ReconnectTimeout`、對方已結束)明確結束 Session 並說明原因,不會永遠停在「重新連線中」
- 驗證:`ResilientChannelTests`、`ReconnectTests`(含整台電腦斷網再恢復)、中繼換代測試;變異檢查(拿掉換代取代、拿掉 Connected 恢復、通道不跨連線接收,都會讓測試失敗);真實進程(真實伺服器 + Agent + 可切斷的 TCP 代理):斷網 8 秒後約 3 秒內接回,伺服器重啟時 4 秒內明確結束
- 已知限制:斷線當下傳到一半的檔案會失敗(需重新傳);伺服器重啟不保留連線(狀態在記憶體)

### 短 ID
- 每台裝置有一個 9 位數字的短 ID(例如 `482 913 057`),取代 36 字元的 GUID 作為給人用的地址;完整 ID 仍可使用
- 伺服器隨機配發(不可由 GUID 推算);裝置記住上次的號碼,重新註冊時申請沿用,只有沒人持有才會批准
- 查詢短 ID 走與完整 ID 相同的列舉防護:相同的錯誤訊息、相同的失敗額度與鎖定、寫入稽核
- 請求方的短 ID 隨請求傳給被控端,核准對話框與被控名單顯示的就是對方自己畫面上的號碼
- 連線輸入接受 `482913057`、`482 913 057`、`482-913-057` 與完整 ID
- 已知限制:短 ID 沒有持久化,伺服器重啟後先到先得(THREAT_MODEL §4 #4b)
- 驗證:協定格式測試、目錄的並行配發、業務層(配發/沿用/枚舉鎖定/OTP)、客戶端端到端、視圖模型;真實進程驗證(B 輸入 A 畫面上的號碼,A 的核准對話框顯示 B 自己畫面上的號碼)

### 有損畫面編碼與碼率控制
- `TileDeltaJpegEncoder/Decoder`:只送變動區域,區域合併成矩形(大變動整張一個 JPEG、小變動用橫條),JPEG 由平台提供(`IJpegCodec`;Windows 用 GDI+ `GdiJpegCodec`)
- 不信任對方資料:先解析 JPEG 標頭驗證尺寸、區域幾何、總面積上限,通過後才呼叫影像解碼器;任何失敗都要求重送關鍵幀
- `RateController`:依實測輸出碼率與送出延遲在畫質階梯(縮放、品質、幀率)上升降,快降慢升
- 雙方以控制通道的 hello 協商編碼,任一方不支援就退回無損;畫面沒變時不送任何東西
- 查看端狀態列顯示 fps、Mbit/s 與壓縮/無損;`VideoBudgetKbps` 可設定
- 修正:發送端從不釋放撷取緩衝;限速從上一幀送完起算導致幀率過低
- 驗證:變異檢查確認三個關鍵檢查(尺寸、面積、釋放)與節奏邏輯的測試有效;模擬 6 Mbit/s 慢線的閉環測試;真實進程驗證(協商出 JPEG、原生 1920x1080)

### 桌面程式(WPF)
- 新增 `RemoteDesktop.UI`(`RemoteMesh.exe`):主視窗、核准對話框、設定、遠端畫面視窗;視圖模型不依賴視窗,可測試
- 核准對話框:顯示對方 ID,預設只授予「查看畫面」,一次只問一個,60 秒無回應自動拒絕;**請求方消失或撤回時對話框自動關閉**
- 遠端畫面視窗:座標以實際顯示區換算(黑邊不算)、依授權停用剪貼簿/檔案按鈕、只在畫面取得焦點時轉發鍵盤
- 伺服器:請求方斷線時立即撤回其尚未回應的請求並通知被控端(原本要等到逾時清掃)
- 客戶端:關閉客戶端會中止進行中的連線嘗試;核准時的取消權杖
- 安全:核准提示等待期間暫停所有已連線 Session 的輸入注入,已獲得滑鼠權限的控制端不能替自己點「允許」(含變異檢查)
- 打包:`scripts/publish-client.ps1` 產生單一 `RemoteMesh.exe`(框架相依約 1.5 MB;自包含約 135 MB);`REMOTEMESH_DATA` 指定資料夾
- 驗證:UI 測試 60 個(含視圖模型 + 真實伺服器);真實進程驗證——兩個真實 `RemoteMesh.exe` 以 UI Automation 操作,完成連線 → 核准 → 遠端畫面 → 中斷的完整流程

### 客戶端組裝
- 新增 `RemoteDesktop.Client`:`RemoteMeshClient`(註冊、核准、Relay、端到端加密、五個通道;同時是被控端與控制端)、`HostSession`/`ViewerSession`、`IApprovalHandler`(核准由 UI 實作;沒有處理者 = 一律拒絕;核准時未授予任何權限視為拒絕)
- 新增 hub 方法對應的客戶端操作:一次性密碼、取得對方公鑰、Relay token、回報連線模式、關閉 Session,以及核准時指定授予的權限
- 控制通道新增「要求關鍵幀」訊息:檢視端漏幀後立即要求主機重送關鍵幀
- 被拒絕的連線立即失敗(原本要等滿逾時才發現)
- `Agent` 改為真正可執行的無介面被控端:`Agent:*` 設定、設定檔白名單核准(絕不授予重啟/關機)、Windows 後端接線;修正原本啟動即因 DI 缺少 `ISignalingClient` 而崩潰
- 驗證:真實伺服器 + 兩個真實客戶端的端到端測試 10 個;另以真實進程冒煙驗證——真實 Agent 被真實控制端連上,收到這台電腦的真實桌面(1600x900、20 fps、往返 0.7 ms)

### Windows 後端
- 新增 `RemoteDesktop.Platform.Windows`:`GdiScreenCapture`(主螢幕,BitBlt,不含滑鼠游標與安全桌面)、`SendInputController`(滑鼠/鍵盤注入,失敗只計數不丟例外)、`WindowsClipboardManager`(文字;圖片尚未支援)
- 輸入線路約定:X/Y 為整個主螢幕正規化的 0–32767、Button 1/2/3 = 左/右/中、KeyCode 為 Windows 虛擬鍵碼(`InputTranslator`,可跨平台測試)
- 剪貼簿主機:對不支援圖片的剪貼簿回覆拒絕,不再讓例外終止通道
- 修正:螢幕 DC 具執行緒親和性,不可跨 `await` 持有(否則擷取會間歇失敗);改為每幀在同一同步呼叫內取得與釋放
- 測試:34 個(轉換邏輯在任何平台執行;擷取/滑鼠/剪貼簿在互動式 Windows 桌面上實測,並還原游標與剪貼簿)

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
