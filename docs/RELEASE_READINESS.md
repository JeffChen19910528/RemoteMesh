# 出貨準備度

> ## 結論:**NOT RELEASE READY(尚未達到可出貨)**
>
> 通訊、安全與工作階段的**基礎層**已完成、經過安全審查與完整測試(1134 個測試通過、0 失敗(45 個需要 PostgreSQL 或 Linux/macOS 的測試在這台 Windows 機器上略過))。
> 但產品本身還不能交給使用者:**使用者介面只有首版(WPF,Windows)、Agent 只有無介面的白名單版本、Windows、Linux(X11)、macOS 後端都是首版(Linux 純 Wayland 不能擷取,macOS 沒有在真實 Mac 上跑過)、有損編碼只有 JPEG 分塊(沒有動態補償,整屏連續變動時約 5 fps)、P2P 直連只在同網段或可直接到達的位址有效(沒有 NAT 打洞,其他情況走 Relay)。**
> 另有已知的 P1 安全風險:前向保密、設備撤銷、外部審查;對方公鑰的首次接觸仍是首次使用即信任(之後的金鑰變更會被擋下,雙方可核對安全碼)。
>
> 只有出貨完成定義(§2)的全部條件成立才能標記 RELEASE READY,目前**不成立**,所以不標記。

狀態圖例:✅ 完成並有證據 · 🟡 部分完成(說明缺什麼) · ❌ 未完成

## 1. 發行準備確認項目

| 項目 | 狀態 | 證據與說明 |
|---|---|---|
| Build PASS | ✅ | `dotnet build RemoteDesktop.slnx`:0 警告、0 錯誤;Release 組態同樣通過 |
| Unit Tests PASS | ✅ | 見 §4 |
| Integration Tests PASS | ✅ | `Integration.Tests` 206 個,含伺服器、Relay、設定、Session 恢復 |
| Security Tests PASS | ✅ | 63 個攻擊情境 + 15 個強化單元測試 + 加密通道/金鑰紀元/關鍵幀等;含變異檢查 |
| Acceptance Tests PASS | 🟡 | 7 個驗收測試通過(真實伺服器 + Relay + 加密 + 五個通道 + 權限 + 恢復 + 稽核)。**NAT 驗收只是邏輯模擬,沒有真實 NAT**;沒有真實 OS 後端、沒有真實網路 |
| Documentation Complete | ✅ | README、ARCHITECTURE、SECURITY、PROTOCOL、NETWORKING、DEPLOYMENT、TESTING、TROUBLESHOOTING、THREAT_MODEL + CONFIGURATION、PERFORMANCE、本文件。文件內容已對照程式碼與實測核對 |
| Installer Complete | 🟡 | 伺服器:發布腳本、Windows 服務與 Linux systemd 安裝腳本、Dockerfile/compose。**桌面程式有 Windows MSI 安裝程式(`build-installer.ps1`,已實際安裝/解除安裝驗證)與簽章腳本(`sign.ps1`),但沒有 Agent 安裝程式、Linux/macOS 沒有安裝程式、套件未用真實憑證簽章**;安裝腳本只做了乾跑驗證,Docker 未實際建置(見 DEPLOYMENT.md「驗證狀態」) |
| Configuration Documentation Complete | ✅ | CONFIGURATION.md;預設值與 `appsettings.json` 一致有測試;不合法設定使伺服器啟動失敗有測試。Agent 有 `Agent:*` 設定(見 CONFIGURATION.md);一般使用者用的客戶端設定與規劃中的 yaml 範例尚未提供 |

## 2. 出貨完成定義(22 項)

