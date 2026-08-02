# RTX Sandbox：遊戲與引擎方向

## 定位

RTX Sandbox 是「RTX 體素建造與物理實驗平台」。玩家的循環不是活下去，而是：

```text
選材／選工具 → 建造或改造 → 啟用物理 → 調整時間與畫面 → 展示／保存 → 用 Mod 擴充
```

四個設計支柱：

1. 建造要比單格方塊快，區域、筆刷與剪貼簿是本體能力。
2. 破壞與結構物理必須能關閉，建築不應因實驗功能而失控。
3. 光線追蹤是展示核心，時間、材質、水、玻璃、金屬與發光體都服務於畫面實驗。
4. Mod 能強制覆蓋本體，但本體仍提供一套完整、好用、可存檔的創造工具。

## 本體範圍

本體負責：世界與存檔、玩家／自由相機、碰撞、方塊資料、核心建造工具、Undo/Redo、基礎物理、時間環境、渲染後端、UI、Mod 載入與錯誤隔離。

Mod 適合增加：特殊方塊、程序筆刷、材質／Shader、展示場景、進階物理規則、匯入匯出格式、遊戲化玩法。它們不是拿來補齊最基本的放置、選取或存檔。

## 編輯模型

目前採整格 voxel，所有操作最後都轉成 `(位置, 舊 ID, 新 ID)` 的變更集合：

- 單格：放置、擦除、替換、吸取。
- 區域：A/B 選取、填充、清除、條件替換。
- 剪貼簿：包含空氣的密集複製、貼上、旋轉、鏡像。
- 筆刷：球形增加、球形擦除、定高平整、爆破。
- 歷史：每個高階操作是一筆命令，最多保留 64 筆 Undo，Redo 在新操作後清空。

精確建造不會直接把同一 byte voxel 偽裝成自由旋轉物件。後續會把 Chunk cell 擴充成「整格材質或 micro-volume 參照」，先支援 1/2、1/4，再評估任意旋轉網格；這能讓碰撞、光追和存檔格式保持一致。

## 物理分級

- Off：預設，世界完全由編輯器控制。
- Simple：只有 `AffectedByGravity` 材質下落，適合沙、雪、礫石和 Mod 方塊。
- Full：從世界底層做六方向支撐連通檢查，與地基斷開的結構逐格落下。現階段是可預測的結構原型，不假裝是完整剛體求解器。

後續 Full 會加入連通元件、質量／強度、碎片剛體、休眠、每幀預算與 PhysX/Jolt 可選後端。物理造成的連續模擬不混入編輯 Undo；手動爆破本身可 Undo。

## 相機與展示

- 第一人稱：碰撞、重力、游泳。
- Flight：自由上下移動但保留碰撞。
- Noclip：穿牆勘景與大型建造。
- Cinematic（後續）：關鍵幀、樣條路徑、焦距、景深、曝光鎖與離線累積輸出。

展示模板規劃：水世界、金屬城市、玻璃森林、暗洞火把。它們應是可編輯的普通世界／內容包，不是寫死的特殊關卡。

## 世界 Preset

- Flat：自訂表層高度、表層／地下方塊、湖泊與植物，服務建築、Benchmark 和 Mod 測試。
- Natural：Auto 或鎖定七種生態，只生成地形、生態、植物、水系與山區洞穴，不生成任何人工建築。
- Pure Nature：以宏觀帶狀分區確保沙漠、草原、森林、竹林／濕地、雪地與山脈有連續過渡，並以連續噪聲河帶切過地形。
- Void：只有 9×9 測試平台、天空與一個底部光源。
- Showcase：本體提供水池、玻璃林、材質展台與 RGB Lighting Lab。

Preset 是世界的生成來源而非玩法規則。建立新世界前會保存現況，舊 Chunk 留在 `.bak`；preset、biome、seed、Flat 材質和生成開關則寫入第 4 版 Chunk 與 `level.json`。Mod 在每次新建／重新載入後都會重新收到 `WorldReady`，可以在自然世界上增加城市、道路或其他結構。

## 渲染

現有 OpenGL compute path tracer 是可工作的基準後端：材質反射、發光、天空日夜、透明／水體路徑、1–12 bounce 和 progressive accumulation。`IRenderBackend` 保留後續 DXR、Vulkan RT 或 OptiX 的替換點。

優先順序：

1. 穩定的水內／水外介質切換與多次折射。
2. 選取區域 overlay、編輯器 gizmo 與去噪。
3. 雨、雪、霧、濕表面與閃電環境參數。
4. 硬體 RT 後端與降級 raster/compute 後端。

## 材質分類庫

0.10 版內建 196 個定義／194 種可建造材質，分散在 Core、Natural、Wood、Building、Metal & Technology、Optical & Emissive JSON 文件。Palette 以 `category`、`tags`、顯示名稱與 namespaced key 搜尋，不依賴硬編碼 ID。

0.11 版把材質外觀提升為固定 16×16 程序圖集。每個材質有 top／side／bottom、逐像素 roughness 與高度法線；`TextureGenerator`、`TextureVariation`、`TextureSeed` 可由資料包或 Mod 覆蓋。自然材質的變化以 key、種子與座標確定，不會隨畫面幀數閃動；Concrete／Reference 等純色分類保持零雜訊。

創造模式使用 Creative Library，而不是生存背包：所有方塊無限、九格快捷欄儲存 namespaced key，並提供等角方塊預覽、搜尋、分類、收藏與最近使用。既有 `IInventory` 僅保留給舊存檔和 Mod 相容。

Natural v2 不生成生物或人工建築。地形由 domain warp、大陸度、侵蝕、山脊、溫濕度、河道與地質噪聲共同決定，地下使用花崗岩、石灰岩、板岩與火山岩分層並以 3D 噪聲挖掘洞穴。

每個 `BlockDefinition` 可描述 base color、metallic、roughness、emission、transmission、IOR、碰撞、流體、可替換性、重力、程序表面 pattern、sound set、particle effect、animation speed 與 Mod key。聲音／粒子欄位目前是可供 Mod 和未來效果系統讀取的 metadata；光學與程序表面參數已直接送入 path tracer。

世界資料與 GPU voxel texture 已升級成 16-bit，渲染材質表保留 4,096 槽，足以容納 1,000+ Mod 方塊。第 5 版存檔寫入 ushort voxel；第 1–4 版 byte voxel 載入時自動擴展並按 namespaced key 重映射。

## Mod 原則

- BepInEx 6 CoreCLR 由本體啟動；HarmonyX 可直接 patch。
- Registry 使用 namespaced key，`force: true` 允許 last-writer-wins 覆蓋。
- `IModContext` 提供 blocks、items、recipes、tools、world、events、event bus 和 service locator。
- `BuildToolDefinition` 可直接寫 `IVoxelWorld`；本體會監聽該次 callback 的 voxel events，合併成單一 Undo 命令。
- 舊的 item／recipe／survival 合約暫時保留為相容層，但不決定本體方向。

## 完整遊戲的最低標準

即使定位是平台，1.0 仍應做到：可建立／命名／複製世界；可找回存檔；大型建造不卡住主執行緒；有材質搜尋、分類與收藏；有選取預覽與操作提示；Mod 壞掉時能定位；設定可回復預設；至少四個示範世界；相機能輸出截圖／影片序列；無 RTX 硬體時有明確降級路徑。

不納入本體主線：飢餓、食物、耐久、怪物壓力、資源稀缺、木石鐵鑽科技線。Mod 可以自行建立這些玩法。
