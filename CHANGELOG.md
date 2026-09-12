# Changelog

## Unreleased

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

## 5.2.2 - 2026-02-10

- Fix special placement ordering for TMDb extras.
- Bump assembly version to match release tag.

## 5.2.0 - 2026-02-10

- Add TMDb special placement support with season mapping fallback.
- Add configuration toggle in UI for specials placement.
- Use build-10.10 Jellyfin source references for backported fixes.
- Document new settings in README.
