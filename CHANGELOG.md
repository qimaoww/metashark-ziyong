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

## 5.2.2 - 2026-02-10

- Fix special placement ordering for TMDb extras.
- Bump assembly version to match release tag.

## 5.2.0 - 2026-02-10

- Add TMDb special placement support with season mapping fallback.
- Add configuration toggle in UI for specials placement.
- Use build-10.10 Jellyfin source references for backported fixes.
- Document new settings in README.
