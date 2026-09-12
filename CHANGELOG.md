# Changelog

## Unreleased

### 新增：豆瓣相似项目提供商（Jellyfin 12）

- 实现 `IRemoteSimilarItemsProvider<Movie>` / `IRemoteSimilarItemsProvider<Series>`，把豆瓣的「喜欢这部电影/电视剧的人也喜欢」接入 Jellyfin 12 的「相似项目」（rexxar 接口，电影走 `/v2/movie/{sid}`、剧集走 `/v2/tv/{sid}`，复用插件已有的豆瓣限流与风控检测）。
- 新增配置项「启用豆瓣相似项目」与「相似项目缓存天数」（0-90 天，默认 7 天，0 表示不缓存），配置页已同步。
- 引用使用 `DoubanID` 作为 ProviderId，由 Jellyfin 按 ProviderIds 解析成库内条目；豆瓣评分按 0-10 换算为 0-1 的相似度分数，无评分时不参与加权。
- 只对已有豆瓣编号的条目生效；是否启用由媒体库设置里的「相似项目提供商」勾选决定（宿主按 `TypeOptions.SimilarItemProviders` 过滤，远程提供商默认不勾选）。
- Provider 由 Jellyfin 反射发现（无需 DI 注册），实测 `GET /Libraries/AvailableOptions` 中电影/剧集的相似项目提供商已出现 `MetaShark`。

## 3.2.1 - 2026-09-12

- 适配 Jellyfin 12.0：目标框架升级到 `net10.0`，依赖 `Jellyfin.Controller` / `Jellyfin.Model` 12.0.0。
- 适配 Jellyfin 12.0 API：`TaskTriggerInfo.Type` 改用 `TaskTriggerInfoType` 枚举，`GetSeasonNumberFromPath` 需要传入 `parentId`，`GetItemList` 返回 `IReadOnlyList<BaseItem>`，排序方向 `SortOrder` 迁移到 `Jellyfin.Database.Implementations.Enums`。
- 移除 `vendor/jellyfin-build-10.10` 子模块和 `UseLocalJellyfinReferences` 本地源码引用，改为直接引用 NuGet 包 `Jellyfin.Controller` / `Jellyfin.Model` 12.0.0。
- CI 工作流改用 .NET 10 SDK，发布产物路径改为 `net10.0`；构件上传升级到 `actions/upload-artifact@v4`，Python 升级到 3.12。
- 插件 manifest 的 `targetAbi` 更新为 `12.0.0.0`。

### 附带修复与优化

- 安全：图片代理接口只允许豆瓣图片域名，过滤 `Set-Cookie`/`Transfer-Encoding` 等响应头并释放 `HttpResponseMessage`，避免被当作任意地址代理。
- 修复合集扫描未递归导致子目录电影漏扫、合集漏建。
- 修复豆瓣人物页与登录检查等 4 个入口未走请求限流、建议接口遇到风控 HTML 反序列化异常穿透、Cookie 值含 `=` 被截断的问题。
- 修复 TMDb/TVDB 反序列化遇到非 JSON 响应时异常穿透到 provider 的问题。
- 修复 TMDb 代理配置非法时构造 `WebProxy` 抛异常导致 `TmdbApi` 无法初始化的问题。
- 修复季级 LLM 查找信息未遵守“允许发送相对路径上下文”开关，以及豆瓣季海报 `ProviderName` 写成剧名的问题。
- 修复合集 provider 读取非数字 TMDb ProviderId 时抛 `FormatException` 的问题。
- 修复配置保存缺少互斥导致并发覆盖、状态文件 IO 异常被当作损坏而被清空的问题。
- 修复人物图片更新事件用相等比较判断 `ItemUpdateType.ImageUpdate`（应为 `HasFlag`）。
- 性能：中文判断与季名解析正则改为静态缓存；移除 `EpisodeProvider` 无调用者的 `GetVideoFileCount` 及其 `MemoryCache`。
- 依赖：AngleSharp 1.0.1 → 1.8.1，修复 GHSA-pgww-w46g-26qg。
- 测试：适配 Jellyfin 12 的 ProviderId 格式校验与 API 变更。

### 基于 Jellyfin 12 的深度优化（第二轮）

