# RTX Sandbox Roadmap

## Hardware DXR emissive visibility（2026-08-06）

| Item | Status | Evidence |
|---|---|---|
| Emissive blocks leaking through opaque walls | **Completed** | Each contributing Hardware DXR emissive light now traces a visibility ray to the emitter. Opaque geometry rejects the contribution; transparent surfaces remain transmissive. |
| Raster/Deferred emissive shadowing | **Not changed** | This round addresses the reported Hardware DXR light leak only. |

## Surface glass material set（2026-08-06）

| Item | Status | Evidence |
|---|---|---|
| Existing colored glass | **Already existed / verified** | Clear, blue, red, green, yellow, purple, smoked, diamond and prismatic glass were already present and were not duplicated. |
| Existing frosted glass | **Already existed / enhanced** | The existing ID 31 material now has explicit transmission plus a frosted color/height/roughness generator; no duplicate block was retained. |
| Matte, mosaic, acrylic, ribbed and etched glass | **Completed** | Five new data-driven blocks use distinct transmission, roughness, IOR and procedural color/height/roughness textures. |
| Shared renderer detail | **Completed** | Raster uses roughness-controlled refraction blur, Compute PT applies atlas normal detail during transmission, and Hardware DXR reads per-material IOR plus atlas normal detail. |
| Camera-obscura / pinhole projection | **Not completed** | Current Hardware DXR is direct/reflection/refraction rendering, not a light-transport projection solver. Compute PT may transport indirect light but has no dedicated stable projection benchmark yet. |

## Reflection termination and bounce limits（2026-08-06）

| Item | Status | Evidence |
|---|---|---|
| RTX over-limit mirror layer | **Completed** | At the configured limit no additional reflection ray is emitted. Reflective candidates are ignored by Any-Hit so the same ray continues to real geometry/sky behind them, avoiding black output, fake lighting and the mirror's white base color. |
| Compute Path Tracing maximum bounces | **Completed** | Settings, shortcut cycle and compute shader loop now permit 1–50 bounces. |
| Hardware RTX maximum reflections | **Partially completed** | Safe, fully DXR-lit recursive limit is 29. D3D12 declares a hard maximum TraceRay recursion depth of 31; 50 needs a separate non-recursive wavefront DXR implementation. |

## 草方塊側面貼圖方向（2026-08-06）

| 項目 | 狀態 | 驗證證據 |
|---|---|---|
| 草方塊側面草帶上下顛倒 | **已完成** | Procedural atlas 改為在側面 V 的上端產生草帶；self-test 直接比較側面頂端與底端色彩，防止回歸。 |

## Hardware RTX 多重反射（2026-08-06）

| 項目 | 狀態 | 驗證證據 |
|---|---|---|
| 鏡面反射追蹤 DXR 世界 | **已完成** | Closest Hit 可遞迴發出反射射線；次級命中也執行 DXR 陰影與材質光照，不再只呈現無陰影的局部光照。 |
| RTX 反射次數設定 | **已完成** | Hardware RTX 設定新增 1～12 次，預設 3 次；DXR Pipeline 最大遞迴深度為 14。 |
| 打蠟模式反射強度 | **已完成** | 有效反射混合提高為原效果 1.7 倍，非金屬材質仍保留底色。 |
| RTX 5050 實機驗證 | **已完成** | RTX 5050 Laptop GPU、DXR Tier 1.1；Hardware smoke 實際走到第三次反射的像素 53,582，112 frames，OpenGL 顯示 readback 通過，無 fallback。 |
| 更高次數效能與最終畫質調校 | **尚未完成** | 允許 4～12 次，但 GPU 成本會上升；本輪未進行各反射次數的完整效能基準。 |

## 本輪：120 FPS 串流／編輯／Physics Grid 優化（2026-08-05）

| 項目 | 狀態 | 驗證證據 |
|---|---|---|
| Raster mesh／GPU upload | ✅ **【已完成－本輪優化】** | CPU vertex／greedy mask 使用 pooled buffer、移除 VBO upload 的 `ToArray()`，並合併重複 dirty；比較 run 的 upload max `35.03 → 1.19 ms`，配置量約 `1.54 GB → 1.18 GB`。 |
| Static collider／Physics Grid | ✅ **【已完成－本輪優化】** | collider shape 背景建立、玩家／Grid 鄰近駐留、逐幀 eviction、full-cell cuboid greedy merge、單 Box body、adaptive CCD、低 body count 單執行緒及最多 2 個 120 Hz catch-up step 均已接入。79/79 self-tests 通過。 |
| Compute streaming | ✅ **【已完成－ring V1】** | Camera 跨一個 Chunk 時只更新 3D texture 新 edge，不再搬移整個 `64×576×64` window；最高品質比較 run 的 upload max `201.4 → 50.5 ms`、Boundary `209.4 → 64.4 ms`。 |
| DXR dynamic update | ✅ **【已完成－scratch cache V1】** | dynamic BLAS refit scratch 重用並移除重複 idle wait；RTX 5050 strict smoke 實際 Trace Rays 91 frames、Tier 1.1、無 fallback。 |
| 所有指定操作不低於 120 FPS | 🟡 **【部分完成－尚未達標】** | 固定唯讀 V5 壓力世界實測：Classic `107.4 FPS / 1% low 15.6`、Compute 最高品質 `10.1 / 0.4`、Hardware DXR `111.8 / 23.2`；操作尖峰仍超過 8.33 ms，故不標完成。詳細階段數據與後續瓶頸見 [ROADMAP_OPTIMIZATION.md](ROADMAP_OPTIMIZATION.md)。 |

### 本輪追加修正

- ✅ **【已完成】** DXR 串流期間取消過期 static geometry job 不再呼叫 `ThrowIfCancellationRequested()`，也不把 cancellation token 交給 Task scheduler。背景工作以 `null` 結果安靜淘汰，避免正常 Chunk invalidation 觸發 `System.OperationCanceledException`／Visual Studio first-chance 中斷。隔離 Debug 與正常 Release 建置皆 0 warning／0 error、79/79 self-tests 通過；RTX 5050 Hardware DXR 雙 7×7 streaming smoke 實跑 163 frames、60,308 triangles、4 次 AS build 且無取消例外、無 fallback。

## 本輪：視窗啟動不搶焦點（2026-08-05）

| 項目 | 狀態 | 驗證證據 |
|---|---|---|
| 啟動視窗不搶前台 | ✅ **【已完成－本輪】** | OpenTK 視窗使用 `StartFocused = false`，初始化時未取得焦點便不抓取滑鼠；使用者點擊後仍可正常取得焦點。Win32 前台句柄探針完整監測 Classic smoke：啟動前／結束後前台 PID 均為 `24276`，RTX Sandbox PID `20340` 從未成為前台，smoke 正常渲染 100 frames 並以 exit code 0 結束。Debug／Release 皆為 0 warnings、0 errors。 |

## 本輪：世界串流與全引擎效能工程（2026-08-05）

詳細的固定世界、1／5／10／20／50／100 Chunk、六 Renderer Before／After、驗證證據與未完成限制已集中記錄於 [ROADMAP_OPTIMIZATION.md](ROADMAP_OPTIMIZATION.md)。

| 項目 | 狀態 | 摘要 |
|---|---|---|
| 固定可重複效能測試 | ✅ **【已完成－本輪】** | baseline 已升級至唯讀 v5；Cold／Warm-up／Measured；同世界包含 100-Chunk、10 秒 route、100 place/break、256-cell Physics Grid、Region scratch 與 JSON／Markdown 完整 metrics。Benchmark 忽略實體鍵盤且不能保存 baseline。 |
| 世界串流主執行緒尖峰 | ✅ **【已完成－V1 可運作】** | 可重用且逐提交授權的低優先 worker、Chunk 所有權直接移交、背景 Raster mesh、集中式 count-based `FrameWorkBudget`、allocation-free collider scan 與背景 autosave 已接入。六 Renderer 的 20-Chunk 隊列皆完整消化且無一秒 freeze。 |
| Raster 幾何／記憶體 | ✅ **【已完成－本輪】** | 不透明方塊與連續水頂面使用安全 greedy merge；Classic 同條件配置量 -59%、GPU mesh/upload 約 -84%，六條 smoke 證明陰影／透明／水與 framebuffer 未消失。 |
| 全部附件架構 | 🟡 **【部分完成】** | ✅ Compute ring／edge upload 已完成。❌ 正式多階段 Job DAG、可調 ms/byte budget、Region dirty payload merge、Section＋Palette、遠距 unload、DXR per-Chunk/per-Grid BLAS 與非同步 readback尚未完成；均未以介面或 TODO 假標完成。 |

## 本轮：新区块载入卡顿再修正（2026-08-05）

用户实测确认上一轮「新区块生成卡死」仍然存在，因此原本的完成标记已更正为部分完成；本轮以当前 Repository 重新检查后，确认地形计算虽已在 worker，但主线程与 Renderer 仍有以下实际热点。

