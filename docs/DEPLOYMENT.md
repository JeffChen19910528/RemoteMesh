# 部署

## 1. 範圍與限制

目前可部署的是**伺服器**(`RemoteDesktop.Server.Api`:信令 hub + Relay + `/health`)。Agent 是空殼、沒有 UI,所以沒有客戶端安裝程式。

伺服器**只能單實例執行**:Session、Token、設備登錄、在線狀態、限流、稽核都在記憶體(`Server.Persistence` 還沒有實作)。重啟會中斷所有進行中的 Session,多實例會各自維護不同的狀態而無法運作。容量規劃見 [PERFORMANCE.md](PERFORMANCE.md)。

## 2. 需求

- 執行環境:.NET 10 ASP.NET Core Runtime(自含式套件則不需要)
- TLS 憑證(正式環境強制 TLS)
- 一個可從客戶端連到的 HTTPS 連接埠

## 3. 打包

```powershell
./scripts/publish.ps1                                # 測試通過後產生 win-x64 與 linux-x64
./scripts/publish.ps1 -Runtime win-x64 -SelfContained -SkipTests
```

```bash
./scripts/publish.sh --rid linux-x64                 # Linux 上產生 tar.gz
```

輸出在 `artifacts/`:每個平台一個壓縮檔(`server/`、`agent/`、`deploy/`、`docs/`、`VERSION`),加上 `SHA256SUMS.txt`。版本號來自 `Directory.Build.props`。腳本會先建置並執行測試,失敗就不產生套件。

分發時請一併提供 `SHA256SUMS.txt`,並在部署前核對。

### 桌面程式安裝程式與程式碼簽章(Windows)

```powershell
./scripts/build-installer.ps1 -SelfContained                       # artifacts/RemoteMesh-<版本>-x64.msi(未簽章)
./scripts/build-installer.ps1 -SelfContained -Thumbprint <指紋>    # 憑證在目前使用者的憑證存放區(或硬體權杖)
./scripts/build-installer.ps1 -SelfContained -PfxPath cert.pfx     # PFX;密碼放環境變數 REMOTEMESH_PFX_PASSWORD
./scripts/sign.ps1 -Path <檔案> -Thumbprint <指紋>                 # 只簽章(需要 Windows SDK 的 signtool)
```

`build-installer.ps1` 用 WiX(`.config/dotnet-tools.json` 鎖定版本,自動還原)把 `RemoteMesh.exe` 包成**使用者層級**的 MSI:不需要系統管理員權限,裝在 `%LocalAppData%\Programs\RemoteMesh`,建立開始功能表捷徑,支援升級與解除安裝。已實際建置並在本機驗證靜默安裝(檔案與捷徑就位)與解除安裝(乾淨移除)。

簽章需要你自己向憑證機構取得的程式碼簽署憑證(憑證不在本專案內);給了憑證,腳本會簽署 exe 與 MSI(SHA-256、加時間戳記)並驗證簽章。沒給憑證就產生未簽章的套件,Windows SmartScreen 第一次執行會警告。伺服器套件(`publish.ps1`)與 Linux/macOS 套件目前不簽章。

## 4. 安裝

### Windows 服務

以系統管理員身分:

```powershell
./deploy/install-server.ps1 -SourceDir .\server -Urls "https://+:8443" -CertificateSubject "relay.example.com" -OpenFirewall
```

- 服務名稱 `RemoteMeshServer`,以低權限的虛擬帳號 `NT SERVICE\RemoteMeshServer` 執行,延遲自動啟動,失敗時自動重啟(5 秒、30 秒、60 秒)。
- 程式資料夾只授予該帳號讀取與執行;不寫入任何祕密。憑證由 `LocalMachine\My` 依 Subject 取用(該帳號需要私鑰的讀取權限)。
- `-WhatIf` 可在不變更任何東西的情況下列出所有步驟;`-Uninstall` 移除。
- 腳本相容 Windows PowerShell 5.1 與 PowerShell 7。

### Linux(systemd)

```bash
sudo ./deploy/install-server.sh --source ./server --urls http://127.0.0.1:5222
```

- 建立無登入權限的 `remotemesh` 系統帳號;程式檔屬於 root、只對服務群組可讀。
- 設定放在 `/etc/remotemesh/server.env`(0640,root:remotemesh)。
- systemd 單元啟用沙盒:`NoNewPrivileges`、`ProtectSystem=strict`、`PrivateTmp`、`PrivateDevices`、空的 capability 集、限制位址家族等。
- `--dry-run` 只列出步驟;`--uninstall` 移除。

