# RTX Sandbox Optimization Roadmap

更新日期：2026-08-05。狀態只依目前 Repository 與本輪實測，不因歷史需求重複標記。

## 本輪追加：串流／編輯／Physics Grid 的 120 FPS 目標

| 項目 | 狀態 | 實作與實測證據 |
|---|---|---|
| 固定 V5 壓力世界 | ✅ **【已完成－本輪】** | baseline 升級為 `streaming-benchmark-v5`，加入 8×8×4（256 格）懸空 Physics Grid，Place／Break 各 100 次改為逐幀執行的受支撐方塊。Benchmark 執行期間完全忽略實體鍵盤並禁止寫回 baseline；Compute 首次建立後，Classic 與 DXR 重載時 `BaselineCreatedThisRun=false`，證明同一份唯讀世界可重複比較。 |
| Raster mesh 配置與提交 | ✅ **【已完成－本輪】** | Chunk mesh 改用 `ArrayPool<float>` 的可回收 buffer，greedy mask 亦池化；GPU 直接由 pooled span 上傳，不再為每個 VBO `ToArray()`。重複 dirty 事件合併，過期／evicted mesh 正確歸還。比較 run 的 GPU upload max `35.03 → 1.19 ms`，配置量約 `1.54 GB → 1.18 GB`。 |
| 靜態碰撞體建立／駐留 | ✅ **【已完成－本輪】** | Collider CPU shape build 移到單一版本化背景工作；主執行緒只按 budget commit。Full block 以 X／Y／Z 精確 cuboid greedy merge；靜態 collider 僅保留玩家半徑 3 Chunk 與動態 Grid 周邊，遠距 body 每幀最多移除 8 個；重複 edit 會 coalesce。 |
| Physics Grid 動態碰撞 | ✅ **【已完成－本輪】** | 同密度 full cell 合併為精確 cuboid；單 shape Grid 使用直接 `Box`，不建立一子節點 Compound。一般速度使用 passive speculative contact，只有每 fixed step 位移 ≥0.45 格才使用 sweep CCD；少於 64 個動態 body 時避免多執行緒 dispatcher 開銷。120 Hz 保留，追幀上限由 6 降為 2，避免卡頓後形成 physics spiral。 |
| Compute 3D texture 串流 | ✅ **【已完成－本輪】** | Camera 跨一個 Chunk 時改用 ring offset，只更新進入視窗的 edge Chunk；大型 teleport 才完整重載。最高品質 V5 的 Compute upload max `201.4 → 50.5 ms`、Boundary `209.4 → 64.4 ms`、上傳量 `304.6 → 245.5 MiB`。GPU smoke 已在 RTX 5050 實際跑 33 frames。 |
| DXR refit scratch | ✅ **【已完成－本輪】** | 移除 AS build/refit 前多餘的 `WaitIdle`，並重用 dynamic update scratch resource，不再每次 refit 配置／釋放 D3D12 buffer。DXR AS max 在比較 run 為 `70.0 → 54.0 ms`；strict smoke 確認 Tier 1.1、正式 Trace Rays、91 frames、無 fallback。 |
| 載入、放置、挖除、Physics Grid 永不低於 120 FPS | 🟡 **【部分完成－未達驗收】** | 不降低畫質的前提下仍未達成。V5 Classic `107.4 FPS / 1% low 15.6`；最高品質 Compute `10.1 / 0.4`；DXR `111.8 / 23.2`。Classic 的 Burst20／Place100／Break100／Physics256 最慢 frame 分別 `97.6/24.2/20.3/16.5 ms`；DXR 為 `12.8/15.1/26.8/53.8 ms`。不可宣稱穩定 120 FPS。 |

## 本輪完成