| 项目 | 状态 | 当前实现与验证证据 |
|---|---|---|
| worker 结果提交 | ✅ **【已完成－本轮】** | 背景生成完成后直接把完整 `VoxelChunkStore` 所有权移交给 live world；不再先转成数万笔 sparse voxel，再于 update thread 逐格复制进第二个 16×576×16 dense Chunk。原有取消、版本隔离和每帧最多提交一 Chunk 保持不变。 |
| Raster Chunk／Grid 网格建立 | ✅ **【已完成－本轮】** | Classic、Forward、Deferred、Hybrid 的 box-face 输出改为 bit mask 与 allocation-free corner 计算；移除每方块反复建立的 face jagged arrays、normal／UV／index arrays 与捕获 lambda。Chunk CPU meshing 现使用带一格边界的不可变快照在单一背景 job 建立，主线程只做每帧最多一个完成 VBO upload；版本检查可丢弃编辑／邻接变化后的过期结果。材质、UV、法线、透明面与选择资料不变。 |
| DXR 静态／动态几何 CPU 建立 | ✅ **【已完成－本轮】** | Hardware RT 使用同一套 allocation-free face 输出与只读材质 lookup；静態普通方塊進一步用同材質、同平面的 greedy face 合併，UV 仍逐格重複，micro／水／透明保持原語義。每 Chunk 的 CPU greedy 結果已有版本化 cache，BLAS 改採 `PreferFastBuild`。標準 strict 場景由 `116,498` 降至 `1,772` triangles，最終 static／refit `5.49／5.56 ms`，命中／陰影／反射／透明／framebuffer 證據不變。Natural 雙 7×7 window 為 `60,532` triangles，最終一次 static／refit `49.42／0.79 ms`。 |
| 實際 Natural 新區塊流程 | ✅ **【已完成－本輪自動實機驗證】** | 新增 streaming smoke：先完成 Natural 7×7 window，再橫移 8 Chunk 進入完全未載入的第二個 7×7 window，等待生成、Raster 背景 mesh／upload 或 DXR AS 更新全部完成。生成改由獨立 `BelowNormal` worker 執行，避免與 render thread 爭搶。RTX 5050 最終結果：Classic 最大／平均 frame delta `13.86／8.61 ms`，Deferred `17.03／8.56 ms`；Hardware DXR `115.14／11.04 ms`，且完成正式 Trace Rays 與 framebuffer 驗證。 |
| 完整消除長距離探索尖峰 | 🟡 **【部分完成】** | Raster 的實際雙 window 流程已沒有原本 0.5～1 秒級主執行緒 terrain copy／meshing 卡死；但 Hardware DXR 仍有一次約 `115.14 ms` 尖峰，來源是同步 build 單一靜態 BLAS，再以 D3D12 readback 回傳 OpenGL。每 Chunk BLAS instance、非同步 readback、time-based GPU upload budget 與遠距 unload 仍為後續項目，不假標為完成。 |

## V1 現況稽核（2026-08-04）

這一節是後續工作的優先比對表。開始修改前仍須用目前程式碼或測試確認；不得只因這裡曾記載問題就重複修改。

狀態標記：

- ✅ **【已完成－本輪】**：本輪新增，且已經建置、測試或實際啟動驗證。
- ✅ **【已完成－原有】**：檢查時原本就可用，本輪只驗證，沒有重新實作。
- ♻️ **【舊問題已不存在】**：Roadmap 舊敘述已不符合目前程式，已依現況修正。
- 🟡 **【部分完成】**：已有正式可用基礎，但仍缺少列出的 V1 要求。
- ❌ **【未完成】**：目前沒有正式可用實作；不可用 Placeholder 或假選項標成完成。

## 本輪：15 項已重現小缺陷打磨（2026-08-05）

本節只列修改前已由目前程式路徑、回歸案例或 GPU smoke 證實的問題；沒有把排版偏好、重構建議或未來功能算進 15 項。

| # | 修改前確認到的實際問題 | 狀態與本輪結果 |
|---:|---|---|
| 1 | 玩家快速移動／傳送後，背景 worker 仍會生成舊位置整圈 Chunk。 | ✅ **【已完成－本輪】** Generation window 改為可取消的 concurrent live set；新中心會剔除舊範圍，worker 對失效 request 直接跳過。 |
| 2 | 已在主執行緒同步生成的 queued Chunk 仍顯示 pending，worker 之後還會送回重複結果。 | ✅ **【已完成－本輪】** `EnsureChunkGenerated` 會取消同座標 queued request；完成佇列也只接受仍在 live set 的結果。新增 cancellation／pending-count 回歸測試。 |
| 3 | 刪除 Chunk 最高／最低方塊會重掃 147,456 個 cell；snapshot copy 與地表查詢也固定掃完整 576 層。 | ✅ **【已完成－本輪】** 每 Y layer 維護 occupied count，邊界收縮只掃 576 個 layer counter；linear/window copy 與 surface/top query 限制在實際垂直 bounds。Regular／micro 交替邊界測試通過。 |
| 4 | Compute PT 每收到一個新 Chunk 都重傳完整 `64×576×64` voxel window。 | ✅ **【已完成－本輪】** 攝影機 window 不變時只上傳生成 Chunk 的 `16×576×16` 子區；跨 window 才完整重傳。 |
| 5 | 世界重載後，舊 BEPU static／dynamic body 要等後續 frame budget 才逐步移除。 | ✅ **【已完成－本輪】** `WorldReloaded` 立即清除舊世界 bodies、shape 與 accumulator，再排新世界 rebuild；測試在 `Step()` 前即確認 body count 為 0。 |
| 6 | BEPU 每個 physics frame 都逐 element 重算 Physics Grid topology hash，儘管已有 `TopologyVersion`。 | ✅ **【已完成－本輪】** Dynamic body signature 直接使用正式 `TopologyVersion`；移動 Grid 不再 O(element count) 重雜湊。 |
| 7 | 玩家剛放置／修改的碰撞 Chunk 會排在串流建立的大量 static Chunk 後方，數幀內沒有新碰撞。 | ✅ **【已完成－本輪】** voxel／micro edit 使用 urgent dirty queue，仍維持每幀一 Chunk budget，但玩家編輯優先；三 Chunk backlog 回歸測試通過。 |
| 8 | Deferred 會在實際 edited mesh 尚未輪到 rebuild 時先消耗 shadow dirty，之後留下舊陰影。 | ✅ **【已完成－本輪】** Shadow dirty 改在 mesh upload 真正完成時提交；有既有 shadow 時等所有可見 dirty mesh 完成才更新。 |
| 9 | Chunk streaming 每提交一個 Chunk 就重畫一次 2048² static shadow map。 | ✅ **【已完成－本輪】** 首張 map 建立後保留上一張，串流 dirty mesh 清空才合併更新；strict smoke 的 static shadow update 由逐 Chunk 更新收斂為初始＋最終共 2 次。 |
| 10 | Classic／Forward 把透明與實心幾何混在同一 depth-writing pass；Deferred 透明 pass 也仍寫 depth，Chunk 順序未按距離。 | ✅ **【已完成－本輪】** Raster 改為 opaque depth pass＋back-to-front transparent pass；透明 pass 關閉 depth write，Deferred forward transparency 同步套用。 |
| 11 | Raster／DXR 對所有 Physics Grid 固定使用 12 格 culling radius，大型結構的一部分仍在畫面時可能整體消失。 | ✅ **【已完成－本輪】** Grid 依全部 element 與質心維護真實 bounding radius，兩條 culling／shadow-caster 路徑共用；48 格寬 Grid 回歸測試通過。 |
| 12 | Deferred 中 Grid 每次物理移動都遍歷全部 element 重新找 emissive material。 | ✅ **【已完成－本輪】** 依 Grid ID＋`TopologyVersion` 快取 local emissive lights；移動時只轉換真正發光點的位置。 |
| 13 | Hardware DXR 的 static BLAS 完全沒加入 micro voxel，micro 編輯也沒有 invalidation event。 | ✅ **【已完成－本輪】** Static micro boxes 正式建立 DXR triangles 並訂閱 cell／reload invalidation；RTX 5050 strict smoke 實測 36 micro triangles。 |
| 14 | DXR 動態 refit 每次重做 exposed-face topology、掃全靜態世界找燈、重建／重配 TLAS 與 emissive buffer。 | ✅ **【已完成－本輪】** Grid local geometry／lights 依 topology cache；靜態燈隨 static build cache；emissive upload buffer 循環使用；TLAS 正式以 `ALLOW_UPDATE + PERFORM_UPDATE` 原地更新。Strict smoke：3 dynamic refits、2 TLAS updates，最後 refit 4.77 ms。 |
| 15 | DXR 水仍逐格取樣不連續 atlas 與 height detail，Raster 接縫修正沒有套到 Hardware RT。 | ✅ **【已完成－本輪】** fluid Closest Hit 使用連續水色與材質 roughness，略過逐 cell height gradient；折射、Fresnel、透明命中與水下霧保持正式 DXR 路徑。 |

驗證：79/79 self-tests；Release 主專案與 solution 0 warning／0 error；RTX 5050 Laptop GPU 上 Classic、Forward、Deferred、Hybrid、Compute PT、Hardware DXR 全部 smoke 通過。DXR 使用 D3D12 Debug Layer：Tier 1.1、116,498 triangles、36 micro triangles、96 Grid triangles、2 static builds＋3 dynamic refits、2 TLAS updates，static/refit 55.83/4.77 ms，framebuffer average 217.83、range 13–220。Deferred／Hybrid static shadow updates 為 2，shadow depth `0.76623–1.0`，非黑 framebuffer 通過。

## 本輪：Infinite World／Region Storage（2026-08-05）

