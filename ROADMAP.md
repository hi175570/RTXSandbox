# RTX Sandbox Roadmap

## 0.13 — Engine Architecture（目前版本）

- [x] `IRenderBackend` 能力模型與資料驅動 catalog
- [x] 安全切換與設定持久化的 `RenderBackendHost`
- [x] Compute Path Tracing、PBR Forward、Classic 三個可用後端
- [x] Hardware RT／Deferred 顯示為鎖定規劃項，不冒充完成
- [x] Mod API 拆出 Blocks、World、Rendering、Physics
- [x] `IRenderBackendService`、`IVoxelPhysicsService` 穩定服務
- [x] Graphics 設定區開始移出主遊戲檔案
- [ ] Chunk ownership、dirty-region mesh job 與 streaming
- [ ] Generator pass／biome／climate／river 分層
- [ ] 獨立 FluidCell 資料層與流動模擬
- [ ] BepInEx loader adapter 獨立專案

## 0.12 — Precision Building（已完成）

- [x] Sparse micro-volume；支援 1/2、1/4、1/6、1/8、1/16、1/32、1/64 與精確混合網格
- [x] 微方塊放置、拆除、材質取樣、獨立 Undo/Redo、UI 與熱鍵
- [x] 精確 ray hit、玩家碰撞與結構支撐
- [x] GPU 二層 DDA + AABB path tracing
- [x] Save format 6 微幾何序列化與 namespaced material remap
- [ ] 任意角度旋轉、斜面與 transform gizmo
- [ ] 動態剛體化微結構；目前微結構作為靜態支撐幾何
- [ ] 無限 chunk streaming；目前仍是 64×32×64 單區域

## 0.8 — Creative Editor Core（已完成）

- [x] 移除本體生存循環與生存 HUD；保留舊 API／存檔相容欄位。
- [x] 完整方塊庫，不限數量放置、擦除、替換與材質吸取。
- [x] A/B 區域選取、填充、清除、條件替換。
- [x] 複製、貼上、Y 旋轉、X 鏡像。
- [x] 球形增加／擦除、平整與可撤銷爆破。
- [x] 64 筆命令式 Undo／Redo。
- [x] 第一人稱、碰撞飛行、noclip。
- [x] 六面連通支撐、局部 Grid 剛體、連續重力／旋轉／碰撞／休眠（取代逐格落下）。
- [x] 網格歸屬轉移工具；限制為座標對齊且六面相鄰。
- [x] 可選衝擊斷裂初版，預設關閉。
- [x] 高速剛體自適應子步、Grid sweep-and-prune 寬相、實心結構碰撞外殼裁剪。
- [x] 衝擊斷裂保留實體碎塊，不再直接刪除受撞方塊。
- [x] 正式切換為 BEPUphysics 2：Chunk 靜態碰撞體、Grid 動態 compound、CCD、接觸流形、摩擦與休眠。
- [x] BepInEx 自訂 Build Tool API 與強制覆蓋。
- [x] 時間循環與 GUI 調時、1–12 bounce 渲染設定。
- [x] Flat／Natural／Pure Nature／Void／Showcase 世界建立介面與版本化生成 metadata。
- [x] 草原、森林、竹林、沙漠、雪地、山地、沼澤、河帶、植物與洞穴原型。

## 0.9 — World Presets（已完成）

- [x] Flat、Natural、Pure Nature、Void、Showcase。
- [x] 七種可鎖定生態與 Auto 氣候分配。
- [x] Flat 分層材質、湖泊、植物與高度選項。
- [x] Pure Nature 宏觀過渡、河帶與洞穴。
- [x] 建立前備份、重新出生、Mod WorldReady 重發與第 4 版存檔 metadata。

## 0.10 — Material Library（完成）

- [x] 196 個資料驅動定義／194 種可建造材質。
- [x] Nature、Wood、Building、Metal、Technology、Optical、Lighting、Special 分類。
- [x] Palette 分類過濾、名稱／key／tag 搜尋與 PBR tooltip。
- [x] Transmission／IOR 與 Tiles、Panels、Circuit、Grid、Rainbow、Hologram、Cloud、Lava、Checker、Fabric 程序表面。
- [x] 4,096 槽 16-bit Registry、R16UI GPU voxel texture、第 5 版存檔與舊 byte 世界遷移。

## 0.11 — Procedural Materials & Creative Library（目前版本）

- [x] 所有內建與 Mod 材質共用真正的 16×16 程序圖集，包含 top/side/bottom、逐像素 roughness 與高度法線細節。
- [x] 自然材質以 namespaced key、材質種子與方塊座標做確定性變化；純色材質可保持零雜訊。
- [x] Creative Library 使用等角 3D 方塊預覽、搜尋、分類、收藏與最近使用。
- [x] 九格快捷欄可配置並以 namespaced key 持久化；數字鍵、滾輪與中鍵吸取共用同一套選取邏輯。
- [x] 有黑色外框的情境準星，可辨識放置、移除、選取與碰撞阻擋，並可在設定調整。
- [x] Natural v2：domain warp、多噪聲大陸度／侵蝕／山脊／氣候／河道、地質分層、3D 洞穴與群落式植被。

## 0.12 — Chunk World & Presentation

- [ ] Chunk 串流、背景生成、dirty mesh/texture 更新與大型世界座標。
- [ ] 多世界瀏覽器：命名、複製、匯出，以及刪除前備份（單一工作區的建立／種子／模板已完成）。
- [ ] 選取範圍線框、筆刷 ghost preview、transform gizmo。
- [ ] 方塊收藏、最近使用與可重排快捷列（搜尋與分類已完成）。
- [ ] 將現有綜合 Showcase 拆成水世界、金屬城市、玻璃森林、黑暗洞穴四個獨立大型模板。
- [ ] 雨、雪、霧、濕表面、雷電與環境 preset。
- [ ] 快速截圖、無 UI 截圖與高樣本離線輸出。

## 0.13 — Precision & Physics

- [ ] Chunk 內 micro-volume 儲存：1/2 與 1/4 voxel。
- [ ] Micro-voxel 選取、碰撞、複製貼上和光追交集。
- [ ] 結構連通元件、材質強度、質量、斷裂與休眠。
- [ ] 大型 Grid compound shape 的分區重建與每幀形狀烹調預算；保留未來 PhysX 後端介面。
- [ ] 爆炸衝擊、碎片壽命、粒子和音效。

## 0.14 — Cinematic & Renderer Backends

- [ ] 固定鏡頭、關鍵幀、樣條路徑、焦距與景深。
- [ ] 時間／天氣軌道與鏡頭序列存檔。
- [ ] 時域／空間去噪、可調 accumulation lock。
- [ ] DXR 或 Vulkan RT 後端；compute 與 raster 降級後端。
- [ ] Mod shader／renderer extension 的能力與安全邊界。

## 1.0 — Platform Release

- [ ] 穩定的 world/content/mod 格式與遷移器。
- [ ] Mod 管理器、錯誤隔離、安全模式、相依版本 UI。
- [ ] 藍圖匯入匯出與可分享展示世界。
- [ ] 大型世界壓力測試、GPU 相容矩陣和無 RTX 降級體驗。
- [ ] 內建教學，以建造與展示任務引導工具，但不加入生存壓力。