| 項目 | 狀態 | 實作與證據 |
|---|---|---|
| 固定 Benchmark 世界 | ✅ **【已完成】** | 固定 seed `20260805`、目前 baseline `streaming-benchmark-v5`；只在首次建立，後續唯讀重載。包含自然地形邊界、大面積水、玻璃、鏡面地板／牆、多色發光、Micro Voxel、Physics Grid 與自然生成的不可破壞 Bedrock。舊 benchmark 只在 `Benchmarks/Worlds` 範圍內退役，普通 `Saves` 不受影響。 |
| Cold／Warm-up／Measured | ✅ **【已完成】** | 報告記錄 baseline Region cold read；先等待 shader／Renderer／Collider 穩定，再跑不計入正式 frame 統計的 20-Chunk warm-up，最後重設 counters 執行 measured run。 |
| 固定壓力流程 | ✅ **【已完成】** | 同一世界依序執行 1／5／10／20／50／100 Chunk Burst、24 個邊界 Chunk 且路線至少 10 秒、放置 100、破壞 100、水／玻璃／發光／Micro／Grid 修改與獨立 scratch Region write。六條 Renderer 全部完成。 |
| 固定報告 | ✅ **【已完成】** | 每次產生 JSON 與 Markdown；含 Average、1%、0.1%、最慢幀、連續 freeze、update/render、generation/commit/mesh/collider/upload/Region/shadow/DXR、upload bytes、queue、pending commit、GC、heap、working set、loaded Chunk、mesh／AS memory。GPU memory 無可靠 API 時明確為 `null`，不填假數據。 |
| Chunk generation 排程 | ✅ **【已完成－有界 V1】** | Natural terrain 在隔離 world 的 `BelowNormal` worker 建立；live world 每幀最多提交 1 Chunk，結果用 world generation/version 驗證。worker 可在 Burst 期間重用，不再每 Chunk 建立 OS Thread；以 permit 保持一次一件工作，舊視窗 request 會取消／略過。完整多佇列 Job DAG 尚未完成。 |
| Chunk commit | ✅ **【已完成】** | worker 直接移交完整 Chunk 所有權，不再轉成大量 sparse voxel 後於主執行緒逐格複製。最大 completed-but-pending-commit 實測為 1。 |
| Raster CPU Mesh／GPU Buffer | ✅ **【已完成－本輪】** | CPU meshing 為單一版本化背景 job、每幀最多上傳 1 個完成 VBO；不透明完整方塊使用 3D greedy face merge，連續同材質水頂面獨立 greedy merge；透明、Micro Voxel 與 Physics Grid 保持精確原路徑。靜態 selection 改由 fragment world position 還原 cell，合併後仍可選單格。 |
| Collider 熱路徑 | ✅ **【已完成－本輪】** | 移除每 Chunk `Where`／`GroupBy`／逐 column `OrderBy`；改掃已排序的緊密 Chunk snapshot 並合併垂直 run。Classic 實測 collider max `15.90 → 5.27 ms`（相同比較 run）。 |
| Compute voxel upload budget | ✅ **【已完成－ring V1】** | dirty Chunk 3D texture 每幀最多上傳 1 Chunk；跨單一 Chunk 邊界使用 ring offset 並只排入新 edge，完整 window upload 僅保留給大型 teleport／初始化。 |
| Region／Autosave | ✅ **【已完成－V1】** | 32×32 Chunk Region generation 維持安全 manifest／generation 提交；自動存檔改為每幀最多擷取 1 個不可變 Chunk snapshot，再由單一背景 write task 寫入。benchmark baseline 永不寫回；每 Renderer scratch 只保留固定目錄。手動保存仍等待完成，維持明確保存語意。 |
| DXR CPU geometry | ✅ **【已完成－本輪】** | dirty Chunk greedy geometry 與完整 vertex assembly 移到可取消、版本化 `BelowNormal` 背景 job；主執行緒只套用仍有效結果並建 AS。Dispose 會取消未完成 job。DXR AS memory、CPU geometry max 與 AS timing 已納入報告。 |
| 集中式 FrameWorkBudget | ✅ **【已完成－count budget V1】** | Chunk commit、Collider commit、GPU upload、save snapshot 的每幀數量與 upload byte／各階段 ms 目標集中於單一 profile，報告會保存實際 profile。count 限制已接入；自適應 ms／byte 截止仍列在下方部分完成。 |

## 實測結果

硬體：NVIDIA GeForce RTX 5050 Laptop GPU，OpenGL 4.3／D3D12 DXR Tier 1.1。正式測試使用 1.0 render scale；Deferred／Hybrid 與 Hardware quality 3；Compute 為 12 bounces、8 samples/frame、16,384 accumulation 上限，沒有降低品質換分數。