| 項目 | 狀態 | Repository 現況與驗證 |
|---|---|---|
| 無限水平世界 | ✅ **【已完成－本輪可運作版】** | X／Z 不再由 `64×64` 限制；`ChunkCoordinate` 使用負數安全的 floor division，玩家周圍以半徑 3 Chunk、Seed 決定性背景惰性生成。自動測試已比較 `(-41, 73)` 遠距 Chunk 的兩個同 Seed 世界，內容完全一致。 |
| 世界垂直範圍 | ✅ **【已完成－本輪】** | 世界座標正式為 Y `-64..511`（上界 512 exclusive）；一般地形／Flat surface 最高按需求限制在 256，但資料、編輯、碰撞與保存可使用完整高度。ModAPI 新增 `MinimumY`、`MaximumYExclusive` 與 `IsInside`。 |
| 32×32 Chunk Region 存檔 | ✅ **【已完成－本輪】** | Directory save 升級為 format v4；每個 `.region` 最多包含 32×32 個 16×16 Chunk，普通 voxel、micro voxel、負座標與空白但已生成 Chunk 都在同一容器。三個不同 Chunk 的測試只產生一個 Region；正負 Region 邊界、材質 ID remap、損壞 active Region 後整代 backup recovery 均通過。舊 v3 每-Chunk目錄與 v1–v7 單檔仍可載入並於下次保存遷移。 |
| Renderer／Physics 接入 | ✅ **【已完成－本輪可運作版】** | Raster 使用實際 Chunk 高度 bounds 與 sparse snapshot 建 mesh；Compute PT 改為跟隨玩家的 64×576×64 GPU window；DXR 從當前可見／ray-neighborhood Chunk 建立世界幾何，動態 Grid 仍走獨立 BLAS refit；BEPU 靜態碰撞由 sparse column 合併建立。RTX 5050 實測 Classic 74 visible／22 culled、Compute PT 3 幀通過；DXR Tier 1.1 跑 16 幀、419,300 triangles、518,400 hit pixels、dynamic refit 1 次與 framebuffer readback 通過。 |
| 程式／檔案結構整理 | ✅ **【已完成－本輪相關範圍】** | 新增獨立 `RegionCoordinate`；Chunk 儲存集中處理世界 Y→local Y 轉換與 sparse snapshot；Build Tool／Voxel Physics 改用 `VoxelPoint` key，移除無限座標下會碰撞的舊固定 64×64 linear key；刪除已停用的逐 Chunk 寫檔器。沒有重構無關子系統。 |
| 遠距卸載／背景生成 | 🟡 **【部分完成－背景生成已完成】** | ♻️ **【舊問題已不存在】**：玩家跨 Chunk 不再同步生成整圈；單一背景 worker 會連續生成，主執行緒每幀最多提交一個完成結果，普通 `Get()` 也不再暗中生成。❌ 已走過的 Chunk 暫不卸載，GPU upload budget、LOD 與遠距 unload 仍未完成。 |

驗證：Release build 0 warning／0 error；Debug 主程式與 ModAPI 使用獨立輸出（因使用者正在執行的 Debug 遊戲鎖住原輸出）0 warning／0 error；目前完整套件 79/79 self-tests 通過；RTX 5050 上六條 Renderer GPU smoke 均通過。

## 本輪：Chunk／放置／水面／DXR 卡頓修正（2026-08-05）

| 項目 | 狀態 | Repository 現況與實機證據 |
|---|---|---|
| 新 Chunk 生成卡死 | 🟡 **【部分完成－2026-08-05 再驗證】** | 地形 noise／洞穴／植被由獨立低優先級 worker 執行；本輪移除 live world 的逐 voxel 重複製、Raster／DXR 每方塊臨時陣列，並完成 Raster 背景 meshing。Natural 雙 7×7 實測 Raster 最大 frame delta 已降至 `17.03 ms`；Hardware DXR 仍有 `115.14 ms` 單體 BLAS 尖峰，因此整體不標記為全部完成。 |
| 放置方塊卡頓 | ✅ **【已完成－本輪可運作版】** | Raster 每幀最多重建一個 dirty Chunk，且只讀該 Chunk 的 micro voxels；BEPU 每幀最多重建一個靜態 Chunk。Deferred 不再因任何方塊修改重掃全世界發光方塊，改為每 Chunk light cache，普通方塊只更新 mesh／shadow，發光變更只掃受影響 Chunk。 |
| Raster 水面方塊接縫 | ✅ **【已完成－本輪】** | Classic／Forward／Deferred transparency／Hybrid 的靜態水只建立最高層雙面 top surface，不再 alpha 疊加每格四側／底面；同類 fluid 內部面一律剔除，水色與波紋改為連續 world-space 模型，不再逐格重複取樣不連續 atlas。DXR 靜態水同樣只保留最高 top surface。 |
| Physics Grid 渲染負載 | ✅ **【已完成－本輪可運作版】** | Raster 與 DXR 共用「只保留 Grid 外露面」規則，完整相鄰 cell 的內部面不再上傳。實際使用者存檔診斷由 465,408 Grid triangles 降至 31,176（約 -93%）；固定 strict 場景為 96 triangles 且 145,898 Grid-hit pixels，證明不是把 Grid 隱藏。 |
| Hardware DXR 切換／轉視角／更新 | 🟡 **【部分完成－本輪顯著修正】** | 移除建構期與第一幀重複的完整 BLAS/TLAS；品質 1–3 使用方向無關的 235 格 ray neighborhood，轉視角不再改 mask；串流／放置變更合併後才重建，且動態 refit 不再誤清靜態 dirty flag。Grid topology／燈與每 Chunk CPU greedy geometry 已快取，TLAS 使用正式原地 update；Hardware backend 切走後保留快取。RTX 5050 final strict smoke：`1,772` triangles、2 static builds＋3 dynamic refits＋2 TLAS updates，static／refit `5.49／5.56 ms`。❌ 同步 D3D12 readback→OpenGL upload、每 Chunk／每 Grid 獨立 BLAS 與純 transform TLAS-only instance 仍是後續瓶頸。 |

## V2-A（物理、RTX、暫停與背包）

| 項目 | 狀態 | 本輪結果／仍有缺口 |
|---|---|---|
| 1. Physics Grid 擠壓玩家 | ✅ **【已完成－本輪再次降速】** | 防卡推出維持 8 個水平安全方向，但固定降為每秒 2.4 格；完全移除 Physics Grid 表面速度加成，因此高速撞击不会把防卡逻辑变成额外冲量。60 FPS 每帧最多 0.04 格，静止与 120 格／秒 Grid 的测试步长完全相同；连续推出仍可解除重叠且不向上瞬移。 |
| 2. RTX 水材質 | ✅ **【已完成－本輪】** | 水／Transmission 幾何正式進入 DXR BLAS；Closest Hit 執行 Fresnel 反射、簡化折射、Albedo tint 與水色吸收，攝影機在水中時依命中距離套用水下霧。RTX 5050 一般場景 40,889 個透明主射線命中；水下場景 242,674 個透明命中且 framebuffer range 13–238。 |
| 3. RTX 發光方塊 | ✅ **【已完成－本輪】** | 世界與 Physics Grid 的發光材質建立最多 32 個 DXR emissive lights；Closest Hit 依距離、半徑、法線與能量照亮附近表面，不再只有自發光材質。一般 smoke 實際上傳 7 個光源，水下展示場景上傳 32 個。 |
| 4. RTX 效能 | 🟡 **【部分完成－本輪更新】** | 靜態世界與 Physics Grid 已拆成兩個 BLAS／TLAS instance；同拓撲 Grid 使用正式 `ALLOW_UPDATE`＋`PERFORM_UPDATE`。Grid exposed topology、靜／動發光候選、每 Chunk CPU greedy geometry 與 upload capacity 已快取；TLAS 現在也正式原地 update。RTX 5050 final strict smoke 為 `1,772` total／96 Grid／36 micro triangles、2 static builds＋3 dynamic refits＋2 TLAS updates、static／refit `5.49／5.56 ms`。Natural 串流仍有 `115.14 ms` 單體靜態 BLAS 尖峰。❌ 每 Chunk BLAS、純 transform TLAS-only instance、非同步 D3D12/OpenGL interop 與大型 benchmark 仍未完成。 |
| 5. ESC 與暫停選單 | ✅ **【已完成－本輪】** | ESC 開啟獨立暫停選單；再次 ESC 先成功存檔才關閉；切到 Windows 背景會自動開啟暫停並釋放滑鼠。✅ 後續修正：ImGui 的一般 `WantCaptureKeyboard` 不再攔截全域 ESC，設定／模組／物品欄都可關閉；只有真正進行中的 Windows IME composition 優先取得 Escape。 |
| 6. 背包 UI 外觀 | ✅ **【已完成－本輪】** | 保留 ImGui 與原功能，改為置中的方塊／物品面板、Minecraft 風格深灰底、粗暗邊框、較方正元件、獨立內容區與一致間距。✅ 物品欄取得 ImGui keyboard focus 後仍可用 ESC 或原開關鍵 E／I／Tab 關閉；文字搜尋欄正在輸入時不會誤把 E／I 當關閉鍵。 |

## V2-B（選單、世界管理、Renderer 與 IME）

| 項目 | 狀態 | 本輪結果／仍有缺口 |
|---|---|---|
| 7. 暫停選單排版 | ✅ **【已完成－本輪】** | 固定 8 個按鈕：頂部大按鈕「繼續遊戲」、中間 2×3「設定／成就／統計／模組／語言／按鍵」、底部大按鈕「離開世界」。✅ 模組已從設定 tabs 移出，暫停選單的「模組」會開啟獨立 BepInEx／插件設定畫面；設定與模組畫面都以「完成」返回原選單。離開世界會存檔並回到世界選單，不會退出程式。 |
| 8. 世界管理 | ✅ **【已完成－本輪】** | 世界選單新增開啟、同 Seed／名稱／可調生成設定的覆蓋重新生成、只修改 display name 的重新命名，以及二次確認刪除；目前世界不可刪除。資料層測試驗證重建清除玩家修改但保留 Seed／Preset、rename 不改 storage／Seed、刪除拒絕路徑逃逸並移除目錄。 |
| 9. 所有 Renderer 基本優化 | 🟡 **【部分完成－本輪 Physics／Shadow 快取】** | 原有 1.6× 保守視錐維持不變。Raster Physics Grid 改為局部座標靜態 VBO＋每幀 model matrix，只有 `TopologyVersion` 改變才重傳網格；Deferred／Hybrid 把完整靜態世界陰影與動態 Grid 陰影拆成兩張 2048² depth map，Grid 移動不再重畫 16 個靜態 Chunk。DXR 拆出靜／動 BLAS 並對同拓撲 Grid 做 refit。❌ 每 Chunk BLAS、occlusion culling、LOD、indirect/multi-draw 與大型世界 benchmark 留後續。 |
| 10. 新 Renderer | ✅ **【已完成－本輪】** | 在 Deferred 與 Compute PT 之間新增真正可選的 **Hybrid PBR Raster（混合式 PBR 光柵）**：Deferred PBR opaque／G-Buffer lighting＋forward water/glass，固定至少品質 2，包含 shadow、SSR、HDR、Bloom、水與透明。RTX 5050 strict smoke feature mask 1023、12 direct-visible＋4 offscreen shadow-caster Chunk、非黑 framebuffer 通過。 |
| 11. 中文輸入法 | 🟡 **【部分完成－等待人工組字確認】** | 取得焦點時以 `ImmAssociateContextEx(IACE_DEFAULT)` 恢復 Windows IME context；composition／candidate 座標完整轉成視窗 client space，排除區也不再混用 screen Y；文字輸入期間遊戲鍵盤控制保持停用。自動 IME callback、多語 Unicode 與鍵盤 ownership smoke 通過。🟡 自動測試無法替代使用者實際切換微軟注音並選字，仍需在本版執行檔做最後人工確認。 |

