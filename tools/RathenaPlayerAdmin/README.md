# rAthena Player Admin (.NET 8)

獨立的 rAthena 玩家資料管理後台，不修改 map/char/login server 核心。

## 功能

- 依角色名稱、Char ID、Account ID 搜尋角色
- 查看並修改人物基本素質：STR、AGI、VIT、INT、DEX、LUK
- 查看並修改四轉特性：POW、STA、WIS、SPL、CON、CRT
- 修改 Base/Job 等級、Zeny、素質點、技能點、特性點
- 查看身上裝備與背包 `inventory`
- 查看帳號倉庫 `storage`
- 修改物品數量、裝備位置、精煉、附魔等級、卡片、隨機詞條、綁定與到期時間
- 自動建立 `dotnet_admin_audit` 操作紀錄表
- 角色在線時禁止修改角色與背包，避免 char-server 回存覆蓋

## Windows 最簡單啟動方式

1. 安裝一次 `.NET 8 SDK`。
2. 進入 `tools/RathenaPlayerAdmin`。
3. 雙擊 `Start.cmd`。
4. 第一次啟動輸入 MySQL/MariaDB 位址、資料庫名稱、帳號及密碼。
5. 程式會自動啟動，並開啟 `http://127.0.0.1:5080`。

第一次設定會儲存在本機的 `local-settings.json`，此檔案已加入 `.gitignore`，不會提交至 GitHub。之後只要再次雙擊 `Start.cmd` 即可啟動。

若要重新輸入資料庫設定，刪除 `local-settings.json` 後再次執行 `Start.cmd`。

## 需要的程式

- `.NET 8 SDK`：目前以原始碼方式啟動，因此必須安裝。
- MySQL 或 MariaDB：通常就是 rAthena 已在使用的資料庫，不需另外再安裝第二套。
- Windows PowerShell：Windows 內建。
- 瀏覽器：Edge、Chrome 或其他瀏覽器皆可。

不需要另外安裝 Apache、Nginx、XAMPP、Node.js、PHP 或 Visual Studio。

## 手動啟動方式

```powershell
cd tools/RathenaPlayerAdmin
$env:ConnectionStrings__Rathena='Server=127.0.0.1;Port=3306;Database=ragnarok;User ID=YOUR_ADMIN_USER;Password=YOUR_PASSWORD;Allow User Variables=true;'
dotnet run --urls http://127.0.0.1:5080
```

## 安全建議

- 僅監聽 `127.0.0.1`，不要直接暴露到公網。
- 建立專用資料庫帳號，只授權 rAthena 資料庫必要表格。
- 正式環境應放在具登入驗證的反向代理後方。
- 修改前先確認角色離線；程式也會再次檢查 `char.online`。
- 建議修改前備份資料庫。

## 注意

目前介面顯示物品 `NameID`，尚未解析 `db/re/item_db.yml` 或 `db/pre-re/item_db.yml` 的物品名稱。後續可加入 YAML 索引及物品圖示。