舊 v1 before 與 v2 final 的世界內容、測試長度及 Compute 品質不同，僅能用來辨識舊的一秒內尖峰是否消失；唯一完全同條件的 expanded before/after 是 Classic v2。

### Classic v2 同條件 Before／After

| 指標 | Before | After |
|---|---:|---:|
| Average FPS | 117.19 | 117.33 |
| 1% Low | 51.28 | 60.12 |
| 0.1% Low | 32.10 | 39.43 |
| Slowest frame | 31.35 ms | 29.85 ms |
| Main-thread max | 27.92 ms | 26.44 ms |
| GC allocated | 2,894,192,928 B | 1,175,074,432 B |
| Resident GPU mesh | 265,812,000 B | 43,090,080 B |
| GPU upload bytes | 412,593,600 B | 66,837,120 B |
| Collider max | 15.90 ms | 5.27 ms |

這組結果代表配置量約下降 59%，GPU mesh／upload 約下降 84%；沒有靠降低解析度、光照或材質品質。

### 六 Renderer final

單位皆為該階段最慢 frame ms；Hybrid 使用第二次重跑結果，第一次 Boundary Route 出現 106.44 ms，仍保留為波動證據。Compute 的高 frame time 主要是最高品質本身與完整 3D window upload，不能拿舊的低品質平均 FPS 當同條件 before。

| Renderer | Avg FPS | 1% Low | 0.1% Low | 20 Chunk | 50 Chunk | 100 Chunk | Boundary Route | Region stage | Place 100 | Break 100 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Classic | 111.9 | 44.1 | 30.2 | 12.14 | 15.14 | 21.17 | 30.54 | 35.72 | 23.18 | 9.31 |
| PBR Forward | 111.8 | 45.7 | 35.3 | 21.39 | 16.03 | 20.92 | 28.78 | 27.90 | 19.74 | 10.24 |
| Deferred | 109.4 | 40.8 | 29.7 | 17.15 | 19.07 | 23.82 | 31.30 | 36.12 | 19.75 | 8.34 |
| Hybrid PBR Raster | 108.8 | 36.0 | 22.3 | 24.84 | 21.67 | 26.45 | 55.81 | 26.68 | 20.83 | 16.06 |
| Compute Path Tracing | 17.4 | 9.2 | 6.5 | 70.52 | 58.91 | 67.10 | 75.64 | 102.02 | 79.85 | 77.91 |
| Hardware DXR | 108.3 | 35.6 | 27.8 | 18.43 | 36.04 | 36.01 | 35.60 | 33.15 | 33.58 | 10.19 |

Hardware final：4 static AS builds、43 dynamic builds／refits、39 TLAS updates，AS max `18.80 ms`、CPU background geometry max `127.90 ms`、AS resident memory `1,056,128 B`。100-Chunk 佇列能完整消化，沒有 crash、資料損壞或一秒 freeze。

舊 v1 before → v2 final 最慢 frame（非完全同條件）為：Classic `115.78 → 35.72 ms`、Forward `117.89 → 28.78 ms`、Deferred `98.71 → 36.12 ms`、Hybrid 重跑 `109.59 → 55.81 ms`、Hardware `113.51 → 36.04 ms`。Compute 改用最高品質後為 `153.74 ms`，不能宣稱改善。

## 驗證

| 項目 | 狀態 | 證據 |
|---|---|---|
| Debug／Release build | ✅ **【通過】** | 0 warnings、0 errors。 |
| Self-tests | ✅ **【通過】** | 79/79；包含 async Chunk commit、舊工作取消、save/load/recovery、碰撞與 Grid lifecycle。 |
| 六 Renderer smoke | ✅ **【通過】** | Classic、Forward、Deferred quality 3、Hybrid quality 3、Compute、Hardware strict 全通過。 |
| Deferred／Hybrid framebuffer | ✅ **【通過】** | feature mask 1023、16 point＋1 spot、shadow depth 約 0.766–1.0、framebuffer range 0–255；陰影、水／透明路徑未被 greedy 關閉。 |
| Hardware strict | ✅ **【通過】** | Tier 1.1、1,772 triangles、2 static＋2 dynamic、1 TLAS update；shadow／reflection／transparent／emissive／Grid／Micro／texture hits 全非零，OpenGL framebuffer range 13–231，拒絕 fallback。 |
| Save／Load | ✅ **【通過】** | baseline 重載、Region recovery self-test 與六次 scratch background write 完成；baseline 未覆寫。 |
| 背景工作生命週期 | ✅ **【通過－現有 V1】** | Chunk version/cancellation self-tests通過；DXR strict 正常 Dispose 且取消 background geometry。 |