| 需求 | 狀態 | 目前可比對結果／下一個缺口 |
|---|---|---|
| 基本遊戲流程 | ✅ **【已完成－本輪放置距離】** | 啟動、世界進入、第一人稱移動、重力／跳躍、碰撞、放置／破壞、準星、選取框與安全保存均有正式流程。本輪統一普通、精密與 Physics Grid 放置規則：眼睛到命中面的距離小於 1.1 格一律拒絕，剛好 1.1 格允許；準星預覽與實際提交使用同一判定。 |
| 方塊、材質與內容資料 | ✅ **【已完成－本輪】** | 原有 namespaced ID、Fallback、Base Content 與 16-bit Registry 已可用；本輪新增 `ExamplePack`，實際載入 `examplepack:cyan_lamp` 並由測試驗證。 |
| 世界與 Chunk | 🟡 **【部分完成－本輪背景串流】** | ✅ 16×16 Chunk-owned 資料已支援負 X／Z 與 Y -64..511；玩家周圍半徑 3 Chunk 由單一 worker 背景生成，32×32 Chunk 合併成單一 Region 檔。❌ 遠距 unload／重新載入與 LOD 尚未完成。 |
| 世界生成 | ✅ **【已完成－本輪可運作版】** | Seed、確定性地形、地質、洞穴、植被與跨長距離 Pure Nature 氣候帶可按 Chunk 在背景生成，正常地形限制在 -64..256；live world 每幀最多提交一個完成 Chunk。79 項測試包含 queued generation 非同步提交、舊 window cancellation 與最終內容驗證。 |
| Chunk Mesh 與基本效能 | 🟡 **【部分完成－本輪背景 meshing】** | Raster camera pass 已按 Chunk 建 mesh、剔除遮蔽面、分離透明幾何；Chunk CPU meshing 已迁移到单一背景 job，主线程每帧最多上传一个版本匹配结果；box-face allocation 与每 voxel Registry lock 已移除。Physics Grid 只上傳外露面與拓撲變更，BEPU 每幀只重建一個靜態 Chunk。DXR 静态方块已使用 greedy face。❌ byte/time GPU upload budget、Raster Greedy Mesh、远距 unload 與長時間大型世界壓力驗證尚未完成。 |
| Renderer 架構與切換 | ✅ **【已完成－本輪】** | Compute Path Tracing、PBR Forward、Classic、Deferred、Hardware DXR 保持可用；Renderer 選單依序加入正式的 Hybrid PBR Raster，位於 Deferred 與 Compute PT 之間並通過 GPU smoke。 |
| Renderer Capability／圖形設定 | 🟡 **【部分完成－本輪有進度】** | ✅ Deferred 已有 Renderer／G-Buffer／Lighting／Shadow／Reflection／AO／Water／Transparency／Fog／Post 品質，以及 HDR、Bloom、Tone Mapping、Exposure、10 種 Debug View。✅ Hardware RT 的打蠟開關已整理成 Render Style：Default／Wax／Reference／Debug。❌ Deferred Cascade Shadow／SSGI／OIT／Clustered 與 DXR 折射／GI／降噪仍是後續。 |
| 水 | ✅ **【已完成－本輪更新】** | 游泳／上岸輸入規則維持已完成狀態。本輪修正所有 Raster 的方塊水接縫：只畫最高層 top surface、剔除 fluid 內部面、使用連續 world-space 水色／波紋；DXR 靜態水也只保留最高表面。水下 framebuffer average 181.10、range 13–238。 |
| 玩家碰撞與移動 | ✅ **【已完成－本輪】** | 固定碰撞體、連續 Grid 推出與固定物理步保持不變；本輪將水中岸邊抬升改為明確受跳躍輸入控制，避免前進鍵把高牆當成自動階梯。 |
| Physics Grid | ✅ **【已完成－本輪渲染效能修正】** | 獨立剛體、平移／旋轉、BEPU compound collision、質量／重心、固定時間步、睡眠、raycast／選取及 Grid 放置保持可用。Raster／DXR 現在只建立 Grid 外露面；使用者存檔診斷 Grid triangles 由 465,408 降到 31,176，strict 場景 96 triangles／145,898 hits。DXR 動態 refit 不再誤清放置方塊造成的 static AS 更新。撞擊二次斷裂仍列後續。 |
| 細分／Micro Build | 🟡 **【部分完成】** | 普通整格破壞、精密編輯、raycast、碰撞、渲染、Undo 與保存已完成。❌ Micro-volume 位於旋轉動態 Grid 的完整局部座標編輯／保存尚未完成。 |
| 存檔與世界管理 | ✅ **【已完成－本輪 Region V4】** | 多世界管理與原子 generation／backup recovery 保持可用；每個 Region 現合併 32×32 Chunk，支援負座標與 Y -64..511，並保留舊 v3 目錄與舊單檔遷移。 |
| Mod 與 ModAPI | ✅ **【已完成－本輪診斷補強】** | 原有獨立 `RTXSandbox.ModAPI` 與 BepInEx loader 已可用；未載入 DLL 現在會檢查 target framework 與 BepInPlugin／BasePlugin 入口，終端和模組頁會顯示明確原因。現有 `SoftBlockPhysicsMod.dll` 已確認為 .NET 9 且不含插件入口，因此不是可載入模組；Example Mod 仍可正常載入。 |
| UI 與操作入口 | 🟡 **【部分完成－本輪修正】** | ✅ 獨立 8 鍵暫停選單、ESC 存檔關閉、背景自動暫停、Minecraft 風格背包與世界管理入口均已接線。✅ 模組從設定移到暫停選單的獨立畫面；設定／模組使用「完成」返回原選單。✅ 全域關閉鍵與 ImGui keyboard capture 已拆開：設定可用 ESC，物品欄可用 ESC／E／I／Tab；只有文字欄輸入與實際 IME composition 會保留輸入鍵。🟡 真正 Windows 中文 IME 的人工組字選字仍需使用者確認。 |
| Shader 與錯誤回退 | 🟡 **【部分完成】** | Shader 編譯錯誤、Renderer 建立失敗保留舊 Renderer、缺少能力不顯示假後端及 GPU 資源釋放已有基礎。❌ 運行中 Shader Reload／保留上一版成功 program 的完整操作入口尚未完成。 |
| Debug、測試與效能資料 | 🟡 **【部分完成－本輪更新】** | 79 項自動測試全部通過；新增 generation cancellation、occupied bounds、urgent collision 與大型 Grid bounds。RTX 5050 上 Classic、Forward、Deferred、Hybrid、Compute PT、Hardware DXR 與水下 framebuffer smoke 全通過。❌ 尚缺長時間無限探索壓力測試與完整 GPU timing／benchmark。 |
| 專案、依賴與輸出 | ✅ **【已完成】** | `RTXSandbox.exe` 位於 `bin/<Configuration>/net6.0/` 輸出根目錄；遊戲與相依 DLL、原生 runtime 集中於 `Managed/`，根目錄沒有 DLL。VS／MSBuild 的 `RunCommand` 與偵錯工作目錄也已指向輸出根目錄，不再指向 `Managed`。Shaders、Content、ContentPacks、settings 與 BepInEx 留在外層，Example Mod 已從 `BepInEx/plugins` 實際載入。Debug／Release 建置與根層啟動均已驗證。 |

### 本輪驗證基準

- Debug：使用者目前執行中的 `RTXSandbox (PID 1460)` 鎖住標準 Debug DLL／plugin deployment，因此沒有關閉使用者遊戲；主程式與 ModAPI 改用獨立輸出完整 Build，Example Mod 以獨立輸出 `Compile` target 驗證，三者 0 警告／0 編譯錯誤。標準 solution post-build copy 仍須關閉該執行中遊戲後才能覆寫 plugin。
- `dotnet build RTXSandbox.sln -c Release --no-restore`：成功，3 個專案，0 警告／0 錯誤。
- `--self-test`：79/79 通過；包含 queued Chunk 非同步提交／取消、occupied bounds、urgent collision rebuild 與大型 Grid bounds。
- Direct3D 12 DXR V1、Compute、Deferred、Hybrid PBR Raster、PBR Forward、Classic GPU smoke：最終版全部通過且都等待 49 個測試 Chunk 串流完成。Classic／Forward 為 43 visible／6 culled。
- Deferred／Hybrid strict GPU smoke：feature mask 1023、16 point lights、1 spot light、35 shadow draws、shadow depth 0.76569–1.0、49 visible Chunk 與非黑 framebuffer 通過。
- DXR strict GPU smoke：Tier 1.1、`1,772` triangles、96 Grid／36 micro triangles、147,826 Grid-hit pixels、2 static builds＋3 dynamic refits＋2 TLAS updates，static／refit `5.49／5.56 ms`；shadow／reflection／transparent／textured hits 與 framebuffer average 217.83、range 13–220 全通過，拒絕 fallback。
- Natural streaming smoke（兩個完整 7×7 window）：Classic 最大／平均 `13.86／8.61 ms`，Deferred `17.03／8.56 ms`，Hardware DXR `115.14／11.04 ms`；DXR `60,532` triangles、static／refit `49.42／0.79 ms`，實際 Trace Rays 與 framebuffer 通過。
- 暫停選單（8 controls）、設定、獨立模組畫面、背包、主選單、世界建立與水下 framebuffer smoke：全部通過。Shortcut smoke 額外讓物品欄／設定先取得 ImGui keyboard focus，再驗證 E／ESC 的共用實際關閉分支成功。
- `--gpu-smoke-ime`：提交中／日／韓／泰／西里爾文字串、觸發 Win32 IME 候選位置 callback，並驗證文字欄位取得鍵盤所有權；此項不再記成真正 OS 中文組字的替代證據。
- `--mod-diagnostics`：BepInEx 實際載入 Example Mod，並準確回報 `SoftBlockPhysicsMod.dll` 目標 .NET 9、高於遊戲 .NET 6，且缺少 BepInPlugin／BasePlugin 插件入口。