- 12.0 行为适配：ProviderId 写入改用 `TrySetProviderId` 并记录被拒绝的值；合集成员读写优先走 `ILinkedChildrenService`（适配 `LinkedChildrenLoaded` 语义）；人物关联查询下推 `InternalItemsQuery.PersonIds`；LLM 单集外部 ID 校验按 `SeriesDisplayOrder` 回退换算真实 S/E。
- 性能：`PersonNameResolver` 在作用域内只做一次全库人物查询；TMDb 纠错映射单槽缓存；候选存储的过期扫描摊还到每分钟且读路径不再落盘；内存候选加 30 分钟 TTL；TMDb 代理 HttpClient 按代理地址复用；`JsonStateFile` 去掉每次状态变更的 fsync。
- 可靠性：LLM 语义校验改用调用链的配置快照；豆瓣交叉校验查询失败改为 fail-closed；单个已有 ProviderId 评估异常不再中断整条目刮削；人物刷新状态按 180 天摊还裁剪；配置保存与状态文件 IO 处理更稳健。
- 安全：`POST /plugin/metashark/tmdb/refresh-series` 改为需要 Jellyfin 鉴权（配置页携带令牌），图片代理与登录检查保持匿名访问；图片代理仅允许豆瓣域名并过滤敏感响应头；豆瓣请求恢复 .NET 默认 TLS 证书校验。
- 日志：LLM 触发评估日志降为 Debug，避免整库刷新时逐条 Information 噪音。
- 依赖：`Jellyfin.Controller` / `Jellyfin.Model` 引用排除 runtime 资产，插件输出目录不再携带宿主 dll。

### 第三轮：事件与外部来源健壮性

- 六个 ItemUpdated 事件处理器不再把插件异常抛回 Jellyfin 的事件调用栈，统一记录 Error 日志，避免中断宿主扫描/刷新流程。
- Jellyfin 12 剧集多版本：`PrimaryVersionId` 指向主版本的次版本不再单独做标题回填与简介清理。
- 豆瓣 Cookie 配置重载时以配置为唯一来源，删除的 Cookie 不再继续发送；TVDB API Key/PIN 改为实时读取，并按 Key 失效 token 缓存。
- 单次元数据刷新内缓存 `FindByPath` 结果，减少同一季/剧目录的重复查询；豆瓣禁止重定向的客户端改为复用。
- 移除未使用的 `LoggingHandler`。
- LLM 文本辅助对同一文件名只解析一次（上下文与提示词共用一次 Anitomy 解析）。
- LLM TMDb 纠错快照增加抢占令牌：并发事件不再重复写库，条目被锁定时只释放抢占并保留待应用纠错。

### 第四轮：事件异步化与原生语义对齐

- 新增 `ItemUpdateDispatchQueue`（有界 Channel + 单消费者 + 异常隔离 + 测试 drain 钩子）；四个阻塞型 ItemUpdated worker 改为后台队列处理，不再在宿主事件线程同步等待 IO/数据库。
- 延迟重试仅在"本次未完成"时记录退避（新增 `IsPending` 判断），成功应用不再产生多余的计数与写盘。
- 能力门控在媒体库缺少该类型选项时回退到 Jellyfin 原生 `IBaseItemManager` fetcher 语义（默认跟随全局配置），并接线到 `MissingMetadataSearchService`、`RefreshMetadataTask`、`BoxSetManager`、`AutoCreateCollectionTask`、`TvMissingImageRefillService` 等入口。
- 合集刷新按名称把过滤下推到数据库，不再把全部合集拉进内存。
- 豆瓣 `ConfigurationChanged` 订阅改为具名委托并在 `Dispose` 退订，避免插件重载时旧实例被静态事件钉住。

### 第五轮：Jellyfin 12 实机全功能验证修复

- 修复 Jellyfin 12 下人物指纹无法构造导致共享实体库解析全线失效：12.0 的 `GetPeople`/`GetPeopleByItems` 投影不再返回 `ProviderIds`，而 `Person` 实体本身没有 `Type`/`Role`（二者只在 `PersonInfo` 上），原先"读取 Person 实体"的适配恒失败。改为把 Person 条目的 `ProviderIds` 合并回 `PersonInfo` 后再构造指纹，并按人物名缓存条目查询避免同批次重复读库。此前人物缺图回填被全部跳过、影视人物刷新状态长期判定 `CurrentItemNotAuthoritative`、电视缺图回填的图片门控失败。
- 修复插件状态文件落到临时目录：Jellyfin 12 在插件实例就绪前调用 `RegisterServices`，注册阶段求值的 `DataFolderPath` 退化为 `Path.GetTempPath()/MetaShark`，导致人物/电视缺图回填冷却、标题回填候选、简介清理候选写在临时目录，重启或清理后丢失。改为延迟到状态存储被解析时读取插件数据目录。
- 实机验证（Jellyfin 12.0.0 + 真实媒体库）：豆瓣/TMDb/IMDb 刮削、人物与图片、合集自动创建、TMDb 剧集组映射、人物缺图回填（删除图片后自动补回）、电视缺图回填（移走海报后自动重下）、影视人物刷新状态结清、剧集标题回填（`第 N 集` → 候选入队并应用）、剧集简介清理、LLM 外部 ID 解析（本地 mock 服务）、3 个插件 API 与配置页。

## 5.2.2 - 2026-02-10

- Fix special placement ordering for TMDb extras.
- Bump assembly version to match release tag.

## 5.2.0 - 2026-02-10

- Add TMDb special placement support with season mapping fallback.
- Add configuration toggle in UI for specials placement.
- Use build-10.10 Jellyfin source references for backported fixes.
- Document new settings in README.