| # | 項目 | 狀態 | 說明 |
|---|---|---|---|
| 1 | Device ID system | ✅ | `DeviceId`、身分服務、金鑰儲存(Windows DPAPI;Unix 檔案權限) |
| 2 | Device authentication | ✅ | 註冊時私鑰簽章挑戰、公鑰釘住;**但無撤銷與輪替**(見 §3 P1) |
| 3 | Signaling | ✅ | 伺服器端與客戶端 `ISignalingClient` 都完整:註冊、請求、回應、恢復、心跳、OTP、信任設備(新增/移除/列出)、ICE 候選、回報連線模式、要求 Relay token 全部都在客戶端類別上,且有真實伺服器 + 真實客戶端的端到端測試 |
| 4 | NAT traversal | 🟡 | **TCP 直連已實作**(同網段、公開位址、轉發的埠;經信令交換位址,失敗自動回退 Relay,有真實伺服器 + 兩個真實客戶端的測試,含直連斷線重連)。**沒有 NAT 打洞與 TURN**:兩邊都在不同 NAT 後面時走 Relay;沒有真實 NAT 測試 |
| 5 | Relay fallback | ✅ | 伺服器與客戶端傳輸完整,有端到端測試。直連不可用時(多數跨網路情況)就是實際的傳輸路徑 |
| 6 | End-to-end encryption | 🟡 | `SecureChannel` 完整(AES-256-GCM、序號、AAD、重放視窗、金鑰紀元)。**缺:內層金鑰的前向保密(外層 TLS 已有);對方公鑰已釘選且可用安全碼核對,但首次接觸仍是首次使用即信任;也沒有 Session 協調器強制所有通道都經過它** |
| 7 | Remote screen | 🟡 | 串流管線、差分編碼、JPEG 分塊有損編碼、依實測碼率與送出延遲的碼率控制、Windows GDI 擷取都有測試與真實驗證。**沒有動態補償的影片編碼器**(整屏連續變動時只有約 5 fps);沒有 Linux/macOS 擷取;滑鼠游標不在畫面內 |
| 8 | Mouse control | 🟡 | 協定、驗證、權限、重放、限流完整。**沒有 OS 注入實作** |
| 9 | Keyboard control | 🟡 | 同上 |
| 10 | Clipboard | 🟡 | 協定、策略、權限完整。**沒有 OS 剪貼簿實作**;沒有檔案型剪貼簿 |
| 11 | File transfer | ✅ | 分塊串流、續傳、校驗、取消、沙盒、權限、資源上限;256 MB 實測 182–290 MB/s。(沒有 UI) |
| 12 | Permission model | ✅ | 九個權限位元,逐訊息強制,撤銷即時生效 |
| 13 | Audit logging | 🟡 | 事件齊全(含授予的權限)。**只在記憶體、不持久、不防竄改,沒有查詢介面** |
| 14 | Rate limiting | ✅ | 註冊、OTP、連線請求、未知查詢、Relay、恢復等都有限流與鎖定。(單行程記憶體;反向代理需正確設定 `ForwardedHeaders`) |
| 15 | Reconnect | 🟡 | 心跳、退避、網路變更偵測、`ConnectionSupervisor`、伺服器端 Session 恢復、客戶端自動重新註冊與恢復都有測試;驗收測試手動串接並驗證整條路徑。**`RemoteMeshClient` 的斷線自動重連已完成**:Relay 連線被切斷、或整台電腦斷網再恢復,兩端都會用新的金鑰紀元重建連線,畫面與滑鼠操作恢復,Session 不中斷;重連不成(伺服器重啟、超過時限、對方已結束)就明確結束並說明原因。已用真實進程驗證(真實伺服器 + Agent + 可切斷的網路代理)。**限制:** 斷線當下傳到一半的檔案會失敗,需重新傳(傳輸本身可續傳);斷線期間的滑鼠鍵盤事件被丟棄(過期的操作比沒有更糟);伺服器重啟不保留連線 |
| 16 | Security review | ✅ | Phase 12 內部審查,9 項漏洞已修正,Phase 13/14 又修正多項(見 CHANGELOG)。**沒有外部滲透測試或第三方稽核** |
| 17 | NAT acceptance test | 🟡 | 五種情境以邏輯模擬通過;**未在真實 NAT 後驗證** |
| 18 | Performance test | ✅ | 行程內量測完整(PERFORMANCE.md);**沒有真實網路、真實擷取/編碼、長時間 soak** |
| 19 | Installer | 🟡 | 見 §1 |
| 20 | Documentation | ✅ | 見 §1 |
| 21 | All automated tests passing | ✅ | 1134 通過、0 失敗(45 個需要 PostgreSQL 或 Linux/macOS 的測試略過);更早曾有 1 次整套並行時單一 Integration 測試失敗,未能重現,原因未確認 |
| 22 | No known P0/P1 security issue | ❌ | 有已知 P1:**對方公鑰由伺服器提供,惡意或被入侵的伺服器可做中間人**(§3);另有 P2 項目 |