## Renderer Pipeline 設定矩陣（逐項比對基準）

這一節定義每條 Pipeline 自己應擁有的完整設定頁；即使名稱重複也不視為跨 Pipeline 共用畫面。`✅` 代表目前確實接入，`🟡` 代表底層已有部分能力但設定尚未齊全，`❌` 代表尚未實作，禁止做成看似有效的假選項。

### Classic（Compatibility）

- ✅ Display：Resolution Scale、VSync、FPS Limit；Camera：FOV；Lighting：分級光照、Gamma／曝光。
- 🟡 Display：Resolution、Fullscreen、Borderless；Camera：Near／Far Clip、View Distance。
- ❌ Shadow：開關、解析度、距離；Rendering：Backface Culling、Depth Test、Wireframe、Face Culling；Post Process：Tone Mapping、Bloom、Fog；Debug：GPU／CPU time、Draw Calls、Chunk Border。

### Forward PBR

- ✅ Display：Resolution Scale、VSync、FPS Limit；Camera：FOV；Lighting：曝光；PBR：Albedo、Metallic、Roughness、Emission、Specular、Transmission、IOR；Water：Reflection、Refraction、Transparency。
- 🟡 Lighting：Sun、Ambient、Gamma；PBR：Normal／AO 由程序材質提供，但尚無獨立品質控制；Reflection／Specular 已有 0–3 品質設定。
- ❌ Resolution／Fullscreen、Near／Far Clip、LOD、Auto Exposure、HDR、Clear Coat、陰影／Cascade、Water Fog／Depth Color、Distance／Height Fog、Bloom／Tone Mapping／FXAA／SMAA／TAA／Sharpen／Vignette，以及完整 Debug views／timing。

### Deferred

- ✅ **【已完成－本輪】** Geometry：不透明 Depth Pre-Pass；五目標 G-Buffer 寫入 Position／Depth、Albedo＋Roughness、Normal＋Metallic、Emission（Alpha 保留材質 AO 通道）、Motion Vector；SSAO 於 Lighting Pass 由 Position／Normal 計算，動態 Physics Grid 與 Chunk Mesh 共用相同流程。
- ✅ **【已完成－本輪】** 共用材質：Raster 現在正式上傳並取樣與 Compute Path Tracing、DXR 相同的 `ProceduralMaterialAtlas.DetailPixels`，以逐像素 roughness 與 height gradient 產生法線細節；三條管線沒有各自維護 Normal Map 資料。
- ✅ **【已完成－本輪】** Lighting：GGX／Smith／Schlick PBR、Directional Sun、最多 16 個由世界發光方塊產生的 Point Light、攝影機 Spot Light、Ambient／Sky／Environment IBL；CPU 只保留能量最高的 16 燈並由半徑衰減剔除。
- ✅ **【已完成－本輪陰影與效能修正】** Shadow／Ambient／Reflection：修正建構後首次 G-Buffer Resize 誤刪 shadow FBO／texture 的資源生命週期錯誤；Directional Sun 使用 2048² light-space depth、opaque-only caster、法線 bias 與品質 1–3 PCF。靜態 Chunk 與動態 Grid 分成兩張 depth layer，shader 取兩者較近深度；Grid 移動不再重畫完整靜態世界。Point／Spot Shadow 目前仍是螢幕空間 contact shadow；SSAO、SSR 與天空環境反射維持原實作。
- ✅ **【已完成－本輪】** Water／Transparency：Deferred 不透明結果完成後，以 Forward Alpha pass 處理水與玻璃；會取樣 HDR Scene Color 做螢幕空間折射、天空反射、Fresnel、透明度與距離水霧。
- ✅ **【已完成－本輪】** Atmosphere／Post：RGBA16F HDR lighting target、Exposure、Reinhard／Filmic／ACES Tone Mapping、亮部鄰域 Bloom、Height＋Distance Fog、簡單 Color Grade 與 Sharpen。
- ✅ **【已完成－本輪】** Debug／設定：Final、Albedo、Normal、Depth、Metallic、Roughness、AO、Emission、Lighting、Motion Vector；Renderer／G-Buffer／Lighting／Shadow／Reflection／AO／Water／Transparency／Fog／Post 品質與 HDR／Bloom／Tone Mapping 均已接到底層。
- ✅ **【實機證據－本輪】** RTX 5050 Laptop GPU：Debug Deferred／Hybrid depth range 0.452230–1.0、feature mask 1023、Point／Spot Light 與非黑 framebuffer 通過；Release Deferred／Hybrid depth range 0.432853–1.0、17 caster draws、1–7 Point Lights、1 Spot Light 與非黑輸出通過。Smoke 直接讀回 shadow texture，不能再只靠 draw counter 假定陰影存在。
- 🟡 **【部分完成】** Directional Shadow V1 已是真正 shadow map，但仍是覆蓋 64×64 世界的單張正交投影，尚非 Cascade Shadow Map；Point／Spot 尚未建立 cubemap／spot shadow map。Environment Reflection 是天空 IBL，尚未建立可烘焙 Reflection Probe；Bloom 是單 pass 鄰域濾波。
- ❌ **【尚未完成】** SSGI、Cascade Shadow Map、多光源 shadow atlas、可放置 Reflection Probe、Alpha Cutout 材質資料、OIT、Volumetric Fog／Cloud、Tiled／Clustered Deferred、TAA／Motion Blur／DOF 與 GPU profiler。

### Compute Path Tracing

- ✅ Display：Resolution Scale、FPS Limit；Sampling：每幀 samples、累計上限；Ray：Max Bounce；Lighting：Sky、Environment、Direct shadow ray；Materials：Albedo、Metallic、Roughness、Emission、Glass、Transmission、IOR；Water：Reflection、Refraction、Absorption、Scattering、Underwater；Debug：累計 SPP。
- 🟡 Accumulation 已可用，但 Adaptive Sampling／獨立 Reflection、Refraction、Shadow Bounce／MIS／NEE／Blue Noise 尚未拆成設定。
- ❌ OIDN／NRD／Temporal／Spatial denoiser、Tile／Workgroup／Frame Budget，以及 Ray Count／Bounce Heatmap／GPU Time／Noise debug views。

### Hardware Ray Tracing（Direct3D 12 DXR V1）

- ✅ **【已完成－本輪】** Windows／高效能 DXGI adapter 選擇、D3D12 Device、DXR Tier 檢測、Command Queue／Allocator／GraphicsCommandList4／Fence 同步。
- ✅ **【已完成－本輪】** 由目前世界不透明可見方塊建立 triangle vertex buffer、BLAS、單 instance TLAS；VoxelChanged／WorldReloaded 後於下一幀安全重建。
- ✅ **【已完成－本輪】** DXC `lib_6_3`、Ray Generation／兩個 Miss／Any Hit／Closest Hit、DXR State Object、Global Root Signature、64-byte 對齊 Shader Table 與 `DispatchRays`。
- ✅ **【已完成－本輪】** Closest Hit 會依品質實際發出二次 DXR `TraceRay`：方向光硬陰影射線與一層材質反射射線；粗糙度／金屬度已隨世界頂點上傳，不再只是 GL 也能仿造的固定方向光著色。
- ✅ **【已完成－本輪】** Hardware RT 0–3 品質已接到真正射線預算；0 可只跑主射線降低負載，1 為主射線＋陰影，2 為陰影＋選擇性反射，3 為陰影＋廣泛反射。未實作的 denoiser 仍禁用且明確標示，不做假選項。
- ✅ **【已完成－本輪】** Hardware RT 的打蠟選項已整理為獨立 Render Style：Default、Wax Mode、Reference、Debug；Wax 仍倍增全部不透明材質反射率並限制非金屬混色，Reference 讓介電材質依 Fresnel 參與反射，Debug 顯示法線／粗糙度／金屬度。
- ✅ **【已完成－本輪】** DXR 現在把共用 `ProceduralMaterialAtlas` 的 Color RGBA8 與 Detail RG8 正式上傳為 D3D12 SRV；Closest Hit 依 material ID／top-side-bottom／UV 取樣與 Path Tracing、Raster 相同的 16×16 顏色、逐像素 roughness 與高度法線細節，不再只用純色 Albedo。
- ✅ **【已完成－本輪效能修正】** Physics Grid 與靜態世界已拆成各自的 BLAS／TLAS instance。相同頂點拓撲的 Position／Orientation 更新會覆寫動態 vertex buffer，使用 `ALLOW_UPDATE`＋`PERFORM_UPDATE` refit 動態 BLAS，再更新 TLAS；Grid 完整相鄰 cell 的內部面不再進 vertex buffer／BLAS，且 Grid 在 Hardware RT 仍可見。
- ✅ **【已完成－本輪】** 水與其他 transmission geometry 已正式進入 BLAS；primary Closest Hit 執行一層折射與 Fresnel 反射，水面使用較低 fluid height，水中攝影機使用命中距離霧化。一般／水下 strict smoke 分別量到 40,889／242,674 個透明主射線命中。
- ✅ **【已完成－本輪】** 發光方塊不再只輸出 emission：CPU 從靜態世界與 Grid 選出最多 32 個 emissive lights，上傳 root SRV；Closest Hit 以距離、半徑與 N·L 對鄰近材質加入直接光照。
- ✅ **【已完成－本輪修正】** DXR 品質 1–3 以 235 格、方向無關 ray neighborhood 保留 shadow／reflection／refraction 可達物件；品質 0 仍使用 camera culling。單純轉視角不再重建靜態 AS；串流／放置更新會 debounce，但最慢 1.5 秒仍正式更新。動態 refit 不再誤清 static dirty flag。
- ✅ **【已完成－本輪陰影修正】** DXR shadow ray 以未受材質高度擾動的幾何法線判斷受光面與偏移起點，Any-Hit 忽略極近自交並讓水／玻璃通過；剔除與 Trace 現在共用同一個日／夜主要光源方向，夜間不再一邊保留月光 caster、一邊把射線射向地平線下的太陽。
- ✅ **【已完成－本輪】** DXR UAV 離屏結果經 D3D12 readback 上傳至既有 OpenGL 全螢幕顯示；天空、共用材質紋理、高度細節、RTX 遮蔽陰影與受材質限制的反射已可辨識。
- ✅ **【實機證據－本輪 Physics 效能】** NVIDIA GeForce RTX 5050 Laptop GPU，DXR Tier 1.1 final strict smoke：116,498 triangles、96 Grid／36 micro triangles、147,826 Grid-hit pixels、2 static builds＋3 dynamic refits＋2 TLAS updates，最後 static/refit 55.83/4.77 ms；shadow 273,753、reflection 279,323、transparent 81,243、textured 360,566 hits，framebuffer average 217.83／range 13–220，拒絕 fallback。使用者大型 Grid 診斷的動態三角形另由 465,408 降至 31,176。
- 🟡 **【部分完成】** Hardware RT 可由 Renderer 選單選擇；初始化失敗會顯示原因並安全回退。靜態世界與動態 Grid 已分離且同拓撲可 refit，但 V1 仍是同步 D3D12 readback→OpenGL upload；尚未做到每 Chunk／每 Grid BLAS cache 與純 transform TLAS-only update。
- ✅ **【已完成－本輪】** 靜態 micro voxel 現在正式進入 static BLAS；cell edit／bulk reload 會使 DXR 幾何失效，strict smoke 實測 36 micro triangles。
- ❌ **【尚未完成】** 具顏色／能量衰減的透明介質陰影（目前水／玻璃安全放行）、軟陰影、粗糙多採樣反射、完整體積水散射、GI／AO、多重反彈、denoiser、temporal、DLSS／FSR／XeSS、共享 D3D12/OpenGL texture、每 Grid BLAS cache 與分 Chunk AS。

