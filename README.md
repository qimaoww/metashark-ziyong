# jellyfin-plugin-metashark

## 项目简介
本仓库基于上游 `cxfksword/jellyfin-plugin-metashark`，自用的 MetaShark 分支。

## AI 改动说明
这是一个 AI 修改过的自用 fork。本文档仅说明当前仓库已经落地的状态，不代表上游官方说明，也不替代上游文档。

## 适用版本
当前已知适配环境为 `nyanmisaka/jellyfin 10.10.7`。其他 Jellyfin 版本是否可用，仍需使用者自行验证。

## 功能
- 支持 TMDb 剧集组映射。
- 支持按需写入 TMDb 关键词标签。
- 支持按 TVDB airs before/after 规则，将 Season 0 特别篇插入季内。
- 支持在元数据缺失时回填剧集标题、单集简介等信息。
- 支持在 Douban 缺失、返回异常或被封时回退到 TMDB 获取元数据与图片。
- 支持图片补全、缺图回填及相关结果处理。

## 安装-更新
- 可通过 Jellyfin 插件仓库方式安装或更新。
- 在 Jellyfin 后台进入 `控制台 -> 插件 -> 仓库`，添加以下 manifest 地址。

manifest 地址：

```text
https://github.com/qimaoww/metashark-ziyong/releases/download/manifest/manifest.json
```

- 添加完成后，可在插件目录中搜索并安装或更新 MetaShark。
- 如果你已手动部署过 MetaShark，也可以直接用新构建覆盖现有插件目录后重启 Jellyfin。
- 本仓库主要面向自用环境，公开 manifest 仅用于当前分支的安装与更新分发。

## 配置说明
- 默认刮削器模式：
  - `默认`：优先使用豆瓣刮削，缺失内容再由 TMDB 补充。
  - `仅 TMDB`：只使用 TMDB 刮削，忽略豆瓣。
- `TMDb 剧集组映射`：用于按 TMDb 剧集组修正或对齐剧集映射。
- `TMDb 关键词标签写入`：开启后可将 TMDb 关键词写入条目标签。
- `特别篇插入`：开启后可按 TVDB airs before/after 规则，将 Season 0 特别篇插入对应季内。
- `缺失元数据回填`：用于在元数据缺失时补回剧集标题、简介等字段。
- `图片补全与回填`：用于在图片缺失或部分来源不可用时继续补全相关图片信息。

## 注意事项
- 这是自用分支，是否适合你的媒体库、数据源和网络环境，需要你自行判断。
- 第三方数据源可用性、网络访问条件、上游差异都会影响实际效果。
- 这里不展开故障排查，也不承诺长期与上游保持完全一致。

## 免责声明
本仓库是 AI 修改过的自用 fork，不代表上游官方立场，也不是官方发布版本。这里不承诺长期兼容、通用适配或固定效果，第三方数据源和网络环境变化带来的问题需要使用者自行承担风险。