報告位於執行輸出目錄 `Benchmarks/Reports/latest-final-*.json`；每次亦保存 timestamped JSON／Markdown。

## 部分完成／尚未完成

| 項目 | 狀態 | 剩餘工作／真實瓶頸 |
|---|---|---|
| 正式 Chunk Job DAG／多 Worker Pool | 🟡 **【部分完成】** | 目前 generation、Raster mesh、Region write、DXR CPU geometry 已分離且有界，但尚未拆成 Requested→RegionRead→Noise→Surface→Caves→Decoration→各 commit stage，也沒有 `clamp(CPU-2,1,8)` 的共用 priority worker pool。 |
| 自適應 millisecond／byte budget | 🟡 **【部分完成】** | 統一 `FrameWorkBudget` 與 count 限制已存在；ms／byte 目前是 benchmark 可見的調校目標，尚未做跨幀成本預測、超額延後與 DXR update queue。 |
| Region dirty merge／單寫者佇列 | 🟡 **【部分完成】** | 現有 generation save 安全且不阻塞主執行緒，但每次仍寫完整 captured generation；尚未做到每 Region dirty Chunk payload 增量合併。 |
| 精確 `ChunkDirtyReason` | 🟡 **【部分完成】** | 現有事件已把普通修改限制在本 Chunk／必要鄰居，Deferred light cache 與 Grid topology cache 已存在；尚未統一成 Opaque／Water／Collider／Lights／Shadow／Compute／DXR／Save bit flags。 |
| Raster draw batching | ❌ **【未完成】** | 每 Chunk VBO cache 已存在，但 MultiDrawIndirect、材質批次與完整 opaque/water/transparent/shadow resource 分離尚未完成。 |
| Section＋Palette | ❌ **【未完成】** | 現有 Chunk 仍使用 dense vertical storage；36 個 16³ sparse Section、1/2/4/8/16-bit Palette、ArrayPool／worker arena 尚未實作。報告因此將 allocated section 標成未實作，不填 0 當完成。 |
| 世界 unload／resident memory 上限 | ❌ **【未完成】** | 測試結束為 251 loaded Chunks；尚未有遠距 unload、mesh eviction、Region read cache 與 LOD。這是長時間無限探索的主要剩餘記憶體風險。 |
| Compute ring／edge streaming | ✅ **【已完成－本輪】** | 一格 Chunk window relocation 已不再整批上傳；V5 upload max 為 `50.5 ms`。❌ 大型 teleport 的 full reload 與最高品質 shader 本身的 GPU frame time仍存在，不能據此宣稱 Compute 已達 120 FPS。 |
| DXR per-Chunk／per-Grid BLAS | ❌ **【未完成】** | 目前仍是 static world＋dynamic grids 兩個 BLAS。旋轉 Grid 在 10 秒 route 造成 43 次 dynamic refit；尚未做到每 Chunk／每 Grid instance、transform-only TLAS update、AS compaction 與 DXR ms budget。 |
| D3D12→OpenGL 非同步傳遞 | ❌ **【未完成】** | 目前仍同步 fence/readback/upload；double/triple buffer 或 shared resource 尚未完成。 |
| GPU memory | ❌ **【無可靠量測】** | OpenGL/DXR 現有 API 可準確統計本引擎 mesh/AS allocation，但未接可靠整卡 VRAM telemetry，JSON 維持 `null`。 |

## 下一輪優先順序

1. per-Chunk／per-Grid BLAS＋multi-instance TLAS，Grid transform 只更新 instance transform。
2. D3D12→OpenGL 非同步／共享資源，移除每幀同步 readback fence。
3. 統一可調 ms／byte `FrameWorkBudget` 與正式 priority/cancellation worker queues。
4. Region dirty payload merge 與遠距 Chunk／mesh／AS unload，限制無限探索 resident memory。
5. sparse vertical Section＋Palette；完成後重跑相同 v5 baseline，不另建簡化世界。