### Hybrid PBR Raster（高畫質光柵）

- ✅ **【已完成－本輪】** 正式 pipeline 名稱為 Hybrid PBR Raster／混合式 PBR 光柵，Renderer 選單順序位於 Deferred 與 Compute Path Tracing 之間。
- ✅ **【已完成－本輪】** 採用 deferred PBR opaque geometry／lighting／HDR post 加 forward water／glass；至少品質 2，包含 multi-light、shadow、SSAO、SSR、HDR、Bloom、tone mapping、水反射／折射／透明。
- ✅ **【實機證據－本輪】** RTX 5050 Debug／Release strict GPU smoke：feature mask 1023、1–7 Point Lights、1 Spot Light、17–18 caster draws；直接讀回的 shadow depth range 為 0.432853–1.0 至 0.452230–1.0，framebuffer 非黑且具變化。

### Voxel GI（未建立 Pipeline）

- ❌ Voxel Resolution／Size／LOD、Voxel GI／AO／Shadow、Update Budget／Cache／Chunk Upload、Visualization／Brick／Clipmap／Heatmap。

## 0.13 — Engine Architecture（目前版本）

- [x] `IRenderBackend` 能力模型與資料驅動 catalog
- [x] 安全切換與設定持久化的 `RenderBackendHost`
- [x] Compute Path Tracing、Deferred G-buffer、Hybrid PBR Raster、PBR Forward、Classic 五個非 DXR 可用後端
- [x] ♻️ Deferred 鎖定的舊記錄已失效；目前已有真正 Geometry／Lighting pass 並通過 GPU smoke
- [x] Hardware RT V1：Windows Direct3D 12 DXR Tier 1.0+，包含 BLAS／TLAS、RayGen／兩個 Miss／AnyHit／ClosestHit、Shader Table、主射線、二次陰影／反射、共用材質 atlas、靜態世界與 Physics Grid 幾何及 OpenGL 顯示
- [x] Mod API 拆出 Blocks、World、Rendering、Physics
- [x] `IRenderBackendService`、`IVoxelPhysicsService` 穩定服務
- [x] Graphics 設定區開始移出主遊戲檔案
- [x] 版本化 `settings.json`、原子寫入與 `.bak` 損壞恢復
- [x] 16×16 共用 Chunk 座標、邊界失效與 Raster 每 Chunk GPU mesh
- [x] Physics Grid 獨立動態 mesh；睡眠剛體不再使靜態世界每幀重建；移動 Grid 擠壓玩家時連續向水平安全方向推出
- [x] Micro-voxel 帶座標變更事件，精確失效 render／physics Chunk
- [x] Regular／micro voxel 的 Chunk-owned 稀疏資料儲存與線性舊存檔快照邊界
- [x] 每 Chunk 稀疏存檔、整代原子提交／備份恢復與舊單區域存檔遷移
- [x] 主選單多世界瀏覽／切換、同名保護、同 Seed 覆蓋重建、display rename、二次確認刪除，以及預設簡潔、可展開模板的建立流程
- [x] 移除失效的 Physics Mode／生存設定；結構 Grid 保持為世界規則
- [ ] 背景 save／mesh job 與 streaming
- [ ] Generator pass／biome／climate／river 分層
- [ ] 獨立 FluidCell 資料層與流動模擬
- [x] 外部 Example Mod 獨立專案、BepInEx DLL 部署及 ModAPI 註冊驗證
- [ ] 將目前位於主程式的 `BepInExBootstrap` 拆成獨立 loader adapter 專案

## 0.12 — Precision Building（已完成）

- [x] Sparse micro-volume；支援 1/2、1/4、1/6、1/8、1/16、1/32、1/64 與精確混合網格
- [x] 微方塊放置、拆除、材質取樣、獨立 Undo/Redo、UI 與熱鍵
- [x] 精確 ray hit、玩家碰撞與結構支撐
- [x] GPU 二層 DDA + AABB path tracing
- [x] Save format 6 微幾何序列化與 namespaced material remap
- [ ] 任意角度旋轉、斜面與 transform gizmo
- [ ] 動態剛體化微結構；目前微結構作為靜態支撐幾何
- [x] 無邊界 X／Z 與玩家周圍惰性 Chunk 生成；遠距 unload／背景 job 仍列後續

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
- [x] 正式切換為 BEPUphysics 2：Chunk 靜態碰撞體、Grid 動態 compound、CCD、接觸流形、摩擦與休眠。
- [x] 120 Hz 固定時間步、兩步追幀上限與世界重載／釋放生命週期；避免長幀後連跑六次 simulation 形成卡頓螺旋。
- [ ] 以 BEPU 接觸事件實作撞擊二次斷裂；舊的未接線實驗選項已移除。
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
- [x] **【已完成－2026-08-06】**「全部」頁使用穩定的簡單分類排序；普通、彩色及特殊表面玻璃會連續排列，彩色材質採固定色序；新增的磨砂、霧面、馬賽克、壓克力、壓紋與蝕刻玻璃均顯示中文名稱。79/79 自我測試通過。
- [x] 九格快捷欄可配置並以 namespaced key 持久化；數字鍵、滾輪與中鍵吸取共用同一套選取邏輯。
- [x] 有黑色外框的情境準星，可辨識放置、移除、選取與碰撞阻擋，並可在設定調整。
- [x] Natural v2：domain warp、多噪聲大陸度／侵蝕／山脊／氣候／河道、地質分層、3D 洞穴與群落式植被。

## 0.12 — Chunk World & Presentation

- [x] Raster Chunk mesh ownership、面鄰接失效與動態 Grid mesh 分離。
- [x] Regular／micro voxel 由 Chunk 擁有，空 Chunk 自動釋放並相容既有存檔。
- [x] 每 Chunk 稀疏存檔、空世界零 payload、整代損壞恢復與第 1–7 版單區域遷移。
- [ ] 串流、背景生成／保存／meshing、texture 分頁與大型世界座標。
- [x] 多世界瀏覽器：命名、seed、列表、切換、模板建立、同 Seed 重建、重新命名與刪除。
- [ ] 世界複製、匯出，以及刪除前自動備份／資源回收筒。
- [ ] 選取範圍線框、筆刷 ghost preview、transform gizmo。
- [ ] 方塊收藏、最近使用與可重排快捷列（搜尋與分類已完成）。
- [ ] 將現有綜合 Showcase 拆成水世界、金屬城市、玻璃森林、黑暗洞穴四個獨立大型模板。
- [ ] 雨、雪、霧、濕表面、雷電與環境 preset。
- [ ] 快速截圖、無 UI 截圖與高樣本離線輸出。

## 0.13 — Precision & Physics

- [x] ♻️ 舊問題已不存在：Chunk 內 sparse micro-volume 已支援 1/2、1/4、1/6、1/8、1/16、1/32、1/64。
- [x] ♻️ 舊問題已不存在：Micro-voxel 選取、碰撞、複製貼上、保存和光追交集已有正式流程與測試。
- [x] 結構六面連通元件、依材質密度／體積計算質量與重心、支撐移除斷裂及 BEPU 休眠。
- [ ] 撞擊接觸造成二次斷裂；舊的未接線開關已移除，不冒充完成。
- [ ] 大型 Grid compound shape 的分區重建與每幀形狀烹調預算；保留未來 PhysX 後端介面。
- [ ] 爆炸衝擊、碎片壽命、粒子和音效。

## 0.14 — Cinematic & Renderer Backends

- [ ] 固定鏡頭、關鍵幀、樣條路徑、焦距與景深。
- [ ] 時間／天氣軌道與鏡頭序列存檔。
- [ ] 時域／空間去噪、可調 accumulation lock。
- [x] DXR V1 後端與一層 RTX 陰影／反射／折射、水下效果、emissive direct lights 與可見 Chunk AS；Compute 與 Raster 保持獨立降級後端。進階 GI／降噪與互通效能仍待後續。
- [ ] Mod shader／renderer extension 的能力與安全邊界。