統計:✅ 11 · 🟡 9 · ❌ 2(另有 Phase 14 確認項目中 Installer、Acceptance 為 🟡)。

## 3. 阻擋項目(依優先順序)

### P0 — 產品無法運作
1. **使用者介面**:**首版已完成**(Windows 為 WPF;Linux/macOS 為 Avalonia,功能相同,在 Windows 與 Linux/Xvfb 上驗證過,macOS 未在 Mac 上跑過):主視窗(ID、一次性密碼、連線、被控制名單與中斷、設定)、核准對話框(顯示對方 ID、可選權限、60 秒倒數、請求被撤回時自動關閉)、遠端畫面視窗(滑鼠鍵盤轉發、剪貼簿、檔案上傳/下載、依授權自動停用按鈕)、單一 exe 打包。真實進程驗證過 UI→UI 的完整流程。**尚缺:** ① 短 ID(9 位數字)已完成,但伺服器沒有持久化,重啟後是「先到先得」地重新申請(THREAT_MODEL §4 #4b);② Windows 有 MSI 安裝程式與簽章腳本,但沒有真實憑證,所以發行物未簽章(SmartScreen 會警告);Linux/macOS 沒有安裝程式;③ 只擷取/控制主螢幕,沒有多螢幕選擇;④ 傳輸進度只有文字;⑤ 沒有系統匣/開機自動啟動;⑥ 介面只有繁體中文;⑦ 只設了無障礙名稱,沒有螢幕閱讀器的完整測試。
2. **應用程式組裝**:**核心已完成**——`RemoteDesktop.Client` 的 `RemoteMeshClient` 同時扮演被控端與控制端(註冊、核准、取得對方公鑰、Relay、端到端加密、五個通道,所有通道都經 `SecureTransport`),並有真實伺服器 + 兩個真實客戶端的端到端測試,以及真實進程的冒煙驗證(真實桌面 1600x900、20 fps)。`Agent` 是無介面的被控端,只接受設定檔白名單內的設備。**尚缺:** Agent 必須在使用者的登入工作階段執行(Windows 服務在 session 0,看不到使用者螢幕);沒有「自己輸入對方 ID 連線」的介面(見第 1 項)。
3. **OS 後端**:**Windows、Linux(X11)、macOS 都有首版**(詳見 [PLATFORMS.md](PLATFORMS.md)):Windows 為 GDI 擷取、`SendInput`、Win32 剪貼簿;Linux 為 `XGetImage`、XTEST、`xclip`/`xsel`/`wl-clipboard`,在 Xvfb 上以真實進程驗證(畫面顏色、滑鼠位置、按鍵、雙向剪貼簿);macOS 為 CoreGraphics、CGEvent、`pbcopy`/`pbpaste`。**仍缺**:**macOS 沒有在真實 Mac 上跑過**(只有邏輯測試;擷取、事件注入、權限提示、`.app` 能否開啟都未驗證);**Linux 純 Wayland 工作階段不能擷取或控制**(需要 xdg-desktop-portal + PipeWire 與 libei);Linux 只在 Xvfb 驗證,沒有在實體桌面環境(GNOME/KDE Xorg)上驗證;macOS 的擷取用已過時的 `CGDisplayCreateImage`(新版 macOS 應改 ScreenCaptureKit)且只擷取主螢幕;各平台都沒有滑鼠游標、剪貼簿不支援圖片、不支援 Windows 的 UAC/鎖定畫面等安全桌面、Windows GDI 擷取效能未量測。
4. **真實影片編碼器**:**部分完成**——已有 JPEG 分塊有損編碼加碼率控制(Windows,見 PERFORMANCE.md §3.3b):靜止桌面幾乎不耗頻寬,一般操作很省,整屏連續變動時會在預算內變軟、降到約 5 fps(5 Mbit/s 預算)。**仍缺有動態補償的影片編碼器**(H.264/VP9/AV1,原生函式庫或硬體編碼)才能讓整屏連續變動也流暢。
5. **P2P 直連**:**TCP 直連已完成**(見 NETWORKING.md):同網段或可到達的位址不經 Relay。**仍缺**:NAT 打洞(UDP/ICE,兩邊都在 NAT 後面時)與 TURN;這些情況仍全部經過 Relay(成本與擴展性)。
6. **持久化**:**大致完成(單實例)**——設定 `RemoteMesh:Database:ConnectionString` 後,設備與公鑰、短 ID 歸屬、信任設備、稽核紀錄、**Session 與恢復 token(只存雜湊)**存進 PostgreSQL。重啟後:短 ID 不會被搶走;連線中的 Session 以「已暫停」狀態回來,雙方客戶端用手上的恢復 token 自動接回(用真實伺服器 + Agent 驗證:殺掉伺服器、6 秒後重啟,檢視端約 1 秒內接回,Session 沒有結束);沒回答的核准請求會失敗;恢復時限從重啟那一刻重新起算。對真實 PostgreSQL 17 測試。**仍缺**:限流、鎖定計數、OTP、在線狀態還在記憶體(重啟會重置暴力破解的鎖定計數),因此仍只能單實例,資料庫是持久副本、不是多實例共用的仲裁者;重啟的空窗期間(伺服器沒起來時)連線中斷,恢復靠客戶端的重試;資料表由 EF 遷移建立與升級(開發期間用舊版建立的資料庫需要刪掉重建);沒有備份/還原與遷移失敗時的回復流程與稽核防竄改(寫入後可被有權限的管理者修改)。

### P1 — 安全
6b. **控制端自己按下被控端核准按鈕**:**已緩解**——只要有核准提示在等待,所有已連線 Session 的輸入注入都會暫停(丟棄,不排隊;變異檢查確認測試有效)。殘留:提示出現前的瞬間已送出的事件、以及非本程式的其他提示視窗(例如 UAC)不在此保護範圍。
7. **對方公鑰的帶外驗證**:**大致完成**——客戶端把見過的設備金鑰釘選在本機,金鑰之後被換會在金鑰協商前中止(雙方都拒絕,畫面說明原因);兩邊畫面顯示相同的安全碼(六組五位數字)供念出來核對,核對後會記住。**仍缺**:第一次接觸時若伺服器就換金鑰且沒人核對,抓不到;核准提示在連線前還看不到安全碼(金鑰要核准後才取得);沒有「未核對前限制權限」的強制流程。
8. **金鑰輪替與設備撤銷**:**金鑰輪替已完成**——裝置可以換成新的金鑰:舊金鑰與新金鑰都簽署同一份聲明,伺服器驗證後原子地換掉金鑰並記錄歷史(最多 100 次、每小時 3 次、有 Session 時不可換);之前釘選過它的客戶端用裝置自己的簽章沿著歷史驗證到新金鑰,自動跟上、保留「已核對」狀態、安全碼改變,伺服器偽造不了(沒有私鑰)。換到一半當機會在下次啟動時自行收尾。UI 有「更換金鑰」按鈕。**仍缺**:私鑰已經外洩時,偷到私鑰的人也能輪替(輪替證明的是「擁有舊金鑰」,不是「是本人」);**被控端有封鎖名單(撤銷的另一半)**:被控端的人可以對一個裝置按「封鎖」,它的連線立刻結束,之後它再怎麼要求(即使伺服器說它是信任裝置、或它知道一次性密碼)都被直接拒絕、不再詢問;名單存在這台電腦上、重啟後仍在,可以解除。**仍缺**:沒有帳號/擁有者的概念,所以沒有人能代替遺失私鑰的裝置宣告它作廢(目前的做法是換新 DeviceId,也就是重新安裝);封鎖只管這一台被控端,不會通知別台;無介面的 Agent 用設定檔的白名單(不在名單的一律拒絕),沒有封鎖指令。
9. **前向保密**:**已完成(傳輸層)**——兩台裝置之間經過 Relay 的連線現在包在平台的 TLS(SChannel / OpenSSL / Secure Transport,TLS 1.3 或 ECDHE 的 TLS 1.2)裡:雙向憑證驗證,用裝置金鑰包成一次性自簽憑證,只接受事先釘選的那把公鑰;金鑰交換是臨時的,所以錄下的流量即使事後裝置私鑰外洩也解不開。沒有自己設計任何協定或密碼學,是把成熟的 TLS 接在既有的通道下面;原本的端到端 AES-GCM 通道保留在 TLS 之內(深度防禦)。每次重連都是新的握手。**限制**:內層 AES-GCM 的金鑰仍來自靜態 ECDH(沒有前向保密,但它外面有 TLS);Relay 仍看得到流量大小與時間;**macOS 的 TLS 沒有在真實 Mac 上驗證過**(.NET 在 macOS 當 TLS 伺服器的支援需在實機確認);Windows 上每條連線會暫時建立一個金鑰容器(連線結束即刪除,當機可能殘留在使用者設定檔);TLS 實作本身是平台提供的,不在本專案的審查範圍。
10. **外部安全審查**:第三方程式碼稽核與滲透測試。

### P2 — 發行品質
11. Unix 私鑰只靠 0600 檔案權限(未整合 libsecret / kernel keyring)。
12. 套件未用真實憑證簽章(簽章腳本已備妥);Docker 與 systemd 未實際驗證;Windows 桌面程式 MSI 已有,Agent/Linux/macOS 無安裝程式。
13. 一般使用者用的客戶端設定檔(規劃中的 yaml 範例)尚未提供;~~客戶端 `ISignalingClient` 補齊其餘操作~~(已完成)。
14. 真實網路與真實 NAT 環境的測試;長時間 soak;fuzzing。
15. ~~關鍵幀要求接上 Control 通道訊息~~(已完成)。~~適應性畫質控制器接上實際網路量測~~(已完成:接上 `KeepAliveService` 量到的真實往返延遲;封包遺失與頻寬在這個以 TCP 為底的傳輸層本來就量不到,前者固定視為 0、後者以設定的頻寬上限頂代,因此實際上是延遲驅動,預算低於門檻時也會連動降級——這是誠實的折衷,不是假量測)。

## 4. 測試統計(最近一次完整執行)

| 專案 | 通過 | | 專案 | 通過 |
|---|---|---|---|---|
| Integration | 233 | | Performance | 23 |
| Server.Persistence(其餘 35 個需要 PostgreSQL,沒有時略過,見 TESTING.md) | 6 | | | |
| Transport | 78 | | Core | 21 |
| FileTransfer | 68 | | Nat | 10 |
| Security | 53 | | Protocol | 24 |
| Streaming | 108 | | Signaling | 2 |
| Input | 26 | | Clipboard | 26 |
| Server.Application | 24 | | Platform.Windows | 52 |
| Client | 83 | | Agent | 21 |
| UI | 70 | | UI.Avalonia | 47 |
| Platform(跨平台;10 個 Linux/macOS 專屬測試在 Windows 上略過) | 159 | | | |
| **合計** | **1134**(另 45 個略過) | | **失敗** | **0** |

- 完整執行約 1–2 分鐘(含效能測試與約 14 秒的碼率控制閉環測試)。
- 建置:0 警告、0 錯誤。
- 已知的測試涵蓋缺口見 [TESTING.md](TESTING.md) §4。

## 5. 已確認可靠的部分

- 授權模型:沒有經過目標核准不可能進入 `Connected`;所有授權決定是原子的;已結束的 Session 不可復活。
- 內容保護:Relay 只看到密文;重放、竄改、反射、跨通道搬移都被擋下;重連後舊流量不可重放。
- 資源:所有接收不可信輸入的元件都有上限;Session 到期由伺服器強制;記憶體不會無限增長。
- 設定:不合法的設定使伺服器啟動失敗;正式環境全站強制 TLS。
- 效能:軟體本身的處理成本很低(微秒到個位數毫秒),大檔案是真正的串流。

## 6. 建議的下一步

1. ~~金鑰指紋驗證~~、~~金鑰輪替~~(已完成);決定「代替遺失私鑰的裝置宣告它作廢」的設計(需要擁有者/帳號概念)。
2. ~~Agent 組裝~~、~~首版 UI~~(已完成)→ ~~短 ID~~、~~傳輸層自動重連~~、~~PostgreSQL 持久化(身分、稽核、Session、token)~~(已完成)→ ~~Windows 安裝程式與簽章流程~~、~~TCP 直連~~(已完成)→ 接著做:動態補償的影片編碼器、NAT 打洞。
3. 接上 Windows 的螢幕擷取、輸入注入、剪貼簿後端。
4. 整合真實影片編碼器。
5. ~~PostgreSQL 持久化與遷移~~(已完成)。
6. NAT 打洞(UDP/ICE)與 TURN(TCP 直連已完成)。
7. 外部安全審查,然後才重新評估是否 RELEASE READY。