### Docker

```bash
docker compose -f deploy/docker-compose.yml up --build
```

包含伺服器(非 root、唯讀檔案系統、移除所有 capability)與終止 TLS 的 nginx。把憑證放到 `deploy/certs/fullchain.pem`、`privkey.pem`。伺服器信任 compose 內部網路送來的轉送標頭。

### 安裝與打包的驗證狀態

| 項目 | 狀態 |
|---|---|
| `publish.ps1`(win-x64、linux-x64,框架相依) | **已實際執行**,產生套件與 SHA256SUMS |
| 發布出來的 Windows 伺服器 | **已實際啟動並驗證**:`/health`、SignalR 協商、正式環境拒絕純 HTTP、`AllowInsecureTransport` 開關、非法設定使啟動失敗 |
| `install-server.ps1` | 只做了語法檢查與 `-WhatIf` 乾跑;**尚未實際安裝成 Windows 服務**(需要系統管理員且會變更系統) |
| `install-server.sh` | 只做了語法檢查與 `--dry-run`;**尚未在 Linux 上實測**;systemd 單元也**未在真實的 systemd 上驗證** |
| `Dockerfile`、`docker-compose.yml` | **尚未建置或執行**(開發機的 Docker daemon 未啟動)。基底映像標籤、`$APP_UID` 與唯讀檔案系統設定請在你的環境驗證 |
| 自含式發布(`-SelfContained`) | 尚未執行(需要下載執行階段套件) |
| 桌面程式 MSI 安裝程式 | **已實際建置並驗證**靜默安裝與解除安裝;簽章流程需要你的憑證,腳本已備妥但未用真實憑證執行 |

**第一次在目標環境部署前,請先在測試環境走完整個流程。**

## 5. TLS 與反向代理

兩種方式擇一:

1. **Kestrel 直接提供 TLS**:用 `ASPNETCORE_URLS=https://+:8443` 與 `Kestrel__Certificates__Default__*`。不要設定 `ForwardedHeaders`。
2. **反向代理終止 TLS**(建議):參考 `deploy/nginx.conf.example`。必要條件:
   - 轉送 WebSocket 升級標頭、關閉緩衝、長逾時(連線可持續數小時)。
   - 以代理的真實觀察值**覆寫** `X-Forwarded-For` 與 `X-Forwarded-Proto`。
   - 伺服器設定 `RemoteMesh:ForwardedHeaders:Enabled=true` 並列出代理位址,否則所有用戶端看起來都來自代理,按位址的限流與鎖定會一次影響所有人。
   - **不要記錄請求標頭**(Relay token 在 `X-Relay-Token`)。

驗證:

```bash
curl https://relay.example.com/health          # {"status":"healthy","utc":"..."}
curl -i http://relay.example.com/hubs/signaling   # 經代理的純 HTTP 應被代理導向 HTTPS;若直接打到伺服器,回 400 "TLS required."
```

## 6. 維運

- **健康檢查**:`GET /health`(不需要驗證),適合負載平衡器/編排系統的探針。
- **日誌**:標準 ASP.NET Core 日誌;程式不記錄金鑰、token、OTP、內容。請依需求調整 `Logging:LogLevel`。
- **稽核**:目前只存在記憶體(上限 100,000 筆、不持久、不防竄改);要保留請在持久化完成前自行擷取。
- **升級**:停止服務、替換檔案、啟動(安裝腳本可原地升級)。升級會中斷所有進行中的 Session;請安排維護時段。
- **備份**:沒有需要備份的資料(全部在記憶體)。設定檔與憑證請自行備份。
- **容量**:每個 Session 的伺服器成本很低(Relay 轉送約數個百分比的單核心,見 PERFORMANCE.md);頻寬是主要限制,因為目前**所有流量都經過 Relay**(直連尚未實作)。以每方向 25 MB/s 的上限與預期同時 Session 數估算出口頻寬。

## 7. 部署前檢查表

- [ ] TLS 正常、HTTP 導向 HTTPS
- [ ] 若在代理後:`ForwardedHeaders` 已設定、代理覆寫轉送標頭、代理不記錄標頭
- [ ] 服務以低權限帳號執行
- [ ] 設定值已審視(特別是 `MaxTotalActiveSessions`、Relay 頻寬與位址限流)
- [ ] `SHA256SUMS.txt` 已核對
- [ ] 已閱讀 [SECURITY.md](SECURITY.md) §3 與 §5 的限制,並接受它們