## 0.15 — Manual Mod Workspace（本輪）

- [x] **【已完成－本輪】** 模組頁新增檔案工作區；任意副檔名只要實際內容是 UTF 文字（上限 4 MiB），右鍵即可開啟「編輯、編譯、移動、刪除」選單，不監看也不自動編譯。
- [x] **【已完成－本輪】** 手動編譯固定輸出 `net6.0` DLL，引用 .NET 6 SDK 系統參考、輸出 `Managed` 內相容組件，以及整個 `BepInEx/plugins` 內相容前置 DLL；native DLL 與高於 .NET 6 的組件不冒充可用參考。
- [x] **【已完成－本輪】** 直接放在 plugins 根目錄的散裝來源只在玩家按「編譯」後建立同名資料夾，來源與同名 DLL／PDB／deps 一起移入；已在子資料夾內的來源保持原位置，編譯不刪除或改寫來源。
- [x] **【已完成－本輪】** 內建純文字編輯器支援輸入、明確保存、撤銷未保存內容與回滾上次保存；保存前副本位於 `BepInEx/editor-backups`。移動拒絕越出 plugins 或覆寫同名檔，刪除需二次確認並移至可復原的 `BepInEx/trash`。
- [x] **【驗證證據－本輪】** Release solution 與隔離 Debug 編譯均零警告；目前完整套件為 79/79 self-tests 通過。模組編譯／回滾、物理、保存遷移、Infinite World／Region V4、generation cancellation、occupied bounds 與 urgent collision 都包含在同一套回歸測試中。
- [ ] **【尚未完成】** 執行中 DLL hot reload、多來源檔／自訂 `.csproj` 專案建置、NuGet 套件管理、相依版本求解、安全沙箱與可信簽章；目前新 DLL 仍需重新啟動遊戲，編譯端需要玩家電腦已安裝可建置 net6.0 的 .NET SDK。

## RTX Sandbox Optimization Roadmap（不降低畫質）

本節只收錄不降低解析度、畫質、Samples 或關閉效果的優化。狀態完全依目前 Repository 判定：`✅` 已有實際執行路徑、`🟡` 只有部分 Pipeline／基礎能力、`❌` 尚未實作。這是程式碼現況，不把「理論上由 Driver 處理」當成已完成，也不以建置成功冒充效能提升。

### 驗收護欄

- 同一場景、相同解析度／設定／Samples／效果，比較 CPU frame、GPU frame、Draw Call、triangle、mesh build、upload、BLAS／TLAS build／update 時間與記憶體峰值。
- 優化前後必須做固定攝影機 framebuffer 比對；允許浮點誤差，不允許可見方塊、陰影、反射、透明水或發光影響消失。
- Frustum／Occlusion／RTX instance culling 必須使用保守 bounds。對 DXR 而言，「主攝影機看不到」不代表可移除：仍可能被 shadow、reflection 或 refraction ray 命中。
- 背景工作只能建立 CPU 資料；OpenGL／D3D12 資源提交必須遵守各 API 的 thread／queue ownership，並設每幀 upload budget，避免把平均 FPS 換成卡頓尖峰。

### 一、CPU 端（1–10）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 1 | View Frustum Culling | 🟡 | `RasterVoxelRenderer` 對 Chunk／Physics Grid 使用 1.6× 保守視錐；DXR 品質 0 使用可見 mask，品質 1–3 為陰影／反射正確性保留全域 ray geometry；Compute PT 不使用 Chunk mesh。 |
| 2 | Occlusion Culling | ❌ | 沒有 Hi-Z、occlusion query 或 software occluder。必須先有可重現 benchmark，避免目前僅 16 Chunk 時查詢成本高於收益。 |
| 3 | Chunk Culling | 🟡 | Raster 已整個 Chunk 不送 draw；DXR 只有品質 0 在重建 monolithic BLAS 前略過 Chunk，尚非獨立 BLAS／instance culling。 |
| 4 | Backface Culling | 🟡 | Mesher 已不產生被相鄰不透明方塊遮住的內部面，但 Raster 明確 `GL.Disable(CullFace)`，尚未啟用真正 GPU backface culling。 |
| 5 | Greedy Mesh | ✅ | Raster 不透明 full blocks、連續同材質水頂面、DXR 靜態 full blocks 與 BEPU full-cell collider 都已使用對應的精確 greedy merge；Micro／透明幾何保留精確路徑。 |
| 6 | Mesh Cache | ✅ | Raster 以 `_chunkMeshes` 長期持有每 Chunk VBO，不會每幀重建所有 Chunk。 |
| 7 | Dirty Chunk Update | 🟡 | Raster／BEPU／Compute 只排入變更 Chunk 並合併重複 edit；Raster Grid 以 `TopologyVersion` 區分拓撲與 transform。DXR CPU geometry 有 per-Chunk cache，但任何靜態 topology commit 最終仍重建 monolithic static BLAS。 |
| 8 | Multi-thread Chunk Build | ✅ | Natural generation、Raster CPU mesh、BEPU collider shape 與 DXR static CPU geometry 均由版本化背景工作建立；render／simulation thread 只提交仍有效結果。 |
| 9 | Async Upload | 🟡 | CPU mesh build 與提交已拆開，Raster／Compute 每幀最多上傳 1 Chunk；但實際 OpenGL `BufferData`／`TexSubImage3D` 仍同步，尚無 persistent staging ring／fence。 |
| 10 | Physics Sleeping | ✅ | BEPU body 的 `Awake` 狀態會同步為 `PhysicsGrid.IsSleeping`，睡眠 Grid 不再持續發布 render invalidation。 |

### 二、GPU Raster（11–20）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 11 | Early-Z | 🟡 | 所有 Raster 使用 depth test；Deferred／Hybrid 有明確 opaque depth pre-pass。Classic／Forward 尚未提供可量測的 early-Z 專用流程。 |
| 12 | Depth Pre-pass | 🟡 | Deferred／Hybrid 已實作；Classic／Forward 尚無，加入前需以 overdraw 場景證明收益大於額外 Draw Call。 |
| 13 | GPU Instancing | ❌ | 目前每 Chunk／Grid 使用獨立 `DrawArrays`，沒有 instance buffer。 |
| 14 | Indirect Draw | ❌ | 沒有 `MultiDrawIndirect`／GPU-generated draw arguments。 |
| 15 | Bindless Texture | ❌ | 使用固定 texture units 與單一 atlas，沒有 bindless handle。 |
| 16 | Persistent Mapping | ❌ | VBO／SSBO 更新使用 `BufferData`，沒有 persistent mapped ring buffer。 |
| 17 | Texture Atlas | ✅ | 所有內建與 Mod 材質使用共用 `ProceduralMaterialAtlas` color／detail atlas。 |
| 18 | Material Batching | ✅ | material ID 隨 vertex 傳入，同一 Chunk 可在同一 draw 取樣 atlas，不需要逐材質切 shader／texture。 |
| 19 | State Sorting | ❌ | 沒有一般化的 pipeline/material state sort key。 |
| 20 | Render Queue | 🟡 | Deferred／Hybrid 已固定 opaque depth→GBuffer→lighting→forward transparency；尚無可擴充、可排序的通用 Render Queue。 |

### 三、Deferred／Hybrid（21–26）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 21 | Light Volume | ❌ | Lighting 仍為全螢幕 pass，沒有 sphere／cone light volume。 |
| 22 | Tiled Deferred | ❌ | 尚無 screen tile light list。 |
| 23 | Clustered Deferred | ❌ | 尚無 3D cluster／depth slice light list。 |
| 24 | Light Culling | 🟡 | CPU 只保留能量最高的 16 個 emissive point lights，shader 也做 radius rejection；但每個 pixel 仍循環全部入選燈，尚非 tiled/clustered culling。 |
| 25 | Shadow Cache | ✅ | 修正 Resize 誤刪 shadow texture/FBO；完整靜態世界與動態 Grid 使用兩張 2048² depth layer。世界／材質只失效 static layer，Grid transform 只失效 dynamic layer；光向變化以小於四分之一 shadow texel 的門檻合併。 |
| 26 | Reflection Probe Cache | ❌ | 目前是天空環境反射／SSR，尚未建立 Reflection Probe。 |

### 四、Hardware RTX／DXR（27–39）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 27 | TLAS 只更新 | 🟡 | Grid transform 現在保留 static BLAS、refit dynamic BLAS 並重建小型雙 instance TLAS；尚未做到純 transform 只更新 instance transform／TLAS refit。 |
| 28 | BLAS Cache | ❌ | 尚無每 Chunk／Grid BLAS cache。 |
| 29 | Static BLAS | 🟡 | 靜態世界已有獨立 BLAS，Grid transform 不再令它失效；尚未拆成每 Chunk cache。 |
| 30 | Dynamic BLAS | 🟡 | 所有 Physics Grid 使用獨立於世界的 dynamic BLAS，相同 vertex count 走 `PERFORM_UPDATE` refit；尚未拆成每 Grid BLAS。 |
| 31 | Instance TLAS | 🟡 | TLAS 已有 static world＋dynamic Grid 兩個 instance；尚未達到每 Chunk／每 Grid instance。 |
| 32 | Ray Distance Limit | ✅ | DXR primary／shadow／reflection／refraction `TMax=220`；Compute PT world trace 上限 140，沒有無限距離射線。後續可依世界 bounds 收緊而不裁掉可達幾何。 |
| 33 | Ray Cone | ❌ | 尚無 ray cone footprint／texture LOD。 |
| 34 | Ray Compaction | ❌ | 尚無 active-ray queue／prefix sum compaction。 |
| 35 | Shader Table Cache | ✅ | Shader Table 在 DXR backend 建立時產生一次，所有 frame／DispatchRays 重用。 |
| 36 | Pipeline Cache | 🟡 | DXR State Object 在 backend 生命週期內重用；尚無 D3D12 pipeline library／跨啟動磁碟 cache。 |
| 37 | AS Refit | 🟡 | Dynamic Grid BLAS 建立時啟用 `AllowUpdate`／`PreferFastBuild`，相同頂點數更新使用 `PerformUpdate`；RTX 5050 strict smoke Debug 9.24 ms、Release 5.92 ms。Static Chunk 與 TLAS 尚未 refit。 |
| 38 | Skip Invisible BLAS | 🟡 | 品質 0 會在建 monolithic BLAS 時略過保守視錐外 Chunk；品質 1–3 為避免再次破壞陰影／反射保留全場景。待每 Chunk BLAS 後，必須以 primary＋shadow＋reflection/refraction 可達集合決定 TLAS inclusion，不能只看主視錐。 |
| 39 | RTX Instance Culling | 🟡 | 現在只有 build-time Chunk mask／Grid bounds，尚無真正多 instance TLAS culling。 |

### 五、Compute Path Tracing（40–47）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 40 | Adaptive Sampling | ❌ | 每個 pixel 每輪固定取樣。 |
| 41 | Russian Roulette | ✅ | bounce > 0 後依 throughput survival probability 終止並補償權重。 |
| 42 | Importance Sampling | ✅ | diffuse 使用 cosine hemisphere；specular／diffuse 依 Fresnel／metallic 機率選擇並做 PDF 權重補償，另有直接方向光 shadow ray。這是基礎 V1，尚非 MIS。 |
| 43 | Blue Noise | ❌ | 使用 hash RNG，沒有 blue-noise texture／sequence。 |
| 44 | ReSTIR | ❌ | 尚無 reservoir sampling。 |
| 45 | Wavefront PT | ❌ | 目前是單一 compute megakernel。 |
| 46 | Ray Sorting | ❌ | 沒有依 material／direction／bounce queue 排序。 |
| 47 | Temporal Reuse | 🟡 | 已有跨 frame running-average accumulation；尚無 motion-aware sample／reservoir reuse，攝影機或世界變更會 reset。 |

### 六、Memory（48–53）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 48 | Texture Streaming | ❌ | atlas／voxel textures 一次常駐。 |
| 49 | Mesh Streaming | ❌ | Raster 為固定世界全部 Chunk 配置 mesh object。 |
| 50 | Chunk Streaming | 🟡 | 無邊界 X／Z、半徑 3 Chunk 背景生成、逐幀 commit、Raster background mesh、Compute ring edge upload 與 DXR background CPU geometry 已接入；遠距 world unload／Region read cache／LOD 尚未完成。 |
| 51 | GPU Upload Budget | 🟡 | Raster／Compute 已有每幀 1 Chunk count budget，`FrameWorkBudget` 也保存 byte／ms 目標；尚未依實測成本做跨幀 byte/time cutoff 或 staging fence。 |
| 52 | Arena Allocator | 🟡 | BEPU 使用 `BufferPool`，Raster vertex／greedy mask 使用 `ArrayPool`；DXR geometry 與一般遊戲資料尚無統一 frame／persistent arena。 |
| 53 | Object Pool | 🟡 | BEPU buffer/body 與 Raster mesh build arrays 會重用底層配置；遊戲與 UI 尚無通用 object pool。 |

### 七、Voxel（54–58）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 54 | Voxel Clipmap | ❌ | 尚無多層 clipmap。 |
| 55 | Sparse Chunk | ✅ | `VoxelChunkStore` 只建立非空 Chunk、空 Chunk 自動移除；存檔也只寫 loaded/non-empty Chunk。 |
| 56 | Sparse Voxel | 🟡 | micro voxel 使用 sparse box dictionary；regular voxel 在已配置 Chunk 內仍為 dense `ushort[]`。 |
| 57 | Chunk LOD | ❌ | 尚無 Chunk 幾何層級。 |
| 58 | Distance Mesh | ❌ | 尚無遠距簡化 mesh／impostor。 |

### 八、Shader（59–63）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 59 | Shader Variant Cache | 🟡 | 每個 backend 生命週期內 program/state object 會重用，OpenGL uniform location 也有 dictionary cache；尚無 variant key、binary cache 或跨啟動 cache。 |
| 60 | Specialization Constant | ❌ | OpenGL shader 使用 uniforms，DXR 使用 root constants；沒有 specialization constant。 |
| 61 | Branch Reduction | ❌ | 尚未有以 profiler／shader permutation 驗證的系統性 branch reduction，不把現有零星 early-out 算完成。 |
| 62 | LUT（例如 BRDF LUT） | ❌ | 尚無 BRDF LUT；材質 atlas 不等於 lighting LUT。 |
| 63 | Shared Material | ✅ | Raster、Compute PT、DXR 共用 `BlockDefinition` 與同一套 `ProceduralMaterialAtlas` color/detail 資料。 |

### 九、世界更新（64–68）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 64 | Update Budget | 🟡 | 結構分析每次最多 6 個 dirty seeds；Chunk／collider commit、Raster／Compute upload、autosave snapshot 均有 count budget；DXR Grid update 以 120 ms coalesce。尚缺可自適應的硬性 ms／byte cutoff。 |
| 65 | Save Budget | 🟡 | Autosave 每幀最多擷取 1 個 Chunk snapshot，背景單寫者處理 Region；手動明確保存仍會等待完整 generation 完成。 |
| 66 | Async Save | 🟡 | 自動保存與 benchmark Region write 已在背景單寫者執行；手動 `SaveWorld` 為維持離開世界的完成語意仍同步等待。 |
| 67 | Async Generate | 🟡 | 玩家周圍 Natural Chunk 使用可取消的 `BelowNormal` worker 與逐幀 commit；整個世界的管理介面 `Regenerate` 仍是明確同步操作。 |
| 68 | Async Lighting | ❌ | 沒有背景 lighting job；目前主要 lighting 是每幀 shader。 |

### 十、UI（69–70）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 69 | ImGui Dirty Update | ❌ | ImGui 每幀重建 draw data，controller 也每幀 `BufferData` 上傳；尚無靜態面板 cache／persistent UI buffers。 |
| 70 | Pause GPU | ❌ | 暫停選單會停止遊戲輸入／更新，但背景 3D Renderer 仍以完整 frame rate 渲染。後續可暫停累計、重用最後一張世界畫面並只重畫 UI，不改變返回遊戲後的畫質。 |

### 依目前程式碼重新排序的前十項

原清單中的 Frustum Culling、Chunk Culling、Mesh Cache、Dirty Chunk、Physics Sleeping、Texture Atlas、Shadow Cache、Russian Roulette、基本 Importance Sampling 與 Shader Table reuse 已全部或部分存在，因此不再把它們當成「從零開始」的前十項。下一輪建議依相依性安排：

1. DXR Static per-Chunk BLAS cache。
2. DXR Dynamic per-Grid BLAS。
3. Multi-instance TLAS＋只更新 transform／instance mask。
4. D3D12→OpenGL 非同步／共享資源，移除同步 readback。
5. GPU staging／persistent upload ring 與依 ms／byte 的跨幀 budget。
6. Raster MultiDraw／Indirect Draw，減少每 Chunk Draw Call。
7. Region dirty payload merge 與遠距 Chunk／mesh／AS unload。
8. sparse vertical Section＋Palette，降低生成／快照配置量。
9. 保守 RTX instance culling（包含 shadow/reflection/refraction ray 可達性驗收）。
10. Deferred Tiled Light Culling；Clustered Deferred 待大型燈光壓力測試後接續。

Compute PT 的 Adaptive Sampling、Blue Noise、Wavefront／Ray Sorting、Temporal Reuse／ReSTIR 是獨立後續階段，不與 DXR AS 重構混在同一輪。世界已突破固定 64×32×64 bounds；下一階段可在現有惰性 Chunk 基礎上實作 Texture／Mesh streaming、遠距 unload、Voxel Clipmap 與 LOD。

### 每條 Renderer 的 Optimization Profile

目前 Repository 實際有六條可選 Pipeline（原本五條之外已加入 Hybrid PBR Raster），Profile 以各自資料流為準，不做一個假開關套全部：

| Pipeline | 目前可用 | 下一階段專屬優化 |
|---|---|---|
| Classic | Frustum／Chunk culling、Greedy Mesh、pooled Mesh cache、Dirty coalesce、背景 meshing、count upload budget、Material atlas | Backface Culling、persistent upload ring、MultiDraw |
| Forward PBR | Classic 基礎＋共用 PBR material batching | 量測後決定 Depth Pre-pass、透明 Render Queue、MultiDraw／Indirect |
| Deferred | Raster 基礎＋Depth Pre-pass、Light radius rejection、Shadow cache | Tiled Light Culling、Clustered Deferred、Light Volume／Shadow atlas cache |
| Hybrid PBR Raster | Deferred opaque 流程＋Forward water／glass | 與 Deferred 共用 tile/cluster，另保留透明 queue／reflection cache |
| Compute Path Tracing | per-Chunk 增量上傳、3D texture ring edge streaming、跨幀 accumulation、Russian Roulette、基本 Importance Sampling | 大型 teleport upload budget、Adaptive Sampling、Blue Noise，之後才是 Wavefront、Ray Sorting、Temporal reservoir／ReSTIR |
| Hardware RTX | 固定 ray distance、static/dynamic BLAS 分離、dynamic refit、雙 instance TLAS、runtime Pipeline／Shader Table／scratch reuse、品質 0 保守 build mask | per-Chunk/per-Grid BLAS cache、multi-instance TLAS、transform-only update、非同步 readback、ray-aware instance culling |

## 1.0 — Platform Release

- [ ] 穩定的 world/content/mod 格式與遷移器。
- [ ] Mod 管理器、錯誤隔離、安全模式、相依版本 UI。
- [ ] 藍圖匯入匯出與可分享展示世界。
- [ ] 大型世界壓力測試、GPU 相容矩陣和無 RTX 降級體驗。
- [ ] 內建教學，以建造與展示任務引導工具，但不加入生存壓力。
